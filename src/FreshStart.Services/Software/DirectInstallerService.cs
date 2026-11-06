using System.Diagnostics;
using System.Security.Cryptography;
using FreshStart.Core;
using FreshStart.Core.Diagnostics;
using FreshStart.Core.Operations;
using FreshStart.Services.System;
using FreshStart.Services.Uninstall;

namespace FreshStart.Services.Software;

/// <summary>
/// 官方直链安装器执行器：下载 → （可选）SHA256 校验 → 静默执行 → 安装后检测。
/// 用于 winget 失败时兜底，不依赖任何源索引。
/// </summary>
public sealed class DirectInstallerService
{
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    /// <summary>
    /// 下载并静默安装；成功后返回安装器路径（便于失败时提示用户手动运行）。
    /// </summary>
    public async Task<OperationResult> InstallAsync(
        DirectInstaller app, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (!app.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"直链必须是 HTTPS：{app.Url}");
        }

        string downloadRoot;
        try
        {
            downloadRoot = SecureWorkDirectory.Create("direct");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"无法创建可信下载目录：{ex.Message}");
        }
        var target = Path.Combine(downloadRoot, Path.GetFileName(app.FileName));

        // 1) 下载
        progress?.Report(new OperationStep { Message = $"正在从官方地址下载 {app.Name}…", Percent = 30 });
        try
        {
            using var response = await Http.GetAsync(app.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperationResult.Fail($"下载 {app.Name} 失败：HTTP {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > MaxDownloadBytes)
            {
                SecureWorkDirectory.TryDelete(downloadRoot);
                return OperationResult.Fail($"安装包过大（{total} 字节），已中止");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = File.Create(target))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (received > MaxDownloadBytes)
                    {
                        throw new IOException($"安装包超过 {MaxDownloadBytes / 1024 / 1024} MB 上限");
                    }
                    if (total > 0)
                    {
                        var percent = 30 + (int)(received * 40 / total);
                        progress?.Report(new OperationStep
                        {
                            Message = $"正在下载 {app.Name}… {received * 100 / total}%",
                            Percent = percent
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            SecureWorkDirectory.TryDelete(downloadRoot);
            throw;
        }
        catch (Exception ex)
        {
            SecureWorkDirectory.TryDelete(downloadRoot);
            return OperationResult.Fail($"下载 {app.Name} 失败：{ex.Message}");
        }

        var size = new FileInfo(target).Length;
        if (size < 1024)
        {
            SecureWorkDirectory.TryDelete(downloadRoot);
            return OperationResult.Fail($"下载的安装器过小（{size} 字节），已中止");
        }

        // 2) 可选校验
        if (!string.IsNullOrWhiteSpace(app.Sha256))
        {
            progress?.Report(new OperationStep { Message = "正在校验安装包 SHA256…", Percent = 72 });
            var actual = await ComputeSha256Async(target, ct).ConfigureAwait(false);
            if (!actual.Equals(app.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                SecureWorkDirectory.TryDelete(downloadRoot);
                return OperationResult.Fail($"安装包校验失败（期望 {app.Sha256}，实际 {actual}），已中止");
            }
        }

        var signature = await VerifyAuthenticodeAsync(target, ct).ConfigureAwait(false);
        if (!signature.Valid)
        {
            SecureWorkDirectory.TryDelete(downloadRoot);
            return OperationResult.Fail($"安装包 Authenticode 签名无效：{signature.Message}");
        }

        // 3) 静默安装
        progress?.Report(new OperationStep { Message = $"正在静默安装 {app.Name}…", Percent = 75 });
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                Arguments = app.SilentArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = downloadRoot
            };

            var command = $"\"{target}\" {app.SilentArguments}".TrimEnd();
            NativeCommandTrace.Started(command, downloadRoot);
            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                NativeCommandTrace.Completed(
                    command, -1, "installer start failed", stopwatch.Elapsed, downloadRoot);
                return OperationResult.Fail($"无法启动安装器：{target}");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                NativeCommandTrace.Completed(
                    command, -1, "installer cancelled", stopwatch.Elapsed, downloadRoot);
                throw;
            }

            NativeCommandTrace.Completed(
                command, process.ExitCode, string.Empty, stopwatch.Elapsed, downloadRoot);
            if (process.ExitCode is not (0 or 3010))
            {
                return OperationResult.Fail(
                    $"{app.Name} 安装器返回 {process.ExitCode}（3010 表示需要重启）。安装器保留在：{target}，可手动运行");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{app.Name} 安装失败：{ex.Message}；安装器保留在 {target}");
        }

        // 4) 安装后检测（ARP 显示名）
        progress?.Report(new OperationStep { Message = "正在确认安装结果…", Percent = 92 });
        var installed = await Task.Run(() => IsInstalled(app), ct).ConfigureAwait(false);
        if (!installed)
        {
            return OperationResult.Fail(
                $"{app.Name} 安装器已结束，但未在已安装列表中找到，无法确认安装成功；安装器保留在 {target}");
        }

        SecureWorkDirectory.TryDelete(downloadRoot);
        return OperationResult.Ok($"{app.Name} 已通过官方直链安装并确认");
    }

    /// <summary>按 ARP 显示名判断是否装成功。</summary>
    private static bool IsInstalled(DirectInstaller app)
    {
        try
        {
            var programs = new InstalledProgramService().List();
            return programs.Any(program => program.DisplayName
                .Contains(app.DetectPattern, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn($"直链安装后检测失败: {ex.Message}");
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<(bool Valid, string Message)> VerifyAuthenticodeAsync(
        string path, CancellationToken ct)
    {
        var escaped = path.Replace("'", "''");
        var script =
            $"$sig = Get-AuthenticodeSignature -LiteralPath '{escaped}'; " +
            "if ($sig.Status -eq [System.Management.Automation.SignatureStatus]::Valid) { 'VALID' } " +
            "else { 'INVALID:' + [string]$sig.Status }";
        var result = await new PowerShellRunner().RunAsync(
            script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var value = result.StdOut.Trim();
        return value.Equals("VALID", StringComparison.OrdinalIgnoreCase)
            ? (true, "有效")
            : (false, string.IsNullOrWhiteSpace(value) ? result.StdErr.Trim() : value);
    }
}
