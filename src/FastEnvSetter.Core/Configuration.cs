namespace FastEnvSetter.Core;

public sealed record ConfigurationReceipt(Guid RequestId, long Revision, Guid? EntityId);

public interface IConfigurationStore
{
    T ReadConfiguration<T>(Func<AppData, T> read);
    T CommitConfiguration<T>(Func<AppData, T> change);
}

public interface IConfigurationService
{
    ConfigurationResponse Execute(ConfigurationRequest? request);
    string ExecuteJson(string? json);
}

public sealed record ConfigurationResponse(bool Success, string Code, long Revision, object? Data = null, Guid? EntityId = null, bool Replayed = false);

public sealed class ConfigurationRequest
{
    public int Version { get; set; } = 1;
    public string Operation { get; set; } = "";
    public Guid? RequestId { get; set; }
    public long? ExpectedRevision { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ProfileId { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? VariableId { get; set; }
    public string? RunnerId { get; set; }
    public ProjectConfiguration? Project { get; set; }
    public ProfileConfiguration? Profile { get; set; }
    public VariableConfiguration? Variable { get; set; }
    public ServiceConfiguration? Service { get; set; }
    public List<Guid>? ProfileIds { get; set; }
    public List<Guid>? DisabledVariableIds { get; set; }
    public List<ProjectTaskConfiguration>? Tasks { get; set; }
    public List<string>? EnvironmentFiles { get; set; }
    public List<EditorTargetConfiguration>? EditorTargets { get; set; }
    public List<ConfigurationRequest>? Changes { get; set; }
}

public sealed record ProjectTaskConfiguration(Guid Id, string Name, ProjectTaskKind Kind, string FileName,
    List<string> Arguments, string WorkingDirectory = ".", List<Guid>? DependsOn = null, int TimeoutSeconds = 600);
public sealed record EditorTargetConfiguration(Guid Id, string Name, string Path = ".", string Profile = "");

public sealed record ProjectConfiguration(string Name, string SourceRoot = "", bool IsFavorite = false, List<string>? Tags = null);
public sealed record ProfileConfiguration(string Name, Guid? ParentId = null);
public sealed record VariableConfiguration(string Name, string? Value = null, bool IsSecret = false, SecretRef? SecretRef = null);
public sealed record ServiceConfiguration(
    string Name, string RunnerId, string WorkingDirectory = "", Dictionary<string, string>? Settings = null,
    List<Guid>? DependsOn = null, List<string>? PreSteps = null, ReadinessCheck? Readiness = null,
    OutputMode OutputMode = OutputMode.Integrated, RestartPolicy RestartPolicy = RestartPolicy.Never,
    int StartDelaySeconds = 0, List<int>? ExpectedPorts = null, string OpenUrl = "");
