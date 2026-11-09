using System.Text;

namespace FastEnvSetter.Core;

public enum SettingKind
{
    Text,
    Choice,
    FilePath,
    FolderPath,
    Toggle,
}

/// <summary>Beschreibt ein Eingabefeld; der Editor erzeugt daraus die Oberfläche.</summary>
public sealed record SettingField(string Key, string Label, SettingKind Kind = SettingKind.Text)
{
    public string Default { get; init; } = "";
    public string? Hint { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Erlaubt bei Choice auch freie Eingaben.</summary>
    public bool AllowCustomValue { get; init; }

    public bool Required { get; init; }

    /// <summary>Feld ist nur sichtbar, wenn das Feld VisibleWhenKey einen dieser Werte hat.</summary>
    public string? VisibleWhenKey { get; init; }
    public IReadOnlyList<string> VisibleWhenValues { get; init; } = [];

    public bool IsVisible(Func<string, string> valueOf) =>
        VisibleWhenKey is null || VisibleWhenValues.Contains(valueOf(VisibleWhenKey));
}

public sealed record DetectionResult
{
    public static readonly DetectionResult Empty = new();

    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Choices { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Unverändert übergebene Argumente; hat Vorrang vor Arguments.</summary>
    public string? RawArguments { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> PrependPath { get; init; } = [];

    /// <summary>Der Prozess ist selbst eine Shell und hält sein Terminal offen.</summary>
    public bool SelfHostedTerminal { get; init; }

    /// <summary>Über cmd.exe, nötig für Befehle wie npm, die keine .exe sind.</summary>
    public static ProcessCommand ViaCmd(string commandLine) =>
        new("cmd.exe", []) { RawArguments = $"/d /s /c \"{commandLine}\"" };

    public static ProcessCommand ViaCmd(string fileName, IEnumerable<string> arguments) =>
        ViaCmd(string.Join(' ', new[] { fileName }.Concat(arguments).Select(Quote)));

    public string ToCommandLine() =>
        Quote(FileName) + " " + (RawArguments ?? string.Join(' ', Arguments.Select(Quote)));

    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '&', '|', '<', '>', '^', '(', ')', '%', '"']) < 0)
            return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}

/// <summary>Eine Startart, z. B. eine Sprache. Neue Runner werden nur per DI registriert.</summary>
public interface IRunner
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Name eines WPF-UI-Symbols.</summary>
    string Icon { get; }

    bool RequiresWorkingDirectory => true;

    IReadOnlyList<SettingField> GetSettingsSchema();
    DetectionResult Detect(string folder) => DetectionResult.Empty;
    IReadOnlyList<string> Validate(ServiceDefinition service) => [];
    bool RequiresTerminal(ServiceDefinition service) => false;
    IReadOnlyList<int> GetExpectedPorts(ServiceDefinition service) => [];
    ProcessCommand BuildStart(ServiceDefinition service);
    ProcessCommand? BuildStop(ServiceDefinition service) => null;
}

public sealed class RunnerRegistry(IEnumerable<IRunner> runners)
{
    public IReadOnlyList<IRunner> All { get; } = runners.ToList();
    public IRunner? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}

public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                    result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            result.Add(current.ToString());
        return result;
    }
}
