namespace MyAIBoard.Core;

public static class BoardPaths
{
    /// <summary>Environment variable that overrides the database location.</summary>
    public const string DatabaseEnvVar = "MYAIBOARD_DB";

    /// <summary>
    /// The database shared by the GUI and the MCP server:
    /// %MYAIBOARD_DB% if set, otherwise %LOCALAPPDATA%\MyAIBoard\board.db.
    /// </summary>
    public static string DefaultDatabasePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DatabaseEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "MyAIBoard", "board.db");
        }
    }
}
