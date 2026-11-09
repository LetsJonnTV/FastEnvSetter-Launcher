using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastEnvSetter.Ipc;

public sealed record CliResponse(int Code, string Output);

public static class PipeNames
{
    /// <param name="dataPath">Pro Datendatei eine eigene Instanz, damit Tests mit --data unabhängig bleiben.</param>
    public static string For(string? dataPath)
    {
        var key = dataPath is null ? "" : Path.GetFullPath(dataPath);
        return "FastEnvSetter-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }
}

public static class PipeClient
{
    public static async Task<CliResponse> SendAsync(string pipeName, string[] arguments, int connectTimeoutMs = 10000)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeoutMs);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(arguments));
        var response = await reader.ReadLineAsync();
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
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                var arguments = line is null ? [] : JsonSerializer.Deserialize<string[]>(line) ?? [];
                await writer.WriteLineAsync(JsonSerializer.Serialize(await handler(arguments)));
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
            {
                // Der aufrufende Prozess ist bereits weg oder hat Unsinn geschickt.
            }
        }
    }
}
