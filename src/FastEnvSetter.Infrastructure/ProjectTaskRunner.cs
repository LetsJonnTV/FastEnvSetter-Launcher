using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class ProjectTaskRunner(IWorkflowProcessExecutor? executor = null, IEnumerable<ISecretProvider>? secretProviders = null)
{
    private static readonly ConcurrentDictionary<Guid, byte> ActiveProjects = new();
    private readonly IWorkflowProcessExecutor _executor = executor ?? new WorkflowProcessExecutor();
    private readonly IReadOnlyList<ISecretProvider> _secretProviders = secretProviders?.ToArray() ?? [];

    public async Task<WorkflowRunResult> RunAsync(Project project, IReadOnlyList<EnvProfile> profiles,
        Guid? selectedTaskId = null, CancellationToken cancellationToken = default)
    {
        if (!ActiveProjects.TryAdd(project.Id, 0))
            return new WorkflowRunResult(project.Id, [], ["A workflow is already running for this project."]);
        try
        {
            return await RunExclusiveAsync(project, profiles, selectedTaskId, cancellationToken);
        }
        finally
        {
            ActiveProjects.TryRemove(project.Id, out _);
        }
    }

    private async Task<WorkflowRunResult> RunExclusiveAsync(Project project, IReadOnlyList<EnvProfile> profiles,
        Guid? selectedTaskId, CancellationToken cancellationToken)
    {
        var snapshot = project.Tasks.Select(task => new ProjectTask
        {
            Id = task.Id,
            Name = task.Name,
            Kind = task.Kind,
            FileName = task.FileName,
            Arguments = [.. task.Arguments],
            WorkingDirectory = task.WorkingDirectory,
            DependsOn = [.. task.DependsOn],
            TimeoutSeconds = task.TimeoutSeconds,
        }).ToArray();
        var plan = WorkflowGraph.Plan(snapshot, selectedTaskId);
        if (plan.Errors.Count > 0)
            return new WorkflowRunResult(project.Id, [], plan.Errors);
        var directories = new Dictionary<Guid, string>();
        try
        {
            foreach (var task in plan.Tasks)
            {
                var directory = EnvFileSources.ResolvePath(project.SourceRoot, task.WorkingDirectory);
                if (!Directory.Exists(directory))
                    return new WorkflowRunResult(project.Id, [], ["A task working directory does not exist."]);
                directories[task.Id] = directory;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WorkflowRunResult(project.Id, [], ["Task working directories must be accessible paths inside SourceRoot, without links."]);
        }

        ResolvedEnvironment environment;
        try
        {
            var (vault, errors) = await VaultFetcher.FetchAsync(EnvResolver.Winners(project, null, profiles).Values, _secretProviders, cancellationToken);
            if (errors.Count > 0)
                return new WorkflowRunResult(project.Id, [], ["A required vault value could not be resolved. Check provider availability and unlock status."]);
            environment = EnvResolver.LoadAndResolve(project, null, profiles, vault);
        }
        catch (OperationCanceledException)
        {
            return new WorkflowRunResult(project.Id, [], ["Workflow cancelled before execution."]);
        }
        if (environment.Errors.Count > 0)
            return new WorkflowRunResult(project.Id, [], ["The workflow environment could not be resolved. Check profiles, vaults and environment file sources."]);

        var results = new List<WorkflowTaskResult>();
        foreach (var task in plan.Tasks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(NotRun(task.Id, WorkflowTaskStatus.Cancelled, "Workflow cancelled."));
                continue;
            }
            if (task.DependsOn.Any(dependency => results.Any(result => result.TaskId == dependency && result.Status != WorkflowTaskStatus.Succeeded)))
            {
                results.Add(NotRun(task.Id, WorkflowTaskStatus.Blocked, "A dependency did not complete successfully."));
                continue;
            }
            results.Add(await ExecuteTaskAsync(task, directories[task.Id], environment, cancellationToken));
        }
        return new WorkflowRunResult(project.Id, results, []);
    }

    private async Task<WorkflowTaskResult> ExecuteTaskAsync(ProjectTask task, string directory, ResolvedEnvironment environment, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var logs = new WorkflowLogBuffer(environment.SecretValues);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));
        WorkflowTaskStatus status;
        int? exitCode = null;
        string? message = null;
        try
        {
            var request = new WorkflowProcessRequest(task.FileName, task.Arguments, directory, environment.Variables);
            var result = await _executor.ExecuteAsync(request, logs.Append, timeout.Token);
            exitCode = result.ExitCode;
            status = exitCode == 0 ? WorkflowTaskStatus.Succeeded : WorkflowTaskStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            status = token.IsCancellationRequested ? WorkflowTaskStatus.Cancelled : WorkflowTaskStatus.TimedOut;
            message = status == WorkflowTaskStatus.Cancelled ? "Task cancelled." : "Task exceeded its timeout.";
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            status = WorkflowTaskStatus.StartFailed;
            message = "Task could not execute. Check the configured executable, PATH, working directory and permissions. Windows batch launchers reject shell metacharacters.";
        }
        return new WorkflowTaskResult(task.Id, status, exitCode, started, DateTimeOffset.UtcNow, logs.Snapshot(), message);
    }

    private static WorkflowTaskResult NotRun(Guid id, WorkflowTaskStatus status, string message) =>
        new(id, status, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], message);
}

public sealed class WorkflowProcessExecutor : IWorkflowProcessExecutor
{
    public async Task<WorkflowProcessResult> ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = CreateStartInfo(request);
        using var process = new Process { StartInfo = info };
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        try
        {
            using (var job = new JobObject())
            {
                process.Start();
                if (!job.Assign(process.Handle))
                    throw new InvalidOperationException("Unable to establish process tree cleanup.");
                stdout = DrainAsync(process.StandardOutput, false, output, cancellationToken);
                stderr = DrainAsync(process.StandardError, true, output, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
            }
            await Task.WhenAll(stdout, stderr);
            return new WorkflowProcessResult(process.ExitCode);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(WorkflowProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = LocateExecutable(request.FileName, request.WorkingDirectory, request.Environment),
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (info.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || info.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new[] { info.FileName }.Concat(request.Arguments).ToArray();
            if (parts.Any(part => part.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0))
                throw new ArgumentException("Unsafe batch argument.");
            info.FileName = "cmd.exe";
            info.Arguments = "/d /v:off /s /c \"" + string.Join(" ", parts.Select(part => "\"" + part + "\"")) + "\"";
        }
        else
        {
            foreach (var argument in request.Arguments)
                info.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in request.Environment)
            info.Environment[name] = value;
        return info;
    }

    private static string LocateExecutable(string fileName, string directory, IReadOnlyDictionary<string, string> environment)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains('/') || fileName.Contains('\\'))
            return Path.GetFullPath(Path.Combine(directory, fileName));
        var path = environment.FirstOrDefault(pair => pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)).Value
            ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in new[] { ".exe", ".cmd", ".bat", "" })
            {
                var candidate = Path.Combine(folder.Trim('"'), fileName + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        throw new FileNotFoundException("Required tool is not available on PATH.");
    }

    private static async Task DrainAsync(StreamReader reader, bool isError, Action<bool, string> output, CancellationToken token)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        var oversized = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\n' or '\r')
                {
                    if (oversized || line.Length > 0)
                        output(isError, oversized ? "[oversized output line omitted]" : line.ToString());
                    line.Clear();
                    oversized = false;
                }
                else if (line.Length < 8192 && !oversized)
                    line.Append(character);
                else
                    oversized = true;
            }
        }
        if (oversized || line.Length > 0)
            output(isError, oversized ? "[oversized output line omitted]" : line.ToString());
    }
}
