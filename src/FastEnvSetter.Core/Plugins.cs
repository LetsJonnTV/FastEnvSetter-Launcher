using System.Text.RegularExpressions;

namespace FastEnvSetter.Core;

/// <summary>Einstiegspunkt eines Code-Plugins. Plugins laufen nach den Modulen der App und dürfen jede Registrierung ersetzen.</summary>
public interface IFastEnvPlugin : IFeatureModule
{
    /// <summary>Wird aufgerufen, nachdem der Dienstanbieter gebaut wurde.</summary>
    void OnStarted(IServiceProvider services) { }
}

public static class PluginApi
{
    public const int Major = 1;
    public const int Minor = 0;
    public const string Version = "1.0";

    public static bool IsCompatible(string? apiVersion)
    {
        var parts = (apiVersion ?? "").Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], out var major) && major == Major
            && int.TryParse(parts[1], out var minor) && minor <= Minor;
    }
}

public static partial class PluginKinds
{
    public const string Style = "style";
    public const string Code = "code";

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);
    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();
}

/// <summary>Inhalt der Datei plugin.json.</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? EntryAssembly { get; set; }

    /// <summary>Angaben des Autors zur Information. Sie sind keine Sandbox.</summary>
    public List<string> Permissions { get; set; } = [];
}

/// <summary>Inhalt der Datei theme.json eines Style-Plugins.</summary>
public sealed partial class ThemeDefinition
{
    public string Base { get; set; } = "Dark";
    public string Accent { get; set; } = "";
    public string? Background { get; set; }
    public string? Card { get; set; }
    public string? CardStroke { get; set; }
    public string? TextPrimary { get; set; }
    public string? TextSecondary { get; set; }

    public bool IsValid() =>
        Base is "Dark" or "Light"
        && ColorPattern().IsMatch(Accent)
        && new[] { Background, Card, CardStroke, TextPrimary, TextSecondary }.All(c => c is null || ColorPattern().IsMatch(c));

    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex ColorPattern();
}

public sealed record ProvidedTheme(string Id, string Name, ThemeDefinition Definition);

/// <summary>Liefert zusätzliche Styles für die Theme-Auswahl.</summary>
public interface IThemeProvider
{
    IEnumerable<ProvidedTheme> GetThemes();
}

public sealed class InstalledPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>SHA-256 des Pakets bei der Installation.</summary>
    public string Digest { get; set; } = "";

    /// <summary>SHA-256 der entpackten Dateien; wird vor dem Laden geprüft.</summary>
    public string ContentHash { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
