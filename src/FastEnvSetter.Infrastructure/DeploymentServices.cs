using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEnvSetter.Core;
using Microsoft.Win32.SafeHandles;

namespace FastEnvSetter.Infrastructure;

public sealed class GitDeploymentSourceState : IDeploymentSourceState
{
    public async Task<DeploymentSourceState> ReadAsync(string sourceRoot, CancellationToken cancellationToken = default)
    {
        var git = await GitService.OpenAsync(sourceRoot, cancellationToken: cancellationToken);
        var sha = await git.GetSourceShaAsync(cancellationToken);
        if ((await git.GetStatusAsync(cancellationToken)).Count != 0 || await git.GetSourceShaAsync(cancellationToken) != sha)
            throw new InvalidOperationException("Deployment requires a committed, clean, stable working tree.");
        return new(git.Repository.SourceRoot, sha, VersionService.Hash(new { git.Repository, Sha = sha }));
    }
}

public sealed class DeploymentNativeCommandExecutor(IGitCommandExecutor? executor = null) : IDeploymentCommandExecutor
{
    private readonly IGitCommandExecutor executor = executor ?? new GitNativeCommandExecutor(timeout: TimeSpan.FromMinutes(5));
    public Task<GitCommandResult> ExecuteAsync(DeploymentCommand command, CancellationToken cancellationToken = default) =>
        executor.ExecuteAsync(new(command.FileName, command.WorkingDirectory, command.Arguments, IsolateEnvironment: true), cancellationToken);
}

public sealed class DeploymentService(IDeploymentSourceState sourceState, IDeploymentCommandExecutor executor,
    IDeploymentApprovalPolicy approvalPolicy, IDeploymentJournal journal)
{
    private readonly SemaphoreSlim executionGate = new(1, 1);

    public async Task<DeploymentPlan> PreviewAsync(DeploymentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TargetId == Guid.Empty || !Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Provider))
            throw new ArgumentException("A known provider, action and stable target identity are required.");
        var root = Absolute(request.SourceRoot);
        var workingDirectory = Within(root, request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Deployment working directory is unavailable.");
        var source = await sourceState.ReadAsync(root, cancellationToken);
        if (source.SourceRoot != root || !Regex.IsMatch(source.CommitSha, "\\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\\z") || string.IsNullOrWhiteSpace(source.Fingerprint))
            throw new InvalidOperationException("Source state must identify the exact requested root and full commit SHA.");
        var inputs = ImmutableArray.CreateBuilder<DeploymentInput>();
        var commands = ImmutableArray.CreateBuilder<DeploymentCommand>();
        var limitations = ImmutableArray.CreateBuilder<string>();
        async Task<string> Input(string path, bool insideRoot)
        {
            var full = insideRoot ? Within(root, Path.GetRelativePath(root, Path.GetFullPath(path, workingDirectory))) : Absolute(path);
            RejectLinks(full);
            await using var stream = File.OpenRead(full);
            if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Deployment input exceeds the 16 MiB limit.");
            inputs.Add(new(full, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))));
            return full;
        }
        void Command(string executable, params string[] arguments) => commands.Add(new(executable, workingDirectory, [.. arguments]));

        switch (request.Provider)
        {
            case DeploymentProvider.DockerCompose:
            case DeploymentProvider.PodmanCompose:
                {
                    if (request.Action is DeploymentAction.Rollback or DeploymentAction.RolloutStatus)
                        throw new NotSupportedException("Compose does not provide revision rollback or rollout history through this service.");
                    var file = await Input(request.FilePath, true);
                    ValidateCompose(await File.ReadAllTextAsync(file, cancellationToken));
                    Identifier(request.Context, "explicit Compose context/connection");
                    var prefix = new List<string> { "compose", "--project-directory", workingDirectory, "-p", "fes-" + request.TargetId.ToString("N"), "-f", file };
                    prefix.InsertRange(0, [request.Provider == DeploymentProvider.DockerCompose ? "--context" : "--connection", request.Context]);
                    prefix.AddRange(["--env-file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"]);
                    var tail = request.Action switch
                    {
                        DeploymentAction.Deploy => new[] { "up", "--detach", "--no-build", "--pull", "never" },
                        DeploymentAction.Status => ["ps", "--format", "json"],
                        DeploymentAction.Logs => ["logs", "--no-color", "--tail", "200"],
                        _ => throw new NotSupportedException()
                    };
                    Command(request.Provider == DeploymentProvider.DockerCompose ? "docker" : "podman", [.. prefix, .. tail]);
                    limitations.Add("Compose accepts a restricted JSON services manifest only: digest-pinned images, no interpolation, build, bind mounts, external files, configs, secrets or includes. An explicit existing context/connection is required. No image pull, down, prune or volume deletion is performed.");
                    limitations.Add("Compose workloads are detached from the launcher. Docker/Podman and its Compose provider must already be installed and configured.");
                    break;
                }
            case DeploymentProvider.Kubernetes:
                {
                    Identifier(request.Context, "context");
                    Identifier(request.Namespace, "namespace");
                    var kubeconfig = await Input(request.Kubeconfig, false);
                    var prefix = new[] { "--kubeconfig", kubeconfig, "--context", request.Context, "--namespace", request.Namespace, "--request-timeout=60s" };
                    if (request.Action == DeploymentAction.Deploy)
                    {
                        var manifest = await Input(request.FilePath, true);
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, cancellationToken));
                        ValidateManifest(document.RootElement, request.Namespace);
                        Command("kubectl", [.. prefix, "apply", "-f", manifest]);
                    }
                    else
                    {
                        Identifier(request.ResourceName, "deployment name");
                        var resource = "deployment/" + request.ResourceName;
                        var tail = request.Action switch
                        {
                            DeploymentAction.Status => new[] { "get", resource, "-o", "json" },
                            DeploymentAction.Logs => ["logs", resource, "--all-containers=true", "--tail=200"],
                            DeploymentAction.RolloutStatus => ["rollout", "status", resource, "--timeout=60s"],
                            DeploymentAction.Rollback when request.RollbackRevision is > 0 => ["rollout", "undo", resource, $"--to-revision={request.RollbackRevision}"],
                            _ => throw new ArgumentException("Rollback requires an explicit positive Deployment revision.")
                        };
                        Command("kubectl", [.. prefix, .. tail]);
                    }
                    limitations.Add("Kubernetes deploy accepts a JSON Deployment or List of Deployments only, with matching explicit namespaces and digest-pinned images. Existing kubeconfig is trusted; no context mutation, namespace creation, provisioning or deletion occurs.");
                    limitations.Add("Rollback is limited to an explicit existing Kubernetes Deployment revision; it does not restore databases or persistent data.");
                    break;
                }
            case DeploymentProvider.Ssh:
                {
                    if (!Regex.IsMatch(request.Host, "\\A(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9.-]*\\z"))
                        throw new ArgumentException("Use a plain SSH hostname, optionally prefixed by a username.");
                    if (!Regex.IsMatch(request.RemotePath, "\\A/[A-Za-z0-9_./-]+\\z") || request.RemotePath.Split('/').Any(part => part is ".." or ".") || request.RemotePath.EndsWith('/'))
                        throw new ArgumentException("Use an absolute, shell-safe remote file path without traversal.");
                    var knownHosts = await Input(request.KnownHostsFile, false);
                    if (new FileInfo(knownHosts).Length == 0) throw new InvalidDataException("An existing pinned known_hosts file is required.");
                    var options = new[] { "-oBatchMode=yes", "-oStrictHostKeyChecking=yes", "-oUserKnownHostsFile=" + knownHosts,
                    "-oGlobalKnownHostsFile=" + knownHosts, "-oUpdateHostKeys=no", "-oConnectTimeout=15", "-oClearAllForwardings=yes", "-F", "none" };
                    if (request.Action == DeploymentAction.Deploy)
                        Command("scp", ["-s", .. options, "--", await Input(request.FilePath, true), request.Host + ":" + request.RemotePath]);
                    else if (request.Action is DeploymentAction.Status or DeploymentAction.Logs)
                        Command("ssh", [.. options, "--", request.Host,
                        request.Action == DeploymentAction.Status ? "stat -- '" + request.RemotePath + "'" : "tail -n 200 -- '" + request.RemotePath + "'"]);
                    else throw new NotSupportedException("SSH supports artifact upload, file status and bounded file logs only.");
                    limitations.Add("SSH deployment uploads one artifact using SFTP-mode scp. The remote parent directory must exist. No arbitrary remote launch, lifecycle management or rollback is supported.");
                    break;
                }
        }
        var plan = new DeploymentPlan(request, source, commands.ToImmutable(), inputs.ToImmutable(), limitations.ToImmutable(), "");
        return plan with { Digest = VersionService.Hash(plan) };
    }

    public async Task<DeploymentExecutionResult> ExecuteAsync(DeploymentPlan reviewedPlan, DeploymentApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            await using var targetLease = await journal.AcquireTargetAsync(reviewedPlan.Request.TargetId, cancellationToken);
            var plan = await PreviewAsync(reviewedPlan.Request, cancellationToken);
            if (plan.Digest != reviewedPlan.Digest || VersionService.Hash(reviewedPlan with { Digest = "" }) != reviewedPlan.Digest)
                throw new InvalidOperationException("Deployment plan changed; preview and approval must be renewed.");
            if (approval.PlanDigest != plan.Digest || approval.SourceFingerprint != plan.Source.Fingerprint ||
                approval.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(approval.ApprovedBy))
                throw new InvalidOperationException("Explicit, current approval bound to this plan and source state is required.");
            await approvalPolicy.ValidateAsync(plan, approval, cancellationToken);
            var events = await journal.ReadAsync(cancellationToken);
            if (events.GroupBy(item => item.OperationId).Select(group => group.Last()).Any(item =>
                item.TargetId == plan.Request.TargetId && item.State is DeploymentOperationState.Started or DeploymentOperationState.Indeterminate))
                throw new InvalidOperationException("An unfinished target operation needs reconciliation before execution.");
            using var inputs = new DeploymentInputLease(plan.Inputs);
            if ((await PreviewAsync(reviewedPlan.Request, cancellationToken)).Digest != plan.Digest)
                throw new InvalidOperationException("Deployment inputs or source changed while obtaining policy approval.");
            var operation = new DeploymentOperation(Guid.NewGuid(), plan.Request.TargetId, plan.Digest, plan.Source.CommitSha,
                plan.Request.Action, DeploymentOperationState.Started, DateTimeOffset.UtcNow, null);
            await journal.AppendAsync(operation, cancellationToken);
            try
            {
                var result = new GitCommandResult(0, "", "");
                foreach (var command in plan.Commands)
                {
                    result = await executor.ExecuteAsync(command, cancellationToken);
                    if (result.ExitCode != 0) break;
                }
                await journal.AppendAsync(operation with
                {
                    State = result.ExitCode == 0 ? DeploymentOperationState.Succeeded : DeploymentOperationState.Failed,
                    RecordedAt = DateTimeOffset.UtcNow,
                    ExitCode = result.ExitCode
                }, CancellationToken.None);
                return new(operation.OperationId, result.ExitCode,
                    result.ExitCode == 0 ? result.StandardOutput : "Provider failed; raw diagnostics withheld because they may contain credentials.");
            }
            catch
            {
                await journal.AppendAsync(operation with { State = DeploymentOperationState.Indeterminate, RecordedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                throw;
            }
        }
        finally { executionGate.Release(); }
    }

    public async Task<ImmutableArray<DeploymentOperation>> GetUnfinishedOperationsAsync(CancellationToken cancellationToken = default)
    {
        var events = await journal.ReadAsync(cancellationToken);
        return [.. events.GroupBy(item => item.OperationId).Select(group => group.Last())
            .Where(item => item.State is DeploymentOperationState.Started or DeploymentOperationState.Indeterminate)];
    }

    public async Task ReconcileAsync(DeploymentPlan originalPlan, DeploymentApproval approval, Guid operationId,
        DeploymentOperationState observedState, CancellationToken cancellationToken = default)
    {
        if (observedState is not (DeploymentOperationState.Succeeded or DeploymentOperationState.Failed))
            throw new ArgumentException("Reconciliation requires an explicitly observed success or failure.");
        if (VersionService.Hash(originalPlan with { Digest = "" }) != originalPlan.Digest || approval.PlanDigest != originalPlan.Digest ||
            approval.SourceFingerprint != originalPlan.Source.Fingerprint || approval.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(approval.ApprovedBy))
            throw new InvalidOperationException("Reconciliation needs explicit approval for the original plan.");
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            await using var targetLease = await journal.AcquireTargetAsync(originalPlan.Request.TargetId, cancellationToken);
            await approvalPolicy.ValidateAsync(originalPlan, approval, cancellationToken);
            var events = await journal.ReadAsync(cancellationToken);
            var operation = events.LastOrDefault(item => item.OperationId == operationId)
                ?? throw new InvalidOperationException("Unknown deployment operation.");
            if (operation.PlanDigest != originalPlan.Digest || operation.TargetId != originalPlan.Request.TargetId ||
                operation.State is not (DeploymentOperationState.Started or DeploymentOperationState.Indeterminate))
                throw new InvalidOperationException("Operation does not match the unresolved original plan.");
            await journal.AppendAsync(operation with { State = observedState, RecordedAt = DateTimeOffset.UtcNow }, cancellationToken);
        }
        finally { executionGate.Release(); }
    }

    private sealed class DeploymentInputLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];

        public DeploymentInputLease(ImmutableArray<DeploymentInput> inputs)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Deployment input leases require Windows file sharing enforcement.");
            try
            {
                var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var inputPath in inputs.Select(input => input.Path))
                {
                    var ancestors = new Stack<string>();
                    for (var current = inputPath; current is not null; current = Path.GetDirectoryName(current))
                        ancestors.Push(current);
                    while (ancestors.TryPop(out var path))
                    {
                        if (!locked.Add(path)) continue;
                        LockPath(path);
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void LockPath(string path)
        {
            var handle = CreateFileW(path, 0x80000000, FileShare.Read, IntPtr.Zero, FileMode.Open,
                0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("Cannot lock deployment input path.", new Win32Exception(error));
            }
            handles.Add(handle);
            if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
                throw new IOException("Cannot inspect deployment input handle.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((information.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Deployment paths must not traverse symlinks or junctions.");
        }

        public void Dispose()
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            handles.Clear();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTagInformation
        {
            public FileAttributes Attributes;
            public uint ReparseTag;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
            FileMode creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
            out AttributeTagInformation information, uint size);
    }

    private static void ValidateManifest(JsonElement manifest, string namespaceName)
    {
        if (manifest.GetProperty("kind").GetString() == "List")
        {
            foreach (var item in manifest.GetProperty("items").EnumerateArray()) ValidateManifest(item, namespaceName);
            return;
        }
        if (manifest.GetProperty("kind").GetString() != "Deployment" || manifest.GetProperty("apiVersion").GetString() != "apps/v1")
            throw new InvalidDataException("Only apps/v1 Deployments are allowed.");
        var metadata = manifest.GetProperty("metadata");
        Identifier(metadata.GetProperty("name").GetString() ?? "", "deployment name");
        if (!metadata.TryGetProperty("namespace", out var declared) || declared.GetString() != namespaceName)
            throw new InvalidDataException("Every Deployment must declare the approved namespace.");
        var pod = manifest.GetProperty("spec").GetProperty("template").GetProperty("spec");
        foreach (var field in new[] { "containers", "initContainers" })
            if (pod.TryGetProperty(field, out var containers))
                foreach (var container in containers.EnumerateArray())
                    if (!Regex.IsMatch(container.GetProperty("image").GetString() ?? "", "\\A[^\\s]+@sha256:[a-fA-F0-9]{64}\\z"))
                        throw new InvalidDataException("Container images must be pinned by SHA-256 digest.");
    }

    private static void ValidateCompose(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { throw new InvalidDataException("Only JSON Compose manifests are supported; YAML must be converted explicitly before approval."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("services" or "name" or "version")))
                throw new InvalidDataException("Compose manifest may contain only services, name and version.");
            var services = root.GetProperty("services");
            if (services.ValueKind != JsonValueKind.Object || !services.EnumerateObject().Any()) throw new InvalidDataException("At least one Compose service is required.");
            foreach (var service in services.EnumerateObject()) ValidateComposeService(service);
            RejectInterpolation(root);
        }
    }

    private static void ValidateComposeService(JsonProperty service)
    {
        Identifier(service.Name, "Compose service name");
        if (service.Value.ValueKind != JsonValueKind.Object || service.Value.EnumerateObject().Any(property => property.Name is not
            ("image" or "ports" or "environment" or "restart" or "command" or "entrypoint" or "healthcheck" or "depends_on" or "labels" or "working_dir" or "user" or "read_only")))
            throw new InvalidDataException("Compose service uses fields outside the supported self-contained subset.");
        if (!Regex.IsMatch(service.Value.GetProperty("image").GetString() ?? "", "\\A[^\\s]+@sha256:[a-fA-F0-9]{64}\\z"))
            throw new InvalidDataException("Compose images must be pinned by SHA-256 digest.");
    }

    private static void RejectInterpolation(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString()!.Contains('$'):
                throw new InvalidDataException("Compose substitutions and dollar expressions are not supported.");
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains('$')) throw new InvalidDataException("Compose substitutions are not supported.");
                    RejectInterpolation(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectInterpolation(item);
                break;
        }
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit absolute path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string Within(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An explicit path is required.");
        var full = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Deployment path escapes SourceRoot.");
        RejectLinks(full);
        return full;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Deployment paths must not traverse symlinks or junctions.");
    }

    private static void Identifier(string value, string name)
    {
        if (!Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9_.:/@-]{0,252}\\z")) throw new ArgumentException("Invalid explicit " + name + ".");
    }
}
