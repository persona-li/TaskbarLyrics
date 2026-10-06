using System.IO;
using System.IO.Pipes;
using System.Text;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Named mutex + named pipe SHOW_SETTINGS IPC.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = "Local\\TaskbarLyrics.SingleInstance";
    public const string PipeName = "TaskbarLyrics.Ipc";
    public const string ShowSettingsCommand = "SHOW_SETTINGS";

    private readonly Mutex _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _listenCts;
    private bool _disposed;

    public event Action? ShowSettingsRequested;
    public event Action? ExitRequested;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
    }

    public bool IsPrimaryInstance => _isPrimary;

    public void StartListening()
    {
        if (!_isPrimary)
        {
            return;
        }

        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        _ = Task.Run(() => ListenLoop(token), token);
    }

    public static bool TryNotifyPrimaryShowSettings() => TrySend(ShowSettingsCommand);

    public static bool TryNotifyPrimaryExit(string executablePath) => TrySend("EXIT:" + executablePath);

    public static bool IsExitCommandFor(string command, string executablePath) =>
        string.Equals(command, "EXIT:" + executablePath, StringComparison.OrdinalIgnoreCase);

    private static bool TrySend(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(800);
            var bytes = Encoding.UTF8.GetBytes(command);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var cmd = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                if (string.Equals(cmd, ShowSettingsCommand, StringComparison.OrdinalIgnoreCase))
                {
                    ShowSettingsRequested?.Invoke();
                }
                else if (IsExitCommandFor(cmd, Environment.ProcessPath ?? ""))
                {
                    ExitRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(200, token).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _listenCts?.Cancel(); } catch { /* ignore */ }
        try { _listenCts?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_isPrimary)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
