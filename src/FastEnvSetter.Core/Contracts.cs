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

public sealed class SecretProviderException(string message) : Exception(message);

/// <summary>Ein Tresor als Quelle für geheime Variablen. Neue Anbieter werden nur per DI registriert.</summary>
public interface ISecretProvider
{
    string Id { get; }
    string DisplayName { get; }
    string ReferenceHint { get; }
    bool SupportsLogin { get; }
    bool UsesServerUrl { get; }

    Task<string> GetStatusAsync(CancellationToken token = default);
    Task LoginAsync(string email, string password, string code, CancellationToken token = default);

    /// <exception cref="SecretProviderException">Mit einer Meldung für die Oberfläche.</exception>
    Task<string> ResolveAsync(string reference, CancellationToken token = default);
}

/// <summary>Ein Eintrag der Befehlspalette.</summary>
public sealed record PaletteCommand(string Title, string Subtitle, string Icon, Func<Task> Execute);

/// <summary>Module steuern hierüber Befehle zur Palette bei.</summary>
public interface ICommandSource
{
    IEnumerable<PaletteCommand> GetCommands();
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
    Waiting,
    Blocked,
}

public enum LogKind
{
    Output,
    Error,
    System,
}

public sealed record LogEntry(long Sequence, DateTime Timestamp, LogKind Kind, string Text, IReadOnlyList<LogSpan>? Spans = null);

public sealed record ServiceStateChange(Guid ProjectId, Guid ServiceId, ServiceState State, string? Message);

public sealed record StartResult(IReadOnlyList<string> Errors)
{
    public static readonly StartResult Ok = new([]);
    public bool Success => Errors.Count == 0;
}

public sealed record PortOwner(int Port, int ProcessId, string ProcessName);

public interface IPortInspector
{
    /// <returns>Der Prozess, der auf dem Port lauscht, oder null, wenn er frei ist.</returns>
    PortOwner? FindOwner(int port);
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

    Task<StartResult> StartProjectAsync(Project project, IReadOnlyList<EnvProfile> profiles, CancellationToken cancellationToken = default);
    Task<StartResult> StartServiceAsync(Project project, ServiceDefinition service, IReadOnlyList<EnvProfile> profiles);
    Task StopServiceAsync(Guid serviceId);
    Task StopProjectAsync(Project project);
    Task StopAllAsync();
}
