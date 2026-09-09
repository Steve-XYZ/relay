using Microsoft.Extensions.Logging;
using Relay.Core;

namespace Relay.Worker;

/// <summary>
/// One job's checkout: the bare-ish clone, the linked worktree the agent edits, and the base
/// commit the diff is taken against.
///
/// This is a value handed back by <see cref="GitWorkspace.PrepareAsync"/> rather than state on
/// the workspace service. A worker runs many jobs in sequence, so per-job paths and — most
/// importantly — the resolved base SHA must not outlive the job that produced them.
/// </summary>
public sealed record JobWorkspace(string OriginDir, string TreeDir, string BaseSha)
{
    public string Branch(Job job) => GitWorkspace.BranchPrefix + job.ShortId;
}

/// <summary>Git operations for job workspaces, executed with the git CLI (argv style).</summary>
public sealed class GitWorkspace(ILogger<GitWorkspace> logger)
{
    public const string BranchPrefix = "relay/";

    private static readonly Dictionary<string, string> GitEnv = new()
    {
        // Commits must work even where the host has no global git identity.
        ["GIT_AUTHOR_NAME"] = "Relay",
        ["GIT_AUTHOR_EMAIL"] = "relay@localhost",
        ["GIT_COMMITTER_NAME"] = "Relay",
        ["GIT_COMMITTER_EMAIL"] = "relay@localhost",
    };

    /// <summary>
    /// Clones the repository (or reuses the existing clone) and creates/reuses the job
    /// worktree. Milestones go to the caller's callback, which owns the lease and can
    /// therefore authorize the write.
    /// </summary>
    public async Task<JobWorkspace> PrepareAsync(
        string workspaceRoot, Job job, Func<string, string, Task> milestone, CancellationToken ct)
    {
        var ws = Path.Combine(workspaceRoot, job.ShortId);
        var originDir = Path.Combine(ws, "origin");
        var treeDir = Path.Combine(ws, "tree");
        Directory.CreateDirectory(ws);

        if (!Directory.Exists(Path.Combine(originDir, ".git")))
        {
            logger.LogInformation("[{Short}] cloning {Repo}", job.ShortId, job.RepoUrl);
            await Git(ws, "clone", job.RepoUrl, "origin");
            await milestone("repo_cloned", job.RepoUrl);
        }

        // Resolve the diff base before any agent edits exist, once per job.
        var baseSha = (await Capture(originDir, "rev-parse", job.BaseRef)).Trim();

        var branch = BranchPrefix + job.ShortId;
        // In a linked worktree .git is a FILE pointing at the admin dir, not a directory.
        var gitMeta = Path.Combine(treeDir, ".git");
        var worktreeExists = Directory.Exists(treeDir) && (File.Exists(gitMeta) || Directory.Exists(gitMeta));

        if (!worktreeExists)
        {
            // Recovery attempts can leave a branch registered without a live worktree.
            await Git(originDir, "worktree", "prune");
            if (await BranchExistsAsync(originDir, branch, ct))
                await Git(originDir, "worktree", "add", treeDir, branch);
            else
                await Git(originDir, "worktree", "add", treeDir, "-b", branch, job.BaseRef);
        }

        await milestone("workspace_created", $"worktree:{treeDir}");
        return new JobWorkspace(originDir, treeDir, baseSha);
    }

    public async Task<string?> CommitIfChangedAsync(JobWorkspace workspace, Job job, CancellationToken ct)
    {
        var status = (await Capture(workspace.TreeDir, "status", "--porcelain")).Trim();
        if (status.Length == 0)
        {
            logger.LogInformation("[{Short}] no changes to commit", job.ShortId);
            return null;
        }

        await Git(workspace.TreeDir, "add", "-A");
        var message = $"relay({job.ShortId}): {FirstLine(job.Prompt)}\n\n"
                    + $"Produced by Relay job {job.ShortId} (attempt {job.Attempt}).";
        await Git(workspace.TreeDir, "commit", "-m", message);
        return (await Capture(workspace.TreeDir, "rev-parse", "HEAD")).Trim();
    }

    public async Task<(string Stat, IReadOnlyList<string> Files)> DiffSummaryAsync(
        JobWorkspace workspace, CancellationToken ct)
    {
        var stat = (await Capture(workspace.TreeDir, "diff", "--stat", workspace.BaseSha)).Trim();
        var files = (await Capture(workspace.TreeDir, "diff", "--name-only", workspace.BaseSha))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (stat, files);
    }

    public async Task<string?> TryCreatePullRequestAsync(JobWorkspace workspace, Job job, CancellationToken ct)
    {
        var title = $"relay({job.ShortId}): {FirstLine(job.Prompt)}";
        var body = $"Resolves task: **{job.Prompt}**\n\n"
                 + $"- Relay job `{job.ShortId}` (attempt {job.Attempt})\n"
                 + "- Tests executed inside sandbox\n";
        try
        {
            var result = await ProcessRunner.RunAsync("gh",
                ["pr", "create", "--title", title, "--body", body, "--head", workspace.Branch(job)],
                workspace.TreeDir, GitEnv, null, ct);
            return result.ExitCode == 0
                ? result.Output.LastOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[{Short}] PR creation skipped: {Message}", job.ShortId, ex.Message);
            return null;
        }
    }

    // ---- git plumbing ----

    private Task<ProcessRunner.Result> Git(string cwd, params string[] argv) => RunChecked(argv, cwd);

    private async Task<string> Capture(string cwd, params string[] argv) =>
        string.Join("\n", (await RunChecked(argv, cwd)).Output);

    /// <summary>Returns true when the ref exists; never throws.</summary>
    private static async Task<bool> BranchExistsAsync(string originDir, string branch, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git",
            ["rev-parse", "--verify", "--quiet", branch], originDir, GitEnv, null, ct);
        return result.ExitCode == 0;
    }

    private async Task<ProcessRunner.Result> RunChecked(IReadOnlyList<string> argv, string? cwd)
    {
        var result = await ProcessRunner.RunAsync("git", argv, cwd, GitEnv,
            line => logger.LogDebug("[git] {Line}", line), CancellationToken.None);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', argv)} failed ({result.ExitCode}): {result.Tail}");
        return result;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
