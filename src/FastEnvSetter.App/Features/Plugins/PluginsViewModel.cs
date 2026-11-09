using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure.Plugins;
using FastEnvSetter.Plugin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public sealed class PluginsModule : IFeatureModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<PluginsPage>();
    }

    public IEnumerable<NavigationEntry> GetNavigation() =>
    [
        new("Plugins", "PuzzlePiece24", typeof(PluginsPage), 40),
    ];
}

public sealed partial class PluginRow(InstalledPlugin plugin, Action<PluginRow, bool> toggled) : ObservableObject
{
    public InstalledPlugin Plugin => plugin;
    public string Name => plugin.Name;
    public string Detail => $"{plugin.Version} · {plugin.Publisher} · {(plugin.Kind == PluginKinds.Style ? "Style" : "Code")}";
    public string Source => plugin.Source;

    public bool IsEnabled
    {
        get => plugin.Enabled;
        set
        {
            if (plugin.Enabled == value)
                return;
            toggled(this, value);
            OnPropertyChanged();
        }
    }
}

public sealed partial class PluginsViewModel(
    PluginManager manager, OciPluginRegistry registry, INotifier notifier, ThemeService themes, IAppDataStore store, IServiceProvider services) : ObservableObject
{
    public ObservableCollection<PluginRow> Installed { get; } = [];
    public ObservableCollection<CatalogEntry> Catalog { get; } = [];

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _catalogStatus = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _restartRequired;

    public bool HasNoPlugins => Installed.Count == 0;

    public void Reload()
    {
        Installed.Clear();
        foreach (var plugin in manager.Installed)
            Installed.Add(new PluginRow(plugin, Toggle));
        OnPropertyChanged(nameof(HasNoPlugins));
        _ = LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync()
    {
        CatalogStatus = "Katalog wird geladen …";
        try
        {
            var entries = await registry.LoadCatalogAsync();
            Catalog.Clear();
            foreach (var entry in entries)
                Catalog.Add(entry);
            CatalogStatus = entries.Count == 0 ? "Der Katalog ist leer." : "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CatalogStatus = "Der Katalog ist gerade nicht erreichbar.";
        }
    }

    private void Toggle(PluginRow row, bool enabled)
    {
        manager.SetEnabled(row.Plugin.Id, enabled);
        AfterChange(row.Plugin);
    }

    private void AfterChange(InstalledPlugin plugin)
    {
        if (plugin.Kind == PluginKinds.Code)
        {
            RestartRequired = true;
            return;
        }

        var themeId = "plugin:" + plugin.Id;
        if (store.Data.Settings.Theme == themeId && !manager.GetThemes().Any(t => t.Id == themeId))
        {
            store.Data.Settings.Theme = "System";
            store.RequestSave();
            themes.Apply("System");
        }
    }

    [RelayCommand]
    private Task InstallFromAddressAsync() => InstallReferenceAsync(Address);

    [RelayCommand]
    private Task InstallFromCatalogAsync(CatalogEntry entry) => InstallReferenceAsync(entry.Reference);

    private async Task InstallReferenceAsync(string text)
    {
        var reference = PluginReference.Parse(text);
        if (reference is null)
        {
            notifier.Show("Ungültige Adresse", "Erwartet wird z. B. ghcr.io/besitzer/plugin:1.0.0.", InfoBarSeverity.Warning);
            return;
        }

        await RunInstallAsync(async () => (await registry.DownloadAsync(reference), reference.Display, true));
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "FastEnvSetter-Plugin|*.fesplugin|Alle Dateien|*.*" };
        if (dialog.ShowDialog() == true)
            await RunInstallAsync(() => Task.FromResult((dialog.FileName, "Datei: " + Path.GetFileName(dialog.FileName), false)));
    }

    private async Task RunInstallAsync(Func<Task<(string Path, string Source, bool Temporary)>> fetch)
    {
        IsBusy = true;
        string? temporary = null;
        try
        {
            var (path, source, isTemporary) = await fetch();
            temporary = isTemporary ? path : null;
            var candidate = manager.Inspect(path, source);
            if (await ConfirmAsync(candidate))
            {
                var installed = manager.Install(candidate);
                Reload();
                AfterChange(installed);
                notifier.Show("Plugin installiert", installed.Kind == PluginKinds.Style ? $"„{installed.Name}“ steht unter Einstellungen → Darstellung bereit." : $"„{installed.Name}“ wird nach einem Neustart aktiv.", InfoBarSeverity.Success);
            }
        }
        catch (Exception e) when (e is PluginException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notifier.Show("Installation fehlgeschlagen", e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
            IsBusy = false;
        }
    }

    private static Task<bool> ConfirmAsync(PluginCandidate candidate)
    {
        var manifest = candidate.Manifest;
        if (manifest.Kind == PluginKinds.Style)
            return Task.FromResult(true);

        var permissions = manifest.Permissions.Count > 0 ? string.Join(", ", manifest.Permissions) : "keine Angabe";
        return Dialogs.ConfirmAsync(
            "Code-Plugin installieren?",
            $"{manifest.Name} {manifest.Version}\nHerausgeber: {manifest.Publisher} (nicht verifiziert)\nQuelle: {candidate.Source}\nPrüfsumme: {candidate.Digest}\nAngaben: {permissions}\n\nDieses Plugin läuft mit deinen Benutzerrechten und kann auf deine Dateien und Secrets zugreifen. Installiere es nur, wenn du dem Herausgeber vertraust.",
            "Installieren");
    }

    [RelayCommand]
    private async Task RemoveAsync(PluginRow row)
    {
        if (!await Dialogs.ConfirmAsync("Plugin entfernen", $"„{row.Name}“ wirklich entfernen?", "Entfernen"))
            return;

        var plugin = row.Plugin;
        manager.Uninstall(plugin.Id);
        Reload();
        plugin.Enabled = false;
        AfterChange(plugin);
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--relaunch-after");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        var dataIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--data");
        if (dataIndex >= 0)
        {
            startInfo.ArgumentList.Add("--data");
            startInfo.ArgumentList.Add(store.FilePath);
        }

        Process.Start(startInfo)?.Dispose();
        await services.GetRequiredService<IAppShell>().RequestExitAsync();
    }
}
