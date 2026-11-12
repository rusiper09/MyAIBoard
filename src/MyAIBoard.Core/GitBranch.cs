namespace MyAIBoard.Core;

/// <summary>Reads the checked-out git branch of a folder straight from .git/HEAD, without running git.</summary>
public static class GitBranch
{
    /// <summary>
    /// The branch checked out in the repository containing <paramref name="folder"/>, the short commit id
    /// if HEAD is detached, or null if the folder is not in a git repository.
    /// </summary>
    public static string? Find(string? folder)
    {
        try
        {
            for (var dir = string.IsNullOrWhiteSpace(folder) ? null : new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit))
                    return FromHead(Path.Combine(dotGit, "HEAD"));

                // Worktrees and submodules: .git is a file saying "gitdir: <path>".
                if (File.Exists(dotGit))
                {
                    var pointer = File.ReadAllText(dotGit).Trim();
                    if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                    var gitDir = pointer["gitdir:".Length..].Trim();
                    return FromHead(Path.Combine(Path.GetFullPath(gitDir, dir.FullName), "HEAD"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    private static string? FromHead(string headPath)
    {
        if (!File.Exists(headPath)) return null;

        var head = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: refs/heads/";
        if (head.StartsWith(prefix, StringComparison.Ordinal)) return head[prefix.Length..];
        return head.Length >= 7 && head.All(Uri.IsHexDigit) ? head[..7] : null;
    }
}
