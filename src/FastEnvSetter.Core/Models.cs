namespace FastEnvSetter.Core;

public sealed class AppData
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public long Revision { get; set; }
    public List<ConfigurationReceipt> ConfigurationReceipts { get; set; } = [];
    public List<EnvProfile> Profiles { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
    public List<InstalledPlugin> Plugins { get; set; } = [];
}

public sealed class AppSettings
{
    public bool AllowExternalConfigurationChanges { get; set; }
    public bool AllowExternalTaskExecution { get; set; }
    public bool AllowExternalGitChanges { get; set; }

    /// <summary>System, Light oder Dark.</summary>
    public string Theme { get; set; } = "System";
    public string VaultwardenUrl { get; set; } = "";
    public bool TrayHintShown { get; set; }

    /// <summary>Wird nach einem erfolgreichen Start geleert; dient der Absturz-Wiederherstellung.</summary>
    public string? LastActivatedPlugin { get; set; }
}

public sealed class EnvProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Optionales Basisprofil, dessen Variablen geerbt werden.</summary>
    public Guid? ParentId { get; set; }

    public List<EnvVariable> Variables { get; set; } = [];
}

public sealed class EnvVariable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Klartext im Speicher. Geheime Werte werden nie so auf die Platte geschrieben.</summary>
    public string Value { get; set; } = "";

    public bool IsSecret { get; set; }

    /// <summary>Wenn gesetzt, kommt der Wert bei jedem Start aus einem Tresor.</summary>
    public SecretRef? SecretRef { get; set; }

    /// <summary>Verschlüsselte Form eines geheimen Werts in der Datei.</summary>
    public string? ProtectedValue { get; set; }

    /// <summary>Wird beim Laden gesetzt, wenn der geheime Wert nicht entschlüsselt werden konnte.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DecryptionFailed { get; set; }
}

public sealed class SecretRef
{
    public string ProviderId { get; set; } = "";
    public string Reference { get; set; } = "";
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public List<EditorTarget> EditorTargets { get; set; } = [];
    public List<ProjectTask> Tasks { get; set; } = [];
    public List<string> EnvironmentFiles { get; set; } = [];
    public List<DeploymentTarget> DeploymentTargets { get; set; } = [];
    public List<ProjectRelease> Releases { get; set; } = [];

    /// <summary>Nur noch zum Einlesen von Dateien im Format v1.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EnvProfileId { get; set; }

    /// <summary>Gewählte Profile; bei gleichen Namen gewinnt das spätere.</summary>
    public List<Guid> EnvProfileIds { get; set; } = [];

    /// <summary>Abgewählte Variablen; neue Variablen eines Profils sind dadurch automatisch angehakt.</summary>
    public HashSet<Guid> DisabledVariableIds { get; set; } = [];

    public List<ServiceDefinition> Services { get; set; } = [];
    public bool IsFavorite { get; set; }
    public List<string> Tags { get; set; } = [];
}

public enum OutputMode
{
    Integrated,
    Terminal,
}

public enum ReadinessKind
{
    None,
    Port,
    Http,
}

public sealed class ReadinessCheck
{
    public ReadinessKind Kind { get; set; }
    public int Port { get; set; }
    public string Url { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
}

public enum RestartPolicy
{
    Never,
    OnFailure,
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

    /// <summary>Variablen der Projektprofile, die nur für diesen Dienst abgewählt sind.</summary>
    public HashSet<Guid> DisabledVariableIds { get; set; } = [];

    /// <summary>Zusätzliche Variablen nur für diesen Dienst; sie haben Vorrang vor allen Profilen.</summary>
    public List<EnvVariable> ExtraVariables { get; set; } = [];

    /// <summary>Dienste desselben Projekts, die vorher bereit sein müssen.</summary>
    public List<Guid> DependsOn { get; set; } = [];

    public ReadinessCheck Readiness { get; set; } = new();

    /// <summary>Befehle, die vor dem Start nacheinander laufen.</summary>
    public List<string> PreSteps { get; set; } = [];

    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.Never;

    /// <summary>Zusätzlich vor dem Start zu prüfende Ports.</summary>
    public List<int> ExpectedPorts { get; set; } = [];

    /// <summary>Adresse für den Öffnen-Button.</summary>
    public string OpenUrl { get; set; } = "";
}
