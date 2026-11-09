using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Runners;

namespace FastEnvSetter.Tests;

public class AnsiTests
{
    [Fact]
    public void Plain_text_has_no_spans() => Assert.Null(Ansi.Parse("hallo").Spans);

    [Fact]
    public void Extracts_colors_and_strips_sequences()
    {
        var (plain, spans) = Ansi.Parse("\u001b[31mrot\u001b[0m normal\u001b[2K");

        Assert.Equal("rot normal", plain);
        Assert.Equal(0xE5534B, spans![0].Color);
        Assert.Null(spans[1].Color);
    }

    [Fact]
    public void Understands_true_color() =>
        Assert.Equal(0x010203, Ansi.Parse("\u001b[38;2;1;2;3mx").Spans![0].Color);
}

public class TransferTests
{
    [Fact]
    public void Export_never_contains_local_secrets_and_keeps_vault_references()
    {
        var profile = new EnvProfile
        {
            Name = "dev",
            Variables =
            [
                new EnvVariable { Name = "PLAIN", Value = "offen" },
                new EnvVariable { Name = "LOCAL", Value = "lokal-geheim", IsSecret = true, ProtectedValue = "verschluesselt" },
                new EnvVariable { Name = "VAULT", IsSecret = true, SecretRef = new SecretRef { ProviderId = "1password", Reference = "op://a/b/c" } },
            ],
        };
        var project = new Project
        {
            Name = "A",
            EnvProfileIds = [profile.Id],
            Services = [new ServiceDefinition { Name = "s", WorkingDirectory = @"C:\repo\api", ExtraVariables = [new EnvVariable { Name = "EXTRA", Value = "dienst-geheim", IsSecret = true }] }],
        };

        var json = ProjectTransfer.Export(project, [profile], @"C:\repo");

        Assert.DoesNotContain("lokal-geheim", json);
        Assert.DoesNotContain("verschluesselt", json);
        Assert.DoesNotContain("dienst-geheim", json);
        Assert.DoesNotContain(@"C:\\repo", json);
        Assert.Contains("op://a/b/c", json);
        Assert.Contains("offen", json);
        Assert.Equal("lokal-geheim", profile.Variables[1].Value);
    }

    [Fact]
    public void Import_remaps_ids_and_rebases_folders()
    {
        var root = new EnvProfile { Name = "base", Variables = [new EnvVariable { Name = "A" }] };
        var child = new EnvProfile { Name = "dev", ParentId = root.Id, Variables = [new EnvVariable { Name = "OFF" }] };
        var db = new ServiceDefinition { Name = "db", WorkingDirectory = @"C:\repo\db" };
        var api = new ServiceDefinition { Name = "api", WorkingDirectory = @"C:\repo\api", DependsOn = [db.Id] };
        var project = new Project { Name = "P", EnvProfileIds = [child.Id], DisabledVariableIds = [child.Variables[0].Id], Services = [db, api] };
        var data = new AppData { Profiles = [new EnvProfile { Name = "dev" }] };

        var package = ProjectTransfer.Parse(ProjectTransfer.Export(project, [root, child], ProjectTransfer.CommonRoot(project)))!;
        var imported = ProjectTransfer.Import(package, @"D:\neu", data);

        Assert.NotEqual(project.Id, imported.Id);
        Assert.Equal(imported.Services[0].Id, imported.Services[1].DependsOn.Single());
        Assert.NotEqual(db.Id, imported.Services[0].Id);
        Assert.Equal(@"D:\neu\db", imported.Services[0].WorkingDirectory);
        var importedChild = data.Profiles.Single(p => p.Id == imported.EnvProfileIds.Single());
        Assert.Equal("dev (Import)", importedChild.Name);
        Assert.Equal(importedChild.ParentId, data.Profiles.Single(p => p.Name == "base").Id);
        Assert.Equal(importedChild.Variables[0].Id, imported.DisabledVariableIds.Single());
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("""{"Format":99,"Project":{"Services":[{}]}}""")]
    [InlineData("""{"Format":1,"Project":{"Services":[]}}""")]
    public void Invalid_packages_are_rejected(string json) => Assert.Null(ProjectTransfer.Parse(json));
}

public sealed class VaultTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("fesbin").FullName;

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    private void Fake(string cli, string script) => File.WriteAllText(Path.Combine(_bin, cli + ".cmd"), "@echo off\r\n" + script + "\r\n");

    [Fact]
    public async Task Vault_value_reaches_process_and_is_masked_but_never_saved()
    {
        Fake("op", "echo tresor-wert-77");
        var variable = new EnvVariable { Name = "FES_VAULT", IsSecret = true, SecretRef = new SecretRef { ProviderId = "1password", Reference = "op://a/b/c" } };
        var profile = new EnvProfile { Variables = [variable] };
        var service = new ServiceDefinition
        {
            Name = "s",
            RunnerId = "node",
            WorkingDirectory = Path.GetTempPath(),
            Settings = new() { [RunnerBase.CustomCommandKey] = "echo Wert=%FES_VAULT%" },
        };
        var project = new Project { EnvProfileIds = [profile.Id], Services = [service] };
        using var supervisor = new ProcessSupervisor(new RunnerRegistry([new NodeRunner()]), [], [new OnePasswordProvider(_bin)]);

        var result = await supervisor.StartProjectAsync(project, [profile]);

        Assert.True(result.Success, string.Join(",", result.Errors));
        for (var i = 0; i < 100 && supervisor.GetState(service.Id) != ServiceState.Stopped; i++)
            await Task.Delay(100);
        Assert.Contains(supervisor.GetLogSince(service.Id, 0), l => l.Text == "Wert=••••••");
        Assert.Equal("", variable.Value);

        var file = Path.Combine(_bin, "data.json");
        using var store = new JsonAppDataStore(new DpapiSecretProtector(), file);
        store.Data.Profiles.Add(profile);
        store.SaveNow();
        Assert.DoesNotContain("tresor-wert-77", File.ReadAllText(file));
    }

    [Fact]
    public async Task Missing_cli_and_failure_block_start_with_reason()
    {
        var empty = Path.Combine(_bin, "leer");
        Directory.CreateDirectory(empty);
        var variable = new EnvVariable { Name = "V", SecretRef = new SecretRef { ProviderId = "1password", Reference = "op://x" } };

        var missing = await VaultFetcher.FetchAsync([variable], [new OnePasswordProvider(empty)], CancellationToken.None);
        Assert.Contains("nicht installiert", missing.Errors.Single());

        Fake("op", "echo gesperrt 1>&2\r\nexit /b 1");
        var failed = await VaultFetcher.FetchAsync([variable], [new OnePasswordProvider(_bin)], CancellationToken.None);
        Assert.Contains("gesperrt", failed.Errors.Single());
    }

    [Fact]
    public async Task Shell_metacharacters_in_reference_are_rejected_for_cmd_launchers()
    {
        Fake("op", "echo nie");
        var variable = new EnvVariable { Name = "V", SecretRef = new SecretRef { ProviderId = "1password", Reference = "x\" & calc & \"" } };

        var result = await VaultFetcher.FetchAsync([variable], [new OnePasswordProvider(_bin)], CancellationToken.None);

        Assert.Contains("unzulässige", result.Errors.Single());
    }

    [Fact]
    public async Task Bitwarden_needs_unlock_and_keeps_password_off_command_line()
    {
        Fake("bw", """
            if "%1"=="status" (echo {"status":"locked"} & exit /b 0)
            if "%1"=="unlock" (if "%FES_BW_PASSWORD%"=="meister" (echo sitzung-1 & exit /b 0) else (exit /b 1))
            if "%1"=="sync" exit /b 0
            if "%1"=="get" (echo [%BW_SESSION%]%BITWARDENCLI_APPDATA_DIR% & exit /b 0)
            exit /b 2
            """);
        var provider = new BitwardenProvider(_bin, _bin);

        await Assert.ThrowsAsync<SecretProviderException>(() => provider.ResolveAsync("eintrag"));
        Assert.Equal("Gesperrt", await provider.GetStatusAsync());

        await provider.LoginAsync("", "meister", "");
        var value = await provider.ResolveAsync("eintrag");

        Assert.StartsWith("[sitzung-1", value);
        Assert.Contains("cli-bitwarden", value);
    }

    [Fact]
    public async Task Vaultwarden_uses_its_own_cli_profile_and_server()
    {
        Fake("bw", """
            if "%1"=="status" (echo {"status":"unauthenticated"} & exit /b 0)
            if "%1"=="config" (echo %3> "%BITWARDENCLI_APPDATA_DIR%.server" & exit /b 0)
            if "%1"=="login" (echo sitzung-2 & exit /b 0)
            exit /b 0
            """);
        using var store = new JsonAppDataStore(new DpapiSecretProtector(), Path.Combine(_bin, "d.json"));
        store.Data.Settings.VaultwardenUrl = "https://vault.example";
        var provider = new VaultwardenProvider(store, _bin, _bin);

        await provider.LoginAsync("a@b.c", "pw", "");

        Assert.Contains("https://vault.example", File.ReadAllText(Path.Combine(_bin, "cli-vaultwarden.server")));
    }
}
