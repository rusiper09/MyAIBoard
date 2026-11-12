using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class GitBranchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyAIBoardTests", Guid.NewGuid().ToString("N"));

    public GitBranchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Repo(string name, string head)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), head);
        return repo;
    }

    [Fact]
    public void Find_ReadsBranch_FromRepoRootOrSubfolder()
    {
        var repo = Repo("app", "ref: refs/heads/feature/login\n");
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src", "deep")).FullName;

        Assert.Equal("feature/login", GitBranch.Find(repo));
        Assert.Equal("feature/login", GitBranch.Find(sub));
    }

    [Fact]
    public void Find_ShortensDetachedHead()
    {
        Assert.Equal("0123abc", GitBranch.Find(Repo("detached", "0123abcdef0123abcdef0123abcdef01234567\n")));
    }

    [Fact]
    public void Find_FollowsWorktreeGitFile()
    {
        var gitDir = Path.Combine(_root, "main", ".git", "worktrees", "wt");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/wt-branch");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}");

        Assert.Equal("wt-branch", GitBranch.Find(worktree));
    }

    [Fact]
    public void Find_ReturnsNullOutsideRepos()
    {
        Assert.Null(GitBranch.Find(Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName));
        Assert.Null(GitBranch.Find(null));
        Assert.Null(GitBranch.Find(Path.Combine(_root, "missing")));
    }
}
