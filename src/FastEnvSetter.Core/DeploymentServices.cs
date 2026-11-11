using System.Collections.Immutable;

namespace FastEnvSetter.Core;

public enum DeploymentAction { Deploy, Status, Logs, RolloutStatus, Rollback }

public sealed record DeploymentRequest(Guid TargetId, DeploymentProvider Provider, DeploymentAction Action,
    string SourceRoot, string WorkingDirectory, string FilePath, string Context = "", string Namespace = "",
    string Kubeconfig = "", string Host = "", string RemotePath = "", string KnownHostsFile = "",
    string ResourceName = "", int? RollbackRevision = null);

public sealed record DeploymentSourceState(string SourceRoot, string CommitSha, string Fingerprint);
public sealed record DeploymentCommand(string FileName, string WorkingDirectory, ImmutableArray<string> Arguments);
public sealed record DeploymentInput(string Path, string Sha256);
public sealed record DeploymentPlan(DeploymentRequest Request, DeploymentSourceState Source,
    ImmutableArray<DeploymentCommand> Commands, ImmutableArray<DeploymentInput> Inputs,
    ImmutableArray<string> Limitations, string Digest);
public sealed record DeploymentApproval(string PlanDigest, string SourceFingerprint, string ApprovedBy,
    DateTimeOffset ExpiresAt);
public sealed record DeploymentExecutionResult(Guid OperationId, int ExitCode, string Output);
public enum DeploymentOperationState { Started, Succeeded, Failed, Indeterminate }
public sealed record DeploymentOperation(Guid OperationId, Guid TargetId, string PlanDigest, string SourceSha,
    DeploymentAction Action, DeploymentOperationState State, DateTimeOffset RecordedAt, int? ExitCode);

public interface IDeploymentSourceState
{
    Task<DeploymentSourceState> ReadAsync(string sourceRoot, CancellationToken cancellationToken = default);
}

public interface IDeploymentCommandExecutor
{
    Task<GitCommandResult> ExecuteAsync(DeploymentCommand command, CancellationToken cancellationToken = default);
}

public interface IDeploymentApprovalPolicy
{
    Task ValidateAsync(DeploymentPlan plan, DeploymentApproval approval, CancellationToken cancellationToken = default);
}

public interface IDeploymentJournal
{
    ValueTask<IAsyncDisposable> AcquireTargetAsync(Guid targetId, CancellationToken cancellationToken = default);
    Task AppendAsync(DeploymentOperation operation, CancellationToken cancellationToken = default);
    Task<ImmutableArray<DeploymentOperation>> ReadAsync(CancellationToken cancellationToken = default);
}
