using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FreshStart.Core;

namespace FreshStart.Services.System;

public sealed class PowerShellResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }

    /// <summary>调用方主动取消（不是超时）。</summary>
    public bool Cancelled { get; init; }

    public bool Success => !TimedOut && !Cancelled && ExitCode == 0;
}

/// <summary>
/// 隐藏窗口、异步、可超时的 PowerShell 执行器。
/// 仅用于系统没有稳定 C# API 的地方（更新历史、Defender、计划任务枚举）。
/// UI 线程只能 await 结果，禁止同步等待。
/// </summary>
public sealed class PowerShellRunner
{
    public async Task<PowerShellResult> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var fullScript = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(fullScript));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"PowerShell 启动失败: {ex.Message}");
            return new PowerShellResult { ExitCode = -1, StdErr = ex.Message };
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            await WaitQuietlyAsync(process, 500).ConfigureAwait(false);

            // 用户取消走标准取消异常，让 OperationRunner 正确标记 Cancelled 并回滚。
            ct.ThrowIfCancellationRequested();
            return new PowerShellResult
            {
                ExitCode = -1,
                StdOut = await SafeReadAsync(stdout).ConfigureAwait(false),
                StdErr = CleanError(await SafeReadAsync(stderr).ConfigureAwait(false)),
                TimedOut = true
            };
        }

        return new PowerShellResult
        {
            ExitCode = process.ExitCode,
            StdOut = await SafeReadAsync(stdout).ConfigureAwait(false),
            StdErr = CleanError(await SafeReadAsync(stderr).ConfigureAwait(false))
        };
    }

    /// <summary>PowerShell 的错误流常是 CLIXML 序列化文本，转成可读的多行纯文本。</summary>
    private static string CleanError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (!text.Contains("<Objs", StringComparison.Ordinal)) return text.Trim();

        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(text, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline))
        {
            var line = WebUtility.HtmlDecode(match.Groups[1].Value)
                .Replace("_x000D__x000A_", "\n")
                .Replace("_x000D_", string.Empty)
                .Replace("_x000A_", "\n")
                .Trim();
            if (line.Length == 0) continue;
            builder.AppendLine(line);
        }
        return builder.Length > 0 ? builder.ToString().Trim() : text.Trim();
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task WaitQuietlyAsync(Process process, int milliseconds)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(milliseconds)).ConfigureAwait(false); }
        catch { /* ignore */ }
    }
}
