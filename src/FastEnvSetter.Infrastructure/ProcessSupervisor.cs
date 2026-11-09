using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class ProcessSupervisor(RunnerRegistry runners, IEnumerable<IAppEventHandler> handlers, IEnumerable<ISecretProvider>? secretProviders = null)
    : IProcessSupervisor, IDisposable
{
    private const int MaxLogLines = 5000;
    private const int CommandNotFoundExitCode = 9009;
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly HttpClient ReadinessClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly IReadOnlyList<IAppEventHandler> _handlers = handlers.ToList();
    private readonly JobObject _job = new();
    private readonly IReadOnlyList<ISecretProvider> _secretProviders = secretProviders?.ToList() ?? [];

    public event Action<ServiceStateChange>? StateChanged;

    public bool IsAnyActive => _sessions.Values.Any(s => IsActive(s.State));

    public ServiceState GetState(Guid serviceId) =>
        _sessions.TryGetValue(serviceId, out var session) ? session.State : ServiceState.Stopped;

    public string? GetMessage(Guid serviceId) =>
        _sessions.TryGetValue(serviceId, out var session) ? session.Message : null;

    public IReadOnlyList<LogEntry> GetLogSince(Guid serviceId, long afterSequence)
    {
        if (!_sessions.TryGetValue(serviceId, out var session))
            return [];
        lock (session.Log)
            return session.Log.Where(e => e.Sequence > afterSequence).ToList();
    }

    public void ClearLog(Guid serviceId)
    {
        if (!_sessions.TryGetValue(serviceId, out var session))
            return;
        lock (session.Log)
            session.Log.Clear();
    }

    public async Task<StartResult> StartProjectAsync(Project project, IReadOnlyList<EnvProfile> profiles, CancellationToken cancellationToken = default)
    {
        var order = ServiceGraph.Order(project.Services);
        if (order is null)
            return new StartResult(["Die Dienste hängen im Kreis voneinander ab."]);

        var (vaultValues, vaultErrors) = await FetchVaultValuesAsync(project, project.Services, profiles, cancellationToken);
        if (vaultErrors.Count > 0)
            return new StartResult(vaultErrors);

        var environments = project.Services.ToDictionary(s => s.Id, s => EnvResolver.Resolve(project, s, profiles, vaultValues));
        var environmentErrors = environments.Values.SelectMany(e => e.Errors).Distinct().ToList();
        if (environmentErrors.Count > 0)
            return new StartResult(environmentErrors);

        // Bereits laufende Dienste gelten als bereit, alle anderen warten sichtbar auf ihren Start.
        var ready = order.Where(s => IsActive(GetState(s.Id))).Select(s => s.Id).ToHashSet();
        var pending = order.Where(s => !ready.Contains(s.Id)).ToList();
        foreach (var service in pending)
        {
            var waiting = new Session(project, service, runners.Find(service.RunnerId), environments[service.Id]);
            _sessions[service.Id] = waiting;
            SetState(waiting, ServiceState.Waiting, null);
        }

        var known = project.Services.Select(s => s.Id).ToHashSet();
        var errors = new List<string>();
        foreach (var service in pending)
        {
            var session = _sessions[service.Id];
            if (session.StopRequested)
                continue;

            var missing = service.DependsOn.Where(id => known.Contains(id) && !ready.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                var names = string.Join(", ", project.Services.Where(s => missing.Contains(s.Id)).Select(s => s.Name));
                var message = $"Nicht gestartet, weil nicht bereit: {names}";
                Append(session, LogKind.Error, message);
                SetState(session, ServiceState.Blocked, message);
                errors.Add($"{Label(service)}: {message}");
                continue;
            }

            var (isReady, problems) = await RunAsync(session);
            errors.AddRange(problems);
            if (isReady)
                ready.Add(service.Id);

            if (!ReferenceEquals(service, pending[^1]) && service.StartDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(service.StartDelaySeconds), cancellationToken);
        }

        return new StartResult(errors);
    }

    public async Task<StartResult> StartServiceAsync(Project project, ServiceDefinition service, IReadOnlyList<EnvProfile> profiles)
    {
        var (vaultValues, vaultErrors) = await FetchVaultValuesAsync(project, [service], profiles, CancellationToken.None);
        if (vaultErrors.Count > 0)
            return new StartResult(vaultErrors);

        var environment = EnvResolver.Resolve(project, service, profiles, vaultValues);
        if (environment.Errors.Count > 0)
            return new StartResult(environment.Errors);
        if (IsActive(GetState(service.Id)))
            return StartResult.Ok;

        var session = new Session(project, service, runners.Find(service.RunnerId), environment);
        _sessions[service.Id] = session;
        var (_, errors) = await RunAsync(session);
        return new StartResult(errors);
    }

    public async Task StopServiceAsync(Guid serviceId)
    {
        if (!_sessions.TryGetValue(serviceId, out var session) || !IsActive(session.State))
            return;

        session.StopRequested = true;
        await session.StopSignal.CancelAsync();
        SetState(session, ServiceState.Stopping, null);

        var stop = session.Runner?.BuildStop(session.Service);
        if (stop is not null && session.Process is not null)
        {
            using var timeout = new CancellationTokenSource(StopCommandTimeout);
            if (await RunHelperAsync(session, stop, LogKind.System, timeout.Token) is null)
                Append(session, LogKind.System, "Der Stopp-Befehl ist fehlgeschlagen; der Prozess wird beendet.");
        }

        if (session.Process is { } process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(KillTimeout);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or OperationCanceledException or NotSupportedException)
            {
                Append(session, LogKind.System, $"Prozess konnte nicht sauber beendet werden: {e.Message}");
            }
        }

        // Ohne laufenden Prozess, etwa während des Wartens, meldet niemand sonst das Ende.
        var finished = false;
        lock (session.Gate)
        {
            if (session.State == ServiceState.Stopping && (session.Finished || session.Process is null))
            {
                session.State = ServiceState.Stopped;
                finished = true;
            }
        }

        if (finished)
            RaiseState(session);
    }

    public async Task StopProjectAsync(Project project)
    {
        var order = ServiceGraph.Order(project.Services) ?? project.Services;
        foreach (var service in order.Reverse())
            await StopServiceAsync(service.Id);
    }

    public Task StopAllAsync() => Task.WhenAll(_sessions.Keys.Select(StopServiceAsync));

    public void Dispose() => _job.Dispose();

    private Task<(IReadOnlyDictionary<Guid, string> Values, IReadOnlyList<string> Errors)> FetchVaultValuesAsync(
        Project project, IEnumerable<ServiceDefinition> services, IReadOnlyList<EnvProfile> profiles, CancellationToken token)
    {
        var variables = services.Where(s => !IsActive(GetState(s.Id)))
            .SelectMany(s => EnvResolver.Winners(project, s, profiles).Values);
        return VaultFetcher.FetchAsync(variables, _secretProviders, token);
    }

    private static bool IsActive(ServiceState state) =>
        state is ServiceState.Waiting or ServiceState.Starting or ServiceState.Running or ServiceState.Stopping;

    private static string Label(ServiceDefinition service) =>
        string.IsNullOrWhiteSpace(service.Name) ? "Dienst" : service.Name;

    private async Task<(bool Ready, List<string> Errors)> RunAsync(Session session)
    {
        var service = session.Service;
        var label = Label(service);
        if (session.Runner is null)
        {
            var missing = $"{label}: Der Runner „{service.RunnerId}“ ist nicht verfügbar.";
            SetState(session, ServiceState.Failed, missing);
            return (false, [missing]);
        }

        var problems = Validate(session.Runner, service).Select(p => $"{label}: {p}").ToList();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                Append(session, LogKind.Error, problem);
            SetState(session, ServiceState.Failed, problems[0]);
            return (false, problems);
        }

        SetState(session, ServiceState.Starting, null);

        foreach (var step in service.PreSteps.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            Append(session, LogKind.System, $"> {step}");
            var exitCode = await RunHelperAsync(session, ProcessCommand.ViaCmd(step), LogKind.Output, session.StopSignal.Token);
            if (session.StopRequested)
                return (false, []);
            if (exitCode != 0)
            {
                var message = $"{label}: Vorbereitung „{step}“ ist fehlgeschlagen.";
                Append(session, LogKind.Error, message);
                session.Finished = true;
                SetState(session, ServiceState.Failed, message);
                return (false, [message]);
            }
        }

        var hasReadiness = service.Readiness.Kind != ReadinessKind.None;
        var error = Spawn(session, markRunning: !hasReadiness);
        if (error is not null)
            return (false, [error]);
        if (!hasReadiness)
            return (session.State != ServiceState.Failed, []);

        if (await WaitUntilReadyAsync(session))
        {
            MarkRunning(session, null);
            return (true, []);
        }

        if (session.StopRequested || session.Finished)
            return (false, []);

        var notReady = $"{label}: Nach {service.Readiness.TimeoutSeconds} s nicht bereit.";
        Append(session, LogKind.Error, notReady);
        MarkRunning(session, notReady);
        return (false, [notReady]);
    }

    private static async Task<bool> WaitUntilReadyAsync(Session session)
    {
        var check = session.Service.Readiness;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, check.TimeoutSeconds));
        Append(session, LogKind.System, check.Kind == ReadinessKind.Port ? $"Warte auf Port {check.Port} …" : $"Warte auf {check.Url} …");

        while (DateTime.UtcNow < deadline && !session.Finished && !session.StopRequested)
        {
            if (await IsReadyAsync(check, session.StopSignal.Token))
                return true;

            try
            {
                await Task.Delay(ReadinessPollInterval, session.StopSignal.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<bool> IsReadyAsync(ReadinessCheck check, CancellationToken token)
    {
        try
        {
            if (check.Kind == ReadinessKind.Port)
            {
                // „localhost“ versucht zuerst IPv6 und läuft bei reinen IPv4-Diensten ins Zeitlimit.
                return await CanConnectAsync(IPAddress.Loopback, check.Port, token)
                    || await CanConnectAsync(IPAddress.IPv6Loopback, check.Port, token);
            }

            using var response = await ReadinessClient.GetAsync(check.Url, token);
            return (int)response.StatusCode < 500;
        }
        catch (Exception e) when (e is SocketException or HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <returns>Eine Fehlermeldung oder null bei Erfolg.</returns>
    private string? Spawn(Session session, bool markRunning)
    {
        var service = session.Service;
        var command = session.Runner!.BuildStart(service);
        var terminal = service.OutputMode == OutputMode.Terminal || session.Runner.RequiresTerminal(service);
        var startInfo = CreateStartInfo(service, command, session.Environment, terminal);
        Append(session, LogKind.System, $"> {command.ToCommandLine()}");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        lock (session.Gate)
        {
            session.Process = process;
            session.IsTerminal = terminal;
            session.Finished = false;
        }

        process.Exited += (_, _) => OnExited(session, process);
        if (!terminal)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(session, LogKind.Output, e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(session, LogKind.Error, e.Data); };
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            var message = $"{Label(service)}: „{command.FileName}“ konnte nicht gestartet werden. Ist es installiert und im PATH? ({e.Message})";
            Append(session, LogKind.Error, message);
            session.Finished = true;
            SetState(session, ServiceState.Failed, message);
            return message;
        }

        _job.Assign(process.Handle);

        if (terminal)
        {
            Append(session, LogKind.System, "Läuft in einem eigenen Terminalfenster.");
        }
        else
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        if (markRunning)
            MarkRunning(session, null);
        return null;
    }

    private void MarkRunning(Session session, string? message)
    {
        lock (session.Gate)
        {
            if (session.Finished || session.StopRequested)
                return;
            session.State = ServiceState.Running;
            session.Message = message;
        }

        RaiseState(session);
        Publish(session, AppEventKind.Started, null);
    }

    private static IEnumerable<string> Validate(IRunner runner, ServiceDefinition service)
    {
        if (string.IsNullOrWhiteSpace(service.WorkingDirectory))
        {
            if (runner.RequiresWorkingDirectory)
                yield return "Es ist kein Arbeitsordner gewählt.";
        }
        else if (!Directory.Exists(service.WorkingDirectory))
        {
            yield return $"Der Arbeitsordner „{service.WorkingDirectory}“ existiert nicht.";
        }

        if (service.Readiness.Kind == ReadinessKind.Port && service.Readiness.Port is < 1 or > 65535)
            yield return "Für die Bereitschaftsprüfung fehlt ein gültiger Port.";
        if (service.Readiness.Kind == ReadinessKind.Http && !Uri.TryCreate(service.Readiness.Url, UriKind.Absolute, out _))
            yield return "Für die Bereitschaftsprüfung fehlt eine gültige URL.";

        foreach (var problem in runner.Validate(service))
            yield return problem;
    }

    private static ProcessStartInfo CreateStartInfo(ServiceDefinition service, ProcessCommand command, ResolvedEnvironment environment, bool terminal)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(service.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : service.WorkingDirectory,
        };

        if (terminal && !command.SelfHostedTerminal)
        {
            // Über /k bleibt das Fenster offen, damit Fehlermeldungen lesbar sind.
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /k \"{command.ToCommandLine()}\"";
        }
        else
        {
            startInfo.FileName = command.FileName;
            if (command.RawArguments is not null)
                startInfo.Arguments = command.RawArguments;
            else
                foreach (var argument in command.Arguments)
                    startInfo.ArgumentList.Add(argument);
        }

        if (!terminal)
        {
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (var (name, value) in command.Environment)
            startInfo.Environment[name] = value;
        foreach (var (name, value) in environment.Variables)
            startInfo.Environment[name] = value;

        if (command.PrependPath.Count > 0)
        {
            startInfo.Environment.TryGetValue("PATH", out var path);
            startInfo.Environment["PATH"] = string.Join(';', command.PrependPath) + ";" + path;
        }

        return startInfo;
    }

    /// <returns>Der Exit-Code oder null, wenn der Befehl nicht lief oder abgebrochen wurde.</returns>
    private async Task<int?> RunHelperAsync(Session session, ProcessCommand command, LogKind outputKind, CancellationToken token)
    {
        var startInfo = CreateStartInfo(session.Service, command, session.Environment, terminal: false);
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(session, outputKind, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(session, outputKind == LogKind.Output ? LogKind.Error : outputKind, e.Data); };

        try
        {
            process.Start();
            _job.Assign(process.Handle);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // Der Prozess ist bereits beendet.
            }

            return null;
        }
        catch (Win32Exception e)
        {
            Append(session, LogKind.Error, $"„{command.FileName}“ konnte nicht gestartet werden: {e.Message}");
            return null;
        }
    }

    private void OnExited(Session session, Process process)
    {
        if (!ReferenceEquals(session.Process, process))
            return;

        if (!session.IsTerminal)
            process.WaitForExit(2000); // Letzte Zeilen der Ausgabe noch einsammeln.

        var exitCode = process.ExitCode;
        var failed = !session.StopRequested && !session.IsTerminal && exitCode != 0;
        var message = failed ? DescribeFailure(exitCode) : null;
        var restartDelay = failed ? NextRestartDelay(session) : null;

        lock (session.Gate)
        {
            session.Finished = true;
            session.State = restartDelay is not null ? ServiceState.Starting
                : failed ? ServiceState.Failed
                : ServiceState.Stopped;
            session.Message = restartDelay is { } delay ? $"Neustart in {delay.TotalSeconds:0} s …" : message;
        }

        Append(session, failed ? LogKind.Error : LogKind.System, message ?? $"Beendet (Exit-Code {exitCode}).");
        if (failed && restartDelay is null && session.Service.RestartPolicy == RestartPolicy.OnFailure)
            Append(session, LogKind.Error, $"Nach {MaxRestarts} Neustarts aufgegeben.");

        RaiseState(session);
        Publish(session, failed ? AppEventKind.Crashed : AppEventKind.Stopped, exitCode);

        if (restartDelay is { } wait)
            _ = RestartAfterAsync(session, wait);
    }

    private static TimeSpan? NextRestartDelay(Session session)
    {
        if (session.Service.RestartPolicy != RestartPolicy.OnFailure)
            return null;

        var now = DateTime.UtcNow;
        session.RestartTimes.RemoveAll(t => now - t > RestartWindow);
        if (session.RestartTimes.Count >= MaxRestarts)
            return null;

        session.RestartTimes.Add(now);
        return TimeSpan.FromSeconds(Math.Pow(2, session.RestartTimes.Count - 1));
    }

    private async Task RestartAfterAsync(Session session, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, session.StopSignal.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!session.StopRequested)
        {
            Append(session, LogKind.System, $"Neustart ({session.RestartTimes.Count}/{MaxRestarts}) …");
            Spawn(session, markRunning: true);
        }
    }

    private static string DescribeFailure(int exitCode) => exitCode == CommandNotFoundExitCode
        ? "Der Befehl wurde nicht gefunden. Ist die Laufzeit installiert und im PATH?"
        : $"Unerwartet beendet (Exit-Code {exitCode}).";

    private void SetState(Session session, ServiceState state, string? message)
    {
        lock (session.Gate)
        {
            session.State = state;
            session.Message = message;
        }

        RaiseState(session);
    }

    private void RaiseState(Session session) =>
        StateChanged?.Invoke(new ServiceStateChange(session.Project.Id, session.Service.Id, session.State, session.Message));

    private void Publish(Session session, AppEventKind kind, int? exitCode)
    {
        var appEvent = new AppEvent(kind, session.Project, session.Service, exitCode);
        foreach (var handler in _handlers)
        {
            try
            {
                handler.Handle(appEvent);
            }
            catch (Exception e)
            {
                Append(session, LogKind.System, $"Fehler in {handler.GetType().Name}: {e.Message}");
            }
        }
    }

    private static void Append(Session session, LogKind kind, string text)
    {
        foreach (var secret in session.MaskedValues)
            text = text.Replace(secret, "••••••");

        var (plain, spans) = Ansi.Parse(text);
        lock (session.Log)
        {
            session.Log.Add(new LogEntry(++session.LastSequence, DateTime.Now, kind, plain, spans));
            if (session.Log.Count > MaxLogLines)
                session.Log.RemoveRange(0, session.Log.Count - MaxLogLines);
        }
    }

    private sealed class Session(Project project, ServiceDefinition service, IRunner? runner, ResolvedEnvironment environment)
    {
        public Project Project { get; } = project;
        public ServiceDefinition Service { get; } = service;
        public IRunner? Runner { get; } = runner;
        public ResolvedEnvironment Environment { get; } = environment;

        /// <summary>Längste zuerst, damit überlappende Secrets vollständig verdeckt werden.</summary>
        public IReadOnlyList<string> MaskedValues { get; } =
            environment.SecretValues.Where(s => s.Length >= 3).OrderByDescending(s => s.Length).ToList();

        public Lock Gate { get; } = new();
        public List<LogEntry> Log { get; } = [];
        public long LastSequence { get; set; }
        public Process? Process { get; set; }
        public bool IsTerminal { get; set; }
        public volatile bool StopRequested;
        public volatile bool Finished;
        public CancellationTokenSource StopSignal { get; } = new();
        public List<DateTime> RestartTimes { get; } = [];
        public ServiceState State { get; set; } = ServiceState.Stopped;
        public string? Message { get; set; }
    }
}
