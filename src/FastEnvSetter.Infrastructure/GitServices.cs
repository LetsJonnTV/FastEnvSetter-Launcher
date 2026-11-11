using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class GitNativeCommandExecutor(int maximumOutputCharacters = 1_048_576, TimeSpan? timeout = null) : IGitCommandExecutor
{
    public async Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default)
    {
        if (maximumOutputCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.IsolateEnvironment)
        {
            var inherited = start.Environment.Where(pair => pair.Key.ToUpperInvariant() is
                "PATH" or "PATHEXT" or "SYSTEMROOT" or "WINDIR" or "TEMP" or "TMP" or "HOME" or "USERPROFILE" or
                "APPDATA" or "LOCALAPPDATA" or "PROGRAMFILES" or "PROGRAMFILES(X86)" or "PROGRAMDATA" or "SSH_AUTH_SOCK").ToArray();
            start.Environment.Clear();
            foreach (var pair in inherited) start.Environment[pair.Key] = pair.Value;
        }
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = ReadAsync(process.StandardOutput, deadline);
        var error = ReadAsync(process.StandardError, deadline);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await output, await error);
        }
        catch
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private async Task<string> ReadAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
        {
            if (result.Length + count > maximumOutputCharacters)
            {
                await deadline.CancelAsync();
                throw new InvalidOperationException("Command output exceeded the configured limit.");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

public sealed class GitService : IGitReader, IGitMutations
{
    private readonly IGitCommandExecutor executor;
    private readonly SemaphoreSlim mutations = new(1, 1);
    public GitRepositoryIdentity Repository { get; }

    private GitService(GitRepositoryIdentity repository, IGitCommandExecutor executor)
    {
        Repository = repository;
        this.executor = executor;
    }

    public static async Task<GitService> OpenAsync(string sourceRoot, IGitCommandExecutor? executor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(sourceRoot)) throw new ArgumentException("SourceRoot must be absolute.", nameof(sourceRoot));
        executor ??= new GitNativeCommandExecutor();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        async Task<string> Read(string argument)
        {
            var result = await executor.ExecuteAsync(new("git", root, ["rev-parse", argument]), cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException("SourceRoot is not an available Git working tree.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim(), root));
        }
        var actualRoot = await Read("--show-toplevel");
        if (!SamePath(root, actualRoot)) throw new InvalidOperationException("SourceRoot must identify the repository root, not a parent or child directory.");
        return new(new(root, await Read("--absolute-git-dir"), await Read("--git-common-dir")), executor);
    }

    public async Task<string> GetSourceShaAsync(CancellationToken cancellationToken = default) =>
        (await RunAsync(["rev-parse", "--verify", "HEAD"], cancellationToken)).Trim();

    public async Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ParseStatus(await RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken));

    public static IReadOnlyList<GitStatusEntry> ParseStatus(string porcelain)
    {
        var records = porcelain.Split('\0');
        var entries = new List<GitStatusEntry>();
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length == 0) continue;
            if (record.Length < 4 || record[2] != ' ') throw new FormatException("Invalid Git status record.");
            string? original = null;
            if (record[0] is 'R' or 'C' || record[1] is 'R' or 'C')
            {
                if (++index >= records.Length || records[index].Length == 0) throw new FormatException("Missing rename source.");
                original = records[index];
            }
            entries.Add(new(record[..1], record.Substring(1, 1), record[3..], original));
        }
        return entries;
    }

    public Task<string> GetDiffAsync(bool staged = false, CancellationToken cancellationToken = default) =>
        RunAsync(staged ? ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--binary", "--"] :
            ["diff", "--no-ext-diff", "--no-textconv", "--binary", "--"], cancellationToken);

    public async Task<IReadOnlyList<GitRevision>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var text = await RunAsync(["log", $"-n{limit}", "--format=%H%x00%an%x00%aI%x00%s%x00", "HEAD", "--"], cancellationToken);
        var fields = text.Split('\0');
        var revisions = new List<GitRevision>();
        for (var index = 0; index + 3 < fields.Length; index += 4)
            revisions.Add(new(fields[index].Trim(), fields[index + 1], DateTimeOffset.Parse(fields[index + 2], CultureInfo.InvariantCulture), fields[index + 3]));
        return revisions;
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var text = await RunAsync(["for-each-ref", "--format=%(refname)%00%(objectname)%00%(HEAD)", "refs/heads/", "refs/remotes/"], cancellationToken);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.TrimEnd('\r').Split('\0');
            return new GitBranch(fields[0], fields[1], fields[0].StartsWith("refs/remotes/", StringComparison.Ordinal), fields[2] == "*");
        }).ToArray();
    }

    public async Task<IReadOnlyList<GitReference>> GetReferencesAsync(CancellationToken cancellationToken = default)
    {
        var text = await RunAsync(["for-each-ref", "--format=%(refname)%00%(objectname)%00%(objecttype)"], cancellationToken);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.TrimEnd('\r').Split('\0');
            return new GitReference(fields[0], fields[1], fields[2]);
        }).ToArray();
    }

    public Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        MutateAsync(async () => { await RunAsync(["--literal-pathspecs", "add", "--", .. ValidatePaths(paths)], cancellationToken); }, cancellationToken);

    public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var selected = ValidatePaths(paths);
            var head = await HeadAsync(cancellationToken);
            await RunAsync(head.Length == 0 ? ["--literal-pathspecs", "rm", "--cached", "--", .. selected] :
                ["--literal-pathspecs", "restore", "--staged", "--source=HEAD", "--", .. selected], cancellationToken);
        }, cancellationToken);

    public async Task<GitCommitReview> ReviewCommitAsync(CancellationToken cancellationToken = default)
    {
        await VerifyIdentityAsync(cancellationToken);
        var head = await HeadAsync(cancellationToken);
        var reference = await ReferenceAsync(cancellationToken);
        var tree = (await RunAsync(["write-tree"], cancellationToken)).Trim();
        var diff = await GetDiffAsync(true, cancellationToken);
        if (diff.Length == 0) throw new InvalidOperationException("There are no staged changes to review.");
        return new(Repository, head, reference, tree, await FingerprintAsync(cancellationToken), diff);
    }

    public async Task<string> CommitAsync(GitCommitReview review, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains('\0')) throw new ArgumentException("A commit message is required.", nameof(message));
        string sha = "";
        await MutateAsync(async () =>
        {
            if (review.Repository != Repository) throw new InvalidOperationException("Review belongs to another repository.");
            var current = await ReviewCommitAsync(cancellationToken);
            if (current != review) throw new InvalidOperationException("The reviewed source or index changed. Review again.");
            await using var indexLock = new FileStream(Path.Combine(Repository.GitDirectory, "index.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            await using var headLock = new FileStream(Path.Combine(Repository.GitDirectory, "HEAD.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            if (await FingerprintAsync(cancellationToken) != review.Fingerprint) throw new InvalidOperationException("Stale review.");
            var arguments = new List<string> { "commit-tree", review.Tree };
            if (review.Head.Length > 0) arguments.AddRange(["-p", review.Head]);
            arguments.AddRange(["-m", message]);
            sha = (await RunAsync(arguments, cancellationToken)).Trim();
            var reference = review.HeadReference;
            await headLock.DisposeAsync();
            if (reference == "HEAD")
            {
                await RunAsync(["update-ref", "--no-deref", "HEAD", sha, review.Head], cancellationToken);
            }
            else await RunAsync(["update-ref", "-m", "FastEnvSetter reviewed commit", reference, sha,
                review.Head.Length == 0 ? new string('0', sha.Length) : review.Head], cancellationToken);
        }, cancellationToken);
        return sha;
    }

    public Task FetchAsync(string remote = "origin", CancellationToken cancellationToken = default) =>
        NetworkAsync("fetch", remote, cancellationToken);
    public Task PullAsync(string remote = "origin", CancellationToken cancellationToken = default) =>
        NetworkAsync("pull", remote, cancellationToken);
    public Task PushAsync(string remote = "origin", CancellationToken cancellationToken = default) =>
        NetworkAsync("push", remote, cancellationToken);

    private Task NetworkAsync(string operation, string remote, CancellationToken cancellationToken) => MutateAsync(async () =>
    {
        if (!Regex.IsMatch(remote, "^[A-Za-z0-9][A-Za-z0-9._-]*$")) throw new ArgumentException("Use a configured remote name.", nameof(remote));
        var url = (await RunAsync(operation == "push" ? ["remote", "get-url", "--push", "--all", remote] : ["remote", "get-url", "--all", remote], cancellationToken)).Trim();
        if (url.Contains("::", StringComparison.Ordinal) || url.Contains('\n') || url.Contains('\r') || url.Contains('?') || url.Contains('#') ||
            (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile &&
             (uri.Scheme is not ("https" or "ssh") || (uri.Scheme == "https" && uri.UserInfo.Length > 0) || uri.UserInfo.Contains(':'))))
            throw new InvalidOperationException("Remote must not contain credentials or insecure transport.");
        if (operation == "fetch") await RunAsync(["-c", "http.followRedirects=false", "fetch", "--no-tags", "--no-recurse-submodules", remote,
            $"refs/heads/*:refs/remotes/{remote}/*"], cancellationToken);
        else
        {
            var reference = await ReferenceAsync(cancellationToken);
            if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) throw new InvalidOperationException("A branch is required for pull/push.");
            var branch = reference[11..];
            if (operation == "pull")
            {
                if ((await GetStatusAsync(cancellationToken)).Count != 0) throw new InvalidOperationException("Pull requires a clean working tree.");
                await RunAsync(["-c", "http.followRedirects=false", "-c", "core.hooksPath=" + Path.Combine(Path.GetTempPath(), "fes-no-hooks-" + Guid.NewGuid().ToString("N")),
                    "pull", "--ff-only", "--no-rebase", "--no-tags", "--no-recurse-submodules", remote, branch], cancellationToken);
            }
            else await RunAsync(["-c", "http.followRedirects=false", "push", "--no-verify", "--no-follow-tags", "--no-mirror", "--no-force",
                "--recurse-submodules=no", remote, $"{reference}:{reference}"], cancellationToken);
        }
    }, cancellationToken);

    private string[] ValidatePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("Select at least one path.", nameof(paths));
        return paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\0')) throw new ArgumentException("Use relative repository paths.", nameof(paths));
            var full = Path.GetFullPath(path, Repository.SourceRoot);
            var relative = Path.GetRelativePath(Repository.SourceRoot, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == "." ||
                relative.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Path is outside the working tree or selects Git metadata.", nameof(paths));
            return relative;
        }).ToArray();
    }

    private async Task<string> FingerprintAsync(CancellationToken cancellationToken)
    {
        var state = string.Join('\0', await HeadAsync(cancellationToken), await ReferenceAsync(cancellationToken),
            await RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken),
            await GetDiffAsync(true, cancellationToken), await GetDiffAsync(false, cancellationToken));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    private async Task<string> HeadAsync(CancellationToken cancellationToken)
    {
        var result = await RawAsync(["rev-parse", "--verify", "HEAD"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : "";
    }

    private async Task<string> ReferenceAsync(CancellationToken cancellationToken)
    {
        var result = await RawAsync(["symbolic-ref", "-q", "HEAD"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : "HEAD";
    }

    private async Task VerifyIdentityAsync(CancellationToken cancellationToken)
    {
        var current = await OpenAsync(Repository.SourceRoot, executor, cancellationToken);
        if (current.Repository != Repository) throw new InvalidOperationException("Repository identity changed.");
    }

    private async Task MutateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await mutations.WaitAsync(cancellationToken);
        try { await VerifyIdentityAsync(cancellationToken); await action(); }
        finally { mutations.Release(); }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RawAsync(arguments, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Git command failed (exit {result.ExitCode}); diagnostic output is withheld because it may contain credentials.");
        return result.StandardOutput;
    }

    private Task<GitCommandResult> RawAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(new("git", Repository.SourceRoot, ["--git-dir=" + Repository.GitDirectory, "--work-tree=" + Repository.SourceRoot,
            "-c", "color.ui=false", "-c", "core.quotePath=false", "-c", "core.fsmonitor=false", .. arguments]), cancellationToken);

    private static bool SamePath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
