using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Appx;
using FreshStart.Services.System;
using Microsoft.Win32;

namespace FreshStart.Services.Defender;

/// <summary>
/// 按 windows-defender-remover 13 的范围移除 Defender 引擎：Defender 服务/驱动、
/// 安全中心、SmartScreen、WebThreat、VBS/凭据保护、驱动阻止列表、系统缓解、
/// Defender 计划任务/启动项/右键菜单/策略，以及 Defender 安装目录。
/// 通过一次性 SYSTEM 计划任务导入注册表并删除受保护文件；不可自动回滚，必须重启。
/// </summary>
public sealed class DefenderRemovalService
{
    private readonly AppxService _appx;

    private static readonly string[] Services =
    {
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot",
        "MsSecCore", "MsSecFlt", "MsSecWfp", "SecurityHealthService",
        "SgrmAgent", "SgrmBroker", "wscsvc", "whesvc",
        "webthreatdefsvc", "webthreatdefusersvc", "PlutonHsp2", "PlutonHeci", "Hsp"
    };

    private static readonly string[] Processes =
    {
        "smartscreen", "SecHealthUI", "SecurityHealthSystray", "SecurityHealthService",
        "MsMpEng", "MpDefenderCoreService", "MpCmdRun", "NisSrv", "SgrmBroker", "SgrmAgent"
    };

    private static readonly string[] DefenderDirectories =
    {
        @"C:\ProgramData\Microsoft\Windows Defender",
        @"C:\Program Files\Windows Defender",
        @"C:\Program Files (x86)\Windows Defender",
        @"C:\Program Files\Windows Defender Advanced Threat Protection"
    };

    private static readonly string[] SmartScreenFiles =
    {
        @"C:\Windows\System32\smartscreen.exe",
        @"C:\Windows\System32\smartscreen.dll",
        @"C:\Windows\System32\smartscreenps.dll"
    };

    public DefenderRemovalService(AppxService appx) => _appx = appx;

    public async Task<OperationResult> RemoveAsync(
        IProgress<OperationStep>? progress, CancellationToken ct)
    {
        // 脚本随后由 SYSTEM 计划任务执行，目录必须随机且 ACL 只允许管理员/系统写入
        var workDir = SecureWorkDirectory.Create("defender-removal");

        var logPath = Path.Combine(workDir, "removal.log");
        var scriptPath = Path.Combine(workDir, "remove.cmd");

        progress?.Report(new OperationStep { Message = "正在准备 Defender 移除脚本…", Percent = 5 });
        if (DefenderRemovalRegistryData.Keys.Count == 0)
        {
            return OperationResult.Fail("内置的 Defender 移除注册表数据缺失，已中止");
        }

        File.WriteAllText(scriptPath, BuildScript(workDir, logPath), Encoding.Default);
        Log.Info($"Defender 移除脚本已生成：{workDir}");

        progress?.Report(new OperationStep { Message = "正在以 SYSTEM 权限执行移除脚本（可能持续数分钟）…", Percent = 15 });
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopwatch = Stopwatch.StartNew();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                progress?.Report(new OperationStep
                {
                    Message = $"正在以 SYSTEM 权限执行移除脚本…已用时 {stopwatch.Elapsed:mm\\:ss}",
                    Percent = 35
                });
                try { await Task.Delay(1000, heartbeatCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);

        SystemProcessResult scriptResult;
        try
        {
            scriptResult = await SystemProcessRunner.RunScriptAsync(
                scriptPath, logPath, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* ignore */ }
        }

        if (!scriptResult.Started)
        {
            return OperationResult.Fail("无法执行 Defender 移除脚本：" + scriptResult.Error);
        }

        if (scriptResult.ExitCode == -1)
        {
            return OperationResult.Fail(
                $"移除脚本超时（{scriptResult.Error}）。已执行部分可能生效，请重启后检查。日志：{logPath}");
        }

        progress?.Report(new OperationStep { Message = "正在移除 Windows 安全中心应用…", Percent = 70 });
        var appxMessage = await RemoveSecurityCenterAppAsync(progress, ct).ConfigureAwait(false);

        progress?.Report(new OperationStep { Message = "正在检查残留项…", Percent = 90 });
        var residualServices = Services.Where(ServiceKeyExists).ToList();
        var residualDirs = DefenderDirectories.Where(Directory.Exists).ToList();
        var appxPresent = await _appx.IsInstalledAsync("Microsoft.SecHealthUI", ct).ConfigureAwait(false);

        var summary = new StringBuilder();
        summary.Append("Defender 移除已执行；");
        summary.Append(residualServices.Count == 0
            ? "服务/驱动键已清理；"
            : $"仍有 {residualServices.Count} 个服务/驱动键残留（{string.Join(", ", residualServices.Take(6))}）；");
        summary.Append(residualDirs.Count == 0
            ? "安装目录已删除；"
            : $"仍有 {residualDirs.Count} 个目录残留（可能需要重启后再清理）；");
        summary.Append(appxPresent ? "安全中心应用仍在；" : "安全中心应用已移除；");
        if (!string.IsNullOrWhiteSpace(appxMessage)) summary.Append(appxMessage).Append('；');
        if (!scriptResult.UsedSystemToken)
        {
            summary.Append("SYSTEM 提权失败，已回退管理员权限，部分受保护项可能未完成；");
        }
        summary.Append("必须重启后生效。");
        summary.Append($" 日志：{logPath}");

        var message = summary.ToString();
        Log.Info("Defender 移除结果：" + message);

        var effective = residualServices.Count < Services.Length || residualDirs.Count < DefenderDirectories.Length || !appxPresent;
        return effective
            ? OperationResult.Ok(message)
            : OperationResult.Fail("移除未生效，可能被篡改保护或系统策略阻止。" + message);
    }

    private async Task<string> RemoveSecurityCenterAppAsync(
        IProgress<OperationStep>? progress, CancellationToken ct)
    {
        try
        {
            var packages = await _appx.EnumerateAsync(ct).ConfigureAwait(false);
            var package = packages.FirstOrDefault(item =>
                item.Name.Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase));
            if (package is null) return "安全中心应用未检测到";

            var result = await _appx.ForceRemoveAsync(package, progress, ct, allowFileFallback: false)
                .ConfigureAwait(false);
            return result.Message;
        }
        catch (Exception ex)
        {
            Log.Warn($"移除安全中心应用失败: {ex.Message}");
            return "安全中心应用移除失败：" + ex.Message;
        }
    }

    private static IEnumerable<string> BuildRegistryCommands()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        foreach (var key in DefenderRemovalRegistryData.Keys)
        {
            var path = key.Path;
            if (!string.IsNullOrWhiteSpace(userSid)
                && path.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                path = @"HKEY_USERS\" + userSid + path["HKEY_CURRENT_USER".Length..];
            }

            if (key.Delete)
            {
                yield return $"reg.exe delete \"{path}\" /f";
                continue;
            }

            foreach (var value in key.Values)
            {
                var selector = value.Name == "@" ? "/ve" : $"/v \"{EscapeBatch(value.Name)}\"";
                if (value.Kind == DefenderRegistryValueKind.Delete)
                {
                    yield return $"reg.exe delete \"{path}\" {selector} /f";
                    continue;
                }

                var type = value.Kind switch
                {
                    DefenderRegistryValueKind.Dword => "REG_DWORD",
                    DefenderRegistryValueKind.Qword => "REG_QWORD",
                    DefenderRegistryValueKind.Binary => "REG_BINARY",
                    _ => "REG_SZ"
                };
                yield return $"reg.exe add \"{path}\" {selector} /t {type} /d \"{EscapeBatch(value.Data)}\" /f";
            }
        }
    }

    /// <summary>cmd 批处理里双引号的转义是 ""（不是 \"），百分号转义为 %%。</summary>
    private static string EscapeBatch(string value)
        => value.Replace("%", "%%").Replace("\"", "\"\"");

    private static string BuildScript(string workDir, string logPath)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine($"set \"LOG={logPath}\"");
        script.AppendLine("echo [FreshStart] Defender removal start >> \"%LOG%\" 2>&1");

        foreach (var service in Services)
        {
            script.AppendLine($"sc stop {service} >> \"%LOG%\" 2>&1");
            script.AppendLine($"sc config {service} start= disabled >> \"%LOG%\" 2>&1");
        }

        foreach (var process in Processes)
        {
            script.AppendLine($"taskkill /f /im {process}.exe >> \"%LOG%\" 2>&1");
        }

        script.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Windows Defender\\*' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue\" >> \"%LOG%\" 2>&1");

        foreach (var command in BuildRegistryCommands())
        {
            script.AppendLine($"{command} >> \"%LOG%\" 2>&1");
        }

        foreach (var directory in DefenderDirectories)
        {
            script.AppendLine($"takeown /f \"{directory}\" /r /d y >> \"%LOG%\" 2>&1");
            script.AppendLine($"icacls \"{directory}\" /grant *S-1-5-32-544:F /t /c >> \"%LOG%\" 2>&1");
            script.AppendLine($"rd /s /q \"{directory}\" >> \"%LOG%\" 2>&1");
        }

        foreach (var file in SmartScreenFiles)
        {
            script.AppendLine($"takeown /f \"{file}\" /a >> \"%LOG%\" 2>&1");
            script.AppendLine($"icacls \"{file}\" /grant *S-1-5-32-544:F >> \"%LOG%\" 2>&1");
            script.AppendLine($"del /f /q \"{file}\" >> \"%LOG%\" 2>&1");
        }

        script.AppendLine("echo [FreshStart] Defender removal done >> \"%LOG%\" 2>&1");
        return script.ToString();
    }

    private static bool ServiceKeyExists(string name)
    {
        try
        {
            using var key = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
