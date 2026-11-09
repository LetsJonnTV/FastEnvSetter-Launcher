using System.Windows;

namespace FastEnvSetter.Plugin.Sdk;

/// <summary>Das Hauptfenster der App. Ein Plugin kann es ersetzen, indem es diese Registrierung überschreibt.</summary>
public interface IAppShell
{
    Window Window { get; }

    /// <summary>Wird ausgelöst, wenn das Fenster in den Tray wandert.</summary>
    event Action? HiddenToTray;

    void AllowClose();
    void ShowFromTray();
    Task RequestExitAsync();
}
