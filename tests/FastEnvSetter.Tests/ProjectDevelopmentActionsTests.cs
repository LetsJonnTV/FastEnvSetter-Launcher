using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class ProjectDevelopmentActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fes-desktop-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store = new();

    public ProjectDevelopmentActionsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Sources_resolve_current_project_and_examples_remain_preview_only()
    {
        var stale = new Project { SourceRoot = _root };
        _store.Data.Projects.Add(stale);
        var current = new Project { Id = stale.Id, SourceRoot = _root };
        _store.Data.Projects[0] = current;
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=private-value");
        File.WriteAllText(Path.Combine(_root, ".env-example"), "TOKEN=sample");
        var actions = new ProjectDevelopmentActions(_store);

        actions.AddEnvironmentFile(stale.Id, ".env");

        Assert.Empty(stale.EnvironmentFiles);
        Assert.Equal([".env"], current.EnvironmentFiles);
        Assert.Equal(1, _store.Saves);
        Assert.Throws<ArgumentException>(() => actions.AddEnvironmentFile(stale.Id, ".env-example"));
        Assert.Single(current.EnvironmentFiles);
        Assert.Equal("TOKEN", Assert.Single(EnvFileSources.Preview(_root, ".env-example").Variables).Name);
        Assert.Equal(2, ProjectDevelopmentActions.DiscoverEnvironmentFiles(_root).Count);
    }

    [Fact]
    public void Suggestions_add_dependency_closure_explicitly_without_duplicates()
    {
        var project = new Project { SourceRoot = _root };
        _store.Data.Projects.Add(project);
        var prepare = new ProjectTask { Name = "Prepare", FileName = "tool" };
        var test = new ProjectTask { Name = "Test", FileName = "tool", DependsOn = [prepare.Id] };
        var actions = new ProjectDevelopmentActions(_store);

        actions.AddSuggestion(project.Id, test, [prepare, test]);
        actions.AddSuggestion(project.Id, test, [prepare, test]);

        Assert.Equal(2, project.Tasks.Count);
        Assert.NotEqual(prepare.Id, project.Tasks[0].Id);
        Assert.Equal(project.Tasks[0].Id, Assert.Single(project.Tasks[1].DependsOn));
        Assert.Empty(WorkflowGraph.Plan(project.Tasks).Errors);
        Assert.Throws<InvalidOperationException>(() => actions.RemoveTask(project.Id, project.Tasks[0].Id));
    }

    [Fact]
    public void Task_edits_reject_stale_values_and_outside_directories()
    {
        var task = new ProjectTask { Name = "Test", FileName = "tool" };
        var project = new Project { SourceRoot = _root, Tasks = [task] };
        _store.Data.Projects.Add(project);
        var actions = new ProjectDevelopmentActions(_store);
        var draft = ProjectDevelopmentActions.CopyTask(task);
        draft.WorkingDirectory = "..";
        Assert.Throws<ArgumentException>(() => actions.SaveTask(project.Id, draft, task));
        draft.WorkingDirectory = ".";
        var expected = ProjectDevelopmentActions.CopyTask(task);
        task.Name = "External change";
        Assert.Throws<InvalidOperationException>(() => actions.SaveTask(project.Id, draft, expected));
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public void Run_snapshot_preserves_secret_failure_flags_and_isolated_configuration()
    {
        var task = new ProjectTask { Name = "Test", FileName = "tool", Arguments = ["test"] };
        var project = new Project { SourceRoot = _root, Tasks = [task] };
        _store.Data.Projects.Add(project);
        var variable = new EnvVariable { Name = "TOKEN", Value = "private", DecryptionFailed = true };
        _store.Data.Profiles.Add(new EnvProfile { Variables = [variable] });
        var actions = new ProjectDevelopmentActions(_store);
        var snapshot = actions.CaptureRun(project.Id);

        Assert.True(actions.IsCurrent(snapshot));
        Assert.True(snapshot.Profiles[0].Variables[0].DecryptionFailed);
        task.Arguments.Add("changed");
        variable.Value = "replacement";

        Assert.False(actions.IsCurrent(snapshot));
        Assert.Equal(["test"], snapshot.Project.Tasks[0].Arguments);
        Assert.Equal("private", snapshot.Profiles[0].Variables[0].Value);
    }

    [Fact]
    public void Task_creation_edit_and_removal_preserve_ids_and_request_save()
    {
        var project = new Project { SourceRoot = _root };
        _store.Data.Projects.Add(project);
        var actions = new ProjectDevelopmentActions(_store);
        var draft = new ProjectTask { Name = "Tests", FileName = "tool", Arguments = ["space value", "--test"] };
        actions.SaveTask(project.Id, draft, null);
        var saved = Assert.Single(project.Tasks);
        Assert.Equal(draft.Id, saved.Id);
        Assert.NotSame(draft, saved);
        var update = ProjectDevelopmentActions.CopyTask(saved);
        update.TimeoutSeconds = 120;
        actions.SaveTask(project.Id, update, saved);
        Assert.Equal(120, Assert.Single(project.Tasks).TimeoutSeconds);
        actions.RemoveTask(project.Id, draft.Id);
        Assert.Empty(project.Tasks);
        Assert.Equal(3, _store.Saves);
        Assert.False(_store.Data.Settings.AllowExternalConfigurationChanges);
        Assert.False(_store.Data.Settings.AllowExternalTaskExecution);
        Assert.False(_store.Data.Settings.AllowExternalGitChanges);
    }

    [Fact]
    public void Invalid_runtime_sources_cannot_be_added_and_removal_keeps_file()
    {
        var project = new Project { SourceRoot = _root };
        _store.Data.Projects.Add(project);
        var actions = new ProjectDevelopmentActions(_store);
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=value");
        File.WriteAllText(Path.Combine(_root, ".env.invalid"), "TOKEN=bad\0value");
        Assert.Throws<ArgumentException>(() => actions.AddEnvironmentFile(project.Id, ".env.invalid"));
        Assert.Throws<ArgumentException>(() => actions.AddEnvironmentFile(project.Id, "../outside.env"));
        actions.AddEnvironmentFile(project.Id, ".env");
        actions.RemoveEnvironmentFile(project.Id, ".env");
        Assert.Empty(project.EnvironmentFiles);
        Assert.True(File.Exists(Path.Combine(_root, ".env")));
        Assert.Equal(2, _store.Saves);
    }

    [Fact]
    public async Task Desktop_run_passes_real_provider_contract_and_retains_masked_logs_on_cancel()
    {
        var variable = new EnvVariable { Name = "TOKEN", IsSecret = true, SecretRef = new SecretRef { ProviderId = "test", Reference = "item" } };
        var profile = new EnvProfile { Variables = [variable] };
        _store.Data.Profiles.Add(profile);
        var task = new ProjectTask { Name = "Test", FileName = "tool" };
        var project = new Project { SourceRoot = _root, Tasks = [task], EnvProfileIds = [profile.Id] };
        _store.Data.Projects.Add(project);
        var actions = new ProjectDevelopmentActions(_store);
        using var cancellation = new CancellationTokenSource();
        var executor = new Executor(async (request, output, token) =>
        {
            Assert.Equal("vault-secret", request.Environment["TOKEN"]);
            output(false, "value vault-secret");
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new WorkflowProcessResult(0);
        });
        var provider = new Provider();

        var result = await actions.RunTaskAsync(actions.CaptureRun(project.Id), task.Id, [provider], cancellation.Token, executor);

        Assert.Equal(1, provider.Calls);
        var completed = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowTaskStatus.Cancelled, completed.Status);
        Assert.Equal("value ***", Assert.Single(completed.Logs).Text);
        Assert.False(_store.Data.Settings.AllowExternalTaskExecution);
    }

    [Fact]
    public async Task Desktop_git_selection_stages_only_one_file_and_stale_review_cannot_commit()
    {
        var executor = new GitNativeCommandExecutor();
        foreach (var arguments in new[]
        {
            new[] { "init", "--initial-branch=main" }, new[] { "config", "user.name", "Desktop Test" },
            new[] { "config", "user.email", "desktop@example.invalid" },
        })
            Assert.Equal(0, (await executor.ExecuteAsync(new GitCommand("git", _root, arguments))).ExitCode);
        var git = await GitService.OpenAsync(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "chosen.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.txt"), "not staged");
        var selected = (await git.GetStatusAsync()).Single(entry => entry.Path == "chosen.txt");

        await git.StageAsync(ProjectDevelopmentActions.SelectedGitPaths(selected));
        var review = await git.ReviewCommitAsync();
        Assert.Contains("chosen.txt", review.StagedDiff);
        Assert.DoesNotContain("other.txt", review.StagedDiff);
        await File.WriteAllTextAsync(Path.Combine(_root, "chosen.txt"), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.CommitAsync(review, "stale"));
        var sha = await git.CommitAsync(await git.ReviewCommitAsync(), "reviewed");
        Assert.Equal(sha, await git.GetSourceShaAsync());
        Assert.Contains(await git.GetStatusAsync(), entry => entry.Path == "other.txt" && entry.IndexStatus == "?");
    }

    [Fact]
    public void Git_selection_is_explicit_and_includes_both_rename_paths()
    {
        Assert.Throws<ArgumentException>(() => ProjectDevelopmentActions.SelectedGitPaths(null));
        Assert.Equal(["new.txt", "old.txt"], ProjectDevelopmentActions.SelectedGitPaths(new("R", " ", "new.txt", "old.txt")));
        Assert.Equal(["file.txt"], ProjectDevelopmentActions.SelectedGitPaths(new(" ", "M", "file.txt", null)));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private sealed class Executor(Func<WorkflowProcessRequest, Action<bool, string>, CancellationToken, Task<WorkflowProcessResult>> execute) : IWorkflowProcessExecutor
    {
        Task<WorkflowProcessResult> IWorkflowProcessExecutor.ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken) =>
            execute(request, output, cancellationToken);
    }

    private sealed class Provider : ISecretProvider
    {
        public int Calls { get; private set; }
        public string Id => "test";
        public string DisplayName => "Test";
        public string ReferenceHint => "item";
        public bool SupportsLogin => false;
        public bool UsesServerUrl => false;
        public Task<string> GetStatusAsync(CancellationToken token = default) => Task.FromResult("ready");
        public Task LoginAsync(string email, string password, string code, CancellationToken token = default) => Task.CompletedTask;
        public Task<string> ResolveAsync(string reference, CancellationToken token = default)
        {
            Calls++;
            Assert.Equal("item", reference);
            return Task.FromResult("vault-secret");
        }
    }

    private sealed class Store : IAppDataStore
    {
        public AppData Data { get; } = new();
        public string FilePath => "";
        public string? LoadWarning => null;
        public int Saves { get; private set; }
        public void RequestSave() => Saves++;
        public void SaveNow() => Saves++;
    }
}
