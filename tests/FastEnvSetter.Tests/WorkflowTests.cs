using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fes-workflow-" + Guid.NewGuid());
    public WorkflowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Completion_dependencies_block_after_nonzero_without_retry()
    {
        var prepare = MakeTask();
        var build = MakeTask(prepare.Id);
        var test = MakeTask(build.Id);
        var fake = new FakeExecutor((_, _, _) => Task.FromResult(new WorkflowProcessResult(7)));
        var result = await new ProjectTaskRunner(fake).RunAsync(new Project { SourceRoot = _root, Tasks = [test, build, prepare] }, [], test.Id);
        Assert.Equal([WorkflowTaskStatus.Failed, WorkflowTaskStatus.Blocked, WorkflowTaskStatus.Blocked], result.Tasks.Select(task => task.Status));
        Assert.Equal(1, fake.Calls);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Selected_task_runs_only_its_dependency_closure_with_structured_args()
    {
        var prepare = MakeTask();
        var test = MakeTask(prepare.Id);
        test.Arguments = ["space value", "& not a shell"];
        var fake = new FakeExecutor((request, _, _) =>
        {
            if (request.Arguments.Count > 0)
                Assert.Equal(test.Arguments, request.Arguments);
            return Task.FromResult(new WorkflowProcessResult(0));
        });
        var result = await new ProjectTaskRunner(fake).RunAsync(new Project { SourceRoot = _root, Tasks = [test, MakeTask(), prepare] }, [], test.Id);
        Assert.True(result.Success);
        Assert.Equal([prepare.Id, test.Id], result.Tasks.Select(task => task.TaskId));
    }

    [Fact]
    public void Graph_rejects_cycles_duplicates_and_missing_dependencies()
    {
        var first = MakeTask();
        var second = MakeTask(first.Id);
        first.DependsOn.Add(second.Id);
        Assert.NotEmpty(WorkflowGraph.Plan([first, second]).Errors);
        Assert.NotEmpty(WorkflowGraph.Plan([first, first]).Errors);
        Assert.NotEmpty(WorkflowGraph.Plan([second]).Errors);
        Assert.NotEmpty(WorkflowGraph.Plan([], Guid.NewGuid()).Errors);
    }

    [Theory]
    [InlineData(false, WorkflowTaskStatus.TimedOut)]
    [InlineData(true, WorkflowTaskStatus.Cancelled)]
    public async Task Timeout_and_cancellation_have_distinct_statuses(bool cancel, WorkflowTaskStatus status)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeExecutor(async (_, _, token) =>
        {
            if (cancel)
                await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new WorkflowProcessResult(0);
        });
        var task = MakeTask();
        task.TimeoutSeconds = 1;
        var result = await new ProjectTaskRunner(fake).RunAsync(new Project { SourceRoot = _root, Tasks = [task] }, [], cancellationToken: cancellation.Token);
        Assert.Equal(status, Assert.Single(result.Tasks).Status);
    }

    [Fact]
    public async Task Concurrent_runs_for_same_project_are_rejected_and_gate_released()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeExecutor(async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new WorkflowProcessResult(0);
        });
        var project = new Project { SourceRoot = _root, Tasks = [MakeTask()] };
        var runner = new ProjectTaskRunner(fake);
        var first = runner.RunAsync(project, []);
        await entered.Task;
        var overlapping = await new ProjectTaskRunner(fake).RunAsync(project, []);
        Assert.NotEmpty(overlapping.Errors);
        release.SetResult();
        Assert.True((await first).Success);
        Assert.True((await runner.RunAsync(project, [])).Success);
    }

    [Fact]
    public async Task File_secrets_are_redacted_and_environment_is_passed_without_logging_args()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "TOKEN=hidden-secret");
        var fake = new FakeExecutor((request, output, _) =>
        {
            Assert.Equal("hidden-secret", request.Environment["TOKEN"]);
            output(false, "value hidden-secret");
            return Task.FromResult(new WorkflowProcessResult(0));
        });
        var result = await new ProjectTaskRunner(fake).RunAsync(new Project { SourceRoot = _root, EnvironmentFiles = [".env"], Tasks = [MakeTask()] }, []);
        Assert.Equal("value ***", Assert.Single(Assert.Single(result.Tasks).Logs).Text);
    }

    [Fact]
    public void Logs_are_bounded_and_mask_multiline_and_short_secrets()
    {
        var logs = new WorkflowLogBuffer(["long-secret", "x", "multi\nline"], 2, 20);
        logs.Append(false, "old");
        logs.Append(false, "x long-secret multi");
        logs.Append(true, "line " + new string('a', 100));
        Assert.Equal(2, logs.Snapshot().Count);
        Assert.Equal("*** *** ***", logs.Snapshot()[0].Text);
        Assert.All(logs.Snapshot(), line => Assert.True(line.Text.Length <= 20));
    }

    [Fact]
    public async Task Real_cmd_drains_both_pipes_and_preserves_exit_code()
    {
        var task = MakeTask();
        task.FileName = "cmd.exe";
        task.Arguments = ["/d", "/c", "(for /L %i in (1,1,1000) do @echo output & @echo error 1>&2) & exit /b 7"];
        var result = await new ProjectTaskRunner().RunAsync(new Project { SourceRoot = _root, Tasks = [task] }, []);
        var completed = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowTaskStatus.Failed, completed.Status);
        Assert.Equal(7, completed.ExitCode);
        Assert.Contains(completed.Logs, line => line.IsError);
        Assert.True(completed.Logs.Count <= 500);
    }

    private static ProjectTask MakeTask(params Guid[] dependencies) => new() { FileName = "fake.exe", DependsOn = [.. dependencies] };

    [Fact]
    public async Task Missing_tool_and_executor_errors_have_safe_messages()
    {
        var task = MakeTask();
        task.FileName = "fes-missing-" + Guid.NewGuid() + ".exe";
        var project = new Project { SourceRoot = _root, Tasks = [task] };
        var result = await new ProjectTaskRunner().RunAsync(project, []);
        Assert.Equal(WorkflowTaskStatus.StartFailed, Assert.Single(result.Tasks).Status);
        var fake = new FakeExecutor((_, _, _) => throw new IOException("sensitive-content"));
        var failed = await new ProjectTaskRunner(fake).RunAsync(project, []);
        Assert.DoesNotContain("sensitive-content", System.Text.Json.JsonSerializer.Serialize(failed));
    }

    [Fact]
    public async Task Missing_environment_and_outside_working_directory_prevent_all_execution()
    {
        var fake = new FakeExecutor((_, _, _) => Task.FromResult(new WorkflowProcessResult(0)));
        var task = MakeTask();
        var project = new Project { SourceRoot = _root, Tasks = [task], EnvironmentFiles = ["absent.env"] };
        Assert.False((await new ProjectTaskRunner(fake).RunAsync(project, [])).Success);
        project.EnvironmentFiles.Clear();
        task.WorkingDirectory = "..";
        Assert.False((await new ProjectTaskRunner(fake).RunAsync(project, [])).Success);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Real_cmd_timeout_cleans_up_and_releases_project_gate()
    {
        var task = MakeTask();
        task.FileName = "cmd.exe";
        task.Arguments = ["/d", "/c", "ping -n 30 127.0.0.1 >nul"];
        task.TimeoutSeconds = 1;
        var project = new Project { SourceRoot = _root, Tasks = [task] };
        var runner = new ProjectTaskRunner();
        var result = await runner.RunAsync(project, []);
        Assert.Equal(WorkflowTaskStatus.TimedOut, Assert.Single(result.Tasks).Status);
        task.Arguments = ["/d", "/c", "exit /b 0"];
        Assert.True((await runner.RunAsync(project, [])).Success);
    }

    [Fact]
    public async Task Batch_launchers_reject_injection_but_allow_structured_space_arguments()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "test.cmd"), "@echo %1\r\n");
        var task = MakeTask();
        task.FileName = ".\\test.cmd";
        task.Arguments = ["hello world"];
        var project = new Project { SourceRoot = _root, Tasks = [task] };
        var result = await new ProjectTaskRunner().RunAsync(project, []);
        Assert.True(result.Success);
        Assert.Contains(Assert.Single(result.Tasks).Logs, entry => entry.Text.Contains("hello world"));
        task.Arguments = ["%PATH% & echo forbidden"];
        var rejected = await new ProjectTaskRunner().RunAsync(project, []);
        Assert.Equal(WorkflowTaskStatus.StartFailed, Assert.Single(rejected.Tasks).Status);
        Assert.Empty(Assert.Single(rejected.Tasks).Logs);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    [InlineData("package-lock.json", "npm")]
    public void Node_discovery_uses_lockfile_and_json_scripts_without_executing(string lockfile, string manager)
    {
        File.WriteAllText(Path.Combine(_root, lockfile), "");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{\"scripts\":{\"build\":\"arbitrary command\",\"test\":\"other command\",\"dev\":\"server\"}}");
        var discovery = WorkflowDiscovery.Discover(_root);
        Assert.Empty(discovery.Warnings);
        Assert.All(discovery.Tasks, task => Assert.Equal(manager, task.FileName));
        Assert.Contains(discovery.Tasks, task => task.Kind == ProjectTaskKind.Test && task.Arguments.SequenceEqual(new[] { "run", "test" }));
        Assert.Empty(WorkflowGraph.Plan(discovery.Tasks).Errors);
    }

    [Theory]
    [InlineData("Cargo.toml", "cargo")]
    [InlineData("pom.xml", "mvn")]
    [InlineData("build.gradle.kts", "gradle")]
    [InlineData("CMakeLists.txt", "cmake")]
    [InlineData("demo.csproj", "dotnet")]
    public void Native_manifests_produce_build_and_completion_test_dependencies(string manifest, string executable)
    {
        File.WriteAllText(Path.Combine(_root, manifest), "");
        var discovery = WorkflowDiscovery.Discover(_root);
        Assert.Contains(discovery.Tasks, task => task.Kind == ProjectTaskKind.Build && task.FileName == executable);
        Assert.Contains(discovery.Tasks, task => task.Kind == ProjectTaskKind.Test && task.DependsOn.Count > 0);
        Assert.Empty(WorkflowGraph.Plan(discovery.Tasks).Errors);
    }

    [Fact]
    public void Python_discovery_uses_explicit_tool_and_venv_interpreter()
    {
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]\nname = 'demo'");
        File.WriteAllText(Path.Combine(_root, "uv.lock"), "");
        var pip = WorkflowDiscovery.Discover(_root, new WorkflowDiscoveryOptions { PythonTool = PythonWorkflowTool.Pip });
        Assert.Contains(pip.Tasks, task => task.Arguments.SequenceEqual(new[] { "-m", "pytest" }) && task.FileName.EndsWith("python.exe"));
        Assert.DoesNotContain(pip.Tasks, task => task.FileName == "uv");
        var uv = WorkflowDiscovery.Discover(_root);
        Assert.Contains(uv.Tasks, task => task.FileName == "uv" && task.Arguments.SequenceEqual(new[] { "sync", "--locked" }));
    }

    [Fact]
    public void Requirements_only_uv_projects_use_explicit_interpreter_for_install_test_and_run()
    {
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "pytest");
        File.WriteAllText(Path.Combine(_root, "main.py"), "print('hello')");
        var result = WorkflowDiscovery.Discover(_root, new WorkflowDiscoveryOptions { PythonTool = PythonWorkflowTool.Uv });
        var install = Assert.Single(result.Tasks, task => task.Arguments.Contains("install"));
        Assert.Equal("uv", install.FileName);
        Assert.Contains("--python", install.Arguments);
        Assert.Contains(result.Tasks, task => task.Kind == ProjectTaskKind.Test && task.FileName.EndsWith("python.exe"));
        Assert.Contains(result.Tasks, task => task.Kind == ProjectTaskKind.Run && task.Arguments.Contains("main.py"));
        Assert.DoesNotContain(result.Tasks, task => task.Arguments.Contains("sync"));
        Assert.Empty(WorkflowGraph.Plan(result.Tasks).Errors);
    }

    [Fact]
    public void Java_discovery_prefers_wrappers_and_bad_json_is_a_safe_warning()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "");
        File.WriteAllText(Path.Combine(_root, "mvnw.cmd"), "");
        File.WriteAllText(Path.Combine(_root, "build.gradle"), "");
        File.WriteAllText(Path.Combine(_root, "gradlew.bat"), "");
        File.WriteAllText(Path.Combine(_root, "package.json"), "secret-invalid-json");
        var result = WorkflowDiscovery.Discover(_root);
        Assert.Contains(result.Tasks, task => task.FileName == ".\\mvnw.cmd");
        Assert.Contains(result.Tasks, task => task.FileName == ".\\gradlew.bat");
        Assert.NotEmpty(result.Warnings);
        Assert.DoesNotContain("secret-invalid-json", string.Join(" ", result.Warnings));
    }

    private sealed class FakeExecutor(Func<WorkflowProcessRequest, Action<bool, string>, CancellationToken, Task<WorkflowProcessResult>> execute) : IWorkflowProcessExecutor
    {
        public int Calls { get; private set; }
        Task<WorkflowProcessResult> IWorkflowProcessExecutor.ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken)
        {
            Calls++;
            return execute(request, output, cancellationToken);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
