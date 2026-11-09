using FastEnvSetter.Core;
using Microsoft.Extensions.DependencyInjection;

namespace SampleRunnerPlugin;

/// <summary>Vorlage für ein Code-Plugin: ergänzt eine neue Startart.</summary>
public sealed class SamplePlugin : IFastEnvPlugin
{
    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IRunner, RubyRunner>();
}

public sealed class RubyRunner : IRunner
{
    public string Id => "sample-ruby";
    public string DisplayName => "Ruby (Beispiel-Plugin)";
    public string Icon => "Code24";

    public IReadOnlyList<SettingField> GetSettingsSchema() =>
    [
        new("script", "Skript") { Required = true, Hint = "z. B. app.rb" },
    ];

    public ProcessCommand BuildStart(ServiceDefinition service) =>
        ProcessCommand.ViaCmd("ruby", [service.Settings.GetValueOrDefault("script", "")]);
}
