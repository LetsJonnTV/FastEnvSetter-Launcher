namespace FastEnvSetter.Core;

public sealed record GitRepositoryIdentity(string SourceRoot, string GitDirectory, string CommonDirectory);
public sealed record GitStatusEntry(string IndexStatus, string WorkTreeStatus, string Path, string? OriginalPath);
public sealed record GitRevision(string Sha, string Author, DateTimeOffset Date, string Subject);
public sealed record GitBranch(string Name, string Sha, bool IsRemote, bool IsCurrent);
public sealed record GitReference(string Name, string Sha, string ObjectType);
public sealed record GitCommitReview(GitRepositoryIdentity Repository, string Head, string HeadReference,
    string Tree, string Fingerprint, string StagedDiff);

public interface IGitReader
{
    GitRepositoryIdentity Repository { get; }
    Task<string> GetSourceShaAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(bool staged = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitRevision>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitReference>> GetReferencesAsync(CancellationToken cancellationToken = default);
}

public interface IGitMutations
{
    Task<GitCommitReview> ReviewCommitAsync(CancellationToken cancellationToken = default);
    Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<string> CommitAsync(GitCommitReview review, string message, CancellationToken cancellationToken = default);
    Task FetchAsync(string remote = "origin", CancellationToken cancellationToken = default);
    Task PullAsync(string remote = "origin", CancellationToken cancellationToken = default);
    Task PushAsync(string remote = "origin", CancellationToken cancellationToken = default);
}

public sealed record GitCommand(string FileName, string WorkingDirectory, IReadOnlyList<string> Arguments,
    bool IsolateEnvironment = false);
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface IGitCommandExecutor
{
    Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default);
}
