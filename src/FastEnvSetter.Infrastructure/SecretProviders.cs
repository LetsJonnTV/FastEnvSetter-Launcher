using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>Grundlage für Tresore, die über ihr Kommandozeilenwerkzeug angesprochen werden.</summary>
public abstract class CliSecretProvider(string cli, string? searchPath) : ISecretProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ReferenceHint { get; }
    public virtual bool SupportsLogin => false;
    public virtual bool UsesServerUrl => false;

    public abstract Task<string> GetStatusAsync(CancellationToken token = default);
    public abstract Task<string> ResolveAsync(string reference, CancellationToken token = default);

    public virtual Task LoginAsync(string email, string password, string code, CancellationToken token = default) =>
        throw new NotSupportedException();

    protected async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null, CancellationToken token = default)
    {
        var executable = Locate() ?? throw new SecretProviderException($"{DisplayName}: Die CLI „{cli}“ ist nicht installiert oder nicht im PATH.");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = executable;
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            // .cmd-Starter laufen über cmd.exe; dort dürfen Verweise keine Sonderzeichen tragen.
            foreach (var argument in arguments)
                EnsureSafe(argument);
            var command = ProcessCommand.ViaCmd(executable, arguments);
            startInfo.FileName = command.FileName;
            startInfo.Arguments = command.RawArguments;
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CliResult(process.ExitCode, (await output).TrimEnd('\r', '\n'), (await error).Trim());
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // Der Prozess ist bereits beendet.
            }

            throw new SecretProviderException($"{DisplayName}: Die CLI hat nicht rechtzeitig geantwortet.");
        }
        catch (Win32Exception e)
        {
            throw new SecretProviderException($"{DisplayName}: Die CLI konnte nicht gestartet werden ({e.Message}).");
        }
    }

    /// <summary>Lehnt Werte ab, die in einer cmd.exe-Zeile mehr als Text wären.</summary>
    protected void EnsureSafe(string value)
    {
        if (value.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0)
            throw new SecretProviderException($"{DisplayName}: Der Verweis enthält unzulässige Zeichen.");
    }

    protected SecretProviderException Failure(string action, CliResult result)
    {
        var detail = result.Error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new SecretProviderException($"{DisplayName}: {action} ist fehlgeschlagen{(detail is null ? "." : $": {detail}")}");
    }

    private string? Locate()
    {
        var paths = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return paths
            .SelectMany(p => new[] { ".exe", ".cmd", ".bat" }.Select(extension => Path.Combine(p.Trim(), cli + extension)))
            .FirstOrDefault(File.Exists);
    }
}

public sealed class AzureKeyVaultProvider(string? searchPath = null) : CliSecretProvider("az", searchPath)
{
    public override string Id => "azure-key-vault";
    public override string DisplayName => "Azure Key Vault";
    public override string ReferenceHint => "https://mein-vault.vault.azure.net/secrets/name";

    public override async Task<string> GetStatusAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["account", "show", "--query", "user.name", "-o", "tsv"], token: token);
        return result.ExitCode == 0 ? $"Angemeldet als {result.Output}" : "Nicht angemeldet. Bitte „az login“ ausführen.";
    }

    public override async Task<string> ResolveAsync(string reference, CancellationToken token = default)
    {
        var result = await RunAsync(["keyvault", "secret", "show", "--id", reference, "--query", "value", "-o", "tsv"], token: token);
        return result.ExitCode == 0 ? result.Output : throw Failure("Das Abrufen", result);
    }
}

public sealed class OnePasswordProvider(string? searchPath = null) : CliSecretProvider("op", searchPath)
{
    public override string Id => "1password";
    public override string DisplayName => "1Password";
    public override string ReferenceHint => "op://Tresor/Eintrag/Feld";

    public override async Task<string> GetStatusAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["whoami"], token: token);
        return result.ExitCode == 0 ? "Angemeldet" : "Nicht angemeldet. Bitte „op signin“ ausführen.";
    }

    public override async Task<string> ResolveAsync(string reference, CancellationToken token = default)
    {
        var result = await RunAsync(["read", "--no-newline", reference], token: token);
        return result.ExitCode == 0 ? result.Output : throw Failure("Das Abrufen", result);
    }
}

/// <summary>Bitwarden-CLI; der Sitzungsschlüssel lebt nur im Speicher dieses Objekts.</summary>
public class BitwardenProvider(string? searchPath = null, string? dataRoot = null) : CliSecretProvider("bw", searchPath)
{
    private const string PasswordVariable = "FES_BW_PASSWORD";

    private readonly string _dataRoot = dataRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastEnvSetter");

    private string? _session;

    public override string Id => "bitwarden";
    public override string DisplayName => "Bitwarden";
    public override string ReferenceHint => "ID oder Name des Eintrags";
    public override bool SupportsLogin => true;

    protected virtual string? ServerUrl => null;

    public override async Task<string> GetStatusAsync(CancellationToken token = default) =>
        await ReadStatusAsync(token) switch
        {
            "unauthenticated" => "Nicht angemeldet",
            "locked" => "Gesperrt",
            "unlocked" => "Entsperrt",
            var other => $"Unbekannter Status: {other}",
        };

    public override async Task LoginAsync(string email, string password, string code, CancellationToken token = default)
    {
        var status = await ReadStatusAsync(token);
        if (status == "unauthenticated" && ServerUrl is { Length: > 0 } server)
        {
            var configured = await RunAsync(["config", "server", server], CliEnvironment(), token);
            if (configured.ExitCode != 0)
                throw Failure("Das Setzen des Servers", configured);
        }

        var environment = new Dictionary<string, string>(CliEnvironment()) { [PasswordVariable] = password };
        List<string> arguments = status == "unauthenticated"
            ? ["login", email, "--passwordenv", PasswordVariable, "--raw"]
            : ["unlock", "--passwordenv", PasswordVariable, "--raw"];
        if (status == "unauthenticated" && code.Length > 0)
            arguments.AddRange(["--method", "0", "--code", code]);

        var result = await RunAsync(arguments, environment, token);
        if (result.ExitCode != 0)
            throw Failure("Das Anmelden", result);

        _session = result.Output;
        await RunAsync(["sync"], CliEnvironment(), token);
    }

    public override async Task<string> ResolveAsync(string reference, CancellationToken token = default)
    {
        if (_session is null)
            throw new SecretProviderException($"{DisplayName} ist gesperrt. Bitte unter „Einstellungen“ entsperren.");

        var result = await RunAsync(["get", "password", reference], CliEnvironment(), token);
        return result.ExitCode == 0 ? result.Output : throw Failure("Das Abrufen", result);
    }

    private async Task<string> ReadStatusAsync(CancellationToken token)
    {
        var result = await RunAsync(["status"], CliEnvironment(), token);
        if (result.ExitCode != 0)
            throw Failure("Die Statusabfrage", result);

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.GetProperty("status").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            throw new SecretProviderException($"{DisplayName}: Die Antwort der CLI ist nicht lesbar.");
        }
    }

    /// <summary>Eigene CLI-Konfiguration pro Anbieter, damit sich Server nicht überschreiben.</summary>
    private IReadOnlyDictionary<string, string> CliEnvironment()
    {
        var environment = new Dictionary<string, string> { ["BITWARDENCLI_APPDATA_DIR"] = Path.Combine(_dataRoot, "cli-" + Id) };
        if (_session is not null)
            environment["BW_SESSION"] = _session;
        return environment;
    }
}

public sealed class VaultwardenProvider(IAppDataStore store, string? searchPath = null, string? dataRoot = null)
    : BitwardenProvider(searchPath, dataRoot)
{
    public override string Id => "vaultwarden";
    public override string DisplayName => "Vaultwarden";
    public override bool UsesServerUrl => true;
    protected override string? ServerUrl => store.Data.Settings.VaultwardenUrl;
}

public static class VaultFetcher
{
    /// <summary>Holt alle Tresorwerte; Werte bleiben im Speicher und werden nie gesichert.</summary>
    public static async Task<(IReadOnlyDictionary<Guid, string> Values, IReadOnlyList<string> Errors)> FetchAsync(
        IEnumerable<EnvVariable> variables, IReadOnlyList<ISecretProvider> providers, CancellationToken token)
    {
        var values = new Dictionary<Guid, string>();
        var errors = new List<string>();
        var cache = new Dictionary<(string, string), string>();

        foreach (var variable in variables.Where(v => v.SecretRef is not null).DistinctBy(v => v.Id))
        {
            var reference = variable.SecretRef!;
            if (cache.TryGetValue((reference.ProviderId, reference.Reference), out var cached))
            {
                values[variable.Id] = cached;
                continue;
            }

            var provider = providers.FirstOrDefault(p => p.Id == reference.ProviderId);
            if (provider is null)
            {
                errors.Add($"„{variable.Name}“: Der Tresor „{reference.ProviderId}“ ist nicht verfügbar.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(reference.Reference))
            {
                errors.Add($"„{variable.Name}“: Es ist kein Tresor-Verweis eingetragen.");
                continue;
            }

            try
            {
                var value = await provider.ResolveAsync(reference.Reference, token);
                cache[(reference.ProviderId, reference.Reference)] = value;
                values[variable.Id] = value;
            }
            catch (SecretProviderException e)
            {
                if (!errors.Contains(e.Message))
                    errors.Add(e.Message);
            }
        }

        return (values, errors);
    }
}
