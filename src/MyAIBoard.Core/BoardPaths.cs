namespace MyAIBoard.Core;

public static class BoardPaths
{
    /// <summary>Environment variable that overrides the database location.</summary>
    public const string DatabaseEnvVar = "MYAIBOARD_DB";

    /// <summary>
    /// %USERPROFILE%\.myaiboard: the database, widget settings and installed programs.
    /// Deliberately not under AppData: Store-packaged AI apps (such as the Codex desktop app) and the
    /// MCP servers they start see a private, virtualized AppData, where the shared board would not exist.
    /// </summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".myaiboard");

    /// <summary>
    /// The database shared by the GUI and the MCP server:
    /// %MYAIBOARD_DB% if set, otherwise <see cref="DataDirectory"/>\board.db.
    /// </summary>
    public static string DefaultDatabasePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DatabaseEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            return Path.Combine(DataDirectory, "board.db");
        }
    }
}
