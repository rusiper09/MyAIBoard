using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class MemoStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyAIBoardTests", Guid.NewGuid().ToString("N"));
    private readonly MemoStore _store;

    public MemoStoreTests() => _store = new MemoStore(Path.Combine(_directory, "board.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Add_PutsNewestMemoFirst()
    {
        var first = _store.Add("first", "", "User");
        var second = _store.Add("second", "body", "Claude");

        var all = _store.GetAll();

        Assert.Equal([second.Id, first.Id], all.Select(m => m.Id));
        Assert.Equal("Claude", all[0].Source);
        Assert.Equal("body", all[0].Body);
        Assert.False(all[0].IsCollapsed);
    }

    [Fact]
    public void Update_ChangesOnlyGivenFields()
    {
        var memo = _store.Add("title", "body", "User");

        var updated = _store.Update(memo.Id, body: "new body");

        Assert.NotNull(updated);
        Assert.Equal("title", updated.Title);
        Assert.Equal("new body", updated.Body);
        Assert.True(updated.UpdatedAt >= memo.UpdatedAt);
    }

    [Fact]
    public void Update_WithExpectedVersion_RefusesToOverwriteNewerChange()
    {
        var original = _store.Add("title", "body", "User");
        Thread.Sleep(5);
        var aiEdit = _store.Update(original.Id, body: "AI body")!;

        Assert.Null(_store.Update(original.Id, body: "my body", expectedUpdatedAt: original.UpdatedAt));
        Assert.Equal("AI body", _store.Get(original.Id)!.Body);

        Assert.NotNull(_store.Update(original.Id, body: "my body", expectedUpdatedAt: aiEdit.UpdatedAt));
        Assert.Equal("my body", _store.Get(original.Id)!.Body);
    }

    [Fact]
    public void MissingMemo_ReturnsNullOrFalse()
    {
        Assert.Null(_store.Get(42));
        Assert.Null(_store.Update(42, title: "x"));
        Assert.False(_store.Delete(42));
        Assert.False(_store.SetCollapsed(42, true));
    }

    [Fact]
    public void Search_MatchesTitleOrBody_AndTreatsWildcardsLiterally()
    {
        _store.Add("Fix login bug", "", "User");
        _store.Add("Groceries", "milk, 100% juice", "User");
        _store.Add("Other", "", "User");

        Assert.Equal(["Fix login bug"], _store.Search("LOGIN").Select(m => m.Title));
        Assert.Equal(["Groceries"], _store.Search("100%").Select(m => m.Title));
        Assert.Empty(_store.Search("_"));
    }

    [Fact]
    public void Reorder_PutsGivenIdsFirst_ThenKeepsTheRest()
    {
        var a = _store.Add("a", "", "User");
        var b = _store.Add("b", "", "User");
        var c = _store.Add("c", "", "User"); // current order: c, b, a

        _store.Reorder([a.Id]);

        Assert.Equal(["a", "c", "b"], _store.GetAll().Select(m => m.Title));
    }

    [Fact]
    public void SetCollapsed_Persists()
    {
        var memo = _store.Add("a", "", "User");

        Assert.True(_store.SetCollapsed(memo.Id, true));

        Assert.True(_store.Get(memo.Id)!.IsCollapsed);
    }

    [Fact]
    public void Delete_MovesMemoToTrash_OutOfEveryBoardQuery()
    {
        var memo = _store.Add("gone", "body", "Claude");

        Assert.True(_store.Delete(memo.Id));

        Assert.Empty(_store.GetAll());
        Assert.Empty(_store.Search("gone"));
        Assert.Null(_store.Get(memo.Id));
        Assert.Null(_store.Update(memo.Id, title: "x"));
        Assert.False(_store.SetCollapsed(memo.Id, true));
        Assert.False(_store.Delete(memo.Id));

        var trashed = Assert.Single(_store.GetDeleted());
        Assert.Equal("gone", trashed.Title);
        Assert.NotNull(trashed.DeletedAt);
    }

    [Fact]
    public void Restore_BringsBackDeletedMemoInPlace()
    {
        var a = _store.Add("a", "", "User");
        var b = _store.Add("b", "body", "Claude");
        _store.SetCollapsed(b.Id, true);
        var before = _store.Get(b.Id)!;
        _store.Delete(b.Id);

        Assert.True(_store.Restore(b.Id));

        Assert.Equal(before, _store.Get(b.Id));
        Assert.Equal(["b", "a"], _store.GetAll().Select(m => m.Title));
        Assert.Empty(_store.GetDeleted());
        Assert.False(_store.Restore(b.Id));
        Assert.False(_store.Restore(a.Id));
    }

    [Fact]
    public void GetDeleted_ListsMostRecentlyDeletedFirst()
    {
        var a = _store.Add("a", "", "User");
        var b = _store.Add("b", "", "User");
        _store.Delete(b.Id);
        Thread.Sleep(5);
        _store.Delete(a.Id);

        Assert.Equal(["a", "b"], _store.GetDeleted().Select(m => m.Title));
    }

    [Fact]
    public void Purge_OnlyRemovesMemosInTheTrash()
    {
        var kept = _store.Add("kept", "", "User");
        var trashed = _store.Add("trashed", "", "User");
        _store.Delete(trashed.Id);

        Assert.False(_store.Purge(kept.Id));
        Assert.True(_store.Purge(trashed.Id));

        Assert.Empty(_store.GetDeleted());
        Assert.False(_store.Restore(trashed.Id));
        Assert.Equal(["kept"], _store.GetAll().Select(m => m.Title));
    }

    [Fact]
    public void EmptyTrash_RemovesOnlyTrashedMemos()
    {
        _store.Add("kept", "", "User");
        _store.Delete(_store.Add("x", "", "User").Id);
        _store.Delete(_store.Add("y", "", "User").Id);

        Assert.Equal(2, _store.EmptyTrash());

        Assert.Empty(_store.GetDeleted());
        Assert.Equal(["kept"], _store.GetAll().Select(m => m.Title));
    }

    [Fact]
    public void PurgeDeletedBefore_KeepsRecentlyDeletedMemos()
    {
        var memo = _store.Add("a", "", "User");
        _store.Delete(memo.Id);

        Assert.Equal(0, _store.PurgeDeletedBefore(DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.Single(_store.GetDeleted());

        Assert.Equal(1, _store.PurgeDeletedBefore(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Empty(_store.GetDeleted());
    }

    [Fact]
    public void Revision_IncreasesOnWrites_AndIsSharedAcrossInstances()
    {
        var other = new MemoStore(Path.Combine(_directory, "board.db"));
        var before = other.GetRevision();

        var memo = _store.Add("a", "", "User");
        _store.Delete(memo.Id);

        Assert.Equal(before + 2, other.GetRevision());
    }

    [Fact]
    public void NewDatabase_IsAtCurrentSchemaVersion()
    {
        Assert.Equal(MemoStore.SchemaVersion, UserVersion(Path.Combine(_directory, "board.db")));
    }

    [Fact]
    public void DatabaseFromBeforeVersioning_IsUpgradedAndKeepsMemos()
    {
        // The layout shipped before migrations existed: same tables, user_version 0.
        var path = Path.Combine(_directory, "legacy.db");
        Exec(path, """
            CREATE TABLE memos (
                id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, body TEXT NOT NULL DEFAULT '',
                is_collapsed INTEGER NOT NULL DEFAULT 0, sort_order INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'User', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE meta (id INTEGER PRIMARY KEY CHECK (id = 1), revision INTEGER NOT NULL);
            INSERT INTO meta VALUES (1, 7);
            INSERT INTO memos (title, body, source, created_at, updated_at)
            VALUES ('old memo', 'body', 'Claude', '2026-10-01T00:00:00.0000000+00:00', '2026-10-01T00:00:00.0000000+00:00');
            """);

        var store = new MemoStore(path);

        Assert.Equal(MemoStore.SchemaVersion, UserVersion(path));
        Assert.Equal(["old memo"], store.GetAll().Select(m => m.Title));
        Assert.Equal(7, store.GetRevision());
    }

    [Fact]
    public void DatabaseFromNewerBuild_IsLeftAlone()
    {
        var path = Path.Combine(_directory, "board.db");
        var newer = MemoStore.SchemaVersion + 1;
        Exec(path, $"PRAGMA user_version = {newer};");

        _ = new MemoStore(path);

        Assert.Equal(newer, UserVersion(path));
    }

    [Fact]
    public void Update_WithExpectedTime_MatchesTheSameInstantWrittenDifferently()
    {
        // Timestamps written by other tools may use "Z" instead of "+00:00".
        var memo = _store.Add("t", "", "User");
        var path = Path.Combine(_directory, "board.db");
        Exec(path, $"UPDATE memos SET updated_at = '2026-11-12T06:00:00.0000000Z', remind_at = '2026-11-12T05:00:00Z' WHERE id = {memo.Id}");
        var stored = _store.Get(memo.Id)!;

        Assert.NotNull(_store.Update(memo.Id, body: "x", expectedUpdatedAt: stored.UpdatedAt));
        Assert.Equal([memo.Id], _store.GetDueReminders(new DateTimeOffset(2026, 11, 12, 14, 0, 0, TimeSpan.FromHours(9))).Select(m => m.Id));
    }

    // ───────────── Done, reminders, origin ─────────────

    [Fact]
    public void Add_RecordsOriginAndReminder()
    {
        var remind = new DateTimeOffset(2030, 1, 2, 3, 4, 0, TimeSpan.Zero);

        var memo = _store.Add("t", "", "Claude", folder: @"E:\Project\App", branch: "main", remindAt: remind);

        Assert.Equal(@"E:\Project\App", memo.Folder);
        Assert.Equal("main", memo.Branch);
        Assert.Equal(remind, memo.RemindAt);
        Assert.Null(memo.CompletedAt);
    }

    [Fact]
    public void SetCompleted_TogglesWithoutTouchingEditTime()
    {
        var memo = _store.Add("t", "", "User");

        Assert.True(_store.SetCompleted(memo.Id, true));
        var done = _store.Get(memo.Id)!;
        Assert.True(done.IsCompleted);
        Assert.Equal(memo.UpdatedAt, done.UpdatedAt);

        // Completing again keeps the first completion time and still reports success.
        Assert.True(_store.SetCompleted(memo.Id, true));
        Assert.Equal(done.CompletedAt, _store.Get(memo.Id)!.CompletedAt);

        Assert.True(_store.SetCompleted(memo.Id, false));
        Assert.False(_store.Get(memo.Id)!.IsCompleted);

        Assert.False(_store.SetCompleted(999, true));
    }

    [Fact]
    public void DueReminders_FireOnceForOpenMemos_AndRearmWhenChanged()
    {
        var now = DateTimeOffset.UtcNow;
        var due = _store.Add("due", "", "User", remindAt: now.AddMinutes(-1));
        var later = _store.Add("later", "", "User", remindAt: now.AddHours(1));
        var done = _store.Add("done", "", "User", remindAt: now.AddMinutes(-1));
        _store.SetCompleted(done.Id, true);

        Assert.Equal([due.Id], _store.GetDueReminders(now).Select(m => m.Id));

        Assert.True(_store.MarkReminded(due.Id, now));
        Assert.Empty(_store.GetDueReminders(now));
        Assert.False(_store.MarkReminded(due.Id, now)); // only the first one counts

        // Moving the reminder arms it again; clearing it removes it.
        _store.SetReminder(due.Id, now.AddMinutes(-0.5));
        Assert.Equal([due.Id], _store.GetDueReminders(now).Select(m => m.Id));
        _store.SetReminder(due.Id, null);
        Assert.Null(_store.Get(due.Id)!.RemindAt);
        Assert.Empty(_store.GetDueReminders(now));

        Assert.Equal([later.Id], _store.GetDueReminders(now.AddHours(2)).Select(m => m.Id));
    }

    [Fact]
    public void ExportImport_KeepsDoneStateReminderAndOrigin_ButPastRemindersDoNotFire()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var future = DateTimeOffset.UtcNow.AddDays(1);
        var a = _store.Add("a", "", "Claude", folder: @"E:\x", branch: "dev", remindAt: past);
        _store.Add("b", "", "User", remindAt: future);
        _store.SetCompleted(a.Id, true);
        var json = MemoExport.ToJson(_store.GetAll(), DateTimeOffset.UtcNow);

        var other = new MemoStore(Path.Combine(_directory, "other.db"));
        other.Import(MemoExport.FromJson(json));

        var imported = other.GetAll().ToDictionary(m => m.Title);
        Assert.True(imported["a"].IsCompleted);
        Assert.Equal((@"E:\x", "dev"), (imported["a"].Folder, imported["a"].Branch));
        Assert.NotNull(imported["a"].RemindedAt);
        Assert.Null(imported["b"].RemindedAt);
        Assert.Empty(other.GetDueReminders(DateTimeOffset.UtcNow));
    }

    private static void Exec(string path, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long UserVersion(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
