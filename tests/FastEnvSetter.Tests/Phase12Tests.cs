using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Runners;

namespace FastEnvSetter.Tests;

public class DotEnvTests
{
    [Fact]
    public void Parses_common_syntax()
    {
        const string text = "# Kommentar\r\nexport A=1\nB = zwei # Notiz\nC=\"x\\ny\"\nD='wört#lich \\n'\nE=\"mehr\nzeilig\"\nF=\n\nkaputt\nG=url#kein-kommentar\nA=letzter";

        var document = DotEnv.Parse(text);
        var values = document.Entries.ToDictionary(e => e.Name, e => e.Value);

        Assert.Equal("letzter", values["A"]);
        Assert.Equal("zwei", values["B"]);
        Assert.Equal("x\ny", values["C"]);
        Assert.Equal("wört#lich \\n", values["D"]);
        Assert.Equal("mehr\nzeilig", values["E"]);
        Assert.Equal("", values["F"]);
        Assert.Equal("url#kein-kommentar", values["G"]);
        Assert.Equal([10], document.SkippedLines);
    }

    [Fact]
    public void Unterminated_quote_is_skipped()
    {
        var document = DotEnv.Parse("A=\"offen\nB=2");
        Assert.Equal("B", document.Entries.Single().Name);
        Assert.Equal([1], document.SkippedLines);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("mit leerzeichen # und raute")]
    [InlineData("zeile1\nzeile2")]
    [InlineData("quote\"back\\slash")]
    [InlineData("")]
    public void Serialize_round_trips(string value)
    {
        var parsed = DotEnv.Parse(DotEnv.Serialize([new DotEnvEntry("NAME", value)]));
        Assert.Equal(value, parsed.Entries.Single().Value);
    }

    [Theory]
    [InlineData("API_KEY", true)]
    [InlineData("github_token", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("SECRET", true)]
    [InlineData("KEYBOARD", false)]
    [InlineData("PORT", false)]
    public void Detects_secret_names(string name, bool expected) => Assert.Equal(expected, DotEnv.LooksSecret(name));
}

public class EnvLayerTests
{
    private static EnvProfile Profile(string name, Guid? parent, params (string Name, string Value)[] variables) => new()
    {
        Name = name,
        ParentId = parent,
        Variables = variables.Select(v => new EnvVariable { Name = v.Name, Value = v.Value }).ToList(),
    };

    [Fact]
    public void Later_layers_win_and_shared_base_counts_once()
    {
        var root = Profile("base", null, ("A", "base"), ("B", "base"));
        var dev = Profile("dev", root.Id, ("A", "dev"));
        var extra = Profile("extra", root.Id, ("C", "extra"));
        var project = new Project { EnvProfileIds = [dev.Id, extra.Id] };

        var resolved = EnvResolver.Resolve(project, null, [root, dev, extra]);

        Assert.Equal("dev", resolved.Variables["A"]);
        Assert.Equal("base", resolved.Variables["B"]);
        Assert.Equal("extra", resolved.Variables["C"]);
        Assert.Empty(resolved.Errors);
    }

    [Fact]
    public void Service_extras_win_and_service_can_disable()
    {
        var profile = Profile("p", null, ("A", "p"), ("B", "p"));
        var project = new Project { EnvProfileIds = [profile.Id] };
        var service = new ServiceDefinition
        {
            DisabledVariableIds = [profile.Variables[1].Id],
            ExtraVariables = [new EnvVariable { Name = "A", Value = "service" }],
        };

        var resolved = EnvResolver.Resolve(project, service, [profile]);

        Assert.Equal("service", resolved.Variables["A"]);
        Assert.False(resolved.Variables.ContainsKey("B"));
    }

    [Fact]
    public void Inheritance_cycle_is_reported()
    {
        var a = Profile("a", null);
        var b = Profile("b", a.Id);
        a.ParentId = b.Id;

        Assert.NotEmpty(EnvResolver.Resolve(new Project { EnvProfileIds = [a.Id] }, null, [a, b]).Errors);
    }

    [Fact]
    public void Describe_marks_inherited_and_overridden()
    {
        var root = Profile("base", null, ("A", "1"));
        var dev = Profile("dev", root.Id, ("A", "2"));

        var rows = EnvResolver.Describe(new Project { EnvProfileIds = [dev.Id] }, [root, dev]);

        Assert.True(rows[0].IsInherited);
        Assert.True(rows[0].IsOverridden);
        Assert.False(rows[1].IsOverridden);
    }

    [Fact]
    public void Migration_moves_single_profile_and_keeps_backup()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(folder, "data.json");
            var id = Guid.NewGuid();
            File.WriteAllText(file, $$"""{"SchemaVersion":1,"Projects":[{"Name":"X","EnvProfileId":"{{id}}"}]}""");

            using (var store = new JsonAppDataStore(new DpapiSecretProtector(), file))
            {
                Assert.Equal([id], store.Data.Projects[0].EnvProfileIds);
                Assert.Equal(2, store.Data.SchemaVersion);
                store.SaveNow();
            }

            Assert.True(File.Exists(file + ".v1.bak"));
            Assert.DoesNotContain("\"EnvProfileId\"", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Service_secret_is_encrypted_on_disk()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(folder, "data.json");
            using (var store = new JsonAppDataStore(new DpapiSecretProtector(), file))
            {
                store.Data.Projects.Add(new Project { Services = [new ServiceDefinition { ExtraVariables = [new EnvVariable { Name = "K", Value = "dienst-geheim", IsSecret = true }] }] });
                store.SaveNow();
            }

            Assert.DoesNotContain("dienst-geheim", File.ReadAllText(file));
            using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), file);
            Assert.Equal("dienst-geheim", reloaded.Data.Projects[0].Services[0].ExtraVariables[0].Value);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class OrchestrationTests
{
    private const string LongRunning = "ping -n 60 127.0.0.1";

    private static ProcessSupervisor Supervisor() => new(new RunnerRegistry([new NodeRunner(), new PowerShellRunner()]), []);

    /// <summary>Läuft über cmd.exe, weil PowerShell je nach Profil sehr langsam startet.</summary>
    private static ServiceDefinition Cmd(string name, string command) => new()
    {
        Name = name,
        RunnerId = "node",
        WorkingDirectory = Path.GetTempPath(),
        Settings = new() { [RunnerBase.CustomCommandKey] = command },
    };

    private static ServiceDefinition Shell(string name, string command) => new()
    {
        Name = name,
        RunnerId = PowerShellRunner.RunnerId,
        WorkingDirectory = Path.GetTempPath(),
        Settings = new() { ["edition"] = "Windows PowerShell 5.1", [RunnerBase.CustomCommandKey] = command },
    };

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 30)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition(), "Bedingung wurde nicht rechtzeitig erfüllt.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ListenCommand(int port, int delaySeconds) =>
        $"Start-Sleep {delaySeconds}; $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port}); $l.Start(); Start-Sleep 60";

    [Fact]
    public void Graph_sorts_dependencies_first_and_detects_cycles()
    {
        var a = new ServiceDefinition { Name = "a" };
        var b = new ServiceDefinition { Name = "b" };
        var c = new ServiceDefinition { Name = "c", DependsOn = [a.Id] };
        a.DependsOn = [b.Id];

        Assert.Equal(["b", "a", "c"], ServiceGraph.Order([a, b, c])!.Select(s => s.Name));

        b.DependsOn = [a.Id];
        Assert.Null(ServiceGraph.Order([a, b]));
    }

    [Fact]
    public async Task Dependent_starts_only_after_port_is_ready()
    {
        using var supervisor = Supervisor();
        var port = FreePort();
        var db = Shell("db", ListenCommand(port, 2));
        db.Readiness = new ReadinessCheck { Kind = ReadinessKind.Port, Port = port, TimeoutSeconds = 90 };
        var api = Cmd("api", $"netstat -an | findstr LISTENING | findstr :{port}");
        api.DependsOn = [db.Id];
        var project = new Project { Services = [api, db] };

        var result = await supervisor.StartProjectAsync(project, []);

        Assert.True(result.Success, string.Join(",", result.Errors));
        Assert.Equal(ServiceState.Running, supervisor.GetState(db.Id));
        await WaitForAsync(() => supervisor.GetLogSince(api.Id, 0).Any(l => l.Text.Contains("LISTENING")));
        await supervisor.StopProjectAsync(project);
    }

    [Fact]
    public async Task Failed_dependency_blocks_dependent_only()
    {
        using var supervisor = Supervisor();
        var db = Cmd("db", "exit 0");
        db.PreSteps = ["cmd /c exit 7"];
        var api = Cmd("api", LongRunning);
        api.DependsOn = [db.Id];
        var other = Cmd("other", LongRunning);
        var project = new Project { Services = [db, api, other] };

        var result = await supervisor.StartProjectAsync(project, []);

        Assert.False(result.Success);
        Assert.Equal(ServiceState.Failed, supervisor.GetState(db.Id));
        Assert.Equal(ServiceState.Blocked, supervisor.GetState(api.Id));
        Assert.Equal(ServiceState.Running, supervisor.GetState(other.Id));
        await supervisor.StopProjectAsync(project);
    }

    [Fact]
    public async Task Prestep_output_is_logged_with_environment()
    {
        using var supervisor = Supervisor();
        var profile = new EnvProfile { Variables = [new EnvVariable { Name = "FES_STEP", Value = "im-schritt" }] };
        var service = Cmd("s", "exit 0");
        service.PreSteps = ["echo %FES_STEP%"];
        var project = new Project { Services = [service], EnvProfileIds = [profile.Id] };

        await supervisor.StartProjectAsync(project, [profile]);

        await WaitForAsync(() => supervisor.GetState(service.Id) == ServiceState.Stopped);
        Assert.Contains(supervisor.GetLogSince(service.Id, 0), l => l.Text == "im-schritt");
    }

    [Fact]
    public async Task Restart_policy_gives_up_after_limit()
    {
        using var supervisor = Supervisor();
        var service = Cmd("s", "exit 3");
        service.RestartPolicy = RestartPolicy.OnFailure;

        await supervisor.StartProjectAsync(new Project { Services = [service] }, []);

        await WaitForAsync(() => supervisor.GetState(service.Id) == ServiceState.Failed, 40);
        var log = supervisor.GetLogSince(service.Id, 0);
        Assert.Equal(3, log.Count(l => l.Text.StartsWith("Neustart (", StringComparison.Ordinal)));
        Assert.Contains(log, l => l.Text.Contains("aufgegeben"));
    }

    [Fact]
    public async Task Stop_during_restart_delay_ends_retries()
    {
        using var supervisor = Supervisor();
        var service = Cmd("s", "exit 3");
        service.RestartPolicy = RestartPolicy.OnFailure;
        await supervisor.StartProjectAsync(new Project { Services = [service] }, []);
        await WaitForAsync(() => supervisor.GetMessage(service.Id)?.StartsWith("Neustart") == true);

        await supervisor.StopServiceAsync(service.Id);

        Assert.Equal(ServiceState.Stopped, supervisor.GetState(service.Id));
        await Task.Delay(1500);
        Assert.DoesNotContain(supervisor.GetLogSince(service.Id, 0), l => l.Text.StartsWith("Neustart (", StringComparison.Ordinal) && l.Text.Contains("(2/"));
    }

    [Fact]
    public void Port_inspector_finds_listener_and_free_port()
    {
        var inspector = new PortInspector();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var owner = inspector.FindOwner(port);

        Assert.Equal(Environment.ProcessId, owner?.ProcessId);
        listener.Stop();
        Assert.Null(inspector.FindOwner(port));
    }
}
