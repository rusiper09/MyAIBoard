using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MyAIBoard.Mcp.Tests;

/// <summary>
/// Starts the real server over stdio, as Claude Code or Codex would, with a throwaway database
/// and a session folder named "MyProject".
/// </summary>
public sealed class StdioServerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyAIBoardTests", Guid.NewGuid().ToString("N"));
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var sessionFolder = Directory.CreateDirectory(Path.Combine(_directory, "MyProject")).FullName;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "myaiboard",
            Command = Path.Combine(AppContext.BaseDirectory, "MyAIBoard.Mcp.exe"),
            WorkingDirectory = sessionFolder,
            ShutdownTimeout = TimeSpan.FromSeconds(1), // the server exits as soon as stdin closes
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["MYAIBOARD_DB"] = Path.Combine(_directory, "board.db"),
            },
        });
        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "claude-code", Version = "1.0" },
        });
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // The server process may still be releasing the database for a moment after disconnecting.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task ExposesTheBoardTools()
    {
        var tools = await _client.ListToolsAsync();

        Assert.Equal(
            ["add_memo", "complete_memo", "delete_memo", "get_memo", "list_memos", "list_trash", "restore_memo",
             "set_reminder", "update_memo"],
            tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task AddThenList_KeepsKoreanReadable_AndTagsTheSessionFolder()
    {
        var added = await Call("add_memo", new() { ["title"] = "한글 제목", ["body"] = "본문 #ui" });

        // Relaxed escaping: Korean comes back as-is, not as \uXXXX.
        Assert.Contains("한글 제목", added);
        Assert.DoesNotContain("\\u", added);

        using var memo = JsonDocument.Parse(added);
        Assert.Equal("본문 #ui\n\n#MyProject", memo.RootElement.GetProperty("body").GetString());
        Assert.Equal("Claude", memo.RootElement.GetProperty("source").GetString());

        using var listed = JsonDocument.Parse(await Call("list_memos", new() { ["tag"] = "myproject" }));
        Assert.Equal("한글 제목", Assert.Single(listed.RootElement.EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task AddWithReminder_ThenComplete_OverTheWire()
    {
        var remindAt = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd'T'10:00");
        using var added = JsonDocument.Parse(await Call("add_memo", new() { ["title"] = "알림", ["remind_at"] = remindAt }));
        var id = added.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(DateTime.Parse(remindAt), added.RootElement.GetProperty("remindAt").GetDateTimeOffset().LocalDateTime);

        using var done = JsonDocument.Parse(await Call("complete_memo", new() { ["id"] = id }));
        Assert.True(done.RootElement.GetProperty("isCompleted").GetBoolean());

        using var open = JsonDocument.Parse(await Call("list_memos", new()));
        Assert.Empty(open.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ToolErrors_AreReportedToTheClient()
    {
        var result = await _client.CallToolAsync("get_memo", new Dictionary<string, object?> { ["id"] = 999 });

        Assert.True(result.IsError);
        Assert.Contains("999", Text(result));
    }

    private async Task<string> Call(string tool, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments);
        Assert.NotEqual(true, result.IsError);
        return Text(result);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;
}
