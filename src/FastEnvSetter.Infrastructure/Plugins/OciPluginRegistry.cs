using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FastEnvSetter.Infrastructure.Plugins;

public sealed record PluginReference(string Host, string Repository, string Tag)
{
    public string Display => Tag.StartsWith("sha256:", StringComparison.Ordinal) ? $"{Host}/{Repository}@{Tag}" : $"{Host}/{Repository}:{Tag}";

    public static PluginReference? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        var slash = value.IndexOf('/');
        if (slash <= 0)
            return null;

        var host = value[..slash];
        var rest = value[(slash + 1)..];
        var at = rest.IndexOf('@');
        var colon = rest.LastIndexOf(':');
        var (repository, tag) = at > 0 ? (rest[..at], rest[(at + 1)..])
            : colon > 0 ? (rest[..colon], rest[(colon + 1)..])
            : (rest, "latest");
        if (repository.Length == 0 || repository != repository.ToLowerInvariant() || tag.Length == 0)
            return null;
        return new PluginReference(host, repository, tag);
    }
}

public sealed record CatalogEntry(string Name, string Description, string Kind, string Publisher, string Reference);

/// <summary>Lädt Plugin-Pakete aus einer OCI-Registry; öffentliche Pakete in GHCR brauchen kein Konto.</summary>
public sealed class OciPluginRegistry(HttpClient http, string allowedHost = "ghcr.io", string scheme = "https")
{
    public const string LayerMediaType = "application/vnd.fastenvsetter.plugin.layer.v1+zip";
    public const string CatalogUrl = "https://raw.githubusercontent.com/LetsJonnTV/FastEnvSetter-Launcher/master/plugins/catalog.json";
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<CatalogEntry>> LoadCatalogAsync(string url = CatalogUrl, CancellationToken token = default)
    {
        var json = await http.GetStringAsync(url, token);
        return JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions) ?? [];
    }

    /// <summary>Lädt das Paket in eine temporäre Datei und prüft seine Prüfsumme.</summary>
    public async Task<string> DownloadAsync(PluginReference reference, CancellationToken token = default)
    {
        if (!reference.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase))
            throw new PluginException($"Plugins können nur von {allowedHost} geladen werden.");

        var bearer = await GetTokenAsync(reference, token);
        var manifestBytes = await GetManifestAsync(reference, bearer, token);
        var layer = FindLayer(manifestBytes);
        if (layer.Size > PluginManager.MaxPackageBytes)
            throw new PluginException("Das Paket ist zu groß.");
        return await DownloadLayerAsync(reference, layer.Digest, bearer, token);
    }

    private string Base(PluginReference reference) => $"{scheme}://{reference.Host}";

    private async Task<string?> GetTokenAsync(PluginReference reference, CancellationToken token)
    {
        var url = $"{Base(reference)}/token?service={reference.Host}&scope=repository:{reference.Repository}:pull";
        using var response = await http.GetAsync(url, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new PluginException("Das Paket wurde nicht gefunden oder ist nicht öffentlich.");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.TryGetProperty("token", out var value) ? value.GetString() : null;
    }

    private async Task<byte[]> GetManifestAsync(PluginReference reference, string? bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base(reference)}/v2/{reference.Repository}/manifests/{reference.Tag}");
        request.Headers.Accept.ParseAdd(ManifestMediaType);
        Authorize(request, bearer);

        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new PluginException($"Das Paket wurde nicht gefunden ({(int)response.StatusCode}).");

        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (reference.Tag.StartsWith("sha256:", StringComparison.Ordinal) && reference.Tag != digest)
            throw new PluginException("Die Prüfsumme des Manifests stimmt nicht.");
        return bytes;
    }

    private static (string Digest, long Size) FindLayer(byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        if (document.RootElement.TryGetProperty("layers", out var layers))
        {
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.GetProperty("mediaType").GetString() == LayerMediaType)
                    return (layer.GetProperty("digest").GetString()!, layer.GetProperty("size").GetInt64());
            }
        }

        throw new PluginException("Das Paket ist kein FastEnvSetter-Plugin.");
    }

    private async Task<string> DownloadLayerAsync(PluginReference reference, string digest, string? bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base(reference)}/v2/{reference.Repository}/blobs/{digest}");
        Authorize(request, bearer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var path = Path.Combine(Path.GetTempPath(), $"fes-{Guid.NewGuid():N}.fesplugin");
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > PluginManager.MaxPackageBytes)
                        throw new PluginException("Das Paket ist zu groß.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }

            if (PluginManager.HashFile(path) != digest)
                throw new PluginException("Die Prüfsumme des Pakets stimmt nicht.");
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }
}
