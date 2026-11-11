using System.ComponentModel;
using FastEnvSetter.Core;

namespace FastEnvSetter.Runners;

public enum ComposeProvider { EnginePlugin, Standalone }
public enum ComposeOperation { Build, Up, Stop, Status, Logs }
public sealed record ComposeOptions(WorkflowContainerEngine Engine, ComposeProvider Provider = ComposeProvider.EnginePlugin)
{
    public string File { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public IReadOnlyList<string> Services { get; init; } = [];
    public bool BuildOnStart { get; init; }
}
public sealed record ComposeCapabilities(bool EngineInstalled, bool EngineReachable, bool ProviderAvailable, IReadOnlyList<string> Messages);

public static class ComposeCommandPlans
{
    public static ProcessCommand Create(ComposeOptions options, ComposeOperation operation)
    {
        if (!Enum.IsDefined(options.Engine) || !Enum.IsDefined(options.Provider) || !Enum.IsDefined(operation))
            throw new ArgumentException("Unknown container engine, provider or operation.");
        ValidateSelection(options);
        var executable = options.Engine == WorkflowContainerEngine.Podman ? "podman" : "docker";
        List<string> args = [];
        if (options.Provider == ComposeProvider.EnginePlugin)
            args.Add("compose");
        else
            executable += "-compose";
        if (options.File.Length > 0)
            args.AddRange(["-f", options.File]);
        if (options.ProjectName.Length > 0)
            args.AddRange(["-p", options.ProjectName]);
        args.AddRange(OperationArguments(operation, options.BuildOnStart));
        args.AddRange(options.Services);
        return new ProcessCommand(executable, args);
    }

    private static void ValidateSelection(ComposeOptions options)
    {
        if (options.Services.Any(service => string.IsNullOrWhiteSpace(service) || service.StartsWith('-') || service.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Compose service names must be nonempty positional names, not options.");
        if (options.ProjectName.Length > 0 && (options.ProjectName[0] == '-' || options.ProjectName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new ArgumentException("Compose project names must contain only letters, digits, underscores or hyphens.");
    }

    private static string[] OperationArguments(ComposeOperation operation, bool buildOnStart) => operation switch
    {
        ComposeOperation.Build => ["build"],
        ComposeOperation.Up => buildOnStart ? ["up", "--build"] : ["up"],
        ComposeOperation.Stop => ["stop"],
        ComposeOperation.Status => ["ps", "--all"],
        ComposeOperation.Logs => ["logs", "--no-color", "--tail", "200"],
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static async Task<ComposeCapabilities> ProbeAsync(ComposeOptions options, string workingDirectory,
        IWorkflowProcessExecutor executor, CancellationToken cancellationToken = default)
    {
        var plan = Create(options, ComposeOperation.Status);
        var engine = options.Engine == WorkflowContainerEngine.Podman ? "podman" : "docker";
        var installed = await ProbeCommandAsync(new ProcessCommand(engine, ["--version"]), workingDirectory, executor, cancellationToken);
        var reachable = installed && await ProbeCommandAsync(new ProcessCommand(engine, ["info"]), workingDirectory, executor, cancellationToken);
        var provider = await ProbeCommandAsync(new ProcessCommand(plan.FileName,
            options.Provider == ComposeProvider.EnginePlugin ? ["compose", "version"] : ["--version"]), workingDirectory, executor, cancellationToken);
        var messages = new List<string>();
        if (!installed)
            messages.Add($"{engine} could not be probed. Check that the selected engine is installed and available on PATH.");
        else if (!reachable)
            messages.Add($"{engine} info failed. Check the engine connection or local machine status; no system changes were attempted.");
        if (!provider)
            messages.Add($"The selected Compose provider ({plan.FileName}) is unavailable or timed out. Configure an installed provider explicitly.");
        if (options.Engine == WorkflowContainerEngine.Podman && options.Provider == ComposeProvider.EnginePlugin)
            messages.Add("podman compose delegates to an external provider. A successful version probe does not establish feature parity with Docker Compose.");
        return new ComposeCapabilities(installed, reachable, provider, messages);
    }

    private static async Task<bool> ProbeCommandAsync(ProcessCommand command, string directory, IWorkflowProcessExecutor executor, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await executor.ExecuteAsync(new WorkflowProcessRequest(command.FileName, command.Arguments, directory,
                new Dictionary<string, string>()), (_, _) => { }, timeout.Token);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

public abstract class WorkflowComposeRunner : RunnerBase
{
    protected abstract WorkflowContainerEngine Engine { get; }
    public override string Icon => "Box24";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("provider", "Compose provider", SettingKind.Choice) { Choices = ["engine-plugin", "standalone"], Default = "engine-plugin" },
        new("file", "Compose file", SettingKind.Choice) { AllowCustomValue = true },
        new("projectName", "Compose project name"),
        new("services", "Services"),
        new("build", "Build before start", SettingKind.Toggle) { Default = "false" },
    ];

    public override DetectionResult Detect(string folder)
    {
        string[] names = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];
        var files = names.Where(name => File.Exists(Path.Combine(folder, name))).ToArray();
        return new DetectionResult
        {
            Values = files.Length > 0 ? new Dictionary<string, string> { ["file"] = files[0] } : new Dictionary<string, string>(),
            Choices = new Dictionary<string, IReadOnlyList<string>> { ["file"] = files },
        };
    }

    public override IReadOnlyList<string> Validate(ServiceDefinition service)
    {
        if (HasCustomCommand(service))
            return base.Validate(service);
        try
        {
            if (Value(service, "provider") is not ("engine-plugin" or "standalone"))
                return ["Select an explicit Compose provider."];
            ComposeCommandPlans.Create(Options(service), ComposeOperation.Up);
            return base.Validate(service);
        }
        catch (ArgumentException)
        {
            return ["Invalid Compose project or service selection."];
        }
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service) => ComposeCommandPlans.Create(Options(service), ComposeOperation.Up);
    public override ProcessCommand? BuildStop(ServiceDefinition service) => HasCustomCommand(service) ? null : ComposeCommandPlans.Create(Options(service), ComposeOperation.Stop);

    private ComposeOptions Options(ServiceDefinition service) => new(Engine,
        Value(service, "provider") == "standalone" ? ComposeProvider.Standalone : ComposeProvider.EnginePlugin)
    {
        File = Value(service, "file"),
        ProjectName = Value(service, "projectName"),
        Services = CommandLineSplitter.Split(Value(service, "services")),
        BuildOnStart = IsOn(service, "build"),
    };
}

public sealed class PodmanComposeRunner : WorkflowComposeRunner
{
    protected override WorkflowContainerEngine Engine => WorkflowContainerEngine.Podman;
    public override string Id => "podman-compose";
    public override string DisplayName => "Podman Compose";
}
