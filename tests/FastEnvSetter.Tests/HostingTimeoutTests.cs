using System.Net;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class HostingTimeoutTests
{
    private const string Token = "fake-sensitive-access-token";

    [Theory]
    [InlineData(GitHostingProvider.GitHub)]
    [InlineData(GitHostingProvider.GitLab)]
    public async Task Deadline_cancels_stalled_response_body_and_disposes_stream(GitHostingProvider provider)
    {
        var stream = new HangingStream();
        using var service = Service(provider, new Handler((_, _) => Task.FromResult(Response(stream))), TimeSpan.FromSeconds(1));
        var pending = service.GetRepositoryAsync("owner/repo");
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(Token, exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.True(stream.Cancelled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_body_without_exposing_stream_diagnostics()
    {
        var stream = new HangingStream();
        using var cancellation = new CancellationTokenSource();
        using var service = Service(GitHostingProvider.GitHub, new Handler((_, _) => Task.FromResult(Response(stream))), TimeSpan.FromSeconds(30));
        var pending = service.GetRepositoryAsync("owner/repo", cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain(Token, exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.True(stream.Cancelled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Deadline_also_covers_response_headers()
    {
        var cancelled = false;
        using var service = Service(GitHostingProvider.GitHub, new Handler(async (_, cancellationToken) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled = true; throw new OperationCanceledException(Token, cancellationToken); }
            return new(HttpStatusCode.OK);
        }), TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => service.GetRepositoryAsync("owner/repo").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancelled);
        Assert.DoesNotContain(Token, exception.ToString());
    }

    [Fact]
    public async Task Body_transport_errors_with_tokens_are_sanitized()
    {
        var stream = new HangingStream { Fail = true };
        using var service = Service(GitHostingProvider.GitLab, new Handler((_, _) => Task.FromResult(Response(stream))), TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRepositoryAsync("owner/repo"));
        Assert.DoesNotContain(Token, exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Authenticated_redirect_is_rejected_without_forwarding_credentials()
    {
        var calls = 0;
        using var service = Service(GitHostingProvider.GitHub, new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("api.example.invalid", request.RequestUri!.Host);
            Assert.Equal(Token, request.Headers.Authorization!.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://untrusted.invalid/" + Token);
            return Task.FromResult(response);
        }), TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRepositoryAsync("owner/repo"));
        Assert.Equal(1, calls);
        Assert.DoesNotContain(Token, exception.ToString());
    }

    private static GitHostingService Service(GitHostingProvider provider, HttpMessageHandler handler, TimeSpan timeout) =>
        new(provider, new("https://api.example.invalid/"), new("https://web.example.invalid/"),
            (origin, _) => ValueTask.FromResult<GitHostCredential?>(new(origin, Token)), handler, timeout);

    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class HangingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public bool Fail { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            if (Fail) throw new IOException(Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw new OperationCanceledException(Token, cancellationToken); }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
