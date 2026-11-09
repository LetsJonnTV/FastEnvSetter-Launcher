using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure.Plugins;

public sealed class PluginException(string message) : Exception(message);

/// <summary>Ein geprüftes, noch nicht installiertes Paket.</summary>
public sealed record PluginCandidate(PluginManifest Manifest, string PackagePath, string Digest, string Source);

public sealed record PluginLoadResult(IReadOnlyList<IFastEnvPlugin> Plugins, IReadOnlyList<string> Warnings);

public sealed class PluginManager(IAppDataStore store) : IThemeProvider
{
    public const long MaxPackageBytes = 100 * 1024 * 1024;
    private const long MaxExtractedBytes = 300 * 1024 * 1024;
    private const int MaxEntries = 2000;
    private const int MaxManifestBytes = 64 * 1024;
    private const string ThemeFile = "theme.json";
    private const string BootMarker = ".boot";
    private static readonly string[] StyleExtensions = [".json", ".png", ".md", ".txt"];

    public string Root { get; } = Path.Combine(Path.GetDirectoryName(store.FilePath)!, "plugins");
    public IReadOnlyList<InstalledPlugin> Installed => store.Data.Plugins;

    /// <summary>Prüft eine Paketdatei, ohne etwas zu installieren.</summary>
    public PluginCandidate Inspect(string packagePath, string source)
    {
        if (new FileInfo(packagePath).Length > MaxPackageBytes)
            throw new PluginException("Das Paket ist zu groß.");

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            ValidateEntries(archive);
            var manifest = ReadManifest(archive);
            ValidateContent(archive, manifest);
            return new PluginCandidate(manifest, packagePath, HashFile(packagePath), source);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            throw new PluginException($"Das Paket ist ungültig: {e.Message}");
        }
    }

    public InstalledPlugin Install(PluginCandidate candidate)
    {
        var manifest = candidate.Manifest;
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractSafely(candidate.PackagePath, temporary);
            var hash = HashFolder(temporary);

            var pluginRoot = Path.Combine(Root, manifest.Id);
            if (Directory.Exists(pluginRoot))
                Directory.Delete(pluginRoot, recursive: true);
            Directory.CreateDirectory(pluginRoot);
            Directory.Move(temporary, Path.Combine(pluginRoot, manifest.Version));

            store.Data.Plugins.RemoveAll(p => p.Id == manifest.Id);
            var installed = new InstalledPlugin
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Publisher = manifest.Publisher,
                Kind = manifest.Kind,
                Source = candidate.Source,
                Digest = candidate.Digest,
                ContentHash = hash,
                Enabled = true,
            };
            store.Data.Plugins.Add(installed);
            MarkActivated(installed);
            store.SaveNow();
            return installed;
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        var plugin = store.Data.Plugins.First(p => p.Id == id);
        plugin.Enabled = enabled;
        if (enabled)
            MarkActivated(plugin);
        store.SaveNow();
    }

    public void Uninstall(string id)
    {
        store.Data.Plugins.RemoveAll(p => p.Id == id);
        store.SaveNow();
        var folder = Path.Combine(Root, id);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    public IEnumerable<ProvidedTheme> GetThemes()
    {
        foreach (var plugin in store.Data.Plugins.Where(p => p.Enabled && p.Kind == PluginKinds.Style))
        {
            var definition = TryReadTheme(plugin);
            if (definition is not null)
                yield return new ProvidedTheme("plugin:" + plugin.Id, plugin.Name, definition);
        }
    }

    /// <summary>Lädt alle aktiven Code-Plugins. Ein defektes Plugin verhindert den Start der anderen nicht.</summary>
    public PluginLoadResult LoadCodePlugins()
    {
        var plugins = new List<IFastEnvPlugin>();
        var warnings = new List<string>();
        foreach (var plugin in store.Data.Plugins.Where(p => p.Enabled && p.Kind == PluginKinds.Code))
        {
            try
            {
                plugins.AddRange(Load(plugin));
            }
            catch (Exception e) when (e is PluginException or IOException or BadImageFormatException or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException or JsonException)
            {
                warnings.Add($"Plugin „{plugin.Name}“ wurde nicht geladen: {e.Message}");
            }
        }

        return new PluginLoadResult(plugins, warnings);
    }

    /// <summary>
    /// Merkt sich den Startversuch. Blieb der letzte Start hängen, wird das zuletzt aktivierte Plugin abgeschaltet.
    /// </summary>
    /// <returns>Der Name des deaktivierten Plugins oder null.</returns>
    public string? BeginBoot()
    {
        Directory.CreateDirectory(Root);
        var marker = Path.Combine(Root, BootMarker);
        string? disabled = null;
        if (File.Exists(marker) && store.Data.Settings.LastActivatedPlugin is { } id)
        {
            var plugin = store.Data.Plugins.FirstOrDefault(p => p.Id == id);
            if (plugin is not null)
            {
                plugin.Enabled = false;
                disabled = plugin.Name;
            }

            store.Data.Settings.LastActivatedPlugin = null;
            store.SaveNow();
        }

        File.WriteAllText(marker, "");
        return disabled;
    }

    public void CompleteBoot()
    {
        var marker = Path.Combine(Root, BootMarker);
        if (File.Exists(marker))
            File.Delete(marker);
        if (store.Data.Settings.LastActivatedPlugin is not null)
        {
            store.Data.Settings.LastActivatedPlugin = null;
            store.RequestSave();
        }
    }

    private void MarkActivated(InstalledPlugin plugin)
    {
        if (plugin.Kind == PluginKinds.Code)
            store.Data.Settings.LastActivatedPlugin = plugin.Id;
    }

    private string FolderOf(InstalledPlugin plugin) => Path.Combine(Root, plugin.Id, plugin.Version);

    private ThemeDefinition? TryReadTheme(InstalledPlugin plugin)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(Path.Combine(FolderOf(plugin), ThemeFile)));
            return definition?.IsValid() == true ? definition : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<IFastEnvPlugin> Load(InstalledPlugin plugin)
    {
        var folder = FolderOf(plugin);
        if (!Directory.Exists(folder))
            throw new PluginException("Die Dateien fehlen.");
        if (HashFolder(folder) != plugin.ContentHash)
            throw new PluginException("Die Dateien wurden seit der Installation verändert.");

        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(folder, "plugin.json")))
            ?? throw new PluginException("plugin.json ist leer.");
        if (!PluginApi.IsCompatible(manifest.ApiVersion))
            throw new PluginException($"Es wird API {manifest.ApiVersion} verlangt, die App bietet {PluginApi.Version}.");

        var context = new PluginLoadContext(folder, manifest.Id);
        var assembly = context.LoadPluginAssembly(manifest.EntryAssembly!);
        var plugins = assembly.GetExportedTypes()
            .Where(t => typeof(IFastEnvPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IFastEnvPlugin)Activator.CreateInstance(t)!)
            .ToList();
        return plugins.Count > 0 ? plugins : throw new PluginException("Es enthält keine Klasse mit IFastEnvPlugin.");
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
            throw new PluginException("Das Paket enthält zu viele Dateien.");
        if (archive.Entries.Sum(e => e.Length) > MaxExtractedBytes)
            throw new PluginException("Das entpackte Paket ist zu groß.");

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains(".."))
                throw new PluginException($"Unzulässiger Pfad im Paket: {entry.FullName}");
        }
    }

    private static PluginManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("plugin.json") ?? throw new PluginException("plugin.json fehlt.");
        if (entry.Length > MaxManifestBytes)
            throw new PluginException("plugin.json ist zu groß.");

        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(stream) ?? throw new PluginException("plugin.json ist leer.");

        if (!PluginKinds.IsValidId(manifest.Id))
            throw new PluginException("Die Plugin-ID ist ungültig.");
        if (!PluginKinds.IsValidVersion(manifest.Version))
            throw new PluginException("Die Plugin-Version ist ungültig.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Publisher))
            throw new PluginException("Name oder Herausgeber fehlen.");
        if (manifest.Kind is not (PluginKinds.Style or PluginKinds.Code))
            throw new PluginException($"Unbekannte Plugin-Art „{manifest.Kind}“.");
        if (!PluginApi.IsCompatible(manifest.ApiVersion))
            throw new PluginException($"Das Plugin verlangt API {manifest.ApiVersion}, die App bietet {PluginApi.Version}.");
        return manifest;
    }

    private static void ValidateContent(ZipArchive archive, PluginManifest manifest)
    {
        if (manifest.Kind == PluginKinds.Style)
        {
            var forbidden = archive.Entries.FirstOrDefault(e => e.Name.Length > 0
                && !StyleExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase));
            if (forbidden is not null)
                throw new PluginException($"Ein Style-Plugin darf „{forbidden.Name}“ nicht enthalten.");

            var themeEntry = archive.GetEntry(ThemeFile) ?? throw new PluginException("theme.json fehlt.");
            using var stream = themeEntry.Open();
            if (JsonSerializer.Deserialize<ThemeDefinition>(stream)?.IsValid() != true)
                throw new PluginException("theme.json ist ungültig.");
            return;
        }

        var entry = manifest.EntryAssembly;
        if (entry is null || entry != Path.GetFileName(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || archive.GetEntry(entry) is null)
            throw new PluginException("entryAssembly fehlt oder ist ungültig.");
    }

    private static void ExtractSafely(string packagePath, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(packagePath);
        ValidateEntries(archive);
        foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PluginException($"Unzulässiger Pfad im Paket: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashFolder(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Lädt aus dem Speicher, damit Dateien während des Betriebs nicht gesperrt sind.</summary>
    private sealed class PluginLoadContext(string folder, string name) : AssemblyLoadContext("plugin:" + name)
    {
        private static readonly string[] SharedPrefixes =
            ["FastEnvSetter.Core", "FastEnvSetter.Plugin.Sdk", "Wpf.Ui", "CommunityToolkit.Mvvm", "Microsoft.Extensions.DependencyInjection",
             "Presentation", "WindowsBase", "System.", "Microsoft.Win32"];

        public Assembly LoadPluginAssembly(string fileName) => LoadFromStream(new MemoryStream(File.ReadAllBytes(Path.Combine(folder, fileName))));

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var simple = assemblyName.Name ?? "";
            if (SharedPrefixes.Any(p => simple.StartsWith(p, StringComparison.Ordinal)))
                return null;

            var candidate = Path.Combine(folder, simple + ".dll");
            return File.Exists(candidate) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(candidate))) : null;
        }
    }
}
