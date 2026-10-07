using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyAIBoard.Core;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new MemoStore(BoardPaths.DefaultDatabasePath));
builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "myaiboard", Version = "0.1.0" })
    .WithStdioServerTransport()
    // Relaxed escaping keeps Korean text readable in tool results instead of \uXXXX escapes.
    .WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });

await builder.Build().RunAsync();
