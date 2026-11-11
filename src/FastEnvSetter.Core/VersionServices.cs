using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FastEnvSetter.Core;

public sealed record ConfigurationSnapshot(Guid ProjectId, long Revision, DateTimeOffset CreatedAt,
    string Name, string SourceRoot, bool IsFavorite, ImmutableArray<Guid> ProfileIds,
    ImmutableArray<Guid> DisabledVariableIds, string ContentHash);

public sealed record ConfigurationRestorePreview(ConfigurationSnapshot Snapshot, string CurrentHash,
    ImmutableArray<string> Limitations);

public interface IConfigurationHistory
{
    Task<ConfigurationSnapshot> SaveAsync(Project project, CancellationToken cancellationToken = default);
    Task<ImmutableArray<ConfigurationSnapshot>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ConfigurationSnapshot> GetAsync(Guid projectId, long revision, CancellationToken cancellationToken = default);
}

public sealed record VersionRelease(Guid Id, Guid ProjectId, string Version, string CommitSha,
    long ConfigurationRevision, string ConfigurationHash, ImmutableSortedDictionary<string, string> ArtifactDigests,
    DateTimeOffset CreatedAt);

public static class VersionService
{
    public static string ConfigurationHash(Project project) => Hash(new
    {
        project.Id,
        project.Name,
        project.SourceRoot,
        project.IsFavorite,
        Profiles = project.EnvProfileIds.ToArray(),
        Disabled = project.DisabledVariableIds.Order().ToArray()
    });

    public static ConfigurationSnapshot Capture(Project project, long revision, DateTimeOffset createdAt)
    {
        if (project.Id == Guid.Empty || revision < 1) throw new ArgumentException("Stable project identity and positive revision are required.");
        return new(project.Id, revision, createdAt, project.Name, project.SourceRoot, project.IsFavorite,
            [.. project.EnvProfileIds], [.. project.DisabledVariableIds.Order()], ConfigurationHash(project));
    }

    public static void Validate(ConfigurationSnapshot snapshot)
    {
        var project = new Project
        {
            Id = snapshot.ProjectId,
            Name = snapshot.Name,
            SourceRoot = snapshot.SourceRoot,
            IsFavorite = snapshot.IsFavorite,
            EnvProfileIds = [.. snapshot.ProfileIds],
            DisabledVariableIds = [.. snapshot.DisabledVariableIds]
        };
        if (snapshot.Revision < 1 || snapshot.ProjectId == Guid.Empty || ConfigurationHash(project) != snapshot.ContentHash)
            throw new InvalidDataException("Configuration snapshot is corrupt.");
    }

    public static ConfigurationRestorePreview PreviewRestore(Project current, ConfigurationSnapshot snapshot)
    {
        Validate(snapshot);
        if (current.Id != snapshot.ProjectId) throw new InvalidOperationException("Snapshot belongs to another project.");
        return new(snapshot, ConfigurationHash(current),
            ["Only project name, source root, favorite flag, profile selections and disabled variable IDs are restored.",
             "Secret values, profile contents, service settings, commands, tasks, editor and deployment targets are not captured or restored.",
             "Referenced profiles and variables must still exist; IDs are never regenerated."]);
    }

    public static void ApplyRestore(Project current, ConfigurationRestorePreview approvedPreview)
    {
        ValidateRestorePreview(current, approvedPreview);
        var snapshot = approvedPreview.Snapshot;
        if (!current.EnvProfileIds.SequenceEqual(snapshot.ProfileIds)
            || !current.DisabledVariableIds.SetEquals(snapshot.DisabledVariableIds))
            throw new InvalidOperationException("Reference changes require AppData validation.");
        ApplyMetadata(current, snapshot);
    }

    public static void ApplyRestore(AppData data, ConfigurationRestorePreview approvedPreview)
    {
        var matches = data.Projects.Where(project => project.Id == approvedPreview.Snapshot.ProjectId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Restore requires one existing project.");
        var current = matches[0];
        ValidateRestorePreview(current, approvedPreview);
        var candidate = new Project { Id = current.Id, Services = current.Services };
        ApplyMetadata(candidate, approvedPreview.Snapshot);
        ValidateRestoreGraph(data, candidate);
        ApplyMetadata(current, approvedPreview.Snapshot);
    }

    private static void ValidateRestorePreview(Project current, ConfigurationRestorePreview approvedPreview)
    {
        Validate(approvedPreview.Snapshot);
        if (current.Id != approvedPreview.Snapshot.ProjectId || ConfigurationHash(current) != approvedPreview.CurrentHash)
            throw new InvalidOperationException("Restore preview is stale or belongs to another project.");
    }

    private static void ValidateRestoreGraph(AppData data, Project candidate)
    {
        static void Require(bool valid)
        {
            if (!valid) throw new InvalidOperationException("Restore would create an invalid configuration graph.");
        }
        var projects = data.Projects.Select(project => project.Id == candidate.Id ? candidate : project).ToArray();
        Require(!string.IsNullOrWhiteSpace(candidate.Name) && candidate.Name.Length <= 200
            && candidate.Name == candidate.Name.Trim() && !candidate.Name.Any(char.IsControl));
        Require(candidate.SourceRoot is not null && (candidate.SourceRoot.Length == 0 || Path.IsPathFullyQualified(candidate.SourceRoot)));
        Require(projects.Select(project => project.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == projects.Length);
        var identities = new HashSet<Guid>();
        foreach (var identity in data.Profiles.Select(profile => profile.Id)
            .Concat(data.Profiles.SelectMany(profile => profile.Variables).Select(variable => variable.Id))
            .Concat(projects.Select(project => project.Id))
            .Concat(projects.SelectMany(project => project.Services).Select(service => service.Id))
            .Concat(projects.SelectMany(project => project.Services).SelectMany(service => service.ExtraVariables).Select(variable => variable.Id)))
            Require(identity != Guid.Empty && identities.Add(identity));
        var profiles = data.Profiles.ToDictionary(profile => profile.Id);
        var variables = data.Profiles.SelectMany(profile => profile.Variables).Select(variable => variable.Id).ToHashSet();
        foreach (var profile in data.Profiles)
        {
            var path = new HashSet<Guid> { profile.Id };
            var parent = profile.ParentId;
            while (parent is { } parentId)
            {
                Require(path.Add(parentId) && profiles.ContainsKey(parentId));
                parent = profiles[parentId].ParentId;
            }
        }
        foreach (var project in projects)
        {
            Require(project.EnvProfileIds.Count == project.EnvProfileIds.Distinct().Count()
                && project.EnvProfileIds.All(profiles.ContainsKey));
            Require(project.DisabledVariableIds.All(variables.Contains));
            foreach (var service in project.Services)
                Require(service.DisabledVariableIds.All(variables.Contains));
        }
    }

    private static void ApplyMetadata(Project current, ConfigurationSnapshot snapshot)
    {
        current.Name = snapshot.Name;
        current.SourceRoot = snapshot.SourceRoot;
        current.IsFavorite = snapshot.IsFavorite;
        current.EnvProfileIds = [.. snapshot.ProfileIds];
        current.DisabledVariableIds = [.. snapshot.DisabledVariableIds];
    }

    public static VersionRelease CreateRelease(Guid projectId, string version, string commitSha,
        ConfigurationSnapshot configuration, IReadOnlyDictionary<string, string> artifactDigests)
    {
        Validate(configuration);
        if (projectId != configuration.ProjectId || string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl))
            throw new ArgumentException("Release must identify its project and version.");
        if (!Regex.IsMatch(commitSha, "\\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\\z")) throw new ArgumentException("A full immutable source SHA is required.");
        if (artifactDigests.Count == 0 || artifactDigests.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
            !Regex.IsMatch(pair.Value, "\\Asha256:[a-fA-F0-9]{64}\\z")))
            throw new ArgumentException("At least one named SHA-256 artifact digest is required.");
        return new(Guid.NewGuid(), projectId, version, commitSha.ToLowerInvariant(), configuration.Revision,
            configuration.ContentHash, artifactDigests.ToImmutableSortedDictionary(StringComparer.Ordinal), DateTimeOffset.UtcNow);
    }

    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
