using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Infrastructure.Plugins;
using FastEnvSetter.Ipc;
using FastEnvSetter.Plugin.Sdk;
using FastEnvSetter.Runners;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public partial class App : Application
{
    private const int AttachParentProcess = -1;
    private const int NotRunningExitCode = 4;
    private static readonly TimeSpan StartupRetryWindow = TimeSpan.FromSeconds(20);

    /// <summary>Neue eingebaute Features werden hier mit einer Zeile ergänzt.</summary>
    private static readonly IFeatureModule[] Modules =
    [
        new ProjectsModule(),
        new EnvProfilesModule(),
        new PluginsModule(),
        new SettingsModule(),
        new TrayModule(),
    ];

    private ServiceProvider? _provider;
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var arguments = e.Args.ToList();
        var dataPath = TakeOption(arguments, "--data");
        var relaunchAfter = TakeOption(arguments, "--relaunch-after");
        var safeMode = arguments.Remove("--safe-mode");
        var tray = arguments.Remove("--tray");
        var noStart = arguments.Remove("--no-start");
        var commandArguments = arguments.ToArray();

        if (commandArguments.FirstOrDefault() is "--version" or "version")
        {
            Print(new CliResponse(0, AppVersion()));
            Shutdown(0);
            return;
        }

        if (int.TryParse(relaunchAfter, out var previous))
            WaitForExit(previous);

        var pipeName = PipeNames.For(dataPath);
        _instance = new SingleInstance(pipeName);
        if (!_instance.IsFirst)
        {
            if (tray)
                Shutdown(0);
            else
                await ForwardAsync(pipeName, commandArguments.Length == 0 ? ["activate"] : commandArguments, StartupRetryWindow);
            return;
        }

        if (CliHandler.IsCommand(commandArguments))
        {
            if (noStart)
            {
                Print(new CliResponse(NotRunningExitCode, "FastEnvSetter läuft nicht."));
                Shutdown(NotRunningExitCode);
                return;
            }

            if (CliHandler.NeedsRunningInstance(commandArguments))
            {
                _instance.Dispose();
                _instance = null;
                StartBackgroundInstance(dataPath, safeMode);
                await ForwardAsync(pipeName, commandArguments, StartupRetryWindow);
                return;
            }
        }

        var store = new JsonAppDataStore(new DpapiSecretProtector(), dataPath);
        var plugins = new PluginManager(store);
        var warnings = new List<string>();
        if (safeMode)
        {
            warnings.Add("Abgesicherter Modus: Es wurden keine Plugins geladen.");
        }
        else if (plugins.BeginBoot() is { } disabled)
        {
            warnings.Add($"Der letzte Start ist fehlgeschlagen. Das Plugin „{disabled}“ wurde deaktiviert.");
        }

        var loaded = safeMode ? new PluginLoadResult([], []) : plugins.LoadCodePlugins();
        warnings.AddRange(loaded.Warnings);
        _provider = BuildServices(store, plugins, loaded.Plugins, warnings);

        DispatcherUnhandledException += (_, args) =>
        {
            _provider.GetRequiredService<INotifier>().Show("Unerwarteter Fehler", args.Exception.Message, InfoBarSeverity.Error);
            args.Handled = true;
        };

        var shellWindow = _provider.GetRequiredService<IAppShell>();
        MainWindow = shellWindow.Window;
        SessionEnding += (_, _) => shellWindow.AllowClose();
        _provider.GetRequiredService<TrayService>().Initialize(shellWindow);
        _provider.GetRequiredService<ThemeService>().Apply(safeMode ? "System" : store.Data.Settings.Theme);

        foreach (var plugin in loaded.Plugins)
            StartPlugin(plugin, warnings);

        var isCommand = CliHandler.IsCommand(commandArguments);
        if (!isCommand && !tray)
            shellWindow.Window.Show();

        var shell = _provider.GetRequiredService<ShellViewModel>();
        shell.Selected = shell.Items.FirstOrDefault();
        if (store.LoadWarning is not null)
            warnings.Add(store.LoadWarning);
        if (warnings.Count > 0)
            shell.Show("Hinweis", string.Join(Environment.NewLine, warnings), InfoBarSeverity.Warning);

        var cli = _provider.GetRequiredService<CliHandler>();
        _instance.Listen(args => Dispatcher.InvokeAsync(() => HandleAsync(cli, shellWindow, args)).Task.Unwrap());

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!safeMode)
            plugins.CompleteBoot();

        if (isCommand)
        {
            var response = await cli.ExecuteAsync(commandArguments);
            Print(response);
            Shutdown(response.Code);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _provider?.GetRequiredService<IAppDataStore>().SaveNow();
        _provider?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static async Task<CliResponse> HandleAsync(CliHandler cli, IAppShell shell, string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "activate")
        {
            shell.ShowFromTray();
            return new CliResponse(0, "");
        }

        return await cli.ExecuteAsync(arguments);
    }

    private void StartPlugin(IFastEnvPlugin plugin, List<string> warnings)
    {
        try
        {
            plugin.OnStarted(_provider!);
        }
        catch (Exception e)
        {
            warnings.Add($"{plugin.GetType().Name}: {e.Message}");
        }
    }

    private static ServiceProvider BuildServices(JsonAppDataStore store, PluginManager plugins, IReadOnlyList<IFastEnvPlugin> loaded, List<string> warnings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IAppDataStore>(store);
        services.AddSingleton(plugins);
        services.AddSingleton<IThemeProvider>(plugins);
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        services.AddSingleton(p => new OciPluginRegistry(p.GetRequiredService<HttpClient>()));
        services.AddSingleton<RunnerRegistry>();
        services.AddBuiltInRunners();
        services.AddSingleton<ISecretProvider>(_ => new AzureKeyVaultProvider());
        services.AddSingleton<ISecretProvider>(_ => new OnePasswordProvider());
        services.AddSingleton<ISecretProvider>(_ => new BitwardenProvider());
        services.AddSingleton<ISecretProvider>(p => new VaultwardenProvider(p.GetRequiredService<IAppDataStore>()));
        services.AddSingleton<SecretSources>();
        services.AddSingleton<IProcessSupervisor, ProcessSupervisor>();
        services.AddSingleton<IPortInspector, PortInspector>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<INavigator>(p => p.GetRequiredService<ShellViewModel>());
        services.AddSingleton<INotifier>(p => p.GetRequiredService<ShellViewModel>());
        services.AddSingleton<ProjectActions>();
        services.AddSingleton<CliHandler>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<PaletteViewModel>();
        services.AddSingleton<ICommandSource, NavigationCommandSource>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IAppShell>(p => p.GetRequiredService<MainWindow>());

        foreach (var module in Modules)
            Register(module, services);

        // Plugins kommen zuletzt: Eine spätere Registrierung ersetzt jede frühere.
        foreach (var plugin in loaded)
        {
            try
            {
                Register(plugin, services);
            }
            catch (Exception e)
            {
                warnings.Add($"{plugin.GetType().Name}: {e.Message}");
            }
        }

        return services.BuildServiceProvider();
    }

    private static void Register(IFeatureModule module, ServiceCollection services)
    {
        module.ConfigureServices(services);
        foreach (var entry in module.GetNavigation())
            services.AddSingleton(entry);
    }

    private static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unbekannt";

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(30000);
        }
        catch (ArgumentException)
        {
            // Der vorherige Prozess ist bereits beendet.
        }
    }

    private static void StartBackgroundInstance(string? dataPath, bool safeMode)
    {
        // Über die Shell starten, damit der Hintergrundprozess keine Ausgabe-Pipes des Aufrufers erbt und ihn blockiert.
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true };
        startInfo.ArgumentList.Add("--tray");
        if (dataPath is not null)
        {
            startInfo.ArgumentList.Add("--data");
            startInfo.ArgumentList.Add(dataPath);
        }

        if (safeMode)
            startInfo.ArgumentList.Add("--safe-mode");
        Process.Start(startInfo)?.Dispose();
    }

    private async Task ForwardAsync(string pipeName, string[] arguments, TimeSpan retryWindow)
    {
        var deadline = DateTime.UtcNow + retryWindow;
        CliResponse response;
        while (true)
        {
            try
            {
                response = await PipeClient.SendAsync(pipeName, arguments, 2000);
                break;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    response = new CliResponse(1, $"Die laufende Instanz ist nicht erreichbar: {e.Message}");
                    break;
                }
            }
        }

        Print(response);
        Shutdown(response.Code);
    }

    private static string? TakeOption(List<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);
        if (index < 0 || index + 1 >= arguments.Count)
            return null;

        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    private static void Print(CliResponse response)
    {
        if (response.Output.Length == 0)
            return;
        AttachConsole(AttachParentProcess);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine(response.Output);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
