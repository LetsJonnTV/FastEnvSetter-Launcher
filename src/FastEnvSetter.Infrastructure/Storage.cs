using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plainText)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string? Unprotect(string protectedText)
    {
        try
        {
            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

public sealed class JsonAppDataStore : IAppDataStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISecretProtector _protector;
    private readonly Lock _saveLock = new();
    private readonly Timer _timer;

    public JsonAppDataStore(ISecretProtector protector, string? filePath = null)
    {
        _protector = protector;
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastEnvSetter", "data.json");
        _timer = new Timer(_ => SaveNow());
        Data = Load();
    }

    public AppData Data { get; }
    public string FilePath { get; }
    public string? LoadWarning { get; private set; }

    public void RequestSave() => _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);

    public void SaveNow()
    {
        lock (_saveLock)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Geschrieben wird eine Kopie, damit Klartext-Secrets im Speicher unangetastet bleiben.
            var snapshot = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(Data))!;
            foreach (var (source, copy) in AllVariables(Data).Zip(AllVariables(snapshot)))
                PrepareForDisk(source, copy);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void PrepareForDisk(EnvVariable source, EnvVariable copy)
    {
        if (source.SecretRef is not null)
        {
            copy.Value = "";
            copy.ProtectedValue = null;
        }
        else if (source.IsSecret)
        {
            copy.Value = "";
            copy.ProtectedValue = source.DecryptionFailed ? source.ProtectedValue : _protector.Protect(source.Value);
        }
        else
        {
            copy.ProtectedValue = null;
        }
    }

    private static IEnumerable<EnvVariable> AllVariables(AppData data) =>
        data.Profiles.SelectMany(p => p.Variables)
            .Concat(data.Projects.SelectMany(p => p.Services).SelectMany(s => s.ExtraVariables));

    private AppData Load()
    {
        if (!File.Exists(FilePath))
            return new AppData();

        AppData data;
        try
        {
            data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath)) ?? new AppData();
        }
        catch (JsonException)
        {
            var backup = $"{FilePath}.defekt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, backup);
            LoadWarning = $"Die Datendatei war beschädigt. Eine Kopie liegt unter {backup}.";
            return new AppData();
        }

        if (data.SchemaVersion < AppData.CurrentSchemaVersion)
        {
            var backup = $"{FilePath}.v{data.SchemaVersion}.bak";
            if (!File.Exists(backup))
                File.Copy(FilePath, backup);
        }

        foreach (var variable in AllVariables(data).Where(v => v.IsSecret && v.SecretRef is null))
        {
            var plain = variable.ProtectedValue is null ? null : _protector.Unprotect(variable.ProtectedValue);
            variable.Value = plain ?? "";
            variable.DecryptionFailed = variable.ProtectedValue is not null && plain is null;
        }

        return Migrate(data);
    }

    /// <summary>Spätere Formatänderungen werden hier schrittweise nachgezogen.</summary>
    private static AppData Migrate(AppData data)
    {
        if (data.SchemaVersion < 2)
        {
            foreach (var project in data.Projects)
            {
                if (project.EnvProfileId is { } id && !project.EnvProfileIds.Contains(id))
                    project.EnvProfileIds.Add(id);
                project.EnvProfileId = null;
            }
        }

        data.SchemaVersion = AppData.CurrentSchemaVersion;
        return data;
    }
}
