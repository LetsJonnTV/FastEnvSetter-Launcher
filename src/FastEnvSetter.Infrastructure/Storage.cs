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

public sealed class JsonAppDataStore : IAppDataStore, IConfigurationStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISecretProtector _protector;
    private readonly Lock _saveLock = new();
    private readonly Timer _timer;
    private bool _dirty;
    private bool _disposed;
    private string? _saveWarning;

    public JsonAppDataStore(ISecretProtector protector, string? filePath = null)
    {
        _protector = protector;
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastEnvSetter", "data.json");
        _timer = new Timer(_ => SaveFromTimer());
        try
        {
            Data = Load();
        }
        catch
        {
            _timer.Dispose();
            throw;
        }
    }

    public AppData Data { get; }
    public string FilePath { get; }
    public string? LoadWarning { get; private set; }

    public string? SaveWarning
    {
        get
        {
            lock (_saveLock)
                return _saveWarning;
        }
    }

    public void RequestSave()
    {
        lock (_saveLock)
        {
            if (_disposed)
                return;
            _dirty = true;
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveFromTimer()
    {
        lock (_saveLock)
        {
            if (_disposed || !_dirty)
                return;
            try
            {
                SaveNow();
            }
            catch (Exception)
            {
                _saveWarning = "Automatisches Speichern fehlgeschlagen. Ungespeicherte Aenderungen bleiben im Speicher. Bitte erneut speichern.";
            }
        }
    }

    public void SaveNow()
    {
        lock (_saveLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dirty = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            var candidate = Clone(Data);
            candidate.Revision = checked(Data.Revision + 1);
            WriteSnapshot(candidate);
            Data.Revision = candidate.Revision;
            _dirty = false;
            _saveWarning = null;
        }
    }

    public T ReadConfiguration<T>(Func<AppData, T> read)
    {
        lock (_saveLock)
            return read(Data);
    }

    public T CommitConfiguration<T>(Func<AppData, T> change)
    {
        lock (_saveLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var candidate = Clone(Data);
            var result = change(candidate);
            WriteSnapshot(candidate);
            Data.SchemaVersion = candidate.SchemaVersion;
            Data.Revision = candidate.Revision;
            Data.ConfigurationReceipts = candidate.ConfigurationReceipts;
            Data.Profiles = candidate.Profiles;
            Data.Projects = candidate.Projects;
            Data.Settings = candidate.Settings;
            Data.Plugins = candidate.Plugins;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _dirty = false;
            _saveWarning = null;
            return result;
        }
    }

    private static AppData Clone(AppData source)
    {
        var copy = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(source))!;
        foreach (var (original, variable) in AllVariables(source).Zip(AllVariables(copy)))
            variable.DecryptionFailed = original.DecryptionFailed;
        return copy;
    }

    private void WriteSnapshot(AppData source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var snapshot = Clone(source);
        foreach (var (original, copy) in AllVariables(source).Zip(AllVariables(snapshot)))
            PrepareForDisk(original, copy);
        var temporary = FilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        lock (_saveLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Dispose();
        }
    }

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

        if (data.SchemaVersion > AppData.CurrentSchemaVersion)
            throw new InvalidDataException("Die Datendatei wurde mit einer neueren FastEnvSetter-Version erstellt.");

        if (data.Profiles is null || data.Projects is null || data.Settings is null || data.Plugins is null || data.ConfigurationReceipts is null ||
            data.Profiles.Any(profile => profile is null || profile.Variables is null || profile.Variables.Any(variable => variable is null)) ||
            data.Projects.Any(project => project is null || project.Services is null || project.EnvProfileIds is null || project.DisabledVariableIds is null ||
                project.Services.Any(service => service is null || service.ExtraVariables is null || service.ExtraVariables.Any(variable => variable is null))))
            throw new InvalidDataException("Die Datendatei enthaelt ungueltige Konfigurationslisten.");

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
