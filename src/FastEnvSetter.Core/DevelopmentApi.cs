namespace FastEnvSetter.Core;

public interface IDevelopmentService
{
    Task<string> ExecuteJsonAsync(string? json, CancellationToken cancellationToken = default);
}

public sealed class DevelopmentRequest
{
    public int Version { get; set; } = 1;
    public string Operation { get; set; } = "";
    public Guid ProjectId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? TaskId { get; set; }
    public long? ExpectedRevision { get; set; }
    public long? ConfigurationRevision { get; set; }
    public string? ReviewFingerprint { get; set; }
    public string? Message { get; set; }
    public string? ReleaseVersion { get; set; }
    public string? CommitSha { get; set; }
    public Dictionary<string, string>? ArtifactDigests { get; set; }
    public string[]? Paths { get; set; }
    public string Remote { get; set; } = "origin";
    public bool Staged { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed record DevelopmentResponse(bool Success, string Code, object? Data = null, int Version = 1);
