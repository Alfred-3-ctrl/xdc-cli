using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Xdc.NativeDesktop;

internal sealed partial class BusinessClient : IDisposable
{
    private sealed class Worker(Process process, NamedPipeServerStream pipe)
    {
        public Process Process { get; } = process;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public StreamWriter? Writer { get; set; }
        public SemaphoreSlim WriteGate { get; } = new(1);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> Requests { get; } = new();
    }

    private readonly NativeOptions options;
    private readonly SemaphoreSlim gate = new(1);
    private Worker? worker;
    private int id;
    private bool disposed;
    public event Action<JsonElement>? Changed;
    public int? ProcessId => worker is { } current && !current.Process.HasExited ? current.Process.Id : null;

    public BusinessClient(NativeOptions options) => this.options = options;

    public async Task<JsonElement> CallAsync(string method, object? payload = null)
    {
        Task<JsonElement> pending;
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = worker;
            if (current is null || current.Process.HasExited)
            {
                current?.Pipe.Dispose();
                current?.Process.Dispose();
                worker = current = StartWorker();
            }
            await current.Ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
            pending = SendAsync(current, method, payload);
        }
        finally
        {
            gate.Release();
        }
        return await pending;
    }

    public async Task StopAsync(bool onlyIfIdle)
    {
        await gate.WaitAsync();
        try
        {
            var current = worker;
            if (current is null || current.Process.HasExited) return;
            if (onlyIfIdle && (!current.Ready.Task.IsCompletedSuccessfully || !current.Requests.IsEmpty)) return;
            if (current.Ready.Task.IsCompletedSuccessfully)
            {
                var result = await SendAsync(current, "service.shutdown", new { ifIdle = onlyIfIdle });
                if (result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return;
            }
            current.Pipe.Dispose();
            await current.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            gate.Release();
        }
    }

    private Worker StartWorker()
    {
        string pipeName = "xdc-native-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var configuration = new ProcessStartInfo
        {
            FileName = Path.Combine(options.Desktop, "node_modules", "electron", "dist", "electron.exe"),
            WorkingDirectory = options.Desktop,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        configuration.ArgumentList.Add(Path.Combine(options.Desktop, "native-service"));
        configuration.ArgumentList.Add("--profile");
        configuration.ArgumentList.Add(options.Profile);
        configuration.ArgumentList.Add("--pipe");
        configuration.ArgumentList.Add(pipeName);
        configuration.Environment.Remove("ELECTRON_RUN_AS_NODE");
        if (!options.Managed && !configuration.Environment.ContainsKey("XD_CONFIG_DIR"))
            configuration.Environment["XD_CONFIG_DIR"] = Path.Combine(options.Profile, "sessions");
        try
        {
            var process = Process.Start(configuration) ?? throw new InvalidOperationException("无法启动学习桌面业务服务。");
            var current = new Worker(process, pipe);
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            _ = ReadAsync(current);
            return current;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private async Task<JsonElement> SendAsync(Worker current, string method, object? payload)
    {
        int requestId = ++id;
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        current.Requests[requestId] = answer;
        try
        {
            var line = JsonSerializer.Serialize(new { id = requestId, method, payload = payload ?? new object() });
            await current.WriteGate.WaitAsync();
            try
            {
                if (current.Writer is not { } writer) throw new InvalidOperationException("业务服务尚未连接。");
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
            finally { current.WriteGate.Release(); }
            return await answer.Task;
        }
        finally
        {
            current.Requests.TryRemove(requestId, out _);
        }
    }

    private async Task ReadAsync(Worker current)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await current.Pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(current.Pipe.SafePipeHandle, out uint clientPid) ||
                clientPid != current.Process.Id)
            {
                current.Pipe.Dispose();
                throw new IOException("业务服务进程身份与管道客户端不符。");
            }
            using var reader = new StreamReader(current.Pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(current.Pipe, new UTF8Encoding(false), leaveOpen: true);
            current.Writer = writer;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var response = JsonDocument.Parse(line);
                var record = response.RootElement;
                if (record.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True)
                    current.Ready.TrySetResult();
                else if (record.TryGetProperty("event", out var notification))
                    Changed?.Invoke(notification.Clone());
                else if (record.TryGetProperty("id", out var identifier) &&
                    identifier.TryGetInt32(out int key) && current.Requests.TryGetValue(key, out var pending))
                {
                    if (record.TryGetProperty("error", out var error))
                        pending.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                    else
                        pending.TrySetResult(record.GetProperty("result").Clone());
                }
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (JsonException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            var failure = new InvalidOperationException("学习桌面业务服务已结束，请刷新重试。");
            current.Ready.TrySetException(failure);
            foreach (var request in current.Requests.Values) request.TrySetException(failure);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (worker is not { } current) return;
        current.Pipe.Dispose();
        current.Process.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
