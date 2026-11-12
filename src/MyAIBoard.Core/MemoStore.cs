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
        "id, title, body, is_collapsed, sort_order, source, created_at, updated_at, deleted_at, " +
        "completed_at, remind_at, reminded_at, folder, branch";

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
    /// <param name="folder">Working folder of the AI session adding it, if any.</param>
    /// <param name="branch">Git branch checked out in that folder, if any.</param>
    /// <param name="remindAt">When to remind the user, if at all.</param>
    public Memo Add(string title, string body, string source,
        string? folder = null, string? branch = null, DateTimeOffset? remindAt = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var now = Timestamp(DateTimeOffset.UtcNow);
        var id = (long)Execute(connection,
            "INSERT INTO memos (title, body, is_collapsed, sort_order, source, created_at, updated_at, folder, branch, remind_at) " +
            "VALUES ($title, $body, 0, (SELECT COALESCE(MIN(sort_order), 1) - 1 FROM memos), $source, $now, $now, " +
            "$folder, $branch, $remind) RETURNING id",
            ("$title", title), ("$body", body), ("$source", source), ("$now", now),
            ("$folder", folder), ("$branch", branch), ("$remind", NullableTimestamp(remindAt)))!;
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
            // Compared as instants, not text: "...Z" and "...+00:00" spell the same time.
            $"WHERE id = $id AND {Live} AND ($expected IS NULL OR julianday(updated_at) = julianday($expected)); SELECT changes();",
            ("$id", id), ("$title", title), ("$body", body), ("$now", Timestamp(DateTimeOffset.UtcNow)),
            ("$expected", expectedUpdatedAt is { } expected ? Timestamp(expected) : null)));
        if (changed == 0)
            return null;
        BumpRevision(connection);

        transaction.Commit();
        return ReadById(connection, id);
    }

    /// <summary>
    /// Adds exported memos at the bottom of the board, in order, keeping their source and timestamps.
    /// A memo already on the board (same title, body and creation time) is skipped, so importing the
    /// same file twice adds nothing. Returns how many were added and skipped.
    /// </summary>
    public (int Added, int Skipped) Import(IEnumerable<MemoExport.ExportedMemo> memos)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var existing = ReadMemos(connection, $"SELECT {MemoColumns} FROM memos WHERE {Live}")
            .Select(m => (m.Title, m.Body, Timestamp(m.CreatedAt)))
            .ToHashSet();
        var position = Convert.ToInt64(Execute(connection, "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM memos"));

        int added = 0, skipped = 0;
        foreach (var memo in memos)
        {
            var created = Timestamp(memo.CreatedAt);
            if (!existing.Add((memo.Title, memo.Body, created)))
            {
                skipped++;
                continue;
            }

            // A reminder whose time passed before the import counts as already shown,
            // so importing an old backup does not set off a burst of notifications.
            var remindedAt = memo.RemindAt is { } remind && remind <= DateTimeOffset.UtcNow ? remind : (DateTimeOffset?)null;
            Execute(connection,
                "INSERT INTO memos (title, body, is_collapsed, sort_order, source, created_at, updated_at, " +
                "completed_at, remind_at, reminded_at, folder, branch) " +
                "VALUES ($title, $body, $collapsed, $order, $source, $created, $updated, " +
                "$completed, $remind, $reminded, $folder, $branch)",
                ("$title", memo.Title), ("$body", memo.Body), ("$collapsed", memo.IsCollapsed ? 1 : 0),
                ("$order", position++), ("$source", memo.Source),
                ("$created", created), ("$updated", Timestamp(memo.UpdatedAt)),
                ("$completed", NullableTimestamp(memo.CompletedAt)), ("$remind", NullableTimestamp(memo.RemindAt)),
                ("$reminded", NullableTimestamp(remindedAt)), ("$folder", memo.Folder), ("$branch", memo.Branch));
            added++;
        }

        if (added > 0)
        {
            BumpRevision(connection);
            transaction.Commit();
        }
        return (added, skipped);
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

    /// <summary>
    /// Marks a memo on the board done (or to do again). The edit time is left alone: ticking a memo
    /// off is not an edit, and must not cancel an open editor. Returns false if it is not on the board.
    /// </summary>
    public bool SetCompleted(long id, bool completed) =>
        WriteChanges(
            $"UPDATE memos SET completed_at = CASE WHEN $done THEN COALESCE(completed_at, $now) END " +
            $"WHERE id = $id AND {Live} AND (completed_at IS NULL) = $done; SELECT changes();",
            ("$id", id), ("$done", completed), ("$now", Timestamp(DateTimeOffset.UtcNow))) > 0
        || Get(id) is not null; // already in the requested state

    /// <summary>
    /// Sets or clears (null) the reminder of a memo on the board. A new time can fire again even if
    /// an earlier reminder already did. Returns false if the memo is not on the board.
    /// </summary>
    public bool SetReminder(long id, DateTimeOffset? remindAt) =>
        WriteChanges($"UPDATE memos SET remind_at = $remind, reminded_at = NULL WHERE id = $id AND {Live}; SELECT changes();",
            ("$id", id), ("$remind", NullableTimestamp(remindAt))) > 0;

    /// <summary>Open memos on the board whose reminder time has come and that have not been reminded yet.</summary>
    public IReadOnlyList<Memo> GetDueReminders(DateTimeOffset now)
    {
        using var connection = Open();
        return ReadMemos(connection,
            $"SELECT {MemoColumns} FROM memos " +
            $"WHERE {Live} AND completed_at IS NULL AND remind_at IS NOT NULL AND julianday(remind_at) <= julianday($now) " +
            "AND reminded_at IS NULL " +
            "ORDER BY remind_at, id",
            ("$now", Timestamp(now)));
    }

    /// <summary>Records that the reminder of a memo was shown, so it does not fire again.</summary>
    public bool MarkReminded(long id, DateTimeOffset at) =>
        WriteChanges("UPDATE memos SET reminded_at = $at WHERE id = $id AND reminded_at IS NULL; SELECT changes();",
            ("$id", id), ("$at", Timestamp(at))) > 0;

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

        // 3: done state, reminders, and where an AI-added memo came from (for "이어서 하기").
        """
        ALTER TABLE memos ADD COLUMN completed_at TEXT;
        ALTER TABLE memos ADD COLUMN remind_at TEXT;
        ALTER TABLE memos ADD COLUMN reminded_at TEXT;
        ALTER TABLE memos ADD COLUMN folder TEXT;
        ALTER TABLE memos ADD COLUMN branch TEXT;
        """,
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
                DeletedAt: ReadTimestamp(reader, 8),
                CompletedAt: ReadTimestamp(reader, 9),
                RemindAt: ReadTimestamp(reader, 10),
                RemindedAt: ReadTimestamp(reader, 11),
                Folder: reader.IsDBNull(12) ? null : reader.GetString(12),
                Branch: reader.IsDBNull(13) ? null : reader.GetString(13)));
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

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static string? NullableTimestamp(DateTimeOffset? value) => value is { } v ? Timestamp(v) : null;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
