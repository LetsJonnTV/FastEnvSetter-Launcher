using Microsoft.Extensions.DependencyInjection;

namespace FastEnvSetter.Core;

/// <summary>Ein Eintrag in der Seitenleiste. PageType ist ein per DI auflösbares WPF-Control.</summary>
public sealed record NavigationEntry(string Title, string Icon, Type PageType, int Order = 100);

/// <summary>Ein Feature: registriert seine Dienste und Seiten beim Start.</summary>
public interface IFeatureModule
{
    void ConfigureServices(IServiceCollection services);
    IEnumerable<NavigationEntry> GetNavigation() => [];
}

public enum AppEventKind
{
    Started,
    Stopped,
    Crashed,
}

public sealed record AppEvent(AppEventKind Kind, Project Project, ServiceDefinition Service, int? ExitCode);

/// <summary>Reagiert auf Ereignisse, z. B. für Benachrichtigungen. Wird im Hintergrund aufgerufen.</summary>
public interface IAppEventHandler
{
    void Handle(AppEvent appEvent);
}

public interface ISecretProtector
{
    string Protect(string plainText);

    /// <returns>Der Klartext oder null, wenn er nicht entschlüsselt werden kann.</returns>
    string? Unprotect(string protectedText);
}

public interface IAppDataStore
{
    AppData Data { get; }
    string FilePath { get; }

    /// <summary>Hinweis für die Oberfläche, falls die Datei beim Laden beschädigt war.</summary>
    string? LoadWarning { get; }

    /// <summary>Speichert kurz verzögert, damit Tippen nicht jeden Tastendruck schreibt.</summary>
    void RequestSave();

    void SaveNow();
}

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
}

public enum LogKind
{
    Output,
    Error,
    System,
}

public sealed record LogEntry(long Sequence, DateTime Timestamp, LogKind Kind, string Text);

public sealed record ServiceStateChange(Guid ProjectId, Guid ServiceId, ServiceState State, string? Message);

public sealed record StartResult(IReadOnlyList<string> Errors)
{
    public static readonly StartResult Ok = new([]);
    public bool Success => Errors.Count == 0;
}

public interface IProcessSupervisor
{
    /// <summary>Wird von Hintergrund-Threads ausgelöst.</summary>
    event Action<ServiceStateChange>? StateChanged;

    ServiceState GetState(Guid serviceId);
    string? GetMessage(Guid serviceId);
    bool IsAnyActive { get; }
    IReadOnlyList<LogEntry> GetLogSince(Guid serviceId, long afterSequence);
    void ClearLog(Guid serviceId);

    Task<StartResult> StartProjectAsync(Project project, EnvProfile? profile, CancellationToken cancellationToken = default);
    Task<StartResult> StartServiceAsync(Project project, ServiceDefinition service, EnvProfile? profile);
    Task StopServiceAsync(Guid serviceId);
    Task StopProjectAsync(Project project);
    Task StopAllAsync();
}
