using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class EnvFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fes-env-" + Guid.NewGuid());

    public EnvFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Files_precede_profiles_and_service_without_expansion()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=file-secret\nVALUE=${TOKEN}\nEMPTY=\n");
        File.WriteAllText(Path.Combine(_root, "local.env"), "TOKEN=later-secret\n");
        var profile = new EnvProfile { Variables = [new() { Name = "TOKEN", Value = "profile-secret", IsSecret = true }] };
        var project = new Project { SourceRoot = _root, EnvironmentFiles = [".env", "local.env"], EnvProfileIds = [profile.Id] };
        var service = new ServiceDefinition { ExtraVariables = [new() { Name = "TOKEN", Value = "service-secret", IsSecret = true }] };
        var result = EnvResolver.LoadAndResolve(project, service, [profile]);
        Assert.Empty(result.Errors);
        Assert.Equal("service-secret", result.Variables["TOKEN"]);
        Assert.Equal("${TOKEN}", result.Variables["VALUE"]);
        Assert.Equal("", result.Variables["EMPTY"]);
        Assert.Contains("file-secret", result.SecretValues);
        Assert.Contains("later-secret", result.SecretValues);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env-example")]
    [InlineData(".env.example.")]
    [InlineData("../outside.env")]
    [InlineData("absent.env")]
    public void Unsafe_or_missing_runtime_sources_fail_safely(string source)
    {
        File.WriteAllText(Path.Combine(_root, ".env.example"), "TOKEN=do-not-expose");
        var result = EnvFileSources.Load(new Project { SourceRoot = _root, EnvironmentFiles = [source] });
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain("do-not-expose", string.Join(" ", result.Errors));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.example")]
    [InlineData(".env-example")]
    public void Preview_exposes_origins_and_names_not_values(string source)
    {
        File.WriteAllText(Path.Combine(_root, source), "API_TOKEN=do-not-expose\nPLAIN=x");
        var preview = EnvFileSources.Preview(_root, source);
        Assert.Equal(source, preview.Variables[0].Source);
        Assert.True(preview.Variables[0].IsSecret);
        Assert.Equal(source != ".env", preview.Variables[0].IsExample);
        Assert.DoesNotContain("do-not-expose", System.Text.Json.JsonSerializer.Serialize(preview));
    }

    [Fact]
    public void Invalid_lines_block_start_without_echoing_content()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "secret-invalid-line\n");
        var result = EnvFileSources.Load(new Project { SourceRoot = _root, EnvironmentFiles = [".env"] });
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain("secret-invalid-line", string.Join(" ", result.Errors));
    }

    [Fact]
    public async Task Supervisor_loads_and_masks_short_file_secrets()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "TOKEN=qz");
        var service = new ServiceDefinition { RunnerId = "env-test", WorkingDirectory = _root };
        var project = new Project { SourceRoot = _root, EnvironmentFiles = [".env"], Services = [service] };
        using var supervisor = new ProcessSupervisor(new RunnerRegistry([new EnvRunner()]), []);
        Assert.True((await supervisor.StartServiceAsync(project, service, [])).Success);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!supervisor.GetLogSince(service.Id, 0).Any(entry => entry.Kind == LogKind.Output))
            await Task.Delay(10, timeout.Token);
        Assert.DoesNotContain(supervisor.GetLogSince(service.Id, 0), entry => entry.Text.Contains("qz"));
        await supervisor.StopProjectAsync(project);
    }

    [Fact]
    public async Task Supervisor_rejects_missing_sources_before_starting_processes()
    {
        var service = new ServiceDefinition { RunnerId = "env-test", WorkingDirectory = _root };
        var project = new Project { SourceRoot = _root, EnvironmentFiles = ["absent.env"], Services = [service] };
        using var supervisor = new ProcessSupervisor(new RunnerRegistry([new EnvRunner()]), []);
        Assert.False((await supervisor.StartProjectAsync(project, [])).Success);
        Assert.Empty(supervisor.GetLogSince(service.Id, 0));
    }

    private sealed class EnvRunner : IRunner
    {
        public string Id => "env-test";
        public string DisplayName => "Environment test";
        public string Icon => "Code24";
        public IReadOnlyList<SettingField> GetSettingsSchema() => [];
        public ProcessCommand BuildStart(ServiceDefinition service) => new("cmd.exe", ["/d", "/c", "echo %TOKEN%"]);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
