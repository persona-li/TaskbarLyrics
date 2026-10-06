using System.IO;
using System.Text;
using TaskbarLyrics.Cache;
using TaskbarLyrics.Core.Logging;
using TaskbarLyrics.Gsmtc;
using TaskbarLyrics.QQMusic.Logging;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.Windows;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Unified file logger under %LOCALAPPDATA%\TaskbarLyrics\Logs\
/// Implements all probe logger interfaces.
/// </summary>
public sealed class AppLogger : IAppLogger, IGsmtcLogger, IQqMusicLogger, ICacheLogger, IWindowsLogger, IDisposable
{
    private readonly object _lock = new();
    public const long MaximumTotalBytes = 20 * 1024 * 1024;
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private StreamWriter _writer = null!;
    private DateTime _fileDate;
    private bool _disposed;

    public AppLogger(string? directory = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory ?? CachePaths.LogsRoot;
        _now = now ?? (() => DateTimeOffset.Now);
        Directory.CreateDirectory(_directory);
        OpenFile(_now());
        Info($"======== TaskbarLyrics session {_now():o} ========");
    }

    public string LogFilePath { get; private set; } = "";

    private void OpenFile(DateTimeOffset now)
    {
        _writer?.Dispose();
        _fileDate = now.Date;
        // Reserve a complete segment so the directory stays below the cap as it grows.
        var files = new DirectoryInfo(_directory).GetFiles("taskbar-lyrics-*.log")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name,
                @"^taskbar-lyrics-\d{8}(?:-\d{9}-[a-f0-9]{32})?\.log$"))
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in files.ToArray())
        {
            if (DateTime.TryParseExact(file.Name.Substring(15, 8), "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) && date < now.Date.AddDays(-6))
            {
                try { file.Delete(); files.Remove(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= MaximumTotalBytes - MaximumFileBytes) break;
            try { var size = file.Length; file.Delete(); total -= size; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        LogFilePath = Path.Combine(_directory, $"taskbar-lyrics-{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
        _writer = new StreamWriter(new FileStream(LogFilePath, FileMode.CreateNew,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        // If old files are locked, stop writing instead of exceeding the total limit.
        _remainingBytes = Math.Max(0, Math.Min(MaximumFileBytes, MaximumTotalBytes - total));
    }

    private long _remainingBytes;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string stage, Exception ex)
    {
        Write("ERROR",
            $"[{stage}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}" +
            $"HResult=0x{ex.HResult:X8}{Environment.NewLine}{ex.StackTrace}");
        if (ex.InnerException is not null)
        {
            Write("ERROR", $"[{stage}] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    public void Http(string stage, HttpDiagnostics d)
    {
        Write("HTTP",
            $"[{stage}] {d.Method} {d.Url} status={d.StatusCode} elapsed={d.Elapsed.TotalMilliseconds:F0}ms len={d.Body.Length}");
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _writer.Flush();
            }
        }
    }

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var now = _now();
                if (message.Length > 16384) message = message[..16384] + " [truncated]";
                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                var bytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                if (now.Date != _fileDate || _writer.BaseStream.Length + bytes > MaximumFileBytes)
                    OpenFile(now);
                if (bytes > _remainingBytes) return;
                _writer.WriteLine(line);
                _remainingBytes -= bytes;
            }
            catch
            {
                // never throw
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
