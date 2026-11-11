using System.Text.Json;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class TransferConsistencyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("fes-transfer-").FullName;

    [Fact]
    public void Repeated_import_allocates_names_and_allows_subsequent_configuration_write()
    {
        using var store = new JsonAppDataStore(new DpapiSecretProtector(), Path.Combine(root, "data.json"));
        store.Data.Settings.AllowExternalConfigurationChanges = true;
        store.Data.Projects.Add(new() { Name = "APP" });
        store.Data.Profiles.Add(new() { Name = "DEV" });
        var profile = new EnvProfile { Name = "dev" };
        var package = new ProjectPackage
        {
            Project = new() { Name = "App", SourceRoot = ".", EnvProfileIds = [profile.Id] },
            Profiles = [profile, new() { Name = "dev (Import)" }, new() { Name = "Dev" }]
        };

        var first = ProjectTransfer.Import(package, root, store.Data);
        var second = ProjectTransfer.Import(package, root, store.Data);

        Assert.Equal("App (Import)", first.Name);
        Assert.Equal("App (Import 2)", second.Name);
        Assert.Equal("dev (Import)", store.Data.Profiles[1].Name);
        Assert.Equal(store.Data.Profiles.Count, store.Data.Profiles.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("App", package.Project.Name);
        var runners = new RunnerRegistry([]);
        using var supervisor = new ProcessSupervisor(runners, []);
        var api = new ConfigurationService(store, runners, supervisor);
        var response = api.Execute(new()
        {
            Operation = "create_project",
            Project = new("Unrelated"),
            RequestId = Guid.NewGuid(),
            ExpectedRevision = store.Data.Revision
        });
        Assert.True(response.Success, response.Code);
    }

    [Theory]
    [InlineData("duplicate_profile_id")]
    [InlineData("duplicate_selection")]
    [InlineData("missing_profile")]
    [InlineData("missing_parent")]
    [InlineData("profile_cycle")]
    [InlineData("duplicate_variable_id")]
    [InlineData("cross_service_variable")]
    public void Invalid_references_are_rejected_without_partial_mutation(string invalid)
    {
        var profile = new EnvProfile { Name = "Dev", Variables = [new() { Name = "VALUE" }] };
        var service = new ServiceDefinition { Name = "Server", ExtraVariables = [new() { Name = "LOCAL" }] };
        var package = new ProjectPackage
        {
            Project = new() { Name = "App", EnvProfileIds = [profile.Id], Services = [service] },
            Profiles = [profile]
        };
        switch (invalid)
        {
            case "duplicate_profile_id": package.Profiles.Add(new() { Id = profile.Id, Name = "Other" }); break;
            case "duplicate_selection": package.Project.EnvProfileIds.Add(profile.Id); break;
            case "missing_profile": package.Project.EnvProfileIds.Add(Guid.NewGuid()); break;
            case "missing_parent": profile.ParentId = Guid.NewGuid(); break;
            case "profile_cycle": profile.ParentId = profile.Id; break;
            case "duplicate_variable_id": service.ExtraVariables[0].Id = profile.Variables[0].Id; break;
            case "cross_service_variable": package.Project.DisabledVariableIds.Add(service.ExtraVariables[0].Id); break;
        }
        var data = new AppData { Projects = [new() { Name = "Existing" }], Profiles = [new() { Name = "Existing" }] };
        var before = JsonSerializer.Serialize(data);
        var source = JsonSerializer.Serialize(package);

        Assert.Throws<InvalidDataException>(() => ProjectTransfer.Import(package, root, data));

        Assert.Equal(before, JsonSerializer.Serialize(data));
        Assert.Equal(source, JsonSerializer.Serialize(package));
    }

    public void Dispose() => Directory.Delete(root, true);
}
