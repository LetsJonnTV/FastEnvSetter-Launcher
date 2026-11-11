using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class GitHostingService : IGitHostingReader, IGitHostingPublisher, IDisposable
{
    private readonly GitHostingProvider provider;
    private readonly Uri apiBaseUri;
    private readonly Uri webBaseUri;
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;
    private readonly Func<Uri, CancellationToken, ValueTask<GitHostCredential?>>? credentials;
    private const int PageSize = 100;
    private const int MaximumPages = 100;

    public GitHostingService(GitHostingProvider provider, Uri apiBaseUri, Uri webBaseUri,
        Func<Uri, CancellationToken, ValueTask<GitHostCredential?>>? credentials = null, HttpMessageHandler? handler = null,
        TimeSpan? requestTimeout = null)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        ValidateUri(apiBaseUri);
        ValidateUri(webBaseUri);
        if (!apiBaseUri.AbsolutePath.EndsWith('/') || !webBaseUri.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("API and web base URIs must end with a slash.");
        this.provider = provider;
        this.apiBaseUri = apiBaseUri;
        this.webBaseUri = webBaseUri;
        this.credentials = credentials;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        DisableRedirects(handler);
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ImmutableArray<GitHostedRepository>> GetRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var records = await PagesAsync(provider == GitHostingProvider.GitHub ? "user/repos" : "projects?membership=true", cancellationToken);
        return [.. records.Select(Repository)];
    }

    public async Task<GitHostedRepository> GetRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, ProjectRoute(repositoryPath), null, cancellationToken);
        return Repository(document.RootElement);
    }

    public async Task<ImmutableArray<GitHostedRelease>> GetReleasesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var records = await PagesAsync(ProjectRoute(repositoryPath) + "/releases", cancellationToken);
        return [.. records.Select(Release)];
    }

    public Uri GetChangeRequestUri(string repositoryPath, int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
        var path = RepositoryPath(repositoryPath);
        return new(webBaseUri, path + (provider == GitHostingProvider.GitHub ? "/pull/" : "/-/merge_requests/") + number);
    }

    public async Task<GitHostedRelease> PublishReleaseAsync(string repositoryPath, VersionRelease release,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(release.CommitSha, "\\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\\z") ||
            !Regex.IsMatch(release.Version, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z") || release.ConfigurationRevision < 1 ||
            release.ArtifactDigests.Count == 0 || release.ArtifactDigests.Any(pair => !Regex.IsMatch(pair.Value, "\\Asha256:[a-fA-F0-9]{64}\\z")))
            throw new ArgumentException("Release must bind a full SHA, configuration revision, safe tag and SHA-256 digests.");
        var description = JsonSerializer.Serialize(new
        {
            release.Id,
            release.ProjectId,
            release.CommitSha,
            release.ConfigurationRevision,
            release.ConfigurationHash,
            release.ArtifactDigests
        });
        object tagPayload = provider == GitHostingProvider.GitHub
            ? new { @ref = "refs/tags/" + release.Version, sha = release.CommitSha }
            : new { tag_name = release.Version, @ref = release.CommitSha };
        var tagRoute = ProjectRoute(repositoryPath) + (provider == GitHostingProvider.GitHub ? "/git/refs" : "/repository/tags");
        using (var tag = await SendAsync(HttpMethod.Post, tagRoute, tagPayload, cancellationToken))
        {
            var sha = provider == GitHostingProvider.GitHub ? tag.RootElement.GetProperty("object").GetProperty("sha").GetString()
                : tag.RootElement.GetProperty("commit").GetProperty("id").GetString();
            if (!string.Equals(sha, release.CommitSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hosting API created a tag at an unexpected commit; release was not published.");
        }
        object payload = provider == GitHostingProvider.GitHub
            ? new { tag_name = release.Version, target_commitish = release.CommitSha, name = release.Version, body = description, draft = false }
            : new { tag_name = release.Version, @ref = release.CommitSha, name = release.Version, description };
        using var document = await SendAsync(HttpMethod.Post, ProjectRoute(repositoryPath) + "/releases", payload, cancellationToken);
        return Release(document.RootElement);
    }

    private async Task<ImmutableArray<JsonElement>> PagesAsync(string route, CancellationToken cancellationToken)
    {
        var records = ImmutableArray.CreateBuilder<JsonElement>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            var pagination = $"per_page={PageSize}&page={page}";
            using var document = await SendAsync(HttpMethod.Get, route + (route.Contains('?') ? "&" : "?") + pagination, null, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Hosting API returned an invalid collection.");
            var count = document.RootElement.GetArrayLength();
            if (count > PageSize) throw new InvalidDataException("Hosting API exceeded the requested page size.");
            foreach (var item in document.RootElement.EnumerateArray()) records.Add(item.Clone());
            if (count < PageSize) return records.ToImmutable();
        }
        throw new InvalidOperationException("Hosting API pagination limit exceeded; results are not silently truncated.");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string route, object? payload, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        var requestToken = deadline.Token;
        var uri = new Uri(apiBaseUri, route);
        if (uri.GetLeftPart(UriPartial.Authority) != apiBaseUri.GetLeftPart(UriPartial.Authority) ||
            !uri.AbsolutePath.StartsWith(apiBaseUri.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Request escaped the configured hosting API.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("FastEnvSetter/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            if (credentials is not null)
            {
                var origin = new Uri(apiBaseUri.GetLeftPart(UriPartial.Authority));
                var credential = await credentials(origin, requestToken);
                if (credential is not null)
                {
                    ValidateUri(credential.Origin);
                    if (credential.Origin.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) ||
                        string.IsNullOrWhiteSpace(credential.Token) || credential.Token.Any(character => character <= ' ' || character >= 127))
                        throw new InvalidOperationException("Credential is invalid or scoped to another host.");
                    if (provider == GitHostingProvider.GitHub) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
                    else request.Headers.Add("PRIVATE-TOKEN", credential.Token);
                }
            }
            if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidOperationException("Hosting API redirects are not followed.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Hosting API returned HTTP {(int)response.StatusCode}; response details are withheld.");
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, requestToken)) > 0)
            {
                if (buffer.Length + count > 2_097_152) throw new InvalidDataException("Hosting API response exceeded 2 MiB.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), requestToken);
            }
            try { return JsonDocument.Parse(buffer.ToArray()); }
            catch (JsonException) { throw new InvalidDataException("Hosting API returned invalid JSON."); }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Hosting API request was cancelled.", cancellationToken);
            throw new TimeoutException("Hosting API request timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && exception is not InvalidDataException)
        {
            throw new InvalidOperationException("Hosting API transport failed; credential-bearing diagnostics are withheld.");
        }
    }

    private string ProjectRoute(string path) => provider == GitHostingProvider.GitHub
        ? "repos/" + RepositoryPath(path) : "projects/" + Uri.EscapeDataString(Uri.UnescapeDataString(RepositoryPath(path)));

    private string RepositoryPath(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 2 || (provider == GitHostingProvider.GitHub && segments.Length != 2) ||
            segments.Any(segment => !Regex.IsMatch(segment, "\\A[A-Za-z0-9_][A-Za-z0-9_.-]*\\z") || segment is "." or ".."))
            throw new ArgumentException("Use an owner/repository path (GitLab subgroups are supported).", nameof(path));
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private GitHostedRepository Repository(JsonElement record) => new(
        record.GetProperty("id").ToString(), record.GetProperty("name").GetString() ?? "",
        record.GetProperty(provider == GitHostingProvider.GitHub ? "full_name" : "path_with_namespace").GetString() ?? "",
        SafeWebUri(record.GetProperty(provider == GitHostingProvider.GitHub ? "html_url" : "web_url").GetString()));

    private GitHostedRelease Release(JsonElement record) => new(
        record.TryGetProperty("id", out var id) ? id.ToString() : record.GetProperty("tag_name").GetString() ?? "",
        record.GetProperty("tag_name").GetString() ?? "", record.GetProperty("name").GetString() ?? "",
        SafeWebUri(provider == GitHostingProvider.GitHub ? record.GetProperty("html_url").GetString()
            : record.GetProperty("_links").GetProperty("self").GetString()));

    private Uri SafeWebUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidDataException("Hosting API returned an invalid link.");
        ValidateUri(uri);
        if (uri.GetLeftPart(UriPartial.Authority) != webBaseUri.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("Hosting API returned a link to another host.");
        return uri;
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute HTTPS URI without credentials, query or fragment.");
    }

    private static void DisableRedirects(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case HttpClientHandler native:
                native.AllowAutoRedirect = false;
                native.UseCookies = false;
                break;
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                sockets.UseCookies = false;
                break;
            case DelegatingHandler delegating when delegating.InnerHandler is not null:
                DisableRedirects(delegating.InnerHandler);
                break;
        }
    }

    public void Dispose() => client.Dispose();
}
