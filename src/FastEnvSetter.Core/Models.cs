namespace FastEnvSetter.Core;

public sealed class AppData
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<EnvProfile> Profiles { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>System, Light oder Dark.</summary>
    public string Theme { get; set; } = "System";
}

public sealed class EnvProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<EnvVariable> Variables { get; set; } = [];
}

public sealed class EnvVariable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Klartext im Speicher. Geheime Werte werden nie so auf die Platte geschrieben.</summary>
    public string Value { get; set; } = "";

    public bool IsSecret { get; set; }

    /// <summary>Verschlüsselte Form eines geheimen Werts in der Datei.</summary>
    public string? ProtectedValue { get; set; }

    /// <summary>Wird beim Laden gesetzt, wenn der geheime Wert nicht entschlüsselt werden konnte.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DecryptionFailed { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? EnvProfileId { get; set; }

    /// <summary>Abgewählte Variablen; neue Variablen eines Profils sind dadurch automatisch angehakt.</summary>
    public HashSet<Guid> DisabledVariableIds { get; set; } = [];

    public List<ServiceDefinition> Services { get; set; } = [];
}

public enum OutputMode
{
    Integrated,
    Terminal,
}

public sealed class ServiceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RunnerId { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public OutputMode OutputMode { get; set; } = OutputMode.Integrated;

    /// <summary>Wartezeit, bevor der nächste Dienst der Gruppe startet.</summary>
    public int StartDelaySeconds { get; set; }

    public Dictionary<string, string> Settings { get; set; } = [];
}
