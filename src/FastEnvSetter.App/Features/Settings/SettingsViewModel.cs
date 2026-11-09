using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public sealed class ThemeService(IEnumerable<IThemeProvider> providers)
{
    private static readonly string[] OverriddenKeys =
    [
        "ApplicationBackgroundBrush", "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush",
        "CardStrokeColorDefaultBrush", "CardBackground", "CardBorderBrush", "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush",
    ];

    private bool _watching;

    public IEnumerable<ProvidedTheme> Themes => providers.SelectMany(p => p.GetThemes());

    public void Apply(string theme)
    {
        var window = Application.Current.MainWindow;
        ClearOverrides();
        SetBackdrop(window, WindowBackdropType.Mica);

        if (Themes.FirstOrDefault(t => t.Id == theme) is { } provided)
        {
            StopWatching(window);
            ApplyProvided(window, provided.Definition);
            return;
        }

        if (theme is "Light" or "Dark")
        {
            StopWatching(window);
            ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica);
            return;
        }

        ApplicationThemeManager.ApplySystemTheme();
        if (window is not null && !_watching)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica);
            _watching = true;
        }
    }

    private void StopWatching(Window? window)
    {
        if (window is not null && _watching)
        {
            SystemThemeWatcher.UnWatch(window);
            _watching = false;
        }
    }

    private static void ApplyProvided(Window? window, ThemeDefinition definition)
    {
        var baseTheme = definition.Base == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
        var hasBackground = definition.Background is not null;
        ApplicationThemeManager.Apply(baseTheme, hasBackground ? WindowBackdropType.None : WindowBackdropType.Mica);

        // Alle Stufen gleich setzen, sonst hellt WPF-UI die Akzentfarbe stark auf.
        var accent = Parse(definition.Accent);
        ApplicationAccentColorManager.Apply(accent, accent, accent, accent);

        Override("ApplicationBackgroundBrush", definition.Background);

        // WPF-UI setzt den Fensterhintergrund beim Anzeigen zurück, daher trägt ihn das Layout.
        Override("ShellBackground", definition.Background);
        Override("CardBackgroundFillColorDefaultBrush", definition.Card);
        Override("CardBackgroundFillColorSecondaryBrush", definition.Card);
        Override("CardBackground", definition.Card);
        Override("CardStrokeColorDefaultBrush", definition.CardStroke);
        Override("CardBorderBrush", definition.CardStroke);
        Override("TextFillColorPrimaryBrush", definition.TextPrimary);
        Override("TextFillColorSecondaryBrush", definition.TextSecondary);

        if (hasBackground)
            SetBackdrop(window, WindowBackdropType.None);
    }

    private static void SetBackdrop(Window? window, WindowBackdropType backdrop)
    {
        if (window is FluentWindow fluent)
        {
            fluent.WindowBackdropType = backdrop;
            if (backdrop == WindowBackdropType.None)
                fluent.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
            else
                fluent.Background = Brushes.Transparent;
        }
    }

    private static void Override(string key, string? color)
    {
        if (color is not null)
        {
            var brush = new SolidColorBrush(Parse(color));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }

    private static void ClearOverrides()
    {
        foreach (var key in OverriddenKeys)
            Application.Current.Resources.Remove(key);

        Application.Current.Resources["ShellBackground"] = Brushes.Transparent;
    }

    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
}

public sealed record ThemeOption(string Id, string Name);

public sealed partial class VaultStatus(ISecretProvider provider, IAppDataStore store) : ObservableObject
{
    public string Name => provider.DisplayName;
    public bool SupportsLogin => provider.SupportsLogin;
    public bool UsesServerUrl => provider.UsesServerUrl;

    public string ServerUrl
    {
        get => store.Data.Settings.VaultwardenUrl;
        set { store.Data.Settings.VaultwardenUrl = value.Trim(); OnPropertyChanged(); store.RequestSave(); }
    }

    [ObservableProperty] private string _status = "Noch nicht geprüft";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _code = "";

    [RelayCommand]
    private async Task CheckAsync()
    {
        Status = "Wird geprüft …";
        try
        {
            Status = await provider.GetStatusAsync();
        }
        catch (SecretProviderException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        Status = "Wird entsperrt …";
        try
        {
            await provider.LoginAsync(Email.Trim(), Password, Code.Trim());
            Status = "Entsperrt";
        }
        catch (SecretProviderException e)
        {
            Status = e.Message;
        }
        finally
        {
            Password = "";
            Code = "";
        }
    }
}

public sealed partial class SettingsViewModel(IAppDataStore store, ThemeService themes, SecretSources sources) : ObservableObject
{
    private static readonly ThemeOption[] BuiltIn =
    [
        new("System", "Wie Windows"),
        new("Light", "Hell"),
        new("Dark", "Dunkel"),
    ];

    public ObservableCollection<ThemeOption> Themes { get; } = new(BuiltIn);

    public void ReloadThemes()
    {
        Themes.Clear();
        foreach (var option in BuiltIn.Concat(themes.Themes.Select(t => new ThemeOption(t.Id, t.Name))))
            Themes.Add(option);
        OnPropertyChanged(nameof(SelectedTheme));
    }

    public IReadOnlyList<VaultStatus> Vaults { get; } = sources.Providers.Select(p => new VaultStatus(p, store)).ToList();

    public string DataPath => store.FilePath;

    public ThemeOption SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.Id == store.Data.Settings.Theme) ?? Themes[0];
        set
        {
            if (value is null || value.Id == store.Data.Settings.Theme)
                return;
            store.Data.Settings.Theme = value.Id;
            store.RequestSave();
            themes.Apply(value.Id);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(store.FilePath)!) { UseShellExecute = true });
}
