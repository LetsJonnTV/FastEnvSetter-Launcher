using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Runners;

namespace FastEnvSetter.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;
    private string DataFile => Path.Combine(_folder, "data.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Secrets_are_encrypted_on_disk_and_restored()
    {
        const string secret = "super-secret-value-123";
        using (var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile))
        {
            store.Data.Profiles.Add(new EnvProfile { Variables = [new EnvVariable { Name = "KEY", Value = secret, IsSecret = true }] });
            store.SaveNow();
            Assert.Equal(secret, store.Data.Profiles[0].Variables[0].Value);
        }

        Assert.DoesNotContain(secret, File.ReadAllText(DataFile));
        using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        Assert.Equal(secret, reloaded.Data.Profiles[0].Variables[0].Value);
    }

    [Fact]
    public void Undecryptable_secret_blocks_start_and_survives_saving()
    {
        File.WriteAllText(DataFile, """{"Profiles":[{"Variables":[{"Name":"KEY","IsSecret":true,"ProtectedValue":"bm9wZQ=="}]}]}""");

        using var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        var profile = store.Data.Profiles[0];
        Assert.True(profile.Variables[0].DecryptionFailed);
        Assert.NotEmpty(EnvResolver.Resolve(new Project { EnvProfileIds = [profile.Id] }, null, store.Data.Profiles).Errors);

        store.SaveNow();
        Assert.Contains("bm9wZQ==", File.ReadAllText(DataFile));
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_reported()
    {
        File.WriteAllText(DataFile, "{ kaputt");

        using var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);

        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(_folder, "*.defekt-*"));
    }

    [Fact]
    public void Newer_schema_is_rejected_without_overwriting_data()
    {
        var contents = $$"""{"SchemaVersion":{{AppData.CurrentSchemaVersion + 1}},"Projects":[]}""";
        File.WriteAllText(DataFile, contents);

        Assert.Throws<InvalidDataException>(() => new JsonAppDataStore(new DpapiSecretProtector(), DataFile));

        Assert.Equal(contents, File.ReadAllText(DataFile));
    }

    [Fact]
    public void Development_project_metadata_survives_reload()
    {
        using (var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile))
        {
            store.Data.Projects.Add(new Project
            {
                Name = "workspace",
                SourceRoot = _folder,
                EditorTargets = [new() { Name = "Backend", Profile = "Backend" }],
                Tasks = [new() { Name = "Tests", FileName = "dotnet", Arguments = ["test"] }],
                EnvironmentFiles = [".env"],
                DeploymentTargets = [new() { Name = "Local" }],
                Releases = [new() { Version = "1.0.0", CommitSha = "abc" }],
            });
            store.SaveNow();
        }

        using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        var project = Assert.Single(reloaded.Data.Projects);
        Assert.Equal(_folder, project.SourceRoot);
        Assert.Equal("Backend", Assert.Single(project.EditorTargets).Profile);
        Assert.Equal("test", Assert.Single(Assert.Single(project.Tasks).Arguments));
        Assert.Equal(".env", Assert.Single(project.EnvironmentFiles));
        Assert.Equal("Local", Assert.Single(project.DeploymentTargets).Name);
        Assert.Equal("1.0.0", Assert.Single(project.Releases).Version);
    }

    [Fact]
    public void Resolver_honours_checkboxes_and_rejects_duplicates()
    {
        var off = new EnvVariable { Name = "OFF", Value = "1" };
        var on = new EnvVariable { Name = "ON", Value = "2" };
        var profile = new EnvProfile { Variables = [off, on] };
        var project = new Project { DisabledVariableIds = [off.Id], EnvProfileIds = [profile.Id] };

        var resolved = EnvResolver.Resolve(project, null, [profile]);
        Assert.Equal(["ON"], resolved.Variables.Keys);

        var duplicates = new EnvProfile { Variables = [new() { Name = "A" }, new() { Name = "a" }] };
        var duplicate = EnvResolver.Resolve(new Project { EnvProfileIds = [duplicates.Id] }, null, [duplicates]);
        Assert.Single(duplicate.Errors);
    }

    private static ProcessSupervisor Supervisor() =>
        new(new RunnerRegistry([new NodeRunner()]), []);

    private static ServiceDefinition Shell(string command) => new()
    {
        Name = "test",
        RunnerId = "node",
        WorkingDirectory = Path.GetTempPath(),
        Settings = new() { [RunnerBase.CustomCommandKey] = command },
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition(), "Bedingung wurde nicht rechtzeitig erfüllt.");
    }

    [Fact]
    public async Task Process_gets_selected_env_and_secret_is_masked_in_log()
    {
        using var supervisor = Supervisor();
        var plain = new EnvVariable { Name = "FES_PLAIN", Value = "sichtbar" };
        var secret = new EnvVariable { Name = "FES_SECRET", Value = "geheim-123", IsSecret = true };
        var off = new EnvVariable { Name = "FES_OFF", Value = "nein" };
        var service = Shell("if defined FES_OFF (echo gesetzt) else (echo %FES_PLAIN% %FES_SECRET% leer)");
        var profile = new EnvProfile { Variables = [plain, secret, off] };
        var project = new Project { Services = [service], DisabledVariableIds = [off.Id], EnvProfileIds = [profile.Id] };

        var result = await supervisor.StartProjectAsync(project, [profile]);

        Assert.True(result.Success, string.Join(", ", result.Errors));
        await WaitForAsync(() => supervisor.GetState(service.Id) == ServiceState.Stopped);
        Assert.Contains(supervisor.GetLogSince(service.Id, 0), e => e.Text.Trim() == "sichtbar •••••• leer");
        Assert.DoesNotContain(supervisor.GetLogSince(service.Id, 0), e => e.Text.Contains("geheim-123"));
    }

    [Fact]
    public async Task Crash_marks_only_that_service_failed()
    {
        using var supervisor = Supervisor();
        var crashing = Shell("exit 3");
        var running = Shell("ping -n 60 127.0.0.1");
        var project = new Project { Services = [crashing, running] };

        await supervisor.StartProjectAsync(project, []);

        await WaitForAsync(() => supervisor.GetState(crashing.Id) == ServiceState.Failed);
        Assert.Equal(ServiceState.Running, supervisor.GetState(running.Id));

        await supervisor.StopProjectAsync(project);
        await WaitForAsync(() => supervisor.GetState(running.Id) == ServiceState.Stopped);
    }

    [Fact]
    public async Task Missing_working_directory_is_reported()
    {
        using var supervisor = Supervisor();
        var service = Shell("exit 0");
        service.WorkingDirectory = @"C:\gibt\es\nicht";

        var result = await supervisor.StartProjectAsync(new Project { Services = [service] }, []);

        Assert.False(result.Success);
        Assert.Equal(ServiceState.Failed, supervisor.GetState(service.Id));
    }
}
