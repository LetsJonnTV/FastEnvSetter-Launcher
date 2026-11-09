using System.Text.Json;
using FastEnvSetter.Core;
using Microsoft.Extensions.DependencyInjection;

namespace FastEnvSetter.Runners;

public static class RunnerServiceCollectionExtensions
{
    /// <summary>Neue Runner werden hier mit einer Zeile ergänzt.</summary>
    public static IServiceCollection AddBuiltInRunners(this IServiceCollection services)
    {
        services.AddSingleton<IRunner, PowerShellRunner>();
        services.AddSingleton<IRunner, PythonRunner>();
        services.AddSingleton<IRunner, NodeRunner>();
        services.AddSingleton<IRunner, DotNetRunner>();
        services.AddSingleton<IRunner, DockerComposeRunner>();
        return services;
    }
}

public sealed class PowerShellRunner : RunnerBase
{
    public const string RunnerId = "powershell";
    private const string Pwsh7 = "PowerShell 7";
    private const string Pwsh5 = "Windows PowerShell 5.1";

    public override string Id => RunnerId;
    public override string DisplayName => "PowerShell";
    public override string Icon => "WindowConsole20";
    public override bool RequiresWorkingDirectory => false;
    protected override string CustomCommandLabel => "Befehl (optional)";
    protected override string CustomCommandHint => "Ohne Befehl öffnet sich eine interaktive Shell in einem eigenen Terminal.";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("edition", "Version", SettingKind.Choice) { Choices = [Pwsh7, Pwsh5], Default = Pwsh7 },
    ];

    public override bool RequiresTerminal(ServiceDefinition service) => !HasCustomCommand(service);

    protected override ProcessCommand BuildDefault(ServiceDefinition service) =>
        new(Executable(service), ["-NoLogo", "-NoExit"]) { SelfHostedTerminal = true };

    protected override ProcessCommand BuildCustom(ServiceDefinition service, string command) =>
        service.OutputMode == OutputMode.Terminal
            ? new(Executable(service), ["-NoLogo", "-NoExit", "-Command", command]) { SelfHostedTerminal = true }
            : new(Executable(service), ["-NoLogo", "-NonInteractive", "-Command", command]);

    private string Executable(ServiceDefinition service) =>
        Value(service, "edition") == Pwsh5 ? "powershell.exe" : "pwsh.exe";
}

public sealed class PythonRunner : RunnerBase
{
    private const string Uvicorn = "Uvicorn";
    private const string Flask = "Flask";
    private const string Django = "Django";
    private const string Script = "Skript";
    private const string Module = "Modul";

    public override string Id => "python";
    public override string DisplayName => "Python";
    public override string Icon => "Code24";
    protected override string CustomCommandHint => "Läuft mit aktivierter virtueller Umgebung, z. B. „celery -A app worker“.";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("venv", "Virtuelle Umgebung", SettingKind.FolderPath) { Hint = "Leer lassen, um Python aus dem PATH zu verwenden." },
        new("mode", "Startart", SettingKind.Choice) { Choices = [Uvicorn, Flask, Django, Script, Module], Default = Uvicorn },
        new("target", "Ziel")
        {
            Required = true,
            Hint = "Uvicorn: main:app · Flask: app · Skript: main.py · Modul: paket.modul",
            VisibleWhenKey = "mode",
            VisibleWhenValues = [Uvicorn, Flask, Script, Module],
        },
        new("host", "Host") { Default = "127.0.0.1", VisibleWhenKey = "mode", VisibleWhenValues = [Uvicorn, Flask, Django] },
        new("port", "Port") { Default = "8000", VisibleWhenKey = "mode", VisibleWhenValues = [Uvicorn, Flask, Django] },
        new("reload", "Automatisch neu laden", SettingKind.Toggle) { Default = "true", VisibleWhenKey = "mode", VisibleWhenValues = [Uvicorn, Flask, Django] },
        ArgsField(),
    ];

    public override IReadOnlyList<int> GetExpectedPorts(ServiceDefinition service) =>
        !HasCustomCommand(service) && Value(service, "mode") is Uvicorn or Flask or Django && int.TryParse(Value(service, "port"), out var port)
            ? [port]
            : [];

    public override IReadOnlyList<string> Validate(ServiceDefinition service)
    {
        var venv = Value(service, "venv");
        if (venv.Length > 0 && !File.Exists(VenvPython(service, venv)))
            return [.. base.Validate(service), $"In „{venv}“ wurde keine python.exe gefunden."];
        return base.Validate(service);
    }

    public override DetectionResult Detect(string folder)
    {
        var values = new Dictionary<string, string>();
        foreach (var candidate in new[] { ".venv", "venv", "env" })
        {
            if (File.Exists(Path.Combine(folder, candidate, "Scripts", "python.exe")))
            {
                values["venv"] = candidate;
                break;
            }
        }

        if (File.Exists(Path.Combine(folder, "manage.py")))
        {
            values["mode"] = Django;
            return new DetectionResult { Values = values };
        }

        foreach (var file in new[] { "main.py", "app.py" })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
                continue;

            var content = File.ReadAllText(path);
            var name = Path.GetFileNameWithoutExtension(file);
            if (content.Contains("FastAPI(", StringComparison.Ordinal))
            {
                values["mode"] = Uvicorn;
                values["target"] = $"{name}:app";
            }
            else if (content.Contains("Flask(", StringComparison.Ordinal))
            {
                values["mode"] = Flask;
                values["target"] = name;
            }
            else
            {
                values["mode"] = Script;
                values["target"] = file;
            }

            break;
        }

        return new DetectionResult { Values = values };
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service)
    {
        var host = Value(service, "host");
        var port = Value(service, "port");
        var reload = IsOn(service, "reload");
        var mode = Value(service, "mode");
        var target = Value(service, "target");

        List<string> args = mode switch
        {
            Uvicorn => ["-m", "uvicorn", target, "--host", host, "--port", port, .. reload ? new[] { "--reload" } : []],
            Flask => ["-m", "flask", "--app", target, "run", "--host", host, "--port", port, .. reload ? new[] { "--debug" } : []],
            Django => ["manage.py", "runserver", $"{host}:{port}", .. reload ? Array.Empty<string>() : ["--noreload"]],
            Script => [target],
            _ => ["-m", target],
        };
        args.AddRange(ExtraArgs(service));

        var venv = Value(service, "venv");
        var command = venv.Length > 0
            ? new ProcessCommand(VenvPython(service, venv), args)
            : ProcessCommand.ViaCmd("python", args); // pyenv & Co. liefern oft nur eine python.bat.
        return WithPythonEnvironment(service, command);
    }

    protected override ProcessCommand BuildCustom(ServiceDefinition service, string command) =>
        WithPythonEnvironment(service, ProcessCommand.ViaCmd(command));

    private ProcessCommand WithPythonEnvironment(ServiceDefinition service, ProcessCommand command)
    {
        var environment = new Dictionary<string, string>
        {
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
        };

        var venv = Value(service, "venv");
        if (venv.Length == 0)
            return command with { Environment = environment };

        var root = Path.GetFullPath(Path.Combine(service.WorkingDirectory, venv));
        environment["VIRTUAL_ENV"] = root;
        return command with { Environment = environment, PrependPath = [Path.Combine(root, "Scripts")] };
    }

    private static string VenvPython(ServiceDefinition service, string venv) =>
        Path.GetFullPath(Path.Combine(service.WorkingDirectory, venv, "Scripts", "python.exe"));
}

public sealed class NodeRunner : RunnerBase
{
    public override string Id => "node";
    public override string DisplayName => "Node.js";
    public override string Icon => "Globe24";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("packageManager", "Paketmanager", SettingKind.Choice) { Choices = ["npm", "pnpm", "yarn"], Default = "npm" },
        new("script", "Skript", SettingKind.Choice) { AllowCustomValue = true, Required = true, Hint = "Aus der package.json, z. B. dev oder start." },
        ArgsField(),
    ];

    public override DetectionResult Detect(string folder)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path))
            return DetectionResult.Empty;

        var values = new Dictionary<string, string>();
        if (File.Exists(Path.Combine(folder, "pnpm-lock.yaml")))
            values["packageManager"] = "pnpm";
        else if (File.Exists(Path.Combine(folder, "yarn.lock")))
            values["packageManager"] = "yarn";

        var scripts = ReadScripts(path);
        var preferred = new[] { "dev", "start", "serve" }.FirstOrDefault(scripts.Contains) ?? scripts.FirstOrDefault();
        if (preferred is not null)
            values["script"] = preferred;

        return new DetectionResult
        {
            Values = values,
            Choices = new Dictionary<string, IReadOnlyList<string>> { ["script"] = scripts },
        };
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service)
    {
        List<string> args = ["run", Value(service, "script")];
        var extra = ExtraArgs(service);
        if (extra.Count > 0)
        {
            if (Value(service, "packageManager") == "npm")
                args.Add("--");
            args.AddRange(extra);
        }

        return ProcessCommand.ViaCmd(Value(service, "packageManager"), args);
    }

    private static List<string> ReadScripts(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                ? scripts.EnumerateObject().Select(p => p.Name).ToList()
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }
}

public sealed class DotNetRunner : RunnerBase
{
    public override string Id => "dotnet";
    public override string DisplayName => ".NET";
    public override string Icon => "Cube24";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("project", "Projektdatei", SettingKind.Choice) { AllowCustomValue = true, Hint = "Leer lassen, um das Projekt im Arbeitsordner zu verwenden." },
        new("configuration", "Konfiguration", SettingKind.Choice) { Choices = ["Debug", "Release"], Default = "Debug" },
        new("watch", "Bei Änderungen neu starten", SettingKind.Toggle) { Default = "false" },
        ArgsField(),
    ];

    public override DetectionResult Detect(string folder)
    {
        var projects = FindFiles(folder, "*.csproj");
        return new DetectionResult
        {
            Values = projects.Count > 0 ? new Dictionary<string, string> { ["project"] = projects[0] } : new Dictionary<string, string>(),
            Choices = new Dictionary<string, IReadOnlyList<string>> { ["project"] = projects },
        };
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service)
    {
        List<string> args = IsOn(service, "watch") ? ["watch", "run"] : ["run"];
        var project = Value(service, "project");
        if (project.Length > 0)
            args.AddRange(["--project", project]);
        args.AddRange(["-c", Value(service, "configuration")]);

        var extra = ExtraArgs(service);
        if (extra.Count > 0)
            args.AddRange(["--", .. extra]);
        return new ProcessCommand("dotnet", args);
    }
}

public sealed class DockerComposeRunner : RunnerBase
{
    public override string Id => "docker-compose";
    public override string DisplayName => "Docker Compose";
    public override string Icon => "Box24";

    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("file", "Compose-Datei", SettingKind.Choice) { AllowCustomValue = true, Hint = "Leer lassen, um die Standarddatei im Arbeitsordner zu verwenden." },
        new("services", "Dienste") { Hint = "Mit Leerzeichen getrennt, leer bedeutet alle." },
        new("build", "Vor dem Start bauen", SettingKind.Toggle) { Default = "false" },
    ];

    public override DetectionResult Detect(string folder)
    {
        string[] names = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];
        var files = names.Where(n => File.Exists(Path.Combine(folder, n))).ToList();
        return new DetectionResult
        {
            Values = files.Count > 0 ? new Dictionary<string, string> { ["file"] = files[0] } : new Dictionary<string, string>(),
            Choices = new Dictionary<string, IReadOnlyList<string>> { ["file"] = files },
        };
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service)
    {
        List<string> args = [.. Base(service), "up"];
        if (IsOn(service, "build"))
            args.Add("--build");
        args.AddRange(CommandLineSplitter.Split(Value(service, "services")));
        return new ProcessCommand("docker", args);
    }

    public override ProcessCommand? BuildStop(ServiceDefinition service)
    {
        if (HasCustomCommand(service))
            return null;
        return new ProcessCommand("docker", [.. Base(service), "stop", .. CommandLineSplitter.Split(Value(service, "services"))]);
    }

    private List<string> Base(ServiceDefinition service)
    {
        var file = Value(service, "file");
        return file.Length > 0 ? ["compose", "-f", file] : ["compose"];
    }
}
