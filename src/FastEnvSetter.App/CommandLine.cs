using System.Text.Json;
using FastEnvSetter.Core;
using FastEnvSetter.Ipc;

namespace FastEnvSetter.App;

/// <summary>Beantwortet Befehle von Kommandozeile, MCP-Server und VS Code. Gibt nie Variablenwerte aus.</summary>
public sealed class CliHandler(IAppDataStore store, ProjectActions actions, IProcessSupervisor supervisor, IConfigurationService configuration, IDevelopmentService development)
{
    private const string Usage = "Verwendung: FastEnvSetter [list|status|profiles|start|stop|restart|logs] [\"Projekt\"|\"Projekt/Dienst\"] [--json] [--tail N]";
    private const int DefaultTail = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Commands = ["list", "status", "profiles", "start", "stop", "restart", "logs", "api", "dev", "external-start", "external-stop", "external-restart"];

    public static bool IsCommand(string[] arguments) =>
        arguments.Length > 0 && Commands.Contains(arguments[0].ToLowerInvariant());

    /// <summary>Befehle, die laufende Dienste voraussetzen und daher einen Launcher im Hintergrund brauchen.</summary>
    public static bool NeedsRunningInstance(string[] arguments) =>
        arguments.Length > 0 && arguments[0].ToLowerInvariant() is "start" or "restart" or "dev" or "external-start" or "external-restart";

    public async Task<CliResponse> ExecuteAsync(string[] arguments)
    {
        if (arguments.FirstOrDefault()?.ToLowerInvariant() is "external-start" or "external-stop" or "external-restart")
        {
            if (!store.Data.Settings.AllowExternalTaskExecution)
                return new CliResponse(4, "permission_required: Externe Ausfuehrung muss lokal in den Einstellungen freigegeben werden.");
            arguments = [arguments[0]["external-".Length..].ToLowerInvariant(), .. arguments.Skip(1)];
        }

        if (arguments.FirstOrDefault() == "dev")
            return new CliResponse(0, await development.ExecuteJsonAsync(arguments.Length == 2 ? arguments[1] : null));

        if (arguments.FirstOrDefault() == "api")
        {
            var output = configuration.ExecuteJson(arguments.Length == 2 ? arguments[1] : null);
            return new CliResponse(0, output);
        }

        var values = arguments.ToList();
        var json = values.Remove("--json");
        var tail = TakeTail(values);
        var command = values.Count > 0 ? values[0].ToLowerInvariant() : "";
        var target = string.Join(' ', values.Skip(1));

        switch (command)
        {
            case "list":
            case "status" when target.Length == 0:
                return List(store.Data.Projects, json);
            case "profiles":
                return Profiles(json);
            case "status":
                return WithProject(target, p => List([p], json));
            case "start":
                return await WithProjectAsync(target, StartAsync);
            case "stop":
                return await WithProjectAsync(target, async p =>
                {
                    await actions.StopAsync(p);
                    return new CliResponse(0, $"{p.Name}: gestoppt");
                });
            case "restart":
                return await WithServiceAsync(target, RestartAsync);
            case "logs":
                return await WithServiceAsync(target, (_, s) => Task.FromResult(Logs(s, tail, json)));
            default:
                return new CliResponse(2, Usage);
        }
    }

    private async Task<CliResponse> StartAsync(Project project)
    {
        var result = await actions.StartHeadlessAsync(project);
        return result.Success
            ? new CliResponse(0, $"{project.Name}: gestartet")
            : new CliResponse(1, string.Join(Environment.NewLine, result.Errors));
    }

    private async Task<CliResponse> RestartAsync(Project project, ServiceDefinition service)
    {
        await supervisor.StopServiceAsync(service.Id);
        var result = await supervisor.StartServiceAsync(project, service, store.Data.Profiles);
        return result.Success
            ? new CliResponse(0, $"{service.Name}: neu gestartet")
            : new CliResponse(1, string.Join(Environment.NewLine, result.Errors));
    }

    private CliResponse List(IEnumerable<Project> projects, bool json)
    {
        var list = projects.ToList();
        if (json)
        {
            var result = list.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sourceRoot = p.SourceRoot,
                editorTargets = p.EditorTargets.Select(editor => new { id = editor.Id, name = editor.Name, path = editor.Path, profile = editor.Profile }),
                favorite = p.IsFavorite,
                tags = p.Tags,
                profiles = store.Data.Profiles.Where(e => p.EnvProfileIds.Contains(e.Id)).Select(e => e.Name),
                services = p.Services.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    runner = s.RunnerId,
                    state = supervisor.GetState(s.Id).ToString(),
                    message = supervisor.GetMessage(s.Id),
                    openUrl = s.OpenUrl,
                }),
            });
            return new CliResponse(0, JsonSerializer.Serialize(result, Json));
        }

        return new CliResponse(0, string.Join(Environment.NewLine, list.Select(p =>
        {
            var running = p.Services.Count(s => supervisor.GetState(s.Id) == ServiceState.Running);
            return $"{p.Name}\t{running}/{p.Services.Count} laufen";
        })));
    }

    private CliResponse Profiles(bool json)
    {
        if (json)
        {
            var result = store.Data.Profiles.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                parent = store.Data.Profiles.FirstOrDefault(e => e.Id == p.ParentId)?.Name,
                variables = p.Variables.Select(v => new
                {
                    name = v.Name,
                    secret = v.IsSecret || v.SecretRef is not null,
                    source = v.SecretRef?.ProviderId ?? "local",
                }),
            });
            return new CliResponse(0, JsonSerializer.Serialize(result, Json));
        }

        return new CliResponse(0, string.Join(Environment.NewLine, store.Data.Profiles.Select(p => $"{p.Name}\t{p.Variables.Count} Variablen")));
    }

    private CliResponse Logs(ServiceDefinition service, int tail, bool json)
    {
        var lines = supervisor.GetLogSince(service.Id, 0).TakeLast(tail).ToList();
        if (json)
        {
            var result = lines.Select(l => new { time = l.Timestamp, kind = l.Kind.ToString(), text = l.Text });
            return new CliResponse(0, JsonSerializer.Serialize(result, Json));
        }

        return new CliResponse(0, string.Join(Environment.NewLine, lines.Select(l => l.Text)));
    }

    private static int TakeTail(List<string> values)
    {
        var index = values.IndexOf("--tail");
        if (index < 0 || index + 1 >= values.Count)
            return DefaultTail;

        var tail = int.TryParse(values[index + 1], out var parsed) && parsed > 0 ? parsed : DefaultTail;
        values.RemoveRange(index, 2);
        return tail;
    }

    private CliResponse WithProject(string name, Func<Project, CliResponse> action)
    {
        var (project, error) = FindProject(name);
        return project is null ? error! : action(project);
    }

    private async Task<CliResponse> WithProjectAsync(string name, Func<Project, Task<CliResponse>> action)
    {
        var (project, error) = FindProject(name);
        return project is null ? error! : await action(project);
    }

    private (Project? Project, CliResponse? Error) FindProject(string name)
    {
        if (name.Length == 0)
            return (null, new CliResponse(2, Usage));

        var matches = Guid.TryParse(name, out var id)
            ? store.Data.Projects.Where(p => p.Id == id).ToList()
            : store.Data.Projects.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = store.Data.Projects.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();

        return matches.Count == 1
            ? (matches[0], null)
            : (null, new CliResponse(3, matches.Count == 0 ? $"Projekt „{name}“ nicht gefunden." : $"„{name}“ ist nicht eindeutig."));
    }

    private async Task<CliResponse> WithServiceAsync(string target, Func<Project, ServiceDefinition, Task<CliResponse>> action)
    {
        if (Guid.TryParse(target, out var id))
        {
            foreach (var project in store.Data.Projects)
            {
                if (project.Services.FirstOrDefault(s => s.Id == id) is { } match)
                    return await action(project, match);
            }

            return new CliResponse(3, $"Dienst „{target}“ nicht gefunden.");
        }

        var slash = target.LastIndexOf('/');
        var (found, error) = FindProject(slash > 0 ? target[..slash] : target);
        if (found is null)
            return error!;

        var serviceName = slash > 0 ? target[(slash + 1)..] : null;
        var services = serviceName is null ? found.Services : found.Services.Where(s => s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase)).ToList();
        return services.Count == 1
            ? await action(found, services[0])
            : new CliResponse(3, "Bitte den Dienst als „Projekt/Dienst“ angeben.");
    }
}
