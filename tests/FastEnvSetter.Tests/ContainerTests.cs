using FastEnvSetter.Core;
using FastEnvSetter.Runners;

namespace FastEnvSetter.Tests;

public sealed class ContainerTests
{
    [Theory]
    [InlineData(WorkflowContainerEngine.Docker, ComposeProvider.EnginePlugin, "docker", true)]
    [InlineData(WorkflowContainerEngine.Docker, ComposeProvider.Standalone, "docker-compose", false)]
    [InlineData(WorkflowContainerEngine.Podman, ComposeProvider.EnginePlugin, "podman", true)]
    [InlineData(WorkflowContainerEngine.Podman, ComposeProvider.Standalone, "podman-compose", false)]
    public void Engine_and_provider_have_explicit_nondestructive_plans(WorkflowContainerEngine engine, ComposeProvider provider, string executable, bool prefix)
    {
        var options = new ComposeOptions(engine, provider) { File = "compose local.yml", ProjectName = "project", Services = ["api"], BuildOnStart = true };
        foreach (var operation in Enum.GetValues<ComposeOperation>())
        {
            var command = ComposeCommandPlans.Create(options, operation);
            Assert.Equal(executable, command.FileName);
            Assert.Equal(prefix, command.Arguments[0] == "compose");
            Assert.Contains("compose local.yml", command.Arguments);
            Assert.DoesNotContain("down", command.Arguments);
            Assert.DoesNotContain("--volumes", command.Arguments);
            Assert.Null(command.RawArguments);
        }
        Assert.Contains("--build", ComposeCommandPlans.Create(options, ComposeOperation.Up).Arguments);
        Assert.Contains("stop", ComposeCommandPlans.Create(options, ComposeOperation.Stop).Arguments);
    }

    [Fact]
    public void Existing_docker_ID_and_start_stop_commands_remain_compatible()
    {
        var runner = new DockerComposeRunner();
        var service = new ServiceDefinition { Settings = new() { ["file"] = "compose.yml", ["services"] = "api db", ["build"] = "true" } };
        Assert.Equal("docker-compose", runner.Id);
        Assert.Equal(["compose", "-f", "compose.yml", "up", "--build", "api", "db"], runner.BuildStart(service).Arguments);
        Assert.Equal(["compose", "-f", "compose.yml", "stop", "api", "db"], runner.BuildStop(service)!.Arguments);
        var podman = new PodmanComposeRunner();
        service.Settings["provider"] = "standalone";
        Assert.Equal("podman-compose", podman.BuildStart(service).FileName);
        Assert.Empty(podman.Validate(service));
    }

    [Fact]
    public async Task Capability_probes_report_missing_provider_without_running_up_or_changing_system()
    {
        var requests = new List<WorkflowProcessRequest>();
        var executor = new ProbeExecutor(requests, [0, 0, 1]);
        var result = await ComposeCommandPlans.ProbeAsync(new ComposeOptions(WorkflowContainerEngine.Podman), ".", executor);
        Assert.True(result.EngineInstalled);
        Assert.True(result.EngineReachable);
        Assert.False(result.ProviderAvailable);
        Assert.Contains(result.Messages, message => message.Contains("provider", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.Contains("parity"));
        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(requests, request => request.Arguments.Contains("up"));
    }

    [Fact]
    public void Container_discovery_requires_explicit_engine_selection()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fes-container-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "compose.yaml"), "services: {}");
            Assert.Empty(WorkflowDiscovery.Discover(folder).Tasks);
            var selected = WorkflowDiscovery.Discover(folder, new WorkflowDiscoveryOptions { ContainerEngine = WorkflowContainerEngine.Podman });
            Assert.Equal(2, selected.Tasks.Count);
            Assert.All(selected.Tasks, task => Assert.Equal("podman", task.FileName));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Service_option_injection_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ComposeCommandPlans.Create(new ComposeOptions(WorkflowContainerEngine.Docker) { Services = ["--remove-orphans"] }, ComposeOperation.Up));
    }

    [Fact]
    public void Language_runners_offer_schemas_and_structured_commands()
    {
        var rust = new RustRunner();
        var service = new ServiceDefinition { Settings = new() { ["release"] = "true" } };
        Assert.Contains("--release", rust.BuildStart(service).Arguments);
        Assert.Equal("cargo", rust.BuildStart(service).FileName);
        Assert.NotEmpty(new JavaRunner().GetSettingsSchema());
        Assert.NotEmpty(new JavaRunner().Validate(new ServiceDefinition { Settings = new() { ["args"] = "%PATH% & echo forbidden" } }));
        Assert.NotEmpty(new CppRunner().Validate(new ServiceDefinition()));
    }

    private sealed class ProbeExecutor(List<WorkflowProcessRequest> requests, IReadOnlyList<int> results) : IWorkflowProcessExecutor
    {
        Task<WorkflowProcessResult> IWorkflowProcessExecutor.ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            output(false, "probe output is not exposed");
            return Task.FromResult(new WorkflowProcessResult(results[requests.Count - 1]));
        }
    }
}
