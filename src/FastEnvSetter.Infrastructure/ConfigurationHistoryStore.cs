using System.Collections.Immutable;
using System.Text.Json;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class ConfigurationHistoryStore
    : IConfigurationHistory
{
    private readonly string directory;
    private const int MaximumRecordBytes = 1_048_576;

    public ConfigurationHistoryStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An explicit absolute history directory is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<ConfigurationSnapshot> SaveAsync(Project project, CancellationToken cancellationToken = default)
    {
        var folder = ProjectDirectory(project.Id);
        Directory.CreateDirectory(folder);
        await using var guard = new FileStream(Path.Combine(folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var snapshots = await ListAsync(project.Id, cancellationToken);
        var revision = snapshots.Length == 0 ? 1 : checked(snapshots.Max(item => item.Revision) + 1);
        var snapshot = VersionService.Capture(project, revision, DateTimeOffset.UtcNow);
        await WriteImmutableAsync(Path.Combine(folder, $"{revision:D20}.json"), snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<ImmutableArray<ConfigurationSnapshot>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var folder = ProjectDirectory(projectId);
        if (!Directory.Exists(folder)) return [];
        var result = ImmutableArray.CreateBuilder<ConfigurationSnapshot>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(path), out var revision) || revision < 1) continue;
            result.Add(await GetAsync(projectId, revision, cancellationToken));
        }
        return result.ToImmutable();
    }

    public async Task<ConfigurationSnapshot> GetAsync(Guid projectId, long revision, CancellationToken cancellationToken = default)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        var snapshot = await ReadAsync<ConfigurationSnapshot>(Path.Combine(ProjectDirectory(projectId), $"{revision:D20}.json"), cancellationToken);
        VersionService.Validate(snapshot);
        if (snapshot.ProjectId != projectId || snapshot.Revision != revision) throw new InvalidDataException("Snapshot identity mismatch.");
        return snapshot;
    }

    public async Task SaveReleaseAsync(VersionRelease release, CancellationToken cancellationToken = default)
    {
        var configuration = await GetAsync(release.ProjectId, release.ConfigurationRevision, cancellationToken);
        if (configuration.ContentHash != release.ConfigurationHash || release.Id == Guid.Empty)
            throw new InvalidOperationException("Release configuration does not match stored history.");
        _ = VersionService.CreateRelease(release.ProjectId, release.Version, release.CommitSha, configuration, release.ArtifactDigests);
        var folder = Path.Combine(ProjectDirectory(release.ProjectId), "releases");
        Directory.CreateDirectory(folder);
        await WriteImmutableAsync(Path.Combine(folder, release.Id.ToString("N") + ".json"), release, cancellationToken);
    }

    public async Task<ImmutableArray<VersionRelease>> ListReleasesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(ProjectDirectory(projectId), "releases");
        if (!Directory.Exists(folder)) return [];
        var records = ImmutableArray.CreateBuilder<VersionRelease>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var release = await ReadAsync<VersionRelease>(path, cancellationToken);
            var configuration = await GetAsync(projectId, release.ConfigurationRevision, cancellationToken);
            _ = VersionService.CreateRelease(projectId, release.Version, release.CommitSha, configuration, release.ArtifactDigests);
            if (release.ProjectId != projectId || release.ConfigurationHash != configuration.ContentHash ||
                Path.GetFileNameWithoutExtension(path) != release.Id.ToString("N")) throw new InvalidDataException("Release identity mismatch.");
            records.Add(release);
        }
        return records.ToImmutable();
    }

    private string ProjectDirectory(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A project ID is required.", nameof(id));
        return Path.Combine(directory, id.ToString("N"));
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumRecordBytes) throw new InvalidDataException("History record exceeds size limit.");
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Empty history record.");
    }

    private static async Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("History record exceeds size limit.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
