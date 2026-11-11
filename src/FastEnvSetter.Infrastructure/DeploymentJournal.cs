using System.Collections.Immutable;
using System.Text.Json;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class DeploymentFileJournal : IDeploymentJournal
{
    private readonly string directory;
    public DeploymentFileJournal(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an explicit absolute journal directory.", nameof(directory));
        this.directory = directory;
    }

    public ValueTask<IAsyncDisposable> AcquireTargetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetId == Guid.Empty) throw new ArgumentException("Target identity is required.", nameof(targetId));
        Directory.CreateDirectory(directory);
        return ValueTask.FromResult<IAsyncDisposable>(new FileStream(Path.Combine(directory, targetId.ToString("N") + ".target-lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public async Task AppendAsync(DeploymentOperation operation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await using var guard = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var count = Directory.EnumerateFiles(directory, "*.json").LongCount();
        var path = Path.Combine(directory, $"{count + 1:D20}-{Guid.NewGuid():N}.json");
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, operation, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ImmutableArray<DeploymentOperation>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return [];
        var result = ImmutableArray.CreateBuilder<DeploymentOperation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > 16_384) throw new InvalidDataException("Oversized deployment journal event.");
            result.Add(await JsonSerializer.DeserializeAsync<DeploymentOperation>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Invalid deployment journal event."));
        }
        return result.ToImmutable();
    }
}
