using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Features;

public sealed class OptionalFeatureInfo
{
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft-Hyper-V-All"] = "Hyper-V",
        ["Microsoft-Hyper-V"] = "Hyper-V（核心）",
        ["Microsoft-Hyper-V-Management-PowerShell"] = "Hyper-V PowerShell",
        ["VirtualMachinePlatform"] = "虚拟机平台",
        ["Microsoft-Windows-Subsystem-Linux"] = "WSL",
        ["Containers"] = "容器",
        ["MicrosoftWindowsSandbox"] = "Windows 沙盒",
        ["NetFx3"] = ".NET Framework 3.5",
        ["OpenSSH.Server~~~~0.0.1.0"] = "OpenSSH 服务器",
        ["OpenSSH.Client~~~~0.0.1.0"] = "OpenSSH 客户端",
        ["Printing-Foundation-Features"] = "打印基础功能",
        ["WorkFolders-Client"] = "工作文件夹客户端",
        ["SmbDirect"] = "SMB Direct",
        ["Microsoft-RemoteDesktopConnection"] = "远程桌面连接"
    };

    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "Disabled";
    public bool Enabled => State.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
    public string StateText => Enabled ? "已启用" : "未启用";
    public string DisplayName => FriendlyNames.TryGetValue(Name, out var friendly) ? friendly : Name;
}

/// <summary>Windows 可选功能（.NET 3.5、WSL、Hyper-V、OpenSSH、Sandbox 等）。</summary>
public sealed class OptionalFeatureService
{
    private readonly PowerShellRunner _powershell = new();

    public async Task<IReadOnlyList<OptionalFeatureInfo>> ListAsync(CancellationToken ct = default)
    {
        var script =
            "Get-WindowsOptionalFeature -Online | Select-Object " +
            "@{n='Name';e={[string]$_.FeatureName}}, @{n='State';e={[string]$_.State}} | " +
            "Sort-Object Name | ConvertTo-Json -Compress";

        var result = await _powershell.RunAsync(script, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var list = new List<OptionalFeatureInfo>();
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return list;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var item in items)
            {
                list.Add(new OptionalFeatureInfo
                {
                    Name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    State = item.TryGetProperty("State", out var s) ? s.GetString() ?? "Disabled" : "Disabled"
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析可选功能列表失败: {ex.Message}");
        }

        return list;
    }

    public async Task<OperationResult> GetStateAsync(string name, CancellationToken ct = default)
    {
        var result = await _powershell.RunAsync(
            $"(Get-WindowsOptionalFeature -Online -FeatureName '{Escape(name)}').State",
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        return result.Success
            ? OperationResult.Ok(result.StdOut.Trim())
            : OperationResult.Fail($"读取功能状态失败：{Error(result)}");
    }

    public async Task<OperationResult> SetStateAsync(string name, bool enable, CancellationToken ct = default)
    {
        var verb = enable ? "Enable" : "Disable";
        var result = await _powershell.RunAsync(
            $"{verb}-WindowsOptionalFeature -Online -FeatureName '{Escape(name)}' -NoRestart -ErrorAction Stop | Out-Null",
            TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        if (!result.Success) return OperationResult.Fail($"{verb} 失败：{Error(result)}");

        var state = await GetStateAsync(name, ct).ConfigureAwait(false);
        if (!state.Success) return state;

        var enabled = state.Message.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                      && !state.Message.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        return enabled == enable
            ? OperationResult.Ok(enable ? "功能已启用（可能需要重启）" : "功能已禁用（可能需要重启）")
            : OperationResult.Fail($"操作后状态校验失败：{state.Message}");
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static string Error(PowerShellResult result)
        => !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr.Trim() : result.StdOut.Trim();
}

/// <summary>启用/禁用 Windows 可选功能；回滚恢复原状态。</summary>
public sealed class OptionalFeatureOperation : OperationBase
{
    private readonly OptionalFeatureService _service;
    private readonly string _name;
    private readonly bool _enable;

    public OptionalFeatureOperation(OptionalFeatureService service, string name, bool enable)
    {
        _service = service;
        _name = name;
        _enable = enable;
    }

    public override string Id => "feature.toggle";
    public override string Name => $"{(_enable ? "启用" : "禁用")}可选功能「{_name}」";
    public override string Description => "Enable/Disable-WindowsOptionalFeature -NoRestart；回滚恢复原状态";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _name,
            Kind = "Windows 可选功能",
            Before = _enable ? "未启用" : "已启用",
            After = _enable ? "启用" : "禁用",
            Impact = "部分功能需要重启后生效；禁用 Hyper-V/WSL 等可能影响已有虚拟机"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = await _service.GetStateAsync(_name, ct).ConfigureAwait(false);
        return CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = _name,
                Existed = state.Success,
                DataJson = state.Success ? state.Message : "Unknown"
            }
        });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在修改可选功能（可能需要几分钟）…", Percent = 40 });
        return await _service.SetStateAsync(_name, _enable, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault();
        var previous = entry?.DataJson ?? "Disabled";
        var enable = previous.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                     && !previous.Contains("Disabled", StringComparison.OrdinalIgnoreCase);

        progress?.Report(new OperationStep { Message = "正在恢复功能状态…", Percent = 50 });
        return await _service.SetStateAsync(_name, enable, ct).ConfigureAwait(false);
    }
}
