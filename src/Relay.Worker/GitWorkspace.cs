using Microsoft.Extensions.Logging;
using Relay.Core;

namespace Relay.Worker;

/// <summary>Git operations for job workspaces, executed with the git CLI (argv style).</summary>
public sealed class GitWorkspace(ILogger<GitWorkspace> logger)
{
    public const string BranchPrefix = "relay/";

    private static readonly Dictionary<string, string> EmptyEnv = new()
    {
        // Commits must work even where the host has no global git identity.
        ["GIT_AUTHOR_NAME"] = "Relay",
        ["GIT_AUTHOR_EMAIL"] = "relay@localhost",
        ["GIT_COMMITTER_NAME"] = "Relay",
        ["GIT_COMMITTER_EMAIL"] = "relay@localhost",
    };

    public string OriginDir { get; private set; } = "";
    public string TreeDir { get; private set; } = "";
    public string? BaseSha { get; private set; }

    /// <summary>Clones the repository (or reuses the existing clone) and creates/reuses the job worktree.</summary>
    public async Task PrepareAsync(string workspaceRoot, Job job, CancellationToken ct)
    {
        var ws = Path.Combine(workspaceRoot, job.ShortId);
        OriginDir = Path.Combine(ws, "origin");
        TreeDir = Path.Combine(ws, "tree");
        Directory.CreateDirectory(ws);

        if (!Directory.Exists(Path.Combine(OriginDir, ".git")))
        {
            logger.LogInformation("[{Short}] cloning {Repo}", job.ShortId, job.RepoUrl);
            await Git("clone", job.RepoUrl, "origin", cwd: ws);
            EmitMilestone(job, "repo_cloned");
        }

        // Resolve the diff base once, before any agent edits exist.
        if (BaseSha is null)
            BaseSha = (await Capture("rev-parse", job.BaseRef, cwd: OriginDir)).Trim();

        var branch = BranchPrefix + job.ShortId;
        // In a linked worktree .git is a FILE pointing at the admin dir, not a directory.
        var gitMeta = Path.Combine(TreeDir, ".git");
        var worktreeExists = Directory.Exists(TreeDir) && (File.Exists(gitMeta) || Directory.Exists(gitMeta));

        if (!worktreeExists)
        {
            // Recovery attempts can leave a branch registered without a live worktree.
            await Git("worktree", "prune", cwd: OriginDir);
            if (await TryVerifyBranch(branch, ct))
                await Git("worktree", "add", TreeDir, branch, cwd: OriginDir);
            else
                await Git("worktree", "add", TreeDir, "-b", branch, job.BaseRef, cwd: OriginDir);
        }
        EmitMilestone(job, "workspace_created", $"worktree:{TreeDir}");
    }

    public async Task CommitIfChangedAsync(Job job, CancellationToken ct)
    {
        var status = (await Capture("status", "--porcelain", cwd: TreeDir)).Trim();
        if (status.Length == 0)
        {
            logger.LogInformation("[{Short}] no changes to commit", job.ShortId);
            return;
        }

        await Git("add", "-A", cwd: TreeDir);
        var message = $"relay({job.ShortId}): {FirstLine(job.Prompt)}\n\nProduced by Relay job {job.ShortId} (attempt {job.Attempt}).";
        await Git("commit", "-m", message, cwd: TreeDir);
        EmitMilestone(job, "commit_created", BranchPrefix + job.ShortId);
    }

    public async Task<(string Stat, IReadOnlyList<string> Files)> DiffSummaryAsync(Job job, CancellationToken ct)
    {
        var baseRef = BaseSha ?? job.BaseRef;
        var stat = (await Capture("diff", "--stat", baseRef, cwd: TreeDir)).Trim();
        var files = (await Capture("diff", "--name-only", baseRef, cwd: TreeDir))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (stat, files);
    }

    public async Task<string?> TryCreatePullRequestAsync(Job job, CancellationToken ct)
    {
        var title = $"relay({job.ShortId}): {FirstLine(job.Prompt)}";
        var body = $"Resolves task: **{job.Prompt}**\n\n- Relay job `{job.ShortId}` (attempt {job.Attempt})\n- Tests executed inside sandbox\n";
        try
        {
            var result = await ProcessRunner.RunAsync("gh",
                ["pr", "create", "--title", title, "--body", body, "--head", BranchPrefix + job.ShortId],
                TreeDir, EmptyEnv, null, ct);
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

    private Task Git(string arg0, string? arg1 = null, string? arg2 = null, string? arg3 = null,
        string? arg4 = null, string? arg5 = null, string? cwd = null)
    {
        var argv = new List<string>();
        foreach (var a in new[] { arg0, arg1, arg2, arg3, arg4, arg5 })
            if (a is not null) argv.Add(a);
        return RunChecked(argv, cwd);
    }

    private async Task<string> Capture(string arg0, string? arg1 = null, string? arg2 = null, string? cwd = null)
    {
        var argv = new List<string>();
        foreach (var a in new[] { arg0, arg1, arg2 })
            if (a is not null) argv.Add(a);
        var result = await RunChecked(argv, cwd);
        return string.Join("\n", result.Output);
    }

    /// <summary>Returns true when the ref exists; never throws.</summary>
    private async Task<bool> TryVerifyBranch(string branchName, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git",
            ["rev-parse", "--verify", "--quiet", branchName], OriginDir, EmptyEnv, null, ct);
        return result.ExitCode == 0;
    }

    private async Task<ProcessRunner.Result> RunChecked(IReadOnlyList<string> argv, string? cwd)
    {
        var result = await ProcessRunner.RunAsync("git", argv, cwd, EmptyEnv,
            line => logger.LogDebug("[git] {Line}", line), CancellationToken.None);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', argv)} failed ({result.ExitCode}): {result.Tail}");
        return result;
    }

    private void EmitMilestone(Job job, string name, string detail = "") =>
        MilestoneEmitted?.Invoke(this, new MilestoneArgs(job.Id, name, detail));

    public event EventHandler<MilestoneArgs>? MilestoneEmitted;

    public sealed record MilestoneArgs(Guid JobId, string Name, string Detail);

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
