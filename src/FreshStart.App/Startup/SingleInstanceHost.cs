using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using FreshStart.Core;

namespace FreshStart.App.Startup;

/// <summary>
/// 单实例宿主：命名 Mutex 保证唯一，命名管道接收二次启动的参数。
/// 管道名带 SessionId，避免多用户/远程会话互相干扰。
/// </summary>
public sealed class SingleInstanceHost : IDisposable
{
    private readonly string _pipeName;
    private readonly string _mutexName;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public SingleInstanceHost()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var suffix = Environment.GetEnvironmentVariable("FRESHSTART_INSTANCE_SUFFIX");
        var suffixPart = string.IsNullOrWhiteSpace(suffix)
            ? string.Empty
            : "." + new string(suffix.Where(char.IsLetterOrDigit).Take(32).ToArray());
        var versionPart = GetInstanceVersionPart();

        _pipeName = $"FreshStart.Activate.{session}.{versionPart}{suffixPart}";
        _mutexName = $@"Local\FreshStart.Host.{session}.{versionPart}{suffixPart}";
    }

    private static string GetInstanceVersionPart()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (Version.TryParse(text, out var version))
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
        }
        catch { /* ignore */ }

        return "dev";
    }

    public event EventHandler<string[]>? ActivationReceived;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (createdNew) return true;

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"创建单实例互斥体失败: {ex.Message}");
            return true;
        }
    }

    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.Equals(header, "activate", StringComparison.Ordinal)) continue;

                var countText = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!int.TryParse(countText, out var count) || count is < 0 or > 64) count = 0;

                var args = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    args.Add(line);
                }

                Log.Info($"收到二次启动激活请求，参数 {args.Count} 个");
                ActivationReceived?.Invoke(this, args.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"单实例管道异常: {ex.Message}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    /// <summary>向已运行的实例发送激活请求。带重试，覆盖对方刚启动、管道尚未就绪的窗口期。</summary>
    public async Task<bool> ActivateExistingAsync(string[] args, int timeoutMs = 1200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(250).ConfigureAwait(false);

                await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("activate").ConfigureAwait(false);
                var forwarded = args.Length > 64 ? args[..64] : args;
                await writer.WriteLineAsync(forwarded.Length.ToString()).ConfigureAwait(false);
                foreach (var arg in forwarded)
                {
                    await writer.WriteLineAsync(arg.ReplaceLineEndings(" ")).ConfigureAwait(false);
                }
                return true;
            }
            catch (TimeoutException)
            {
                await Task.Delay(120).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"激活已有实例失败: {ex.Message}");
                await Task.Delay(120).ConfigureAwait(false);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();
    }
}
