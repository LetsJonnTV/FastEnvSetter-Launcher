using FastEnvSetter.Core;

namespace FastEnvSetter.Runners;

public abstract class RunnerBase : IRunner
{
    public const string CustomCommandKey = "customCommand";

    private IReadOnlyList<SettingField>? _schema;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Icon { get; }
    public virtual bool RequiresWorkingDirectory => true;

    protected virtual string CustomCommandHint => "Ersetzt die Einstellungen oben, z. B. für ungewöhnliche Startbefehle.";
    protected virtual string CustomCommandLabel => "Eigener Befehl (optional)";

    protected abstract IEnumerable<SettingField> DefineFields();
    protected abstract ProcessCommand BuildDefault(ServiceDefinition service);
    protected virtual ProcessCommand BuildCustom(ServiceDefinition service, string command) => ProcessCommand.ViaCmd(command);

    public IReadOnlyList<SettingField> GetSettingsSchema() => _schema ??=
    [
        .. DefineFields(),
        new SettingField(CustomCommandKey, CustomCommandLabel) { Hint = CustomCommandHint },
    ];

    public virtual DetectionResult Detect(string folder) => DetectionResult.Empty;
    public virtual bool RequiresTerminal(ServiceDefinition service) => false;
    public virtual ProcessCommand? BuildStop(ServiceDefinition service) => null;
    public virtual IReadOnlyList<int> GetExpectedPorts(ServiceDefinition service) => [];

    public virtual IReadOnlyList<string> Validate(ServiceDefinition service)
    {
        if (HasCustomCommand(service))
            return [];

        return GetSettingsSchema()
            .Where(f => f.Required && f.IsVisible(k => Value(service, k)) && string.IsNullOrWhiteSpace(Value(service, f.Key)))
            .Select(f => $"{DisplayName}: Das Feld „{f.Label}“ fehlt.")
            .ToList();
    }

    public ProcessCommand BuildStart(ServiceDefinition service)
    {
        return HasCustomCommand(service)
            ? BuildCustom(service, Value(service, CustomCommandKey).Trim())
            : BuildDefault(service);
    }

    protected bool HasCustomCommand(ServiceDefinition service) =>
        !string.IsNullOrWhiteSpace(Value(service, CustomCommandKey));

    /// <summary>Der gespeicherte Wert oder der Standardwert des Felds.</summary>
    protected string Value(ServiceDefinition service, string key)
    {
        if (service.Settings.TryGetValue(key, out var value))
            return value;
        return GetSettingsSchema().FirstOrDefault(f => f.Key == key)?.Default ?? "";
    }

    protected bool IsOn(ServiceDefinition service, string key) =>
        string.Equals(Value(service, key), "true", StringComparison.OrdinalIgnoreCase);

    protected IReadOnlyList<string> ExtraArgs(ServiceDefinition service, string key = "args") =>
        CommandLineSplitter.Split(Value(service, key));

    protected static SettingField ArgsField() => new("args", "Zusätzliche Argumente");

    /// <summary>Dateien bis zu einer Tiefe von drei Ebenen, Build- und Abhängigkeitsordner ausgenommen.</summary>
    protected static List<string> FindFiles(string folder, string pattern)
    {
        string[] ignored = ["bin", "obj", "node_modules", ".git", ".venv", "venv"];
        var result = new List<string>();
        Walk(folder, 0);
        return result.Select(f => Path.GetRelativePath(folder, f)).OrderBy(f => f.Count(c => c == '\\')).ThenBy(f => f).ToList();

        void Walk(string current, int depth)
        {
            try
            {
                result.AddRange(Directory.GetFiles(current, pattern));
                if (depth >= 2)
                    return;
                foreach (var directory in Directory.GetDirectories(current))
                {
                    if (!ignored.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                        Walk(directory, depth + 1);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Nicht lesbare Ordner sind für Vorschläge unerheblich.
            }
        }
    }
}
