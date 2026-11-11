namespace FastEnvSetter.Core;

public sealed class EditorTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = ".";
    public string Profile { get; set; } = "";
}

public enum ProjectTaskKind { Prepare, Build, Test, Run }

public sealed class ProjectTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProjectTaskKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public List<Guid> DependsOn { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 600;
}

public enum DeploymentProvider { DockerCompose, PodmanCompose, Ssh, Kubernetes }

public sealed class DeploymentTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DeploymentProvider Provider { get; set; }
    public string WorkingDirectory { get; set; } = ".";
    public string FilePath { get; set; } = "";
    public string Context { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Host { get; set; } = "";
    public string RemotePath { get; set; } = "";
}

public sealed class ProjectRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public long ConfigurationRevision { get; set; }
    public Dictionary<string, string> ArtifactDigests { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
