using System.Collections.Immutable;

namespace FastEnvSetter.Core;

public enum GitHostingProvider { GitHub, GitLab }
public sealed record GitHostCredential(Uri Origin, string Token);
public sealed record GitHostedRepository(string Id, string Name, string Path, Uri WebUri);
public sealed record GitHostedRelease(string Id, string Tag, string Name, Uri WebUri);

public interface IGitHostingReader
{
    Task<ImmutableArray<GitHostedRepository>> GetRepositoriesAsync(CancellationToken cancellationToken = default);
    Task<GitHostedRepository> GetRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default);
    Task<ImmutableArray<GitHostedRelease>> GetReleasesAsync(string repositoryPath, CancellationToken cancellationToken = default);
    Uri GetChangeRequestUri(string repositoryPath, int number);
}

public interface IGitHostingPublisher
{
    Task<GitHostedRelease> PublishReleaseAsync(string repositoryPath, VersionRelease release, CancellationToken cancellationToken = default);
}
