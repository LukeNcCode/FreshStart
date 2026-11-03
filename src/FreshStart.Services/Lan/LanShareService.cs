using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FreshStart.Core;

namespace FreshStart.Services.Lan;

/// <summary>
/// 极简局域网只读文件共享：绑定本机局域网 IPv4，token 路径鉴权，仅提供目录浏览与文件下载。
/// 不监听公网地址，不提供上传/删除，不做目录逃逸。
/// </summary>
public sealed class LanShareService : IDisposable
{
    /// <summary>并发连接上限，避免局域网内被大量连接拖住。</summary>
    private const int MaxConcurrentClients = 8;

    /// <summary>单个请求的最长处理时间（含读取请求行与传输文件）；大文件传输也要留足时间。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>单页最多展示的目录项，避免超大目录一次构建几十万行 HTML。</summary>
    private const int MaxListingEntries = 5000;

    /// <summary>请求行长度上限，防止没有换行的超长行撑爆内存。</summary>
    private const int MaxRequestLineLength = 2048;

    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _root = string.Empty;
    private string _token = string.Empty;

    public bool IsRunning => _listener is not null;
    public string Url { get; private set; } = string.Empty;
    public string ShortCode { get; private set; } = string.Empty;

    public string Start(string folder)
    {
        Stop();
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);

        var address = GetLanAddress() ?? throw new InvalidOperationException("未找到可用的局域网 IPv4 地址");
        for (var port = 18080; port < 18200; port++)
        {
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                _listener = listener;
                break;
            }
            catch { /* 端口占用，继续 */ }
        }
        if (_listener is null) throw new InvalidOperationException("没有可用端口（18080–18199）");

        var fullRoot = Path.GetFullPath(folder);
        var pathRoot = Path.GetPathRoot(fullRoot);
        _root = string.Equals(fullRoot, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullRoot
            : fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ContainsReparsePoint(_root))
        {
            throw new InvalidOperationException("共享根目录或其父目录包含 junction / symbolic link，已拒绝启动");
        }

        _token = Guid.NewGuid().ToString("N")[..10];
        ShortCode = _token[..6].ToUpperInvariant();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = $"http://{endpoint.Address}:{endpoint.Port}/{_token}/";
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        Log.Info($"局域网共享已启动: {Url} -> {_root}");
        return Url;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Url = string.Empty;
        ShortCode = string.Empty;
        _root = string.Empty;
        _token = string.Empty;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn($"局域网监听暂时失败，将继续重试: {ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }
            _ = ServeClientAsync(client, ct);
        }
    }

    /// <summary>占用一个并发槽位后处理连接；超出上限的连接直接丢弃。</summary>
    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            if (!await _clientSlots.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            return;
        }

        try { await HandleClientAsync(client, ct).ConfigureAwait(false); }
        finally { _clientSlots.Release(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(HeaderTimeout);
                var requestLine = await reader.ReadLineAsync(headerCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                if (requestLine.Length > MaxRequestLineLength) return;

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestCts.CancelAfter(RequestTimeout);
                var token = requestCts.Token;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) return;
                var path = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
                if (!string.Equals(path, _token, StringComparison.Ordinal)
                    && !path.StartsWith(_token + "/", StringComparison.Ordinal))
                {
                    await WriteTextAsync(stream, "403 Forbidden", "无效的访问令牌", token).ConfigureAwait(false);
                    return;
                }

                var relative = path[_token.Length..].TrimStart('/');
                if (relative.Length == 0)
                {
                    await WriteListingAsync(stream, _root, token).ConfigureAwait(false);
                    return;
                }

                if (!TryResolveInsideRoot(relative, out var fullPath))
                {
                    await WriteTextAsync(stream, "403 Forbidden", "路径非法", token).ConfigureAwait(false);
                    return;
                }

                if (Directory.Exists(fullPath))
                {
                    await WriteListingAsync(stream, fullPath, token).ConfigureAwait(false);
                }
                else if (File.Exists(fullPath))
                {
                    await WriteFileAsync(stream, fullPath, token).ConfigureAwait(false);
                }
                else
                {
                    await WriteTextAsync(stream, "404 Not Found", "文件不存在", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"局域网请求处理失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 把请求路径解析成共享根目录内的绝对路径。
    /// 用"根目录 + 分隔符"做边界比较，避免 C:\Share 前缀匹配到 C:\Share-evli
    /// 这类兄弟目录（`..\` 与绝对路径一律拒绝）。
    /// </summary>
    private bool TryResolveInsideRoot(string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (relative.Length == 0 || _root.Length == 0) return false;

        var normalized = relative
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return false;
        // Windows 上冒号可指向备用数据流（file.txt:stream），一律拒绝
        if (normalized.Contains(':')) return false;

        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(_root, normalized)); }
        catch { return false; }

        var rootBoundary = _root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootBoundary, StringComparison.OrdinalIgnoreCase)) return false;
        if (ContainsReparsePoint(candidate)) return false;

        fullPath = candidate;
        return true;
    }

    private static bool ContainsReparsePoint(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root)) return true;

            var current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;

            var relative = full[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task WriteListingAsync(NetworkStream stream, string directory, CancellationToken ct)
    {
        var builder = new StringBuilder()
            .AppendLine("<html><meta charset=\"utf-8\"><title>FreshStart LAN</title>")
            .AppendLine("<style>body{font-family:Segoe UI,sans-serif;max-width:760px;margin:40px auto}" +
                        "a{display:block;padding:6px 0;color:#007AFF;text-decoration:none}</style>")
            .AppendLine("<h2>FreshStart 局域网共享</h2>");

        var parent = Directory.GetParent(directory);
        if (parent is not null && directory != _root)
        {
            var rel = Path.GetRelativePath(_root, parent.FullName).Replace('\\', '/');
            builder.AppendLine($"<a href=\"/{_token}/{Uri.EscapeDataString(rel)}\">../</a>");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var entries = Directory.EnumerateFileSystemEntries(directory, "*", options)
            .OrderBy(Path.GetFileName)
            .Take(MaxListingEntries + 1)
            .ToList();
        foreach (var entry in entries.Take(MaxListingEntries))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var rel = Path.GetRelativePath(_root, entry).Replace('\\', '/');
            var suffix = Directory.Exists(entry) ? "/" : "";
            var size = File.Exists(entry) ? $" <small>{new FileInfo(entry).Length / 1024.0:0.#} KB</small>" : "";
            builder.AppendLine($"<a href=\"/{_token}/{Uri.EscapeDataString(rel)}{suffix}\">{WebUtility.HtmlEncode(name)}{suffix}{size}</a>");
        }
        if (entries.Count > MaxListingEntries)
        {
            builder.AppendLine($"<p>目录项超过 {MaxListingEntries:N0} 个，请通过更具体的子目录访问。</p>");
        }
        builder.AppendLine("</html>");
        await WriteTextAsync(stream, "200 OK", builder.ToString(), ct).ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                     $"Content-Length: {info.Length}\r\nContent-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(info.Name)}\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await using var file = File.OpenRead(path);
        await file.CopyToAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static IPAddress? GetLanAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                // 跳过 Hyper-V / WSL / 虚拟机网卡：它们的地址局域网内不可达
                if (nic.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsVirtualAdapter(nic.Description)) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address.Address))
                    {
                        return address.Address;
                    }
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool IsVirtualAdapter(string description)
    {
        foreach (var keyword in new[] { "Hyper-V", "WSL", "VirtualBox", "VMware", "Loopback", "TAP-Windows" })
        {
            if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        Stop();
        _clientSlots.Dispose();
    }
}
