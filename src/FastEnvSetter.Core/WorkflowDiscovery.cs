using System.Text.Json;

namespace FastEnvSetter.Core;

public enum PythonWorkflowTool { Pip, Uv }
public enum WorkflowContainerEngine { Docker, Podman }
public sealed record WorkflowDiscoveryOptions
{
    public PythonWorkflowTool? PythonTool { get; init; }
    public WorkflowContainerEngine? ContainerEngine { get; init; }
    public int MaximumDepth { get; init; } = 3;
}
public sealed record WorkflowDiscoveryResult(IReadOnlyList<ProjectTask> Tasks, IReadOnlyList<string> Warnings);

public static class WorkflowDiscovery
{
    private const string Build = "build";
    private const string DefaultVenv = ".venv";
    private const string Install = "install";
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", DefaultVenv, "venv", "env", "node_modules", "bin", "obj", "target", Build, ".gradle", "vendor", "publish",
    };

    public static WorkflowDiscoveryResult Discover(Project project, WorkflowDiscoveryOptions? options = null) => Discover(project.SourceRoot, options);

    public static WorkflowDiscoveryResult Discover(string sourceRoot, WorkflowDiscoveryOptions? options = null)
    {
        options ??= new WorkflowDiscoveryOptions();
        var tasks = new List<ProjectTask>();
        var warnings = new List<string>();
        try
        {
            var root = EnvFileSources.ResolvePath(sourceRoot, ".");
            if (!Directory.Exists(root))
                return new WorkflowDiscoveryResult([], ["SourceRoot does not exist."]);
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.TryDequeue(out var current) && visited++ < 500)
            {
                try
                {
                    var folder = current.Path;
                    var relative = Path.GetRelativePath(root, folder);
                    new FolderDiscovery(folder, relative, options, tasks, warnings).Collect();
                    if (current.Depth >= Math.Clamp(options.MaximumDepth, 0, 5))
                        continue;
                    foreach (var child in Directory.EnumerateDirectories(folder).Where(IsDiscoverableDirectory).Order(StringComparer.OrdinalIgnoreCase))
                        queue.Enqueue((child, current.Depth + 1));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    warnings.Add("A project directory or manifest could not be read; review the suggested tasks.");
                }
            }
            if (queue.Count > 0)
                warnings.Add("Discovery reached its 500-directory limit; discover a narrower SourceRoot for additional tasks.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add("SourceRoot must be an accessible absolute directory without links.");
        }
        return new WorkflowDiscoveryResult(tasks, warnings.Distinct().ToArray());
    }

    private static bool IsDiscoverableDirectory(string directory) =>
        !IgnoredDirectories.Contains(Path.GetFileName(directory)) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;

    private sealed class FolderDiscovery(string folder, string relative, WorkflowDiscoveryOptions options, List<ProjectTask> tasks, List<string> warnings)
    {
        private const string DotNet = "dotnet";
        private const string Python = "python";
        private const string Cargo = "cargo";
        private const string PackageManifest = "package.json";
        private const string PythonManifest = "pyproject.toml";
        private const string UvLock = "uv.lock";
        private const string Requirements = "requirements.txt";

        public void Collect()
        {
            DiscoverDotNet();
            DiscoverNode();
            DiscoverPython();
            DiscoverCargo();
            DiscoverJava();
            DiscoverCMake();
            DiscoverContainers();
        }

        private bool Has(string name) => File.Exists(Path.Combine(folder, name))
            && (File.GetAttributes(Path.Combine(folder, name)) & FileAttributes.ReparsePoint) == 0;

        private ProjectTask Add(string name, ProjectTaskKind kind, string executable, IEnumerable<string> arguments, params Guid[] dependencies)
        {
            var task = new ProjectTask { Name = name, Kind = kind, FileName = executable, Arguments = [.. arguments], WorkingDirectory = relative, DependsOn = [.. dependencies] };
            tasks.Add(task);
            return task;
        }

        private void DiscoverDotNet()
        {
            var files = Directory.EnumerateFiles(folder).Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
            var projects = files.Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj").Order().Select(path => Path.GetFileName(path)).ToArray();
            var solution = files.FirstOrDefault(path => Path.GetExtension(path) is ".sln" or ".slnx");
            var targets = solution is null ? projects : [Path.GetFileName(solution)];
            foreach (var target in targets)
            {
                var restore = Add($".NET restore {target}", ProjectTaskKind.Prepare, DotNet, ["restore", target]);
                var build = Add($".NET build {target}", ProjectTaskKind.Build, DotNet, [Build, target, "--no-restore"], restore.Id);
                Add($".NET test {target}", ProjectTaskKind.Test, DotNet, ["test", target, "--no-build", "--no-restore"], build.Id);
                foreach (var project in solution is null ? new[] { target } : projects)
                    Add($".NET run {project}", ProjectTaskKind.Run, DotNet, ["run", "--project", project, "--no-build", "--no-restore"], build.Id);
            }
        }

        private void DiscoverNode()
        {
            if (!Has(PackageManifest))
                return;
            try
            {
                using var document = JsonDocument.Parse(ReadManifest(Path.Combine(folder, PackageManifest)));
                var locks = new[] { "pnpm-lock.yaml", "yarn.lock", "package-lock.json", "npm-shrinkwrap.json" }.Where(Has).ToArray();
                var manager = locks.FirstOrDefault() switch { "pnpm-lock.yaml" => "pnpm", "yarn.lock" => "yarn", _ => "npm" };
                if (locks.Length > 1)
                    warnings.Add("Multiple Node lockfiles found; pnpm, then yarn, then npm takes precedence. Review the selected package manager.");
                var prepare = Add($"{manager} install", ProjectTaskKind.Prepare, manager, manager == "npm" && locks.Length > 0 ? ["ci"] : [Install]);
                var names = ReadNodeScripts(document.RootElement);
                var buildId = names.Contains(Build) ? Add($"{manager} build", ProjectTaskKind.Build, manager, ["run", Build], prepare.Id).Id : prepare.Id;
                foreach (var name in names.Where(name => name != Build))
                    Add($"{manager} {name}", ScriptKind(name), manager, ["run", name], buildId);
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                warnings.Add("package.json could not be parsed; no Node task commands were inferred from its content.");
            }
        }

        private static string[] ReadNodeScripts(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                return [];
            return scripts.EnumerateObject().Where(script => script.Value.ValueKind == JsonValueKind.String).Select(script => script.Name).ToArray();
        }

        private static ProjectTaskKind ScriptKind(string name)
        {
            if (name is "test" or "lint" or "typecheck" || name.StartsWith("test:", StringComparison.Ordinal))
                return ProjectTaskKind.Test;
            return name is "start" or "dev" or "serve" ? ProjectTaskKind.Run : ProjectTaskKind.Build;
        }

        private void DiscoverPython()
        {
            if (!Has(PythonManifest) && !Has(Requirements) && !Has("setup.py"))
                return;
            var tool = options.PythonTool ?? (Has(UvLock) ? PythonWorkflowTool.Uv : PythonWorkflowTool.Pip);
            if (tool == PythonWorkflowTool.Uv)
            {
                if (!Has(PythonManifest))
                {
                    DiscoverUvPip();
                    return;
                }
                var prepare = Add("uv sync", ProjectTaskKind.Prepare, "uv", Has(UvLock) ? ["sync", "--locked"] : ["sync"]);
                Add("uv pytest", ProjectTaskKind.Test, "uv", ["run", "--no-sync", Python, "-m", "pytest"], prepare.Id);
                Add("uv build", ProjectTaskKind.Build, "uv", [Build], prepare.Id);
                AddPythonRun("uv", ["run", "--no-sync", Python], prepare.Id);
                return;
            }
            DiscoverPip();
        }

        private void DiscoverPip()
        {
            var venv = new[] { DefaultVenv, "venv", "env" }.FirstOrDefault(name => Has(Path.Combine(name, "Scripts", "python.exe")) || Has(Path.Combine(name, "bin", Python))) ?? DefaultVenv;
            var interpreter = OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", Python);
            Guid[] prerequisites = [];
            if (!Has(interpreter))
                prerequisites = [Add("Python create venv", ProjectTaskKind.Prepare, Python, ["-m", "venv", venv]).Id];
            var prepare = Add("Python pip install", ProjectTaskKind.Prepare, interpreter,
                Has(Requirements) ? ["-m", "pip", Install, "-r", Requirements] : ["-m", "pip", Install, "-e", "."], prerequisites);
            Add("Python pytest", ProjectTaskKind.Test, interpreter, ["-m", "pytest"], prepare.Id);
            AddPythonRun(interpreter, [], prepare.Id);
            if (Has(PythonManifest))
                Add("Python build (requires build module)", ProjectTaskKind.Build, interpreter, ["-m", Build], prepare.Id);
        }

        private void DiscoverUvPip()
        {
            var interpreter = OperatingSystem.IsWindows() ? Path.Combine(DefaultVenv, "Scripts", "python.exe") : Path.Combine(DefaultVenv, "bin", Python);
            Guid[] prerequisites = [];
            if (!Has(interpreter))
                prerequisites = [Add("uv create venv", ProjectTaskKind.Prepare, "uv", ["venv", DefaultVenv]).Id];
            var install = Has(Requirements) ? new[] { "-r", Requirements } : ["-e", "."];
            var prepare = Add("uv pip install", ProjectTaskKind.Prepare, "uv", ["pip", Install, "--python", interpreter, .. install], prerequisites);
            Add("uv venv pytest", ProjectTaskKind.Test, interpreter, ["-m", "pytest"], prepare.Id);
            AddPythonRun(interpreter, [], prepare.Id);
        }

        private void AddPythonRun(string executable, IReadOnlyList<string> prefix, Guid dependency)
        {
            var script = new[] { "manage.py", "main.py", "app.py" }.FirstOrDefault(Has);
            if (script is not null)
                Add("Python run", ProjectTaskKind.Run, executable, [.. prefix, script, .. script == "manage.py" ? new[] { "runserver" } : []], dependency);
        }

        private void DiscoverCargo()
        {
            if (!Has("Cargo.toml"))
                return;
            var prepare = Add("Cargo fetch", ProjectTaskKind.Prepare, Cargo, ["fetch"]);
            var build = Add("Cargo build", ProjectTaskKind.Build, Cargo, [Build], prepare.Id);
            Add("Cargo test", ProjectTaskKind.Test, Cargo, ["test"], build.Id);
            Add("Cargo run", ProjectTaskKind.Run, Cargo, ["run"], build.Id);
        }

        private void DiscoverJava()
        {
            if (Has("pom.xml"))
            {
                var executable = JavaTool("mvn", "mvnw.cmd", "mvnw");
                var prepare = Add("Maven prepare", ProjectTaskKind.Prepare, executable, ["dependency:go-offline"]);
                var build = Add("Maven compile", ProjectTaskKind.Build, executable, ["compile"], prepare.Id);
                Add("Maven test", ProjectTaskKind.Test, executable, ["test"], build.Id);
            }
            if (Has("build.gradle") || Has("build.gradle.kts"))
            {
                var executable = JavaTool("gradle", "gradlew.bat", "gradlew");
                var build = Add("Gradle classes", ProjectTaskKind.Build, executable, ["--no-daemon", "classes"]);
                Add("Gradle test", ProjectTaskKind.Test, executable, ["--no-daemon", "test"], build.Id);
            }
        }

        private string JavaTool(string fallback, string windowsWrapper, string unixWrapper)
        {
            var wrapper = OperatingSystem.IsWindows() ? windowsWrapper : unixWrapper;
            return Has(wrapper) ? "." + Path.DirectorySeparatorChar + wrapper : fallback;
        }

        private void DiscoverCMake()
        {
            if (!Has("CMakeLists.txt"))
                return;
            var prepare = Add("CMake configure", ProjectTaskKind.Prepare, "cmake", ["-S", ".", "-B", Build]);
            var build = Add("CMake build", ProjectTaskKind.Build, "cmake", ["--build", Build, "--config", "Debug"], prepare.Id);
            Add("CTest", ProjectTaskKind.Test, "ctest", ["--test-dir", Build, "-C", "Debug", "--output-on-failure"], build.Id);
        }

        private void DiscoverContainers()
        {
            if (options.ContainerEngine is not { } engine)
                return;
            var file = new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }.FirstOrDefault(Has);
            if (file is null)
                return;
            var executable = engine == WorkflowContainerEngine.Podman ? "podman" : "docker";
            var build = Add($"{executable} compose build", ProjectTaskKind.Build, executable, ["compose", "-f", file, Build]);
            Add($"{executable} compose up", ProjectTaskKind.Run, executable, ["compose", "-f", file, "up"], build.Id);
        }
        private static string ReadManifest(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024)
                throw new InvalidDataException("Manifest exceeds the discovery size limit.");
            return File.ReadAllText(path);
        }
    }
}
