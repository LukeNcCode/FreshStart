using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Appx;
using FreshStart.Services.System;

namespace FreshStart.Services.Defender;

public enum DefenderExclusionKind
{
    Path,
    Process,
    Extension
}

public sealed class DefenderExclusion
{
    public DefenderExclusionKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;

    public string KindText => Kind switch
    {
        DefenderExclusionKind.Path => "路径",
        DefenderExclusionKind.Process => "进程",
        DefenderExclusionKind.Extension => "扩展名",
        _ => "排除项"
    };
}

public sealed class DefenderStatus
{
    public bool EngineAvailable { get; set; } = true;
    public bool RealTimeEnabled { get; set; }
    public bool CloudEnabled { get; set; }
    public bool TamperProtected { get; set; }
    public bool AntivirusEnabled { get; set; }
    public int MapsReporting { get; set; }
    public List<DefenderExclusion> Exclusions { get; set; } = new();

    /// <summary>Windows 安全中心（Microsoft.SecHealthUI）是否仍注册在当前用户。</summary>
    public bool SecurityCenterInstalled { get; set; } = true;

    /// <summary>托盘图标进程 SecurityHealthSystray 是否在运行。</summary>
    public bool TrayRunning { get; set; }

    /// <summary>安全中心托盘是否仍有登录自启动项。</summary>
    public bool TrayAutostart { get; set; }
}

internal sealed class DefenderPreferenceState
{
    public bool DisableRealTime { get; set; }
    public int MapsReporting { get; set; }
}

internal sealed class DefenderExclusionState
{
    public List<string> Paths { get; set; } = new();
    public List<string> Processes { get; set; } = new();
    public List<string> Extensions { get; set; } = new();
}

/// <summary>
/// Defender 状态与操作。实时防护开关受“篡改保护”限制，失败时明确返回原因，
/// 不做“假装成功”的处理；排除项与开启/关闭都进入统一备份/回滚管线。
/// </summary>
public sealed class DefenderService
{
    private readonly PowerShellRunner _powershell = new();

    public async Task<DefenderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        const string script = """
            $s = $null
            $p = $null
            try { $s = Get-MpComputerStatus -ErrorAction Stop } catch { }
            try { $p = Get-MpPreference -ErrorAction Stop } catch { }
            $sc = Get-AppxPackage -Name 'Microsoft.SecHealthUI' -ErrorAction SilentlyContinue
            $tray = Get-Process -Name 'SecurityHealthSystray' -ErrorAction SilentlyContinue
            $run = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SecurityHealth' -ErrorAction SilentlyContinue
            [pscustomobject]@{
                Engine = [bool]$s
                RealTime = if ($s) { [bool]$s.RealTimeProtectionEnabled } else { $false }
                Tamper = if ($s) { [bool]$s.IsTamperProtected } else { $false }
                Antivirus = if ($s) { [bool]$s.AntivirusEnabled } else { $false }
                DisableRealTime = if ($p) { [bool]$p.DisableRealtimeMonitoring } else { $true }
                Maps = if ($p) { [int]$p.MAPSReporting } else { 0 }
                Paths = [string[]]@($p.ExclusionPath)
                Processes = [string[]]@($p.ExclusionProcess)
                Extensions = [string[]]@($p.ExclusionExtension)
                SecurityCenter = [bool]$sc
                TrayRunning = [bool]$tray
                TrayAutostart = [bool]$run.SecurityHealth
            } | ConvertTo-Json -Depth 4 -Compress
            """;

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr)
            ? $"读取 Defender 状态失败（exit {result.ExitCode}）"
            : result.StdErr.Trim());

        var data = JsonSerializer.Deserialize<StatusDto>(result.StdOut, JsonConfig.Options)
                   ?? throw new InvalidOperationException("Defender 状态为空");

        var status = new DefenderStatus
        {
            EngineAvailable = data.Engine,
            RealTimeEnabled = data.RealTime,
            CloudEnabled = data.Maps > 0,
            TamperProtected = data.Tamper,
            AntivirusEnabled = data.Antivirus,
            MapsReporting = data.Maps,
            SecurityCenterInstalled = data.SecurityCenter,
            TrayRunning = data.TrayRunning,
            TrayAutostart = data.TrayAutostart
        };
        status.Exclusions.AddRange((data.Paths ?? Array.Empty<string>())
            .Where(IsRealExclusion)
            .Select(v => new DefenderExclusion { Kind = DefenderExclusionKind.Path, Value = v }));
        status.Exclusions.AddRange((data.Processes ?? Array.Empty<string>())
            .Where(IsRealExclusion)
            .Select(v => new DefenderExclusion { Kind = DefenderExclusionKind.Process, Value = v }));
        status.Exclusions.AddRange((data.Extensions ?? Array.Empty<string>())
            .Where(IsRealExclusion)
            .Select(v => new DefenderExclusion { Kind = DefenderExclusionKind.Extension, Value = v }));
        return status;
    }

    private static bool IsRealExclusion(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)
           && !value.Contains("administrator", StringComparison.OrdinalIgnoreCase);

    public ISystemOperation CreateRealTimeOperation(bool enable)
        => new DefenderSettingOperation(this, DefenderSetting.RealTime, enable);

    public ISystemOperation CreateCloudOperation(bool enable)
        => new DefenderSettingOperation(this, DefenderSetting.Cloud, enable);

    public ISystemOperation CreateExclusionOperation(DefenderExclusion exclusion, bool add)
        => new DefenderExclusionOperation(this, exclusion, add);

    public ISystemOperation CreateTimedDisableOperation(int minutes)
        => new DefenderTimedDisableOperation(this, minutes);

    /// <summary>移除安全中心托盘图标与登录自启动（不改动 Defender 服务/实时防护）。</summary>
    public ISystemOperation CreateSecurityCenterTrayOperation()
        => new SecurityCenterTrayOperation();

    /// <summary>彻底移除 Defender 引擎与安全组件（不可回滚，必须重启）。</summary>
    public ISystemOperation CreateRemovalOperation(AppxService appx)
        => new DefenderRemovalOperation(new DefenderRemovalService(appx));

    internal async Task<DefenderPreferenceState> GetPreferenceStateAsync(CancellationToken ct)
    {
        const string script = """
            $p = Get-MpPreference
            [pscustomobject]@{
                DisableRealTime = [bool]$p.DisableRealtimeMonitoring
                Maps = [int]$p.MAPSReporting
            } | ConvertTo-Json -Compress
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.StdErr.Trim());
        return JsonSerializer.Deserialize<DefenderPreferenceState>(result.StdOut, JsonConfig.Options)
               ?? throw new InvalidOperationException("Defender 偏好为空");
    }

    internal async Task<OperationResult> SetRealTimeAsync(bool enable, CancellationToken ct)
    {
        var script = $"Set-MpPreference -DisableRealtimeMonitoring ${(!enable).ToString().ToLowerInvariant()}";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"Defender 拒绝修改实时防护（exit {result.ExitCode}），可能由篡改保护阻止"
                : result.StdErr.Trim());
        }

        var state = await GetPreferenceStateAsync(ct).ConfigureAwait(false);
        if (state.DisableRealTime != !enable)
        {
            return OperationResult.Fail("实时防护状态校验失败：系统可能启用了篡改保护");
        }
        return OperationResult.Ok(enable ? "实时防护已开启" : "实时防护已关闭");
    }

    internal async Task<OperationResult> SetCloudAsync(bool enable, CancellationToken ct)
    {
        // MAPSReporting: 0=关闭，2=高级
        var script = $"Set-MpPreference -MAPSReporting {(enable ? 2 : 0)}";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"Defender 拒绝修改云保护（exit {result.ExitCode}）"
                : result.StdErr.Trim());
        }

        var state = await GetPreferenceStateAsync(ct).ConfigureAwait(false);
        if ((state.MapsReporting > 0) != enable)
        {
            return OperationResult.Fail("云保护状态校验失败");
        }
        return OperationResult.Ok(enable ? "云保护已开启" : "云保护已关闭");
    }

    internal async Task<DefenderExclusionState> GetExclusionsAsync(CancellationToken ct)
    {
        const string script = """
            $p = Get-MpPreference
            [pscustomobject]@{
                Paths = @($p.ExclusionPath)
                Processes = @($p.ExclusionProcess)
                Extensions = @($p.ExclusionExtension)
            } | ConvertTo-Json -Depth 4 -Compress
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.StdErr.Trim());
        return JsonSerializer.Deserialize<DefenderExclusionState>(result.StdOut, JsonConfig.Options)
               ?? new DefenderExclusionState();
    }

    internal async Task<OperationResult> SetExclusionAsync(DefenderExclusion exclusion, bool add, CancellationToken ct)
    {
        var parameter = exclusion.Kind switch
        {
            DefenderExclusionKind.Path => "ExclusionPath",
            DefenderExclusionKind.Process => "ExclusionProcess",
            DefenderExclusionKind.Extension => "ExclusionExtension",
            _ => "ExclusionPath"
        };
        var cmdlet = add ? "Add-MpPreference" : "Remove-MpPreference";
        var script = $"{cmdlet} -{parameter} '{Escape(exclusion.Value)}'";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"Defender 排除项操作失败（exit {result.ExitCode}）"
                : result.StdErr.Trim());
        }

        var exclusions = await GetExclusionsAsync(ct).ConfigureAwait(false);
        var exists = GetList(exclusions, exclusion.Kind)
            .Where(IsRealExclusion)
            .Any(value => string.Equals(value, exclusion.Value, StringComparison.OrdinalIgnoreCase));
        if (exists != add) return OperationResult.Fail("排除项状态校验失败");
        return OperationResult.Ok(add ? "已添加排除项" : "已移除排除项");
    }

    internal async Task<(OperationResult Result, string TaskName)> EnableTimedDisableAsync(int minutes, CancellationToken ct)
    {
        var disable = await SetRealTimeAsync(false, ct).ConfigureAwait(false);
        if (!disable.Success) return (disable, string.Empty);

        var taskName = $"FreshStart-DefenderRestore-{DateTime.Now:HHmmss}";
        var script = $"""
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -Command "Set-MpPreference -DisableRealtimeMonitoring $false"'
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes({Math.Max(1, minutes)})
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            Register-ScheduledTask -TaskName '{taskName}' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return (OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? "创建 Defender 自动恢复任务失败"
                : result.StdErr.Trim()), string.Empty);
        }

        return (OperationResult.Ok($"{minutes} 分钟后将自动恢复实时防护"), taskName);
    }

    internal async Task<bool> RemoveRestoreTaskAsync(string taskName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return true;
        var script = $"Unregister-ScheduledTask -TaskName '{Escape(taskName)}' -Confirm:$false -ErrorAction SilentlyContinue";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return result.Success;
    }

    private static IEnumerable<string> GetList(DefenderExclusionState state, DefenderExclusionKind kind) => kind switch
    {
        DefenderExclusionKind.Path => state.Paths,
        DefenderExclusionKind.Process => state.Processes,
        DefenderExclusionKind.Extension => state.Extensions,
        _ => Array.Empty<string>()
    };

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed class StatusDto
    {
        public bool Engine { get; set; } = true;
        public bool RealTime { get; set; }
        public bool Tamper { get; set; }
        public bool Antivirus { get; set; }
        public bool DisableRealTime { get; set; }
        public int Maps { get; set; }
        public string[]? Paths { get; set; }
        public string[]? Processes { get; set; }
        public string[]? Extensions { get; set; }
        public bool SecurityCenter { get; set; } = true;
        public bool TrayRunning { get; set; }
        public bool TrayAutostart { get; set; }
    }
}
