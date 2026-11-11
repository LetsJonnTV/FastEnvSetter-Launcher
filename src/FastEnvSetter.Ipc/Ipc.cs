using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastEnvSetter.Ipc;

public sealed record CliResponse(int Code, string Output);

public static class PipeProtocol
{
    public const int MaximumMessageLength = 1048576;

    public static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var message = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return message.Length == 0 ? null : message.ToString();
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (message.Length + length > MaximumMessageLength)
                throw new IOException("IPC message exceeds the size limit.");
            message.Append(buffer, 0, length);
            if (newline >= 0) return message.ToString().TrimEnd('\r');
        }
    }
}

public static class PipeNames
{
    /// <param name="dataPath">Pro Datendatei eine eigene Instanz, damit Tests mit --data unabhängig bleiben.</param>
    public static string For(string? dataPath)
    {
        var path = dataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastEnvSetter", "data.json");
        var key = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            key = key.ToUpperInvariant();
        return "FastEnvSetter-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }
}

public static class PipeClient
{
    public static async Task<CliResponse> SendAsync(string pipeName, string[] arguments, int connectTimeoutMs = 10000)
    {
        var message = JsonSerializer.Serialize(arguments);
        if (message.Length > PipeProtocol.MaximumMessageLength)
            throw new IOException("IPC message exceeds the size limit.");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeoutMs);
        using var timeout = new CancellationTokenSource(arguments.FirstOrDefault() == "api" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(message.AsMemory(), timeout.Token);
        var response = await PipeProtocol.ReadLineAsync(reader, timeout.Token);
        return response is null
            ? new CliResponse(1, "Die laufende Instanz hat nicht geantwortet.")
            : JsonSerializer.Deserialize<CliResponse>(response)!;
    }
}

/// <summary>Stellt sicher, dass nur eine Instanz läuft, und nimmt Befehle anderer Prozesse an.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    public SingleInstance(string pipeName)
    {
        PipeName = pipeName;
        _mutex = new Mutex(initiallyOwned: true, $"Local\\{pipeName}", out var created);
        IsFirst = created;
    }

    public string PipeName { get; }
    public bool IsFirst { get; }

    /// <summary>Prüft, ob eine Instanz läuft, ohne selbst die Sperre zu belegen.</summary>
    public static bool IsRunning(string pipeName)
    {
        if (!Mutex.TryOpenExisting($"Local\\{pipeName}", out var mutex))
            return false;

        mutex.Dispose();
        return true;
    }

    public void Listen(Func<string[], Task<CliResponse>> handler) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                _ = ServeAsync(pipe, handler);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<string[], Task<CliResponse>> handler)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await PipeProtocol.ReadLineAsync(reader, timeout.Token);
                var arguments = line is null ? [] : JsonSerializer.Deserialize<string[]>(line) ?? [];
                if (arguments.Any(argument => argument is null))
                    throw new JsonException();
                await writer.WriteLineAsync(JsonSerializer.Serialize(await handler(arguments)));
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                // Der aufrufende Prozess ist bereits weg oder hat Unsinn geschickt.
            }
        }
    }
}
