using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public interface INavigator
{
    void Navigate(Type pageType, object? parameter = null);
}

/// <summary>Optional für Seiten, die beim Ein- und Ausblenden reagieren.</summary>
public interface INavigationTarget
{
    void OnNavigatedTo(object? parameter);
    void OnNavigatedFrom() { }
}

public interface INotifier
{
    void Show(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational);
}

public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = Create(title, message);
        box.PrimaryButtonText = confirmText;
        box.IsSecondaryButtonEnabled = false;
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <returns>Das Ergebnis des Dialogs; None bedeutet Abbrechen.</returns>
    public static Task<Wpf.Ui.Controls.MessageBoxResult> ChooseAsync(string title, string message, string primary, string secondary)
    {
        var box = Create(title, message);
        box.PrimaryButtonText = primary;
        box.SecondaryButtonText = secondary;
        return box.ShowDialogAsync();
    }

    private static Wpf.Ui.Controls.MessageBox Create(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Abbrechen",
        };

        // Ohne Besitzer kann der Dialog hinter dem blockierten Hauptfenster verschwinden.
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return box;
    }
}

public static class Ui
{
    public static void Post(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}

public static class StatePresentation
{
    public static readonly Brush Green = Freeze(0x3F, 0xB9, 0x50);
    public static readonly Brush Red = Freeze(0xE5, 0x53, 0x4B);
    public static readonly Brush Amber = Freeze(0xD2, 0x99, 0x22);
    public static readonly Brush Gray = Freeze(0x8B, 0x94, 0x9E);

    public static Brush BrushFor(ServiceState state) => state switch
    {
        ServiceState.Running => Green,
        ServiceState.Failed or ServiceState.Blocked => Red,
        ServiceState.Starting or ServiceState.Stopping or ServiceState.Waiting => Amber,
        _ => Gray,
    };

    public static string TextFor(ServiceState state) => state switch
    {
        ServiceState.Running => "Läuft",
        ServiceState.Failed => "Fehler",
        ServiceState.Starting => "Startet …",
        ServiceState.Stopping => "Stoppt …",
        ServiceState.Waiting => "Wartet …",
        ServiceState.Blocked => "Blockiert",
        _ => "Gestoppt",
    };

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Enum.TryParse<SymbolRegular>(value as string, out var symbol) ? symbol : SymbolRegular.Apps24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class VisibilityConverter : IValueConverter
{
    /// <summary>Kehrt die Logik um.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            int i => i > 0,
            string s => s.Length > 0,
            _ => value is not null,
        };
        return visible != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed partial class ShellViewModel : ObservableObject, INavigator, INotifier
{
    private readonly IServiceProvider _services;
    private bool _syncing;
    private INavigationTarget? _currentTarget;

    public ShellViewModel(IServiceProvider services, IEnumerable<NavigationEntry> entries)
    {
        _services = services;
        Items = new ObservableCollection<NavigationEntry>(entries.OrderBy(e => e.Order));
    }

    public ObservableCollection<NavigationEntry> Items { get; }

    [ObservableProperty] private NavigationEntry? _selected;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private bool _isInfoOpen;
    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private string _infoMessage = "";
    [ObservableProperty] private InfoBarSeverity _infoSeverity;

    partial void OnSelectedChanged(NavigationEntry? value)
    {
        if (value is not null && !_syncing)
            Navigate(value.PageType);
    }

    public void ReselectCurrent()
    {
        if (Selected is not null && CurrentPage?.GetType() != Selected.PageType)
            Navigate(Selected.PageType);
    }

    public void Navigate(Type pageType, object? parameter = null)
    {
        _currentTarget?.OnNavigatedFrom();

        var page = _services.GetRequiredService(pageType);
        var match = Items.FirstOrDefault(e => e.PageType == pageType);
        if (match is not null && !ReferenceEquals(match, Selected))
        {
            _syncing = true;
            Selected = match;
            _syncing = false;
        }

        CurrentPage = page;
        _currentTarget = page as INavigationTarget;
        _currentTarget?.OnNavigatedTo(parameter);
    }

    public void Show(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
    }
}

/// <summary>Start und Stopp eines Projekts, gemeinsam genutzt von allen Seiten.</summary>
public sealed class ProjectActions(
    IAppDataStore store, IProcessSupervisor supervisor, INotifier notifier, RunnerRegistry runners, IPortInspector ports)
{
    /// <summary>Für die Kommandozeile: Bei belegten Ports wird nicht nachgefragt, sondern abgebrochen.</summary>
    public async Task<StartResult> StartHeadlessAsync(Project project)
    {
        store.SaveNow();
        var conflicts = project.Services
            .Where(s => !IsActive(supervisor.GetState(s.Id)))
            .SelectMany(ExpectedPorts)
            .Distinct()
            .Select(ports.FindOwner)
            .OfType<PortOwner>()
            .Select(o => $"Port {o.Port} ist belegt von {o.ProcessName} (PID {o.ProcessId}).")
            .ToList();

        return conflicts.Count > 0
            ? new StartResult(conflicts)
            : await supervisor.StartProjectAsync(project, store.Data.Profiles);
    }

    public async Task StartAsync(Project project)
    {
        store.SaveNow();
        if (project.Services.Count == 0)
        {
            notifier.Show("Nichts zu starten", $"„{project.Name}“ hat noch keinen Dienst.", InfoBarSeverity.Warning);
            return;
        }

        if (await ResolvePortConflictsAsync(project.Services))
            Report(project.Name, await supervisor.StartProjectAsync(project, store.Data.Profiles));
    }

    public async Task StartServiceAsync(Project project, ServiceDefinition service)
    {
        if (await ResolvePortConflictsAsync([service]))
            Report(service.Name, await supervisor.StartServiceAsync(project, service, store.Data.Profiles));
    }

    public Task StopAsync(Project project) => supervisor.StopProjectAsync(project);

    public bool IsActive(Project project) => project.Services.Any(s =>
        IsActive(supervisor.GetState(s.Id)));

    public static bool IsActive(ServiceState state) =>
        state is ServiceState.Waiting or ServiceState.Starting or ServiceState.Running or ServiceState.Stopping;

    public void OpenUrl(string url) => Launch(url, "Adresse", shell: true);
    public void OpenFolder(string folder) => Launch(folder, "Ordner", shell: true);

    public void OpenInVsCode(string folder)
    {
        if (!Directory.Exists(folder))
        {
            notifier.Show("Ordner nicht gefunden", folder, InfoBarSeverity.Warning);
            return;
        }

        // Der VS-Code-Starter ist eine .cmd-Datei und braucht deshalb cmd.exe.
        Launch("cmd.exe", "VS Code", shell: false, ProcessCommand.ViaCmd("code", [folder]).RawArguments);
    }

    private void Launch(string target, string what, bool shell, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = shell, CreateNoWindow = !shell })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            notifier.Show($"{what} ließ sich nicht öffnen", e.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ResolvePortConflictsAsync(IEnumerable<ServiceDefinition> services)
    {
        var expected = services
            .Where(s => !IsActive(supervisor.GetState(s.Id)))
            .SelectMany(ExpectedPorts)
            .Distinct();

        foreach (var port in expected)
        {
            if (ports.FindOwner(port) is not { } owner)
                continue;

            var choice = await Dialogs.ChooseAsync(
                $"Port {port} ist belegt",
                $"Auf Port {port} lauscht bereits „{owner.ProcessName}“ (PID {owner.ProcessId}). Soll der Prozess beendet werden?",
                "Beenden",
                "Ignorieren");

            if (choice == Wpf.Ui.Controls.MessageBoxResult.None)
                return false;
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary && !await KillAsync(owner))
                return false;
        }

        return true;
    }

    private IEnumerable<int> ExpectedPorts(ServiceDefinition service)
    {
        foreach (var port in runners.Find(service.RunnerId)?.GetExpectedPorts(service) ?? [])
            yield return port;
        foreach (var port in service.ExpectedPorts)
            yield return port;
        if (service.Readiness.Kind == ReadinessKind.Port && service.Readiness.Port > 0)
            yield return service.Readiness.Port;
    }

    private async Task<bool> KillAsync(PortOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // Der Prozess ist inzwischen beendet.
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            notifier.Show($"„{owner.ProcessName}“ ließ sich nicht beenden", e.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private void Report(string name, StartResult result)
    {
        if (!result.Success)
            notifier.Show($"„{name}“ konnte nicht vollständig starten", string.Join(Environment.NewLine, result.Errors), InfoBarSeverity.Error);
    }
}
