using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class DeploymentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fes-deployment-" + Guid.NewGuid().ToString("N"));
    private readonly Source source = new();
    private readonly Executor executor = new();
    private readonly Policy policy = new();

    public DeploymentTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "compose.json"), "{\"services\":{\"app\":{\"image\":\"example/app@sha256:" + new string('a', 64) + "\"}}}");
        File.WriteAllText(Path.Combine(root, "known_hosts"), "example.invalid ssh-ed25519 test-key\n");
        File.WriteAllText(Path.Combine(root, "kubeconfig"), "fake kubeconfig");
    }

    [Theory]
    [InlineData(DeploymentProvider.DockerCompose, "docker")]
    [InlineData(DeploymentProvider.PodmanCompose, "podman")]
    public async Task Compose_is_detached_and_requires_explicit_current_policy_approval(DeploymentProvider provider, string executable)
    {
        var service = Service();
        var plan = await service.PreviewAsync(Request(provider));
        var command = Assert.Single(plan.Commands);
        Assert.Equal(executable, command.FileName);
        Assert.Contains("--detach", command.Arguments);
        Assert.Contains("--no-build", command.Arguments);
        Assert.DoesNotContain("--volumes", command.Arguments);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ExecuteAsync(plan, null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, Approval(plan) with { PlanDigest = "forged" }));
        policy.Deny = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(plan, Approval(plan)));
        Assert.Equal(0, executor.Calls);
        policy.Deny = false;
        var result = await service.ExecuteAsync(plan, Approval(plan));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, executor.Calls);
        var events = await new DeploymentFileJournal(Path.Combine(root, "journal")).ReadAsync();
        Assert.Equal(DeploymentOperationState.Succeeded, events[^1].State);
        await File.AppendAllTextAsync(Path.Combine(root, "compose.json"), " \n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, Approval(plan)));
        var fresh = await service.PreviewAsync(Request(provider));
        source.Fingerprint = "new source";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(fresh, Approval(fresh)));
    }

    [Fact]
    public async Task Kubernetes_requires_explicit_scope_digest_images_and_limits_rollback()
    {
        var service = Service();
        var request = Request(DeploymentProvider.Kubernetes) with { FilePath = "deployment.json" };
        await File.WriteAllTextAsync(Path.Combine(root, request.FilePath), "{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\",\"metadata\":{\"name\":\"app\",\"namespace\":\"test\"},\"spec\":{\"template\":{\"spec\":{\"containers\":[{\"name\":\"app\",\"image\":\"app@sha256:" + new string('a', 64) + "\"}]}}}}");
        var plan = await service.PreviewAsync(request);
        Assert.Contains("--kubeconfig", plan.Commands[0].Arguments);
        Assert.Contains("--namespace", plan.Commands[0].Arguments);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(request with { Namespace = "other" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { Context = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { Action = DeploymentAction.Rollback, ResourceName = "app" }));
        var rollback = await service.PreviewAsync(request with { Action = DeploymentAction.Rollback, ResourceName = "app", RollbackRevision = 2 });
        Assert.Contains("--to-revision=2", rollback.Commands[0].Arguments);
        Assert.DoesNotContain("delete", rollback.Commands[0].Arguments);
    }

    [Fact]
    public async Task Ssh_pins_known_hosts_and_rejects_shell_injection_and_unimplemented_lifecycle()
    {
        var service = Service();
        var request = Request(DeploymentProvider.Ssh);
        var plan = await service.PreviewAsync(request);
        Assert.Equal("scp", plan.Commands[0].FileName);
        Assert.Contains("-s", plan.Commands[0].Arguments);
        Assert.Contains("-oStrictHostKeyChecking=yes", plan.Commands[0].Arguments);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { Host = "host;echo secret" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { RemotePath = "/tmp/a;id" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { RemotePath = "/tmp/../etc/passwd" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.PreviewAsync(request with { Action = DeploymentAction.Rollback }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { WorkingDirectory = "../" }));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Interrupted_operations_are_journaled_without_raw_output_and_surface_for_reconciliation()
    {
        var service = Service();
        var plan = await service.PreviewAsync(Request(DeploymentProvider.PodmanCompose));
        executor.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan, Approval(plan)));
        Assert.Single(await service.GetUnfinishedOperationsAsync());
        var journal = await new DeploymentFileJournal(Path.Combine(root, "journal")).ReadAsync();
        Assert.Equal(DeploymentOperationState.Indeterminate, journal[^1].State);
        Assert.DoesNotContain("sensitive-provider-output", string.Join("", Directory.EnumerateFiles(Path.Combine(root, "journal"), "*.json").Select(File.ReadAllText)));
        executor.Fail = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, Approval(plan)));
        await service.ReconcileAsync(plan, Approval(plan), journal[^1].OperationId, DeploymentOperationState.Failed);
        Assert.Empty(await service.GetUnfinishedOperationsAsync());
        Assert.Equal(0, (await service.ExecuteAsync(plan, Approval(plan))).ExitCode);
    }

    [Theory]
    [InlineData(DeploymentProvider.PodmanCompose)]
    [InlineData(DeploymentProvider.Kubernetes)]
    [InlineData(DeploymentProvider.Ssh)]
    public async Task Approved_inputs_stay_locked_from_final_preview_through_journal_and_execution(DeploymentProvider provider)
    {
        var request = Request(provider);
        if (provider == DeploymentProvider.Kubernetes)
            await File.WriteAllTextAsync(Path.Combine(root, "compose.json"), "{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\",\"metadata\":{\"name\":\"app\",\"namespace\":\"test\"},\"spec\":{\"template\":{\"spec\":{\"containers\":[{\"name\":\"app\",\"image\":\"app@sha256:" + new string('a', 64) + "\"}]}}}}");
        DeploymentPlan? plan = null;
        var originals = new Dictionary<string, string>();
        var checks = 0;
        void AttemptMutation()
        {
            checks++;
            foreach (var input in plan!.Inputs)
            {
                var replacement = input.Path + ".replacement";
                File.WriteAllText(replacement, "unapproved replacement");
                Assert.Throws<IOException>(() => File.Move(replacement, input.Path, true));
                Assert.Throws<IOException>(() => File.WriteAllText(input.Path, "unapproved overwrite"));
                Assert.Equal(originals[input.Path], File.ReadAllText(input.Path));
            }
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        var journal = new MutatingJournal(new DeploymentFileJournal(Path.Combine(root, "journal")), AttemptMutation);
        executor.BeforeExecute = AttemptMutation;
        var service = new DeploymentService(source, executor, policy, journal);
        plan = await service.PreviewAsync(request);
        foreach (var input in plan.Inputs) originals.Add(input.Path, await File.ReadAllTextAsync(input.Path));
        Assert.Equal(0, (await service.ExecuteAsync(plan, Approval(plan))).ExitCode);
        Assert.Equal(2, checks);
        Assert.Equal(1, executor.Calls);
        foreach (var input in plan.Inputs) await File.WriteAllTextAsync(input.Path, "released");
    }

    private sealed class MutatingJournal(IDeploymentJournal inner, Action mutate) : IDeploymentJournal
    {
        public ValueTask<IAsyncDisposable> AcquireTargetAsync(Guid targetId, CancellationToken cancellationToken = default) =>
            inner.AcquireTargetAsync(targetId, cancellationToken);
        public Task<System.Collections.Immutable.ImmutableArray<DeploymentOperation>> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);
        public Task AppendAsync(DeploymentOperation operation, CancellationToken cancellationToken = default)
        {
            if (operation.State == DeploymentOperationState.Started) mutate();
            return inner.AppendAsync(operation, cancellationToken);
        }
    }

    private DeploymentService Service() => new(source, executor, policy, new DeploymentFileJournal(Path.Combine(root, "journal")));

    [Fact]
    public async Task Journal_excludes_concurrent_target_owners_across_instances()
    {
        var first = new DeploymentFileJournal(Path.Combine(root, "journal"));
        var second = new DeploymentFileJournal(Path.Combine(root, "journal"));
        var target = Guid.NewGuid();
        await using (await first.AcquireTargetAsync(target))
            await Assert.ThrowsAsync<IOException>(() => second.AcquireTargetAsync(target).AsTask());
        await using var released = await second.AcquireTargetAsync(target);
    }

    [Theory]
    [InlineData("{\"include\":\"outside.yml\",\"services\":{}}")]
    [InlineData("{\"services\":{\"app\":{\"image\":\"example/app:latest\"}}}")]
    [InlineData("{\"services\":{\"app\":{\"image\":\"${IMAGE}\"}}}")]
    [InlineData("services: {app: {image: app}}")]
    public async Task Compose_rejects_unbound_external_inputs_and_mutable_images(string manifest)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "compose.json"), manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().PreviewAsync(Request(DeploymentProvider.PodmanCompose)));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Native_adapter_isolates_ambient_environment_without_running_a_real_provider()
    {
        var capture = new NativeCapture();
        var adapter = new DeploymentNativeCommandExecutor(capture);
        await adapter.ExecuteAsync(new("podman", root, ["compose", "ps"]));
        Assert.NotNull(capture.Command);
        Assert.True(capture.Command.IsolateEnvironment);
        Assert.Equal(new[] { "compose", "ps" }, capture.Command.Arguments);
    }

    private sealed class NativeCapture : IGitCommandExecutor
    {
        public GitCommand? Command { get; private set; }
        public Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Command = command;
            return Task.FromResult(new GitCommandResult(0, "", ""));
        }
    }

    private DeploymentRequest Request(DeploymentProvider provider) => new(Guid.NewGuid(), provider, DeploymentAction.Deploy,
        root, ".", "compose.json", "test-context", "test", Path.Combine(root, "kubeconfig"), "user@example.invalid", "/srv/app/artifact", Path.Combine(root, "known_hosts"));
    private static DeploymentApproval Approval(DeploymentPlan plan) => new(plan.Digest, plan.Source.Fingerprint, "test-user", DateTimeOffset.UtcNow.AddMinutes(2));

    private sealed class Source : IDeploymentSourceState
    {
        public string Fingerprint { get; set; } = "source-state";
        public Task<DeploymentSourceState> ReadAsync(string sourceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeploymentSourceState(sourceRoot, new string('a', 40), Fingerprint));
    }
    private sealed class Executor : IDeploymentCommandExecutor
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Action? BeforeExecute { get; set; }
        public Task<GitCommandResult> ExecuteAsync(DeploymentCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeExecute?.Invoke();
            if (Fail) throw new IOException("fake interruption");
            return Task.FromResult(new GitCommandResult(0, "sensitive-provider-output", ""));
        }
    }
    private sealed class Policy : IDeploymentApprovalPolicy
    {
        public bool Deny { get; set; }
        public Task ValidateAsync(DeploymentPlan plan, DeploymentApproval approval, CancellationToken cancellationToken = default)
        {
            if (Deny) throw new UnauthorizedAccessException();
            return Task.CompletedTask;
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}
