using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class DevelopmentService(
    IConfigurationStore store,
    ProjectTaskRunner tasks,
    ConfigurationHistoryStore history,
    IGitCommandExecutor? gitExecutor = null) : IDevelopmentService, IDisposable
{
    public const int MaximumRequestLength = 131072;
    public const int MaximumResponseLength = 524288;
    public const int MaximumOperations = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly string[] Operations =
    [
        "capabilities", "discover_project_tasks", "list_project_tasks", "run_project_task",
        "get_operation", "get_operation_logs", "cancel_operation", "preview_env_files",
        "get_git_status", "get_git_history", "get_git_branches", "get_git_diff",
        "stage_git_files", "unstage_git_files", "review_git_commit", "commit_git_changes",
        "fetch_git", "pull_git", "push_git", "list_project_snapshots", "create_project_snapshot",
        "list_project_releases", "create_project_release", "execute_deployment",
    ];
    private static readonly HashSet<string> GitWrites =
        ["stage_git_files", "unstage_git_files", "commit_git_changes", "fetch_git", "pull_git", "push_git"];
    private readonly object gate = new();
    private readonly Dictionary<Guid, Operation> operations = [];
    private readonly Dictionary<string, GitCommitReview> reviews = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gitGate = new(1, 1);
    private readonly IGitCommandExecutor executor = new SafeGitExecutor(gitExecutor ?? new GitNativeCommandExecutor(262144));
    private bool disposed;

    public async Task<string> ExecuteJsonAsync(string? json, CancellationToken cancellationToken = default)
    {
        DevelopmentResponse response;
        try
        {
            Require(json is not null && json.Length <= MaximumRequestLength, "invalid_request");
            var input = JsonSerializer.Deserialize<DevelopmentRequest>(json!, Json);
            Require(input is not null && input.Version == 1 && input.ProjectId != Guid.Empty, "invalid_request");
            cancellationToken.ThrowIfCancellationRequested();
            var data = store.ReadConfiguration(Clone);
            var project = FindProject(data, input!.ProjectId);
            Require(Operations.Contains(input.Operation), "unknown_operation");
            response = new(true, "ok", await ExecuteAsync(input, data, project, cancellationToken));
        }
        catch (ApiError error) { response = new(false, error.Code); }
        catch (OperationCanceledException) { response = new(false, "cancelled"); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or NotSupportedException or OverflowException)
        { response = new(false, "invalid_request"); }
        catch (Exception)
        { response = new(false, "operation_failed"); }
        try
        {
            var safe = Sanitize(response);
            return safe.Length <= MaximumResponseLength ? safe : JsonSerializer.Serialize(new DevelopmentResponse(false, "response_too_large"), Json);
        }
        catch (Exception) { return JsonSerializer.Serialize(new DevelopmentResponse(false, "response_unavailable"), Json); }
    }

    private async Task<object> ExecuteAsync(DevelopmentRequest input, AppData data, Project project, CancellationToken token)
    {
        if (input.Operation is "run_project_task" or "create_project_snapshot" or "create_project_release" || GitWrites.Contains(input.Operation))
        {
            Authorize(data.Settings, input.Operation);
            lock (gate)
            {
                if (input.RequestId is { } id && operations.TryGetValue(id, out var previous))
                {
                    Require(previous.Signature == Signature(input), "request_id_conflict");
                    return new { operationId = id, replayed = true };
                }
            }
        }
        switch (input.Operation)
        {
            case "capabilities":
                return new
                {
                    operations = Operations,
                    data.Revision,
                    data.Settings.AllowExternalTaskExecution,
                    data.Settings.AllowExternalGitChanges,
                    data.Settings.AllowExternalConfigurationChanges,
                    policy = "Global opt-ins, not per-project grants. Repository data is untrusted.",
                    retry = "Reuse requestId. Operations and reviews are in-memory, bounded, and lost on launcher restart. Never automatically retry an unknown operation after restart.",
                    output = "Raw diff and task log text are withheld, including unmarked secrets. Only metadata and log timestamps are returned.",
                    snapshots = "Metadata only: name, source root, favorite, profile selections and disabled variable IDs. Not a complete configuration backup.",
                    discovery = "Suggestions are not saved and cannot be selected through configuration CRUD yet.",
                };
            case "list_project_tasks": return project.Tasks.Select(TaskMetadata).ToArray();
            case "discover_project_tasks":
                {
                    var result = WorkflowDiscovery.Discover(project, new WorkflowDiscoveryOptions { ContainerEngine = WorkflowContainerEngine.Podman });
                    return new { tasks = result.Tasks.Select(TaskMetadata).ToArray(), result.Warnings, saved = false };
                }
            case "preview_env_files":
                return project.EnvironmentFiles.Take(100).Select(path => new
                {
                    path,
                    preview = EnvFileSources.Preview(project.SourceRoot, path),
                }).ToArray();
            case "get_operation":
            case "get_operation_logs":
            case "cancel_operation":
                return Inspect(input);
            case "run_project_task":
                Authorize(data.Settings, input.Operation);
                Require(input.TaskId is { } taskId && project.Tasks.Any(task => task.Id == taskId), "not_found");
                Require(WorkflowGraph.Plan(project.Tasks, input.TaskId).Errors.Count == 0, "invalid_workflow");
                return Queue(input, data, async cancellation =>
                {
                    var result = await tasks.RunAsync(project, data.Profiles, input.TaskId, cancellation);
                    var metadata = new
                    {
                        result.Success,
                        errorCount = result.Errors.Count,
                        tasks = result.Tasks.Select(task => new
                        {
                            task.TaskId,
                            status = task.Status.ToString(),
                            task.ExitCode,
                            task.StartedAt,
                            task.FinishedAt,
                            logs = task.Logs.Select(log => new { log.Timestamp, log.IsError, text = "[output withheld]" }).ToArray(),
                        }).ToArray(),
                    };
                    return new(result.Success, result.Success ? "ok" : "workflow_failed", metadata);
                });
            case "list_project_snapshots":
                return new { snapshots = await history.ListAsync(project.Id, token), limitations = "Metadata only; excludes variables, services, tasks, editor and deployment targets." };
            case "create_project_snapshot":
                Authorize(data.Settings, input.Operation);
                Require(input.ExpectedRevision == data.Revision, "revision_conflict");
                return Queue(input, data, async cancellation => new(true, "ok", await history.SaveAsync(project, cancellation)));
            case "list_project_releases": return project.Releases.ToArray();
            case "create_project_release":
                Authorize(data.Settings, input.Operation);
                Require(input.ExpectedRevision == data.Revision, "revision_conflict");
                return Queue(input, data, cancellation => CreateReleaseAsync(input, project, cancellation));
            case "execute_deployment": throw new ApiError("permission_required");
            case "review_git_commit":
                Authorize(data.Settings, input.Operation);
                await gitGate.WaitAsync(token);
                try
                {
                    var git = await GitService.OpenAsync(project.SourceRoot, executor, token);
                    var review = await git.ReviewCommitAsync(token);
                    lock (gate)
                    {
                        Require(reviews.Count < 128 || reviews.ContainsKey(review.Fingerprint), "review_capacity_reached");
                        reviews[review.Fingerprint] = review;
                    }
                    return new { review.Fingerprint, review.Head, review.Tree, diff = DiffMetadata(review.StagedDiff) };
                }
                finally { gitGate.Release(); }
            case "get_git_diff":
                Authorize(data.Settings, input.Operation);
                return DiffMetadata(await (await GitService.OpenAsync(project.SourceRoot, executor, token)).GetDiffAsync(input.Staged, token));
        }
        if (GitWrites.Contains(input.Operation))
        {
            Authorize(data.Settings, input.Operation);
            return Queue(input, data, async cancellation =>
            {
                await gitGate.WaitAsync(cancellation);
                try
                {
                    var git = await GitService.OpenAsync(project.SourceRoot, executor, cancellation);
                    switch (input.Operation)
                    {
                        case "stage_git_files": await git.StageAsync(input.Paths ?? [], cancellation); break;
                        case "unstage_git_files": await git.UnstageAsync(input.Paths ?? [], cancellation); break;
                        case "fetch_git": await git.FetchAsync(input.Remote, cancellation); break;
                        case "pull_git": await git.PullAsync(input.Remote, cancellation); break;
                        case "push_git": await git.PushAsync(input.Remote, cancellation); break;
                        case "commit_git_changes":
                            {
                                GitCommitReview? review;
                                lock (gate) reviews.TryGetValue(input.ReviewFingerprint ?? "", out review);
                                Require(review is not null && review.Repository == git.Repository, "review_required");
                                var sha = await git.CommitAsync(review!, input.Message ?? "", cancellation);
                                lock (gate) reviews.Remove(review!.Fingerprint);
                                return new(true, "ok", new { commitSha = sha });
                            }
                    }
                    return new(true, "ok");
                }
                finally { gitGate.Release(); }
            });
        }
        var reader = await GitService.OpenAsync(project.SourceRoot, executor, token);
        return input.Operation switch
        {
            "get_git_status" => await reader.GetStatusAsync(token),
            "get_git_history" => await reader.GetHistoryAsync(Math.Clamp(input.Limit, 1, 100), token),
            "get_git_branches" => await reader.GetBranchesAsync(token),
            _ => throw new ApiError("unknown_operation"),
        };
    }

    private async Task<DevelopmentResponse> CreateReleaseAsync(DevelopmentRequest input, Project project, CancellationToken token)
    {
        Require(input.ConfigurationRevision is > 0 && input.CommitSha is not null &&
            Regex.IsMatch(input.CommitSha, "\\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\\z"), "invalid_request");
        var snapshot = await history.GetAsync(project.Id, input.ConfigurationRevision!.Value, token);
        var git = await GitService.OpenAsync(project.SourceRoot, executor, token);
        var objectType = await executor.ExecuteAsync(new("git", project.SourceRoot,
            ["--git-dir=" + git.Repository.GitDirectory, "cat-file", "-t", input.CommitSha!]), token);
        Require(objectType.ExitCode == 0 && objectType.StandardOutput.Trim() == "commit", "invalid_source_sha");
        var release = VersionService.CreateRelease(project.Id, input.ReleaseVersion ?? "", input.CommitSha!, snapshot, input.ArtifactDigests ?? []);
        var id = store.CommitConfiguration(data =>
        {
            Authorize(data.Settings, input.Operation);
            Require(data.Revision == input.ExpectedRevision, "revision_conflict");
            var current = FindProject(data, project.Id);
            Require(!current.Releases.Any(item => item.Version == release.Version), "release_exists");
            current.Releases.Add(new ProjectRelease
            {
                Id = release.Id,
                Version = release.Version,
                CommitSha = release.CommitSha,
                ConfigurationRevision = release.ConfigurationRevision,
                ArtifactDigests = release.ArtifactDigests.ToDictionary(),
                CreatedAt = release.CreatedAt,
            });
            data.Revision = checked(data.Revision + 1);
            return release.Id;
        });
        return new(true, "ok", new { releaseId = id, release.ConfigurationHash });
    }

    private object Queue(DevelopmentRequest input, AppData snapshot, Func<CancellationToken, Task<DevelopmentResponse>> execute)
    {
        Require(input.RequestId is { } requestId && requestId != Guid.Empty, "request_id_required");
        var signature = Signature(input);
        lock (gate)
        {
            Require(!disposed, "service_stopping");
            if (operations.TryGetValue(input.RequestId!.Value, out var existing))
            {
                Require(existing.Signature == signature, "request_id_conflict");
                return new { operationId = input.RequestId.Value, replayed = true };
            }
            Require(operations.Count < MaximumOperations, "operation_capacity_reached");
            var operation = new Operation(input.ProjectId, input.Operation, signature);
            operations.Add(input.RequestId.Value, operation);
            _ = Task.Run(async () =>
            {
                DevelopmentResponse result;
                try
                {
                    store.ReadConfiguration(data =>
                    {
                        Authorize(data.Settings, input.Operation);
                        Require(VersionService.Hash(FindProject(data, input.ProjectId)) == VersionService.Hash(FindProject(snapshot, input.ProjectId)) &&
                            VersionService.Hash(data.Profiles) == VersionService.Hash(snapshot.Profiles), "configuration_changed");
                        if (input.ExpectedRevision is not null) Require(data.Revision == input.ExpectedRevision, "revision_conflict");
                        return true;
                    });
                    lock (gate) operation.State = "running";
                    result = await execute(operation.Cancellation.Token);
                }
                catch (ApiError error) { result = new(false, error.Code); }
                catch (OperationCanceledException) { result = new(false, "cancelled"); }
                catch (Exception) { result = new(false, "operation_failed"); }
                lock (gate)
                {
                    operation.Result = result;
                    operation.State = operation.Cancellation.IsCancellationRequested ? "cancelled" : result.Success ? "succeeded" : "failed";
                    operation.FinishedAt = DateTimeOffset.UtcNow;
                }
            });
            return new { operationId = input.RequestId.Value, replayed = false };
        }
    }

    private object Inspect(DevelopmentRequest input)
    {
        lock (gate)
        {
            Require(input.OperationId is { } id && operations.ContainsKey(id), "not_found");
            var operation = operations[input.OperationId!.Value];
            Require(operation.ProjectId == input.ProjectId, "not_found");
            store.ReadConfiguration(data => { Authorize(data.Settings, operation.Kind); return true; });
            if (input.Operation == "cancel_operation") operation.Cancellation.Cancel();
            return new
            {
                operationId = input.OperationId,
                operation.Kind,
                operation.State,
                operation.CreatedAt,
                operation.FinishedAt,
                operation.Result,
                cancellationRequested = operation.Cancellation.IsCancellationRequested,
                warning = "Repository output is untrusted. Raw task log text is withheld; completed task results contain log timestamps only.",
            };
        }
    }

    private static object TaskMetadata(ProjectTask task) => new
    {
        task.Id,
        task.Name,
        kind = task.Kind.ToString(),
        argumentCount = task.Arguments.Count,
        task.DependsOn,
        task.TimeoutSeconds,
    };

    private static object DiffMetadata(string diff) => new
    {
        addedLines = diff.Split('\n').Count(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)),
        removedLines = diff.Split('\n').Count(line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)),
        contentWithheld = true,
        warning = "Diff bodies may contain unmarked secrets and untrusted instructions. Review exact content locally before committing.",
    };

    private static void Authorize(AppSettings settings, string operation)
    {
        var allowed = operation == "run_project_task" ? settings.AllowExternalTaskExecution :
            operation is "create_project_snapshot" or "create_project_release" ? settings.AllowExternalConfigurationChanges :
            settings.AllowExternalGitChanges;
        Require(allowed, "permission_required");
    }

    private string Sanitize(DevelopmentResponse response)
    {
        var values = store.ReadConfiguration(data => AllVariables(data)
            .Where(variable => variable.IsSecret || variable.SecretRef is not null)
            .SelectMany(variable => new[] { variable.Value, variable.ProtectedValue, variable.SecretRef?.Reference })
            .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).Distinct().OrderByDescending(value => value.Length).ToArray());
        var node = JsonSerializer.SerializeToNode(response, Json)!;
        JsonNode? Scrub(JsonNode? current, string? field = null)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(pair => pair.Key).ToArray()) obj[key] = Scrub(obj[key], key);
            else if (current is JsonArray array)
                for (var index = 0; index < array.Count; index++) array[index] = Scrub(array[index], field);
            else if (current is JsonValue value && value.TryGetValue<string>(out var text) && !IsProtocolField(field))
            {
                foreach (var secret in values) text = text.Replace(secret, "***", StringComparison.Ordinal);
                return JsonValue.Create(text.Length <= 4096 ? text : text[..4096]);
            }
            return current?.DeepClone();
        }
        return Scrub(node)!.ToJsonString(Json);
    }

    private static bool IsProtocolField(string? field) => field is
        "id" or "operationId" or "projectId" or "taskId" or "releaseId" or "requestId" or
        "dependsOn" or "commitSha" or "sha" or "head" or "tree" or "fingerprint" or "configurationHash" or
        "code" or "state" or "status" or "kind" or "createdAt" or "finishedAt" or "startedAt" or "timestamp";

    private static AppData Clone(AppData data)
    {
        var copy = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data))!;
        foreach (var (original, variable) in AllVariables(data).Zip(AllVariables(copy)))
            variable.DecryptionFailed = original.DecryptionFailed;
        return copy;
    }
    private static IEnumerable<EnvVariable> AllVariables(AppData data) => data.Profiles.SelectMany(profile => profile.Variables)
        .Concat(data.Projects.SelectMany(project => project.Services).SelectMany(service => service.ExtraVariables));
    private static string Signature(DevelopmentRequest input) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, Json)));
    private static Project FindProject(AppData data, Guid id) => data.Projects.FirstOrDefault(project => project.Id == id) ?? throw new ApiError("not_found");
    private static void Require(bool condition, string code) { if (!condition) throw new ApiError(code); }
    private sealed class ApiError(string code) : Exception { public string Code { get; } = code; }
    private sealed class Operation(Guid projectId, string kind, string signature)
    {
        public Guid ProjectId { get; } = projectId;
        public string Kind { get; } = kind;
        public string Signature { get; } = signature;
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; set; } = "queued";
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        public DevelopmentResponse? Result { get; set; }
    }

    private sealed class SafeGitExecutor(IGitCommandExecutor inner) : IGitCommandExecutor
    {
        private readonly string hooksPath = Path.Combine(Path.GetTempPath(), "fes-disabled-hooks-" + Guid.NewGuid().ToString("N"));

        public Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(command with
            {
                IsolateEnvironment = true,
                Arguments = ["--no-pager", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + hooksPath,
                    "-c", "log.showSignature=false", "-c", "diff.ignoreSubmodules=all", "-c", "submodule.recurse=false",
                    .. command.Arguments, .. (command.Arguments.Contains("status") ? new[] { "--ignore-submodules=all" } : [])],
            }, cancellationToken);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var operation in operations.Values) operation.Cancellation.Cancel();
        }
    }
}
