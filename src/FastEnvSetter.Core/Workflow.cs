namespace FastEnvSetter.Core;

public enum WorkflowTaskStatus { Succeeded, Failed, Blocked, Cancelled, TimedOut, StartFailed }
public sealed record WorkflowLogEntry(DateTimeOffset Timestamp, bool IsError, string Text);
public sealed record WorkflowTaskResult(Guid TaskId, WorkflowTaskStatus Status, int? ExitCode,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<WorkflowLogEntry> Logs, string? Message = null);
public sealed record WorkflowRunResult(Guid ProjectId, IReadOnlyList<WorkflowTaskResult> Tasks, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0 && Tasks.All(task => task.Status == WorkflowTaskStatus.Succeeded);
}

public sealed record WorkflowProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
public sealed record WorkflowProcessResult(int ExitCode);

public interface IWorkflowProcessExecutor
{
    Task<WorkflowProcessResult> ExecuteAsync(WorkflowProcessRequest request, Action<bool, string> output, CancellationToken cancellationToken);
}

public sealed record WorkflowPlan(IReadOnlyList<ProjectTask> Tasks, IReadOnlyList<string> Errors);

public static class WorkflowGraph
{
    public static WorkflowPlan Plan(IReadOnlyList<ProjectTask> tasks, Guid? selectedTaskId = null)
    {
        if (tasks.Any(task => task.Id == Guid.Empty) || tasks.Select(task => task.Id).Distinct().Count() != tasks.Count)
            return new WorkflowPlan([], ["Task identifiers must be nonempty and unique."]);
        var known = tasks.ToDictionary(task => task.Id);
        if (selectedTaskId.HasValue && !known.ContainsKey(selectedTaskId.Value))
            return new WorkflowPlan([], ["The selected task does not exist."]);
        if (tasks.Any(task => task.DependsOn.Any(dependency => !known.ContainsKey(dependency))))
            return new WorkflowPlan([], ["A task references a missing dependency."]);
        if (tasks.Any(task => string.IsNullOrWhiteSpace(task.FileName) || task.FileName.Contains('\0')
            || task.Arguments.Any(argument => argument.Contains('\0')) || task.TimeoutSeconds is < 1 or > 86400))
            return new WorkflowPlan([], ["Tasks require an executable, valid arguments and a timeout between 1 and 86400 seconds."]);

        var ordered = new List<ProjectTask>();
        var remaining = tasks.ToList();
        var completed = new HashSet<Guid>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(task => task.DependsOn.All(completed.Contains));
            if (next is null)
                return new WorkflowPlan([], ["Task dependencies contain a cycle."]);
            ordered.Add(next);
            completed.Add(next.Id);
            remaining.Remove(next);
        }

        if (selectedTaskId is not { } selected)
            return new WorkflowPlan(ordered, []);
        var required = new HashSet<Guid> { selected };
        foreach (var task in ordered.AsEnumerable().Reverse())
            if (required.Contains(task.Id))
                required.UnionWith(task.DependsOn);
        return new WorkflowPlan(ordered.Where(task => required.Contains(task.Id)).ToArray(), []);
    }
}

public sealed class WorkflowLogBuffer(IEnumerable<string> secretValues, int maximumEntries = 500, int maximumLineLength = 4096)
{
    private readonly object _gate = new();
    private readonly Queue<WorkflowLogEntry> _entries = new();
    private readonly string[] _secrets = secretValues.SelectMany(value => new[] { value }.Concat(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
        .Where(value => value.Length > 0).Distinct().OrderByDescending(value => value.Length).ToArray();

    public void Append(bool isError, string text)
    {
        foreach (var secret in _secrets)
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        if (text.Length > maximumLineLength)
            text = text[..Math.Max(0, maximumLineLength)];
        lock (_gate)
        {
            if (maximumEntries < 1)
                return;
            _entries.Enqueue(new WorkflowLogEntry(DateTimeOffset.UtcNow, isError, text));
            while (_entries.Count > maximumEntries)
                _entries.Dequeue();
        }
    }

    public IReadOnlyList<WorkflowLogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }
}
