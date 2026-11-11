using System.Security.Cryptography;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class PersistenceFailureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;
    private string DataFile => Path.Combine(_folder, "data.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timer_failure_preserves_edits_reports_warning_and_does_not_retry(bool diskFailure)
    {
        var protector = new FailingProtector();
        using var store = new JsonAppDataStore(protector, DataFile);
        store.SaveNow();
        var original = await File.ReadAllTextAsync(DataFile);
        var revision = store.Data.Revision;
        store.Data.Profiles.Add(SecretProfile());
        if (diskFailure)
            Directory.CreateDirectory(DataFile + ".tmp");
        else
            protector.Fail = true;

        store.RequestSave();
        await WaitForAsync(() => store.SaveWarning is not null);

        Assert.Null(store.LoadWarning);
        Assert.Equal(revision, store.Data.Revision);
        Assert.Equal(original, await File.ReadAllTextAsync(DataFile));
        Assert.Equal("private-value", Assert.Single(Assert.Single(store.Data.Profiles).Variables).Value);
        Assert.DoesNotContain("private-value", store.SaveWarning!);
        await Task.Delay(900);
        Assert.Equal(1, protector.Attempts);

        if (diskFailure)
            Directory.Delete(DataFile + ".tmp");
        protector.Fail = false;
        store.SaveNow();

        Assert.Null(store.SaveWarning);
        Assert.Equal(revision + 1, store.Data.Revision);
        using var reloaded = new JsonAppDataStore(protector, DataFile);
        Assert.Equal("private-value", Assert.Single(Assert.Single(reloaded.Data.Profiles).Variables).Value);
    }

    [Fact]
    public void Explicit_save_failure_throws_without_mutating_revision_or_secrets()
    {
        var protector = new FailingProtector();
        using var store = new JsonAppDataStore(protector, DataFile);
        store.Data.Profiles.Add(SecretProfile());
        store.SaveNow();
        var original = File.ReadAllText(DataFile);
        var revision = store.Data.Revision;
        protector.Fail = true;

        Assert.Throws<CryptographicException>(store.SaveNow);

        Assert.Equal(revision, store.Data.Revision);
        Assert.Equal(original, File.ReadAllText(DataFile));
        var variable = Assert.Single(Assert.Single(store.Data.Profiles).Variables);
        Assert.Equal("private-value", variable.Value);
        Assert.Null(variable.ProtectedValue);
    }

    [Fact]
    public async Task Failed_commit_preserves_live_state_and_pending_ui_save()
    {
        var protector = new FailingProtector();
        using var store = new JsonAppDataStore(protector, DataFile);
        store.Data.Profiles.Add(SecretProfile());
        store.SaveNow();
        var original = await File.ReadAllTextAsync(DataFile);
        var revision = store.Data.Revision;
        var profiles = store.Data.Profiles;
        store.Data.Settings.Theme = "Light";
        protector.Fail = true;
        store.RequestSave();

        Assert.Throws<CryptographicException>(() => store.CommitConfiguration(candidate =>
        {
            candidate.Revision++;
            candidate.Settings.Theme = "Dark";
            candidate.Profiles.Clear();
            candidate.Profiles.Add(SecretProfile());
            return true;
        }));

        Assert.Same(profiles, store.Data.Profiles);
        Assert.Equal("Light", store.Data.Settings.Theme);
        Assert.Equal(revision, store.Data.Revision);
        Assert.Equal(original, await File.ReadAllTextAsync(DataFile));
        await WaitForAsync(() => store.SaveWarning is not null);
        protector.Fail = false;
        store.RequestSave();
        await WaitForAsync(() => store.SaveWarning is null);

        using var reloaded = new JsonAppDataStore(protector, DataFile);
        Assert.Equal("Light", reloaded.Data.Settings.Theme);
    }

    [Fact]
    public async Task Successful_commit_clears_warning_and_consumes_pending_save_including_ui_edits()
    {
        var protector = new FailingProtector { Fail = true };
        using var store = new JsonAppDataStore(protector, DataFile);
        store.Data.Profiles.Add(SecretProfile());
        store.RequestSave();
        await WaitForAsync(() => store.SaveWarning is not null);
        protector.Fail = false;
        store.Data.Settings.Theme = "Light";
        store.RequestSave();

        var result = store.CommitConfiguration(candidate =>
        {
            candidate.Revision++;
            candidate.Projects.Add(new Project { Name = "Committed" });
            return "result";
        });
        var revision = store.Data.Revision;
        var attempts = protector.Attempts;
        var contents = await File.ReadAllTextAsync(DataFile);

        Assert.Equal("result", result);
        Assert.Null(store.SaveWarning);
        await Task.Delay(900);
        Assert.Equal(revision, store.Data.Revision);
        Assert.Equal(attempts, protector.Attempts);
        Assert.Equal(contents, await File.ReadAllTextAsync(DataFile));
        using (var reloaded = new JsonAppDataStore(protector, DataFile))
        {
            Assert.Equal("Light", reloaded.Data.Settings.Theme);
            Assert.Equal("Committed", Assert.Single(reloaded.Data.Projects).Name);
        }

        store.Data.Settings.Theme = "Dark";
        store.RequestSave();
        await WaitForAsync(() => store.ReadConfiguration(data => data.Revision) == revision + 1);
        using var latest = new JsonAppDataStore(protector, DataFile);
        Assert.Equal("Dark", latest.Data.Settings.Theme);
    }

    [Fact]
    public async Task Dispose_racing_with_save_requests_cancels_pending_callbacks()
    {
        var protector = new FailingProtector { Fail = true };
        using var store = new JsonAppDataStore(protector, DataFile);
        store.Data.Profiles.Add(SecretProfile());
        store.RequestSave();
        await WaitForAsync(() => store.SaveWarning is not null);
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var attempt = 0; attempt < 100; attempt++)
                    store.RequestSave();
            }),
            Task.Run(store.Dispose));
        var attempts = protector.Attempts;

        store.RequestSave();
        store.Dispose();
        await Task.Delay(900);

        Assert.Equal(attempts, protector.Attempts);
        Assert.False(File.Exists(DataFile));
        Assert.NotNull(store.SaveWarning);
        Assert.Throws<ObjectDisposedException>(store.SaveNow);
        Assert.Throws<ObjectDisposedException>(() => store.CommitConfiguration(_ => true));
    }

    [Fact]
    public async Task Dispose_waits_for_inflight_timer_failure_without_exception()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var protector = new FailingProtector
        {
            Fail = true,
            BeforeProtect = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The test did not release the pending save.");
            },
        };
        using var store = new JsonAppDataStore(protector, DataFile);
        store.Data.Profiles.Add(SecretProfile());
        Task? disposing = null;
        try
        {
            store.RequestSave();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = Task.Run(() =>
            {
                disposeStarted.SetResult();
                store.Dispose();
            });
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            release.Set();
            if (disposing is not null)
                await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.NotNull(store.SaveWarning);
        Assert.Equal(0, store.Data.Revision);
        Assert.False(File.Exists(DataFile));
        Assert.Equal("private-value", Assert.Single(Assert.Single(store.Data.Profiles).Variables).Value);
    }

    private static EnvProfile SecretProfile() => new()
    {
        Variables = [new EnvVariable { Name = "SECRET", Value = "private-value", IsSecret = true }],
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        private int _attempts;
        public bool Fail { get; set; }
        public Action? BeforeProtect { get; init; }
        public int Attempts => Volatile.Read(ref _attempts);

        public string Protect(string plainText)
        {
            Interlocked.Increment(ref _attempts);
            BeforeProtect?.Invoke();
            if (Fail)
                throw new CryptographicException("private-value");
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));
        }

        public string? Unprotect(string protectedText) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }
}
