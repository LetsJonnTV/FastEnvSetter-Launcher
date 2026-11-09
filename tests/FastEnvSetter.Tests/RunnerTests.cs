using FastEnvSetter.Core;
using FastEnvSetter.Runners;

namespace FastEnvSetter.Tests;

public class RunnerTests
{
    private static ServiceDefinition Service(string runner, params (string Key, string Value)[] settings) => new()
    {
        RunnerId = runner,
        WorkingDirectory = @"C:\work",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value),
    };

    [Fact]
    public void Python_without_venv_goes_through_cmd_for_shims()
    {
        var command = new PythonRunner().BuildStart(Service("python", ("target", "main:app"), ("port", "9000")));

        Assert.Equal("cmd.exe", command.FileName);
        Assert.Contains("python -m uvicorn main:app --host 127.0.0.1 --port 9000 --reload", command.RawArguments);
        Assert.Equal("1", command.Environment["PYTHONUNBUFFERED"]);
    }

    [Fact]
    public void Python_with_venv_uses_its_interpreter_and_path()
    {
        var command = new PythonRunner().BuildStart(Service("python", ("venv", ".venv"), ("mode", "Skript"), ("target", "main.py")));

        Assert.Equal(@"C:\work\.venv\Scripts\python.exe", command.FileName);
        Assert.Equal(["main.py"], command.Arguments);
        Assert.Equal(@"C:\work\.venv\Scripts", command.PrependPath.Single());
    }

    [Fact]
    public void Python_requires_target_unless_custom_command_is_set()
    {
        var runner = new PythonRunner();

        Assert.NotEmpty(runner.Validate(Service("python")));
        Assert.Empty(runner.Validate(Service("python", (RunnerBase.CustomCommandKey, "celery worker"))));
    }

    [Fact]
    public void Node_passes_extra_args_after_separator_for_npm_only()
    {
        var runner = new NodeRunner();

        Assert.Contains("npm run dev -- --port 3000", runner.BuildStart(Service("node", ("script", "dev"), ("args", "--port 3000"))).RawArguments);
        Assert.Contains("pnpm run dev --port 3000", runner.BuildStart(Service("node", ("packageManager", "pnpm"), ("script", "dev"), ("args", "--port 3000"))).RawArguments);
    }

    [Fact]
    public void DotNet_builds_run_command()
    {
        var command = new DotNetRunner().BuildStart(Service("dotnet", ("project", @"src\Api\Api.csproj")));

        Assert.Equal("dotnet", command.FileName);
        Assert.Equal(["run", "--project", @"src\Api\Api.csproj", "-c", "Debug"], command.Arguments);
    }

    [Fact]
    public void Compose_has_matching_stop_command()
    {
        var runner = new DockerComposeRunner();
        var service = Service("docker-compose", ("file", "compose.yaml"), ("services", "db redis"));

        Assert.Equal(["compose", "-f", "compose.yaml", "up", "db", "redis"], runner.BuildStart(service).Arguments);
        Assert.Equal(["compose", "-f", "compose.yaml", "stop", "db", "redis"], runner.BuildStop(service)!.Arguments);
    }

    [Fact]
    public void PowerShell_without_command_needs_terminal_and_selects_edition()
    {
        var runner = new PowerShellRunner();
        var service = Service("powershell", ("edition", "Windows PowerShell 5.1"));

        Assert.True(runner.RequiresTerminal(service));
        Assert.Equal("powershell.exe", runner.BuildStart(service).FileName);
    }

    [Fact]
    public void Detection_finds_node_scripts_and_fastapi()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "package.json"), """{"scripts":{"build":"x","dev":"y"}}""");
            File.WriteAllText(Path.Combine(folder, "pnpm-lock.yaml"), "");
            File.WriteAllText(Path.Combine(folder, "main.py"), "app = FastAPI()");

            var node = new NodeRunner().Detect(folder);
            var python = new PythonRunner().Detect(folder);

            Assert.Equal("dev", node.Values["script"]);
            Assert.Equal("pnpm", node.Values["packageManager"]);
            Assert.Equal(["build", "dev"], node.Choices["script"]);
            Assert.Equal("main:app", python.Values["target"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class ExampleRunner : RunnerBase
    {
        public override string Id => "example";
        public override string DisplayName => "Beispiel";
        public override string Icon => "Code24";
        protected override IEnumerable<SettingField> DefineFields() => [new("message", "Nachricht") { Required = true }];
        protected override ProcessCommand BuildDefault(ServiceDefinition service) => ProcessCommand.ViaCmd($"echo {Value(service, "message")}");
    }

    [Fact]
    public void New_runner_only_needs_a_class_and_registration()
    {
        var registry = new RunnerRegistry([new ExampleRunner()]);
        var runner = registry.Find("example")!;

        Assert.Equal(["message", RunnerBase.CustomCommandKey], runner.GetSettingsSchema().Select(f => f.Key));
        Assert.NotEmpty(runner.Validate(Service("example")));
        Assert.Contains("echo hallo", runner.BuildStart(Service("example", ("message", "hallo"))).RawArguments);
    }

    [Theory]
    [InlineData("--a \"b c\" d", new[] { "--a", "b c", "d" })]
    [InlineData("  ", new string[0])]
    [InlineData("\"\"", new[] { "" })]
    public void Splitter_respects_quotes(string input, string[] expected) =>
        Assert.Equal(expected, CommandLineSplitter.Split(input));
}
