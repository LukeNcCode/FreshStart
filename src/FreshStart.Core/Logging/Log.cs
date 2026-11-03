using System.Text;
using System.Threading.Channels;

namespace FreshStart.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 轻量异步日志：UI 线程只负责入队，落盘由后台单线程完成，按天滚动。
/// 故意不引入 Serilog/NLog，减少启动期程序集加载。
/// </summary>
public static class Log
{
    private static readonly Channel<LogEntry> Channel =
        System.Threading.Channels.Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim WriterLock = new(1, 1);
    private static Task? _writer;
    private static string _currentDate = string.Empty;
    private static StreamWriter? _writerStream;
    private static volatile bool _initialized;

    public static string? CurrentLogFile { get; private set; }

    public static void Init()
    {
        if (_initialized) return;
        lock (StateLock)
        {
            if (_initialized) return;
            _initialized = true;
            _writer = Task.Run(WriteLoopAsync);
        }
        Info("---- FreshStart 日志启动 ----");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        Write(LogLevel.Error, text);
    }

    private static void Write(LogLevel level, string message)
    {
        if (!_initialized)
        {
            // 日志系统未初始化时退化为控制台输出，避免异常吞噬信息
            System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
            return;
        }

        Channel.Writer.TryWrite(new LogEntry(DateTimeOffset.Now, level, message));
    }

    private static async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var entry in Channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var lockTaken = false;
                try
                {
                    await WriterLock.WaitAsync().ConfigureAwait(false);
                    lockTaken = true;
                    await EnsureWriterAsync(entry.Timestamp).ConfigureAwait(false);
                    if (_writerStream is null) continue;
                    var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level.ToString().ToUpperInvariant(),-5}] {entry.Message}";
                    await _writerStream.WriteLineAsync(line).ConfigureAwait(false);
                    await _writerStream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 日志失败不能影响主流程
                }
                finally
                {
                    if (lockTaken) WriterLock.Release();
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static Task EnsureWriterAsync(DateTimeOffset timestamp)
    {
        var date = timestamp.ToString("yyyy-MM-dd");
        if (date == _currentDate && _writerStream is not null) return Task.CompletedTask;

        try { _writerStream?.Dispose(); } catch { /* ignore */ }

        Directory.CreateDirectory(AppPaths.LogRoot);
        var path = Path.Combine(AppPaths.LogRoot, $"freshstart-{date}.log");
        _writerStream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        _currentDate = date;
        CurrentLogFile = path;
        return Task.CompletedTask;
    }

    /// <summary>进程退出前调用，尽量把队列写完。</summary>
    public static void Flush(TimeSpan timeout)
    {
        // 日志系统未初始化时没有任何写入者，直接返回，避免白等到超时
        if (!_initialized) return;

        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Channel.Reader.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !WriterLock.Wait(remaining)) return;
            try
            {
                _writerStream?.Flush();
            }
            finally
            {
                WriterLock.Release();
            }
        }
        catch { /* ignore */ }
    }

    private readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message);
}
