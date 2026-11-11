using System.Text.Json;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using Xunit;

namespace FastEnvSetter.Tests;

public sealed class DevelopmentApiTests
{
    [Fact]
    public void Execution_permissions_default_to_denied_independently_of_configuration()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"AllowExternalConfigurationChanges\":true}")!;
        Assert.True(settings.AllowExternalConfigurationChanges);
        Assert.False(settings.AllowExternalTaskExecution);
        Assert.False(settings.AllowExternalGitChanges);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"projectId\":\"not-a-guid\"}")]
    public async Task Invalid_requests_are_structured(string json)
    {
        using var fixture = new Fixture();
        var result = JsonSerializer.Deserialize<JsonElement>(await fixture.Api.ExecuteJsonAsync(json));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_request", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Requests_are_bounded_versioned_strict_and_project_scoped()
    {
        using var fixture = new Fixture();
        Assert.Contains("invalid_request", await fixture.Api.ExecuteJsonAsync(new string('x', DevelopmentService.MaximumRequestLength + 1)));
        Assert.Equal("invalid_request", (await fixture.Send(new { version = 2, operation = "capabilities", projectId = fixture.Project.Id })).GetProperty("code").GetString());
        Assert.Equal("invalid_request", (await fixture.Send(new { operation = "capabilities", projectId = fixture.Project.Id, typo = true })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await fixture.Send(new { operation = "capabilities", projectId = Guid.NewGuid() })).GetProperty("code").GetString());
        Assert.Equal("unknown_operation", (await fixture.Send(new { operation = "unknown", projectId = fixture.Project.Id })).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("run_project_task")]
    [InlineData("stage_git_files")]
    [InlineData("unstage_git_files")]
    [InlineData("review_git_commit")]
    [InlineData("commit_git_changes")]
    [InlineData("get_git_diff")]
    [InlineData("fetch_git")]
    [InlineData("pull_git")]
    [InlineData("push_git")]
    [InlineData("create_project_snapshot")]
    [InlineData("create_project_release")]
    [InlineData("execute_deployment")]
    public async Task Actions_are_denied_before_any_execution(string operation)
    {
        var git = new RecordingGit();
        using var fixture = new Fixture(git: git);
        var result = await fixture.Send(new { operation, projectId = fixture.Project.Id, requestId = Guid.NewGuid() });
        Assert.Equal("permission_required", result.GetProperty("code").GetString());
        Assert.Empty(git.Commands);
    }

    [Fact]
    public async Task Configuration_grant_does_not_allow_tasks_or_git_and_deployment_stays_denied()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Settings.AllowExternalConfigurationChanges = true;
        foreach (var operation in new[] { "run_project_task", "stage_git_files", "execute_deployment" })
            Assert.Equal("permission_required", (await fixture.Send(new { operation, projectId = fixture.Project.Id })).GetProperty("code").GetString());
        fixture.Store.Data.Settings.AllowExternalTaskExecution = true;
        fixture.Store.Data.Settings.AllowExternalGitChanges = true;
        Assert.Equal("permission_required", (await fixture.Send(new { operation = "execute_deployment", projectId = fixture.Project.Id })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Task_discovery_lists_and_dotenv_previews_never_return_commands_or_values()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "package.json"), "{\"scripts\":{\"test\":\"echo undisclosed-command-value\"}}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".env"), "TOKEN=undisclosed-env-value\nPLAIN=also-private\n");
        fixture.Project.EnvironmentFiles.Add(".env");
        fixture.Project.Tasks.Add(new ProjectTask { Name = "stored", FileName = "undisclosed-executable", Arguments = ["undisclosed-argument"] });
        foreach (var operation in new[] { "list_project_tasks", "discover_project_tasks", "preview_env_files" })
        {
            var result = (await fixture.Send(new { operation, projectId = fixture.Project.Id })).GetRawText();
            Assert.DoesNotContain("undisclosed", result);
            Assert.DoesNotContain("also-private", result);
            Assert.DoesNotContain("fileName", result);
            Assert.DoesNotContain("arguments", result);
        }
    }

    [Fact]
    public async Task Task_runs_return_quickly_deduplicate_reject_conflicts_and_cancel()
    {
        var process = new BlockingProcess();
        using var fixture = new Fixture(process);
        fixture.Store.Data.Settings.AllowExternalTaskExecution = true;
        var task = new ProjectTask { FileName = "fake", Name = "test" };
        fixture.Project.Tasks.Add(task);
        var requestId = Guid.NewGuid();
        var input = new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId };
        var queued = await fixture.Send(input).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(requestId, queued.GetProperty("data").GetProperty("operationId").GetGuid());
        await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await fixture.Send(input)).GetProperty("data").GetProperty("replayed").GetBoolean());
        Assert.Equal(1, process.Count);
        Assert.Equal("request_id_conflict", (await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = Guid.NewGuid(), requestId })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await fixture.Send(new { operation = "get_operation", projectId = fixture.OtherProject.Id, operationId = requestId })).GetProperty("code").GetString());
        await fixture.Send(new { operation = "cancel_operation", projectId = fixture.Project.Id, operationId = requestId });
        var completed = await fixture.Wait(requestId);
        Assert.Equal("cancelled", completed.GetProperty("state").GetString());
        Assert.Equal(1, process.Count);
        fixture.Store.Data.Settings.AllowExternalTaskExecution = false;
        Assert.Equal("permission_required", (await fixture.Send(new { operation = "get_operation", projectId = fixture.Project.Id, operationId = requestId })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Task_logs_are_metadata_only_even_for_unknown_secrets()
    {
        using var fixture = new Fixture(new OutputProcess());
        fixture.Store.Data.Settings.AllowExternalTaskExecution = true;
        var task = new ProjectTask { FileName = "fake" };
        fixture.Project.Tasks.Add(task);
        var requestId = Guid.NewGuid();
        await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId });
        var result = await fixture.Wait(requestId);
        Assert.Equal("succeeded", result.GetProperty("state").GetString());
        Assert.DoesNotContain("unknown-output-secret", result.GetRawText());
        Assert.Contains("output withheld", result.GetRawText());
    }

    [Fact]
    public async Task Operation_registry_is_bounded_without_evicting_retry_receipts()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Settings.AllowExternalTaskExecution = true;
        var task = new ProjectTask { FileName = "fake" };
        fixture.Project.Tasks.Add(task);
        var firstId = Guid.NewGuid();
        for (var index = 0; index < DevelopmentService.MaximumOperations; index++)
        {
            var requestId = index == 0 ? firstId : Guid.NewGuid();
            var response = await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId });
            Assert.True(response.GetProperty("success").GetBoolean());
            await fixture.Wait(requestId);
        }
        var overflow = await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId = Guid.NewGuid() });
        Assert.Equal("operation_capacity_reached", overflow.GetProperty("code").GetString());
        var replay = await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId = firstId });
        Assert.True(replay.GetProperty("data").GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Task_snapshot_preserves_decryption_failures_and_prevents_execution()
    {
        var process = new OutputProcess();
        using var fixture = new Fixture(process);
        fixture.Store.Data.Settings.AllowExternalTaskExecution = true;
        var profile = new EnvProfile { Variables = [new EnvVariable { Name = "TOKEN", IsSecret = true, DecryptionFailed = true }] };
        fixture.Store.Data.Profiles.Add(profile);
        fixture.Project.EnvProfileIds.Add(profile.Id);
        var task = new ProjectTask { FileName = "fake" };
        fixture.Project.Tasks.Add(task);
        var requestId = Guid.NewGuid();
        await fixture.Send(new { operation = "run_project_task", projectId = fixture.Project.Id, taskId = task.Id, requestId });
        Assert.Equal("failed", (await fixture.Wait(requestId)).GetProperty("state").GetString());
        Assert.Equal(0, process.Count);
    }

    [Fact]
    public async Task Ordinary_values_do_not_corrupt_task_identifiers_or_labels()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Profiles.Add(new EnvProfile { Variables = [new EnvVariable { Name = "FLAG", Value = "1" }] });
        var task = new ProjectTask { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "task1" };
        fixture.Project.Tasks.Add(task);

        var response = await fixture.Send(new { operation = "list_project_tasks", projectId = fixture.Project.Id });

        var metadata = response.GetProperty("data")[0];
        Assert.Equal(task.Id, metadata.GetProperty("id").GetGuid());
        Assert.Equal("task1", metadata.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Secret_redaction_preserves_typed_protocol_identifiers()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Profiles.Add(new EnvProfile { Variables = [new EnvVariable { Name = "TOKEN", Value = "1", IsSecret = true }] });
        var task = new ProjectTask { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "task1" };
        fixture.Project.Tasks.Add(task);

        var response = await fixture.Send(new { operation = "list_project_tasks", projectId = fixture.Project.Id });

        Assert.Equal("ok", response.GetProperty("code").GetString());
        Assert.Equal(task.Id, response.GetProperty("data")[0].GetProperty("id").GetGuid());
        Assert.Equal("task***", response.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Git_metadata_disables_execution_helpers_and_masks_known_values()
    {
        var git = new RecordingGit();
        using var fixture = new Fixture(git: git);
        fixture.Store.Data.Profiles.Add(new EnvProfile { Variables = [new EnvVariable { Name = "TOKEN", Value = "stored-token", IsSecret = true }] });
        var result = await fixture.Send(new { operation = "get_git_history", projectId = fixture.Project.Id });
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("stored-token", result.GetRawText());
        Assert.NotEmpty(git.Commands);
        Assert.All(git.Commands, command =>
        {
            Assert.True(command.IsolateEnvironment);
            Assert.Contains("core.fsmonitor=false", command.Arguments);
            Assert.Contains("log.showSignature=false", command.Arguments);
            Assert.Contains("--no-pager", command.Arguments);
            Assert.Contains(command.Arguments, argument => argument.StartsWith("core.hooksPath=", StringComparison.Ordinal));
        });
        await fixture.Send(new { operation = "get_git_status", projectId = fixture.Project.Id });
        Assert.Contains("--ignore-submodules=all", git.Commands.Last().Arguments);
    }

    [Fact]
    public async Task Git_errors_never_return_native_diagnostics()
    {
        using var fixture = new Fixture(git: new ThrowingGit());
        var result = await fixture.Send(new { operation = "get_git_status", projectId = fixture.Project.Id });
        Assert.Equal("operation_failed", result.GetProperty("code").GetString());
        Assert.DoesNotContain("native-secret", result.GetRawText());
    }

    [Fact]
    public async Task Git_review_is_caller_bound_stale_reviews_fail_and_mutations_replay_once()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Settings.AllowExternalGitChanges = true;
        await fixture.Git("init");
        await fixture.Git("config", "user.name", "Test");
        await fixture.Git("config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "file.txt"), "unknown-file-secret\n");
        var stageId = Guid.NewGuid();
        var stage = new { operation = "stage_git_files", projectId = fixture.Project.Id, paths = new[] { "file.txt" }, requestId = stageId };
        await fixture.Send(stage);
        Assert.Equal("succeeded", (await fixture.Wait(stageId)).GetProperty("state").GetString());
        Assert.True((await fixture.Send(stage)).GetProperty("data").GetProperty("replayed").GetBoolean());
        var review = await fixture.Send(new { operation = "review_git_commit", projectId = fixture.Project.Id });
        var fingerprint = review.GetProperty("data").GetProperty("fingerprint").GetString();
        Assert.DoesNotContain("unknown-file-secret", review.GetRawText());
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "file.txt"), "changed\n");
        var staleId = Guid.NewGuid();
        await fixture.Send(new { operation = "commit_git_changes", projectId = fixture.Project.Id, requestId = staleId, reviewFingerprint = fingerprint, message = "stale" });
        Assert.Equal("failed", (await fixture.Wait(staleId)).GetProperty("state").GetString());
        review = await fixture.Send(new { operation = "review_git_commit", projectId = fixture.Project.Id });
        fingerprint = review.GetProperty("data").GetProperty("fingerprint").GetString();
        var commitId = Guid.NewGuid();
        var commit = new { operation = "commit_git_changes", projectId = fixture.Project.Id, requestId = commitId, reviewFingerprint = fingerprint, message = "reviewed" };
        await fixture.Send(commit);
        Assert.Equal("succeeded", (await fixture.Wait(commitId)).GetProperty("state").GetString());
        Assert.True((await fixture.Send(commit)).GetProperty("data").GetProperty("replayed").GetBoolean());
        Assert.Equal("1", (await fixture.Git("rev-list", "--count", "HEAD")).Trim());
        var noReviewId = Guid.NewGuid();
        await fixture.Send(new { operation = "commit_git_changes", projectId = fixture.Project.Id, requestId = noReviewId, reviewFingerprint = fingerprint, message = "reused" });
        Assert.Equal("review_required", (await fixture.Wait(noReviewId)).GetProperty("result").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Snapshots_and_releases_validate_sha_revision_and_survive_service_recreation()
    {
        using var fixture = new Fixture();
        fixture.Store.Data.Settings.AllowExternalConfigurationChanges = true;
        await fixture.Git("init");
        await fixture.Git("config", "user.name", "Test");
        await fixture.Git("config", "user.email", "test@example.invalid");
        await fixture.Git("commit", "--allow-empty", "-m", "initial");
        var sha = (await fixture.Git("rev-parse", "HEAD")).Trim();
        var snapshotId = Guid.NewGuid();
        await fixture.Send(new { operation = "create_project_snapshot", projectId = fixture.Project.Id, requestId = snapshotId, expectedRevision = 0 });
        Assert.Equal("succeeded", (await fixture.Wait(snapshotId)).GetProperty("state").GetString());
        var releases = new Dictionary<string, string> { ["app"] = "sha256:" + new string('a', 64) };
        var invalidId = Guid.NewGuid();
        await fixture.Send(new { operation = "create_project_release", projectId = fixture.Project.Id, requestId = invalidId, expectedRevision = 0, configurationRevision = 1, releaseVersion = "bad", commitSha = new string('f', 40), artifactDigests = releases });
        Assert.Equal("invalid_source_sha", (await fixture.Wait(invalidId)).GetProperty("result").GetProperty("code").GetString());
        var releaseId = Guid.NewGuid();
        var input = new { operation = "create_project_release", projectId = fixture.Project.Id, requestId = releaseId, expectedRevision = 0, configurationRevision = 1, releaseVersion = "1.0.0", commitSha = sha, artifactDigests = releases };
        await fixture.Send(input);
        Assert.Equal("succeeded", (await fixture.Wait(releaseId)).GetProperty("state").GetString());
        Assert.Equal(1, fixture.Store.Data.Revision);
        Assert.Single(fixture.Store.Data.Projects.First(project => project.Id == fixture.Project.Id).Releases);
        Assert.True((await fixture.Send(input)).GetProperty("data").GetProperty("replayed").GetBoolean());
        var stale = await fixture.Send(new { operation = "create_project_release", projectId = fixture.Project.Id, requestId = Guid.NewGuid(), expectedRevision = 0, configurationRevision = 1, releaseVersion = "2.0.0", commitSha = sha, artifactDigests = releases });
        Assert.Equal("revision_conflict", stale.GetProperty("code").GetString());
        using var reloaded = new JsonAppDataStore(new PlainProtector(), fixture.Store.FilePath);
        Assert.Single(reloaded.Data.Projects.First(project => project.Id == fixture.Project.Id).Releases);
        var list = await fixture.Send(new { operation = "list_project_snapshots", projectId = fixture.Project.Id });
        Assert.Contains("Metadata only", list.GetRawText());
        Assert.Single(list.GetProperty("data").GetProperty("snapshots").EnumerateArray());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fes-dev-tests-" + Guid.NewGuid().ToString("N"));
        public JsonAppDataStore Store { get; }
        public Project Project { get; }
        public Project OtherProject { get; } = new();
        public DevelopmentService Api { get; }
        public Fixture(IWorkflowProcessExecutor? process = null, IGitCommandExecutor? git = null)
        {
            Directory.CreateDirectory(Root);
            Store = new JsonAppDataStore(new PlainProtector(), Path.Combine(Root, "data.json"));
            Project = new Project { SourceRoot = Root };
            Store.Data.Projects.AddRange([Project, OtherProject]);
            Api = new DevelopmentService(Store, new ProjectTaskRunner(process ?? new OutputProcess()), new ConfigurationHistoryStore(Path.Combine(Root, "history")), git);
        }
        public async Task<JsonElement> Send(object input) => JsonSerializer.Deserialize<JsonElement>(await Api.ExecuteJsonAsync(JsonSerializer.Serialize(input)));
        public async Task<JsonElement> Wait(Guid operationId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var response = await Send(new { operation = "get_operation", projectId = Project.Id, operationId });
                var data = response.GetProperty("data");
                if (data.GetProperty("state").GetString() is not ("queued" or "running")) return data;
                await Task.Delay(10, timeout.Token);
            }
        }
        public async Task<string> Git(params string[] arguments)
        {
            var result = await new GitNativeCommandExecutor().ExecuteAsync(new("git", Root, arguments));
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput;
        }
        public void Dispose()
        {
            Api.Dispose();
            Store.Dispose();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plainText) => plainText;
        public string? Unprotect(string protectedText) => protectedText;
    }
    private sealed class BlockingProcess : IWorkflowProcessExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
        public async Task<WorkflowProcessResult> ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(0);
        }
    }
    private sealed class OutputProcess : IWorkflowProcessExecutor
    {
        public int Count;
        public Task<WorkflowProcessResult> ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            output(false, "unknown-output-secret");
            return Task.FromResult(new WorkflowProcessResult(0));
        }
    }
    private sealed class RecordingGit : IGitCommandExecutor
    {
        public List<GitCommand> Commands { get; } = [];
        public Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var output = command.Arguments.Contains("--show-toplevel") ? command.WorkingDirectory :
                command.Arguments.Contains("rev-parse") ? Path.Combine(command.WorkingDirectory, ".git") :
                command.Arguments.Contains("log") ? new string('a', 40) + "\0Author\02026-11-10T12:00:00+00:00\0stored-token\0" : "";
            return Task.FromResult(new GitCommandResult(0, output, ""));
        }
    }
    private sealed class ThrowingGit : IGitCommandExecutor
    {
        public Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default) => throw new IOException("native-secret");
    }
}
