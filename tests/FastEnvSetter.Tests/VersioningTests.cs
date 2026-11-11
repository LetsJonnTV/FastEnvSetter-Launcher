using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using System.Text.Json;

namespace FastEnvSetter.Tests;

public sealed class VersioningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fes-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Snapshots_omit_sensitive_settings_and_restore_stable_ids_without_import()
    {
        var profile = Guid.NewGuid();
        var project = new Project
        {
            Name = "Original",
            SourceRoot = root,
            EnvProfileIds = [profile],
            Services = [new() { Settings = new() { ["password"] = "do-not-persist" },
                ExtraVariables = [new() { Name = "SECRET", Value = "plaintext-secret", IsSecret = true }] }],
            Tasks = [new() { Arguments = ["--password=task-secret"] }]
        };
        var serviceId = project.Services[0].Id;
        var store = new ConfigurationHistoryStore(root);
        var first = await store.SaveAsync(project);
        project.Name = "Changed";
        var second = await store.SaveAsync(project);
        Assert.Equal(2, second.Revision);
        var loaded = await store.GetAsync(project.Id, first.Revision);
        var preview = VersionService.PreviewRestore(project, loaded);
        VersionService.ApplyRestore(project, preview);
        Assert.Equal("Original", project.Name);
        Assert.Equal(profile, Assert.Single(project.EnvProfileIds));
        Assert.Equal(serviceId, project.Services[0].Id);
        Assert.Equal("plaintext-secret", project.Services[0].ExtraVariables[0].Value);
        var text = string.Join("", Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("do-not-persist", text);
        Assert.DoesNotContain("plaintext-secret", text);
        Assert.DoesNotContain("task-secret", text);
        project.Name = "Concurrent change";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { VersionService.ApplyRestore(project, preview); await Task.CompletedTask; });
        Assert.Equal(2, (await new ConfigurationHistoryStore(root).ListAsync(project.Id)).Length);
        Assert.Throws<InvalidDataException>(() => VersionService.Validate(loaded with { Name = "tampered" }));
    }

    [Fact]
    public void Restore_with_data_validates_references_and_preserves_commands_and_secrets()
    {
        var profile = new EnvProfile { Name = "Dev", Variables = [new() { Name = "VALUE" }] };
        var project = new Project
        {
            Name = "Original",
            EnvProfileIds = [profile.Id],
            DisabledVariableIds = [profile.Variables[0].Id],
            Services = [new() { Name = "Server", Settings = new() { ["customCommand"] = "original" },
                ExtraVariables = [new() { Name = "SECRET", Value = "original-secret", IsSecret = true }] }]
        };
        var data = new AppData { Profiles = [profile], Projects = [project] };
        var snapshot = VersionService.Capture(project, 1, DateTimeOffset.UtcNow);
        project.Name = "Changed";
        project.EnvProfileIds.Clear();
        project.DisabledVariableIds.Clear();
        var service = project.Services[0];
        service.Settings["customCommand"] = "current-command";
        service.ExtraVariables[0].Value = "current-secret";
        var preview = VersionService.PreviewRestore(project, snapshot);

        VersionService.ApplyRestore(data, preview);

        Assert.Same(project, data.Projects[0]);
        Assert.Equal("Original", project.Name);
        Assert.Equal(profile.Id, Assert.Single(project.EnvProfileIds));
        Assert.Equal(profile.Variables[0].Id, Assert.Single(project.DisabledVariableIds));
        Assert.Same(service, Assert.Single(project.Services));
        Assert.Equal("current-command", service.Settings["customCommand"]);
        Assert.Equal("current-secret", service.ExtraVariables[0].Value);
    }

    [Theory]
    [InlineData("deleted_profile")]
    [InlineData("deleted_variable")]
    [InlineData("missing_parent")]
    [InlineData("profile_cycle")]
    [InlineData("duplicate_selection")]
    [InlineData("name_collision")]
    [InlineData("service_variable")]
    [InlineData("cross_service_disabled")]
    public void Restore_rejects_invalid_candidate_atomically(string invalid)
    {
        var profile = new EnvProfile { Name = "Dev", Variables = [new() { Name = "VALUE" }] };
        var service = new ServiceDefinition { Name = "Server", ExtraVariables = [new() { Name = "LOCAL" }] };
        var project = new Project
        {
            Name = "Original",
            SourceRoot = root,
            IsFavorite = true,
            EnvProfileIds = [profile.Id],
            DisabledVariableIds = [profile.Variables[0].Id],
            Services = [service]
        };
        if (invalid == "duplicate_selection") project.EnvProfileIds.Add(profile.Id);
        if (invalid == "service_variable") project.DisabledVariableIds = [service.ExtraVariables[0].Id];
        var snapshot = VersionService.Capture(project, 1, DateTimeOffset.UtcNow);
        project.Name = "Changed";
        project.IsFavorite = false;
        project.EnvProfileIds.Clear();
        project.DisabledVariableIds.Clear();
        var data = new AppData { Projects = [project], Profiles = [profile] };
        var preview = VersionService.PreviewRestore(project, snapshot);
        switch (invalid)
        {
            case "deleted_profile": data.Profiles.Clear(); break;
            case "deleted_variable": profile.Variables.Clear(); break;
            case "missing_parent": profile.ParentId = Guid.NewGuid(); break;
            case "profile_cycle": profile.ParentId = profile.Id; break;
            case "name_collision": data.Projects.Add(new() { Name = "ORIGINAL" }); break;
            case "cross_service_disabled": service.DisabledVariableIds.Add(service.ExtraVariables[0].Id); break;
        }
        var before = JsonSerializer.Serialize(data);
        var selections = project.EnvProfileIds;

        Assert.Throws<InvalidOperationException>(() => VersionService.ApplyRestore(data, preview));

        Assert.Equal(before, JsonSerializer.Serialize(data));
        Assert.Same(selections, project.EnvProfileIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_restore_rejects_reference_changes_without_data(bool profileChange)
    {
        var project = new Project { Name = "Original", EnvProfileIds = [Guid.NewGuid()], DisabledVariableIds = [Guid.NewGuid()] };
        var snapshot = VersionService.Capture(project, 1, DateTimeOffset.UtcNow);
        project.Name = "Changed";
        if (profileChange) project.EnvProfileIds.Clear();
        else project.DisabledVariableIds.Clear();
        var before = JsonSerializer.Serialize(project);
        var preview = VersionService.PreviewRestore(project, snapshot);

        Assert.Throws<InvalidOperationException>(() => VersionService.ApplyRestore(project, preview));

        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public async Task Releases_bind_immutable_sha_configuration_and_digests_and_cannot_be_overwritten()
    {
        var store = new ConfigurationHistoryStore(root);
        var project = new Project { Name = "Release" };
        var snapshot = await store.SaveAsync(project);
        var digests = new Dictionary<string, string> { ["app"] = "sha256:" + new string('b', 64) };
        var release = VersionService.CreateRelease(project.Id, "1.2.3", new string('a', 40), snapshot, digests);
        digests["app"] = "modified";
        await store.SaveReleaseAsync(release);
        await Assert.ThrowsAsync<IOException>(() => store.SaveReleaseAsync(release));
        var stored = Assert.Single(await store.ListReleasesAsync(project.Id));
        Assert.Equal(snapshot.ContentHash, stored.ConfigurationHash);
        Assert.Equal("sha256:" + new string('b', 64), stored.ArtifactDigests["app"]);
        Assert.Throws<ArgumentException>(() => VersionService.CreateRelease(project.Id, "bad", "main", snapshot, release.ArtifactDigests));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
