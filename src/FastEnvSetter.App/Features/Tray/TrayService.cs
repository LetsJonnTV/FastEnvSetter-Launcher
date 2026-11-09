using System.Drawing;
using FastEnvSetter.Core;
using FastEnvSetter.Plugin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Forms = System.Windows.Forms;

namespace FastEnvSetter.App;

public sealed class TrayModule : IFeatureModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TrayService>();
        services.AddSingleton<IAppEventHandler>(p => p.GetRequiredService<TrayService>());
    }
}

/// <summary>Tray-Icon mit Schnellstart. Dienste werden erst bei Bedarf aufgelöst, weil der Supervisor diesen Handler braucht.</summary>
public sealed class TrayService(IServiceProvider services) : IAppEventHandler, IDisposable
{
    private const int MaxMenuProjects = 12;

    private Forms.NotifyIcon? _icon;

    public void Initialize(IAppShell window)
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "FastEnvSetter",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };

        _icon.ContextMenuStrip.Opening += (_, _) => RebuildMenu(window);
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                window.ShowFromTray();
        };
        window.HiddenToTray += ShowHintOnce;
        RebuildMenu(window);
    }

    public void Handle(AppEvent appEvent)
    {
        if (appEvent.Kind == AppEventKind.Crashed)
            Ui.Post(() => Notify($"{appEvent.Project.Name}: {appEvent.Service.Name}", $"Unerwartet beendet (Exit-Code {appEvent.ExitCode})."));
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }

    private void Notify(string title, string text) => _icon?.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private void ShowHintOnce()
    {
        var store = services.GetRequiredService<IAppDataStore>();
        if (store.Data.Settings.TrayHintShown)
            return;

        store.Data.Settings.TrayHintShown = true;
        store.RequestSave();
        Notify("FastEnvSetter läuft weiter", "Beenden kannst du es über das Symbol im Infobereich.");
    }

    private void RebuildMenu(IAppShell window)
    {
        var store = services.GetRequiredService<IAppDataStore>();
        var actions = services.GetRequiredService<ProjectActions>();
        var menu = _icon!.ContextMenuStrip!;
        menu.Items.Clear();

        var favorites = store.Data.Projects.Where(p => p.IsFavorite).ToList();
        var projects = (favorites.Count > 0 ? favorites : store.Data.Projects).Take(MaxMenuProjects);
        foreach (var project in projects)
        {
            var active = actions.IsActive(project);
            menu.Items.Add($"{(active ? "■ Stoppen" : "▶ Starten")}: {project.Name}", null, async (_, _) =>
            {
                if (active)
                    await actions.StopAsync(project);
                else
                    await actions.StartAsync(project);
            });
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Öffnen", null, (_, _) => window.ShowFromTray());
        menu.Items.Add("Beenden", null, async (_, _) => await window.RequestExitAsync());
    }

    private static Icon LoadIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico")).Stream;
        return new Icon(stream, 32, 32);
    }
}
