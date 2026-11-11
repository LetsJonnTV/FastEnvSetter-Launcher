using System.Net;
using System.Text;
using System.Text.Json;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class GitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fes-git-" + Guid.NewGuid().ToString("N"));
    private readonly GitNativeCommandExecutor executor = new();

    public GitTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Porcelain_rename_consumes_original_path_and_preserves_unusual_names()
    {
        var status = GitService.ParseStatus("R  new name\n.txt\0old name.txt\0?? other.txt\0");
        Assert.Equal(2, status.Count);
        Assert.Equal("new name\n.txt", status[0].Path);
        Assert.Equal("old name.txt", status[0].OriginalPath);
        Assert.Equal("other.txt", status[1].Path);
        Assert.Throws<FormatException>(() => GitService.ParseStatus("R  missing\0"));
    }

    [Fact]
    public async Task Reviewed_commit_rejects_stale_state_and_commits_only_staged_tree()
    {
        var git = await InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(root, "other.txt"), "not staged");
        await git.StageAsync(["selected.txt"]);
        var review = await git.ReviewCommitAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.CommitAsync(review, "stale"));
        var fresh = await git.ReviewCommitAsync();
        var sha = await git.CommitAsync(fresh, "reviewed");
        Assert.Equal(sha, await git.GetSourceShaAsync());
        Assert.Equal("first", await RunAsync("show", "HEAD:selected.txt"));
        Assert.Equal(2, (await git.GetStatusAsync()).Count);
        Assert.Single(await git.GetHistoryAsync());
        Assert.Single(await git.GetBranchesAsync());
        await git.StageAsync(["selected.txt"]);
        await git.UnstageAsync(["selected.txt"]);
        Assert.Empty(await git.GetDiffAsync(true));
        await Assert.ThrowsAsync<ArgumentException>(() => git.StageAsync(["../outside"]));
        Directory.CreateDirectory(Path.Combine(root, "child"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitService.OpenAsync(Path.Combine(root, "child")));
    }

    private async Task<GitService> InitializeAsync()
    {
        await RunAsync("init", "--initial-branch=main");
        await RunAsync("config", "user.name", "Test User");
        await RunAsync("config", "user.email", "test@example.invalid");
        return await GitService.OpenAsync(root);
    }

    [Fact]
    public async Task Bare_remote_fetch_push_and_fast_forward_pull_use_only_temporary_repositories()
    {
        var git = await InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "first");
        await git.StageAsync(["file.txt"]);
        await git.CommitAsync(await git.ReviewCommitAsync(), "initial");
        var remote = Path.Combine(root, ".git", "remote.git");
        await RunAsync("init", "--bare", remote);
        await RunAsync("remote", "add", "origin", remote);
        await git.PushAsync();
        await git.FetchAsync();
        Assert.Contains(await git.GetBranchesAsync(), branch => branch.IsRemote && branch.Name == "refs/remotes/origin/main");
        var clone = Path.Combine(root, ".git", "clone");
        await RunAsync("clone", "--branch", "main", remote, clone);
        foreach (var arguments in new[]
        {
            new[] { "config", "user.name", "Other" }, new[] { "config", "user.email", "other@example.invalid" }
        })
            Assert.Equal(0, (await executor.ExecuteAsync(new("git", clone, arguments))).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(clone, "file.txt"), "second");
        var other = await GitService.OpenAsync(clone);
        await other.StageAsync(["file.txt"]);
        await other.CommitAsync(await other.ReviewCommitAsync(), "remote update");
        await other.PushAsync();
        await git.PullAsync();
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(root, "file.txt")));
        await RunAsync("remote", "set-url", "--push", "origin", "https://user:token@example.invalid/repo");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.PushAsync());
    }

    [Fact]
    public async Task Linked_worktree_and_detached_head_keep_distinct_repository_identity()
    {
        var git = await InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "first");
        await git.StageAsync(["file.txt"]);
        var sha = await git.CommitAsync(await git.ReviewCommitAsync(), "initial");
        var worktree = Path.Combine(root, "linked");
        await RunAsync("worktree", "add", "--detach", worktree, sha);
        var linked = await GitService.OpenAsync(worktree);
        Assert.NotEqual(git.Repository.GitDirectory, linked.Repository.GitDirectory);
        Assert.Equal(git.Repository.CommonDirectory, linked.Repository.CommonDirectory);
        await File.WriteAllTextAsync(Path.Combine(worktree, "file.txt"), "detached");
        await linked.StageAsync(["file.txt"]);
        var review = await linked.ReviewCommitAsync();
        Assert.Equal("HEAD", review.HeadReference);
        await linked.CommitAsync(review, "detached commit");
        Assert.Equal(sha, await git.GetSourceShaAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => linked.PushAsync());
    }

    [Fact]
    public async Task Output_is_bounded_and_cancelled_instead_of_truncated()
    {
        var bounded = new GitNativeCommandExecutor(1, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounded.ExecuteAsync(new("git", root, ["--version"])));
    }

    [Theory]
    [InlineData(GitHostingProvider.GitHub)]
    [InlineData(GitHostingProvider.GitLab)]
    public async Task Hosting_paginates_repositories_reads_releases_and_scopes_credentials(GitHostingProvider provider)
    {
        var calls = 0;
        var handler = new HostingHandler(async request =>
        {
            calls++;
            Assert.Equal("api.example.invalid", request.RequestUri!.Host);
            if (provider == GitHostingProvider.GitHub) Assert.Equal("fake-test-credential", request.Headers.Authorization!.Parameter);
            else Assert.Equal("fake-test-credential", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            await Task.CompletedTask;
            if (request.RequestUri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                return Json("[" + ReleaseJson(provider) + "]");
            if (request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)) return Json("[]");
            if (!request.RequestUri.Query.Contains("per_page", StringComparison.Ordinal)) return Json(RepositoryJson(provider));
            return Json("[" + string.Join(',', Enumerable.Repeat(RepositoryJson(provider), 100)) + "]");
        });
        using var service = new GitHostingService(provider, new("https://api.example.invalid/api/"), new("https://web.example.invalid/"),
            (origin, _) => ValueTask.FromResult<GitHostCredential?>(new(origin, "fake-test-credential")), handler);
        Assert.Equal(100, (await service.GetRepositoriesAsync()).Length);
        Assert.Equal("owner/repo", (await service.GetRepositoryAsync("owner/repo")).Path);
        Assert.Single(await service.GetReleasesAsync("owner/repo"));
        Assert.Equal(provider == GitHostingProvider.GitHub ? "https://web.example.invalid/owner/repo/pull/42"
            : "https://web.example.invalid/owner/repo/-/merge_requests/42", service.GetChangeRequestUri("owner/repo", 42).AbsoluteUri);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Hosting_rejects_redirects_cross_host_credentials_and_url_secrets()
    {
        var calls = 0;
        var handler = new HostingHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://untrusted.invalid/token-collector");
            return Task.FromResult(response);
        });
        using var service = new GitHostingService(GitHostingProvider.GitHub, new("https://api.example.invalid/"), new("https://web.example.invalid/"), handler: handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRepositoriesAsync());
        Assert.Equal(1, calls);
        using var wrongCredential = new GitHostingService(GitHostingProvider.GitHub, new("https://api.example.invalid/"), new("https://web.example.invalid/"),
            (_, _) => ValueTask.FromResult<GitHostCredential?>(new(new("https://other.invalid/"), "fake-test-credential")), new HostingHandler(_ => throw new InvalidOperationException("must not send")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongCredential.GetRepositoriesAsync());
        Assert.Throws<ArgumentException>(() => new GitHostingService(GitHostingProvider.GitHub, new("https://user:token@api.example.invalid/"), new("https://web.example.invalid/")));
        Assert.Throws<ArgumentException>(() => service.GetChangeRequestUri("../repo", 1));
    }

    [Theory]
    [InlineData(GitHostingProvider.GitHub)]
    [InlineData(GitHostingProvider.GitLab)]
    public async Task Hosting_release_publish_binds_sha_and_configuration_without_real_http(GitHostingProvider provider)
    {
        var project = new Project { Name = "Release" };
        var snapshot = VersionService.Capture(project, 1, DateTimeOffset.UtcNow);
        var release = VersionService.CreateRelease(project.Id, "v1.0", new string('a', 40), snapshot,
            new Dictionary<string, string> { ["app"] = "sha256:" + new string('b', 64) });
        var handler = new HostingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains(release.CommitSha, body);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                return Json(provider == GitHostingProvider.GitHub ? "{\"object\":{\"sha\":\"" + release.CommitSha + "\"}}"
                    : "{\"commit\":{\"id\":\"" + release.CommitSha + "\"}}");
            Assert.Contains(snapshot.ContentHash, body);
            Assert.Contains("ConfigurationRevision", body);
            return Json(ReleaseJson(provider));
        });
        using var service = new GitHostingService(provider, new("https://api.example.invalid/"), new("https://web.example.invalid/"), handler: handler);
        Assert.Equal("v1.0", (await service.PublishReleaseAsync("owner/repo", release)).Tag);
    }

    [Fact]
    public async Task Hosting_release_publish_refuses_an_existing_or_wrongly_targeted_tag()
    {
        var project = new Project();
        var release = VersionService.CreateRelease(project.Id, "v1", new string('a', 40), VersionService.Capture(project, 1, DateTimeOffset.UtcNow),
            new Dictionary<string, string> { ["app"] = "sha256:" + new string('b', 64) });
        var calls = 0;
        using var service = new GitHostingService(GitHostingProvider.GitHub, new("https://api.example.invalid/"), new("https://web.example.invalid/"),
            handler: new HostingHandler(_ => { calls++; return Task.FromResult(Json("{\"object\":{\"sha\":\"" + new string('c', 40) + "\"}}")); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PublishReleaseAsync("owner/repo", release));
        Assert.Equal(1, calls);
    }

    private static string RepositoryJson(GitHostingProvider provider) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["id"] = 1,
        ["name"] = "repo",
        [provider == GitHostingProvider.GitHub ? "full_name" : "path_with_namespace"] = "owner/repo",
        [provider == GitHostingProvider.GitHub ? "html_url" : "web_url"] = "https://web.example.invalid/owner/repo"
    });

    private static string ReleaseJson(GitHostingProvider provider) => provider == GitHostingProvider.GitHub
        ? "{\"id\":1,\"tag_name\":\"v1.0\",\"name\":\"Release\",\"html_url\":\"https://web.example.invalid/owner/repo/releases/v1.0\"}"
        : "{\"tag_name\":\"v1.0\",\"name\":\"Release\",\"_links\":{\"self\":\"https://web.example.invalid/owner/repo/-/releases/v1.0\"}}";

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class HostingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private async Task<string> RunAsync(params string[] arguments)
    {
        var result = await executor.ExecuteAsync(new("git", root, arguments));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
