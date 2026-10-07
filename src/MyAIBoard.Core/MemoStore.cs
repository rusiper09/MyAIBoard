using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MyAIBoard.Core;

/// <summary>
/// SQLite-backed memo storage. Safe to use from several processes at once
/// (the GUI and one or more MCP servers): every call opens its own connection,
/// the database runs in WAL mode, and writers wait on a busy timeout.
/// Deleting moves a memo to the trash; only <see cref="Purge"/>, <see cref="EmptyTrash"/>
/// and <see cref="PurgeDeletedBefore"/> remove it for good.
/// </summary>
public sealed class MemoStore
{
    private const string MemoColumns =
        "id, title, body, is_collapsed, sort_order, source, created_at, updated_at, deleted_at";

    private const string Live = "deleted_at IS NULL";
    private const string Trashed = "deleted_at IS NOT NULL";

    private readonly string _connectionString;

    public MemoStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10, // also used as the busy timeout
        }.ToString();

        EnsureSchema();
    }

    /// <summary>All memos on the board (not in the trash), in manual order.</summary>
    public IReadOnlyList<Memo> GetAll()
    {
        using var connection = Open();
        return ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE {Live} ORDER BY sort_order, id");
    }

    /// <summary>Case-insensitive substring search over title and body of memos on the board.</summary>
    public IReadOnlyList<Memo> Search(string query)
    {
        using var connection = Open();
        return ReadMemos(connection,
            $"SELECT {MemoColumns} FROM memos " +
            $"WHERE {Live} AND (title LIKE $pattern ESCAPE '\\' OR body LIKE $pattern ESCAPE '\\') " +
            "ORDER BY sort_order, id",
            ("$pattern", "%" + EscapeLike(query) + "%"));
    }

    /// <summary>A memo on the board; null if it does not exist or is in the trash.</summary>
    public Memo? Get(long id)
    {
        using var connection = Open();
        return ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE id = $id AND {Live}", ("$id", id))
            .SingleOrDefault();
    }

    /// <summary>Memos in the trash, most recently deleted first.</summary>
    public IReadOnlyList<Memo> GetDeleted()
    {
        using var connection = Open();
        return ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE {Trashed} ORDER BY deleted_at DESC, id DESC");
    }

    /// <summary>Adds a memo at the top of the manual order.</summary>
    public Memo Add(string title, string body, string source)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var now = Timestamp(DateTimeOffset.UtcNow);
        var id = (long)Execute(connection,
            "INSERT INTO memos (title, body, is_collapsed, sort_order, source, created_at, updated_at) " +
            "VALUES ($title, $body, 0, (SELECT COALESCE(MIN(sort_order), 1) - 1 FROM memos), $source, $now, $now) " +
            "RETURNING id",
            ("$title", title), ("$body", body), ("$source", source), ("$now", now))!;
        BumpRevision(connection);

        transaction.Commit();
        return ReadById(connection, id);
    }

    /// <summary>
    /// Updates the given fields; null leaves a field unchanged. Returns null if the memo is not on the board,
    /// or if <paramref name="expectedUpdatedAt"/> is given and the memo has been changed since then.
    /// </summary>
    public Memo? Update(long id, string? title = null, string? body = null, DateTimeOffset? expectedUpdatedAt = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var changed = Convert.ToInt64(Execute(connection,
            "UPDATE memos SET title = COALESCE($title, title), body = COALESCE($body, body), updated_at = $now " +
            $"WHERE id = $id AND {Live} AND ($expected IS NULL OR updated_at = $expected); SELECT changes();",
            ("$id", id), ("$title", title), ("$body", body), ("$now", Timestamp(DateTimeOffset.UtcNow)),
            ("$expected", expectedUpdatedAt is { } expected ? Timestamp(expected) : null)));
        if (changed == 0)
            return null;
        BumpRevision(connection);

        transaction.Commit();
        return ReadById(connection, id);
    }

    public bool SetCollapsed(long id, bool isCollapsed)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var changed = Convert.ToInt64(Execute(connection,
            $"UPDATE memos SET is_collapsed = $collapsed WHERE id = $id AND {Live}; SELECT changes();",
            ("$id", id), ("$collapsed", isCollapsed ? 1 : 0)));
        if (changed == 0)
            return false;
        BumpRevision(connection);

        transaction.Commit();
        return true;
    }

    /// <summary>Sets the manual order: the given ids come first, in this order; any others keep their relative order after them.</summary>
    public void Reorder(IReadOnlyList<long> orderedIds)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var remaining = ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE {Live} ORDER BY sort_order, id")
            .Select(m => m.Id)
            .Where(id => !orderedIds.Contains(id));

        long position = 0;
        foreach (var id in orderedIds.Distinct().Concat(remaining))
            Execute(connection, "UPDATE memos SET sort_order = $order WHERE id = $id", ("$id", id), ("$order", position++));
        BumpRevision(connection);

        transaction.Commit();
    }

    /// <summary>Moves a memo to the trash. Returns false if it is not on the board.</summary>
    public bool Delete(long id) =>
        WriteChanges($"UPDATE memos SET deleted_at = $now WHERE id = $id AND {Live}; SELECT changes();",
            ("$id", id), ("$now", Timestamp(DateTimeOffset.UtcNow))) > 0;

    /// <summary>Takes a memo out of the trash, back to its old position. Returns false if it is not in the trash.</summary>
    public bool Restore(long id) =>
        WriteChanges($"UPDATE memos SET deleted_at = NULL WHERE id = $id AND {Trashed}; SELECT changes();",
            ("$id", id)) > 0;

    /// <summary>Permanently deletes a memo that is in the trash. Returns false if it is not in the trash.</summary>
    public bool Purge(long id) =>
        WriteChanges($"DELETE FROM memos WHERE id = $id AND {Trashed}; SELECT changes();", ("$id", id)) > 0;

    /// <summary>Permanently deletes everything in the trash; returns how many memos were removed.</summary>
    public int EmptyTrash() =>
        WriteChanges($"DELETE FROM memos WHERE {Trashed}; SELECT changes();");

    /// <summary>Permanently deletes memos that went into the trash before <paramref name="cutoff"/>; returns how many.</summary>
    public int PurgeDeletedBefore(DateTimeOffset cutoff) =>
        WriteChanges($"DELETE FROM memos WHERE {Trashed} AND deleted_at < $cutoff; SELECT changes();",
            ("$cutoff", Timestamp(cutoff)));

    /// <summary>
    /// A counter that increases on every write from any process.
    /// Poll it to detect changes made elsewhere (e.g. by an AI via MCP).
    /// </summary>
    public long GetRevision()
    {
        using var connection = Open();
        return Convert.ToInt64(Execute(connection, "SELECT revision FROM meta WHERE id = 1"));
    }

    /// <summary>The schema version this build creates and expects (stored in PRAGMA user_version).</summary>
    public static int SchemaVersion => Migrations.Length;

    /// <summary>
    /// Schema changes in order: running Migrations[i] takes the database from version i to i + 1.
    /// Only ever append; never edit a step that has shipped.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // 1: initial schema. IF NOT EXISTS because databases created before versioning
        // already have these tables but still report user_version 0.
        """
        CREATE TABLE IF NOT EXISTS memos (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            title        TEXT    NOT NULL,
            body         TEXT    NOT NULL DEFAULT '',
            is_collapsed INTEGER NOT NULL DEFAULT 0,
            sort_order   INTEGER NOT NULL DEFAULT 0,
            source       TEXT    NOT NULL DEFAULT 'User',
            created_at   TEXT    NOT NULL,
            updated_at   TEXT    NOT NULL
        );
        CREATE TABLE IF NOT EXISTS meta (
            id       INTEGER PRIMARY KEY CHECK (id = 1),
            revision INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO meta (id, revision) VALUES (1, 0);
        """,

        // 2: trash. A memo with deleted_at set is in the trash.
        "ALTER TABLE memos ADD COLUMN deleted_at TEXT;",
    ];

    private void EnsureSchema()
    {
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");

        // Each step runs in an immediate transaction and re-reads the version inside it,
        // so the widget and MCP servers starting at the same time never apply a step twice.
        while (true)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = Convert.ToInt32(Execute(connection, "PRAGMA user_version;"));

            // A newer build already upgraded this database; leave it alone.
            if (version >= Migrations.Length)
                return;

            Execute(connection, Migrations[version]);
            Execute(connection, $"PRAGMA user_version = {version + 1};");
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Runs a statement ending in "SELECT changes();" and bumps the revision if it changed anything.</summary>
    private int WriteChanges(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var changed = Convert.ToInt32(Execute(connection, sql, parameters));
        if (changed == 0)
            return 0;
        BumpRevision(connection);

        transaction.Commit();
        return changed;
    }

    private static Memo ReadById(SqliteConnection connection, long id) =>
        ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE id = $id", ("$id", id)).Single();

    private static void BumpRevision(SqliteConnection connection) =>
        Execute(connection, "UPDATE meta SET revision = revision + 1 WHERE id = 1");

    private static object? Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private static List<Memo> ReadMemos(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();

        var memos = new List<Memo>();
        while (reader.Read())
        {
            memos.Add(new Memo(
                Id: reader.GetInt64(0),
                Title: reader.GetString(1),
                Body: reader.GetString(2),
                IsCollapsed: reader.GetInt64(3) != 0,
                SortOrder: reader.GetInt64(4),
                Source: reader.GetString(5),
                CreatedAt: ParseTimestamp(reader.GetString(6)),
                UpdatedAt: ParseTimestamp(reader.GetString(7)),
                DeletedAt: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8))));
        }
        return memos;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
