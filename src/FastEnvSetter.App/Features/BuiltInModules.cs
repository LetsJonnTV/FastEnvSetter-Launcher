using FastEnvSetter.Core;
using Microsoft.Extensions.DependencyInjection;

namespace FastEnvSetter.App;

public sealed class ProjectsModule : IFeatureModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<ProjectsPage>();
        services.AddSingleton<ProjectEditorViewModel>();
        services.AddSingleton<ProjectEditorPage>();
        services.AddSingleton<RunningViewModel>();
        services.AddSingleton<RunningPage>();
        services.AddSingleton<ICommandSource, ProjectCommandSource>();
    }

    public IEnumerable<NavigationEntry> GetNavigation() =>
    [
        new("Projekte", "Apps24", typeof(ProjectsPage), 10),
        new("Läuft", "Pulse24", typeof(RunningPage), 20),
    ];
}

public sealed class EnvProfilesModule : IFeatureModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<EnvProfilesViewModel>();
        services.AddSingleton<EnvProfilesPage>();
    }

    public IEnumerable<NavigationEntry> GetNavigation() =>
    [
        new("Env-Profile", "Key24", typeof(EnvProfilesPage), 30),
    ];
}

public sealed class SettingsModule : IFeatureModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();
    }

    public IEnumerable<NavigationEntry> GetNavigation() =>
    [
        new("Einstellungen", "Settings24", typeof(SettingsPage), 90),
    ];
}
