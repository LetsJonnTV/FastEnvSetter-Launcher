using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FastEnvSetter.Core;
using FastEnvSetter.Plugin.Sdk;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public partial class MainWindow : FluentWindow, IAppShell
{
    private readonly ShellViewModel _shell;
    private readonly IProcessSupervisor _supervisor;
    private readonly PaletteViewModel _palette;
    private bool _allowClose;

    public MainWindow(ShellViewModel shell, IProcessSupervisor supervisor, PaletteViewModel palette)
    {
        _shell = shell;
        _supervisor = supervisor;
        _palette = palette;
        DataContext = shell;
        InitializeComponent();
        PaletteOverlay.DataContext = palette;
        Closing += OnClosing;
        PreviewKeyDown += OnWindowKeyDown;
    }

    /// <summary>Wird ausgelöst, wenn das Fenster-X das Tool in den Tray schickt.</summary>
    public event Action? HiddenToTray;

    public Window Window => this;

    public void AllowClose() => _allowClose = true;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        // Topmost holt das Fenster auch dann nach vorn, wenn Windows den Fokuswechsel sonst unterbindet.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    public async Task RequestExitAsync()
    {
        if (_supervisor.IsAnyActive)
        {
            ShowFromTray();
            if (!await Dialogs.ConfirmAsync("Dienste laufen noch", "Beim Beenden werden alle laufenden Dienste gestoppt.", "Stoppen und beenden"))
                return;
            await _supervisor.StopAllAsync();
        }

        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void OnNavigationClicked(object sender, MouseButtonEventArgs e) => _shell.ReselectCurrent();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _palette.Open();
            Dispatcher.BeginInvoke(() =>
            {
                PaletteInput.Focus();
                Keyboard.Focus(PaletteInput);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private async void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _palette.Close();
                e.Handled = true;
                break;
            case Key.Down:
                _palette.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _palette.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await _palette.ExecuteSelectedAsync();
                break;
        }
    }

    private async void OnPaletteResultClicked(object sender, MouseButtonEventArgs e) => await _palette.ExecuteSelectedAsync();

    private void OnPaletteBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteOverlay))
            _palette.Close();
    }
}
