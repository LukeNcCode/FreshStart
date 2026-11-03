using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreshStart.Core;
using FreshStart.Core.Operations;
using Microsoft.Win32;

namespace FreshStart.Services.Power;

public sealed class PowerPlanInfo
{
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsAvailable { get; init; } = true;
}

public sealed record PowerPlanDefinition(string Guid, string Name, string Description);

/// <summary>
/// 电源计划读取/切换。读取走注册表（PowerSchemes + ActivePowerScheme），
/// 用 SHLoadIndirectString 解析本地化名称；切换用 powercfg /setactive。
/// 切换前快照当前计划，失败或撤销时切回。
/// </summary>
public sealed class PowerService
{
    private const string SchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    private static readonly HashSet<string> OverlaySchemeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "3af9b8d9-7c97-431d-ad78-34a8bfea439f",
        "ded574b5-45a0-4f42-8737-46345c09c238",
        "961cc777-2547-4f9d-8174-7d86181b8a7a"
    };

    public Task<IReadOnlyList<PowerPlanInfo>> GetPlansAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<PowerPlanInfo>>(() =>
        {
            var plans = new List<PowerPlanInfo>();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(SchemesPath);
                if (root is null) return plans;

                var activeGuid = (root.GetValue("ActivePowerScheme") as string ?? string.Empty).Trim('{', '}');
                foreach (var guid in root.GetSubKeyNames())
                {
                    if (OverlaySchemeIds.Contains(guid)) continue;
                    using var scheme = root.OpenSubKey(guid);
                    if (scheme is null) continue;
                    var name = ResolveIndirectString(scheme.GetValue("FriendlyName") as string);
                    if (string.IsNullOrWhiteSpace(name)) name = guid;
                    plans.Add(new PowerPlanInfo
                    {
                        Guid = guid,
                        Name = name,
                        IsActive = string.Equals(guid, activeGuid, StringComparison.OrdinalIgnoreCase)
                    });
                }

                // 同名的“终极性能”等重复项只保留一个（优先当前活动项）
                plans = plans
                    .GroupBy(plan => plan.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => group.OrderByDescending(plan => plan.IsActive).First())
                    .OrderByDescending(plan => plan.IsActive)
                    .ThenBy(plan => plan.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"读取电源计划失败: {ex.Message}");
            }
            return plans;
        }, ct);
    }

    /// <summary>
    /// 本机已安装的电源计划 + 未创建的主流内置方案。
    /// 带 IsBuiltIn 且 IsAvailable=false 的条目会在应用时用 duplicatescheme 创建。
    /// </summary>
    public async Task<IReadOnlyList<PowerPlanInfo>> GetPlansWithBuiltInsAsync(CancellationToken ct = default)
    {
        var installed = (await GetPlansAsync(ct).ConfigureAwait(false)).ToList();
        var result = new List<PowerPlanInfo>(installed);

        foreach (var definition in MainstreamPlans)
        {
            if (result.Any(plan => plan.Guid.Equals(definition.Guid, StringComparison.OrdinalIgnoreCase))) continue;
            if (result.Any(plan => plan.Name.Equals(definition.Name, StringComparison.CurrentCultureIgnoreCase))) continue;

            result.Add(new PowerPlanInfo
            {
                Guid = definition.Guid,
                Name = definition.Name,
                Description = definition.Description,
                IsBuiltIn = true,
                IsAvailable = false
            });
        }

        return result
            .OrderByDescending(plan => plan.IsActive)
            .ThenByDescending(plan => plan.IsAvailable)
            .ThenBy(plan => plan.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>主流 Windows 电源方案；本机被隐藏/未创建时可在应用时自动创建。</summary>
    public static IReadOnlyList<PowerPlanDefinition> MainstreamPlans { get; } = new[]
    {
        new PowerPlanDefinition("381b4222-f694-41f0-9685-ff5bb260df2e", "平衡",
            "Windows 默认方案，按负载自动调节性能与功耗，适合日常使用。"),
        new PowerPlanDefinition("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "高性能",
            "提高处理器性能、减少节能降频，适合游戏、渲染和固定电源设备；笔记本会增加耗电与发热。"),
        new PowerPlanDefinition("a1841308-3541-4fab-bc81-f71556f20b4a", "节能",
            "降低性能以延长续航，适合移动办公和电池供电。"),
        new PowerPlanDefinition("e9a42b02-d5df-448d-aa00-03f14749eb61", "卓越性能",
            "最低延迟与最高性能，适合台式机/工作站；笔记本不建议长时间使用。")
    };

    /// <summary>Windows 内置电源方案 GUID；被隐藏/删除时可以用 duplicatescheme 重新生成。</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownSchemes = MainstreamPlans
        .ToDictionary(plan => plan.Guid, plan => plan.Name, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeGuid(string guid) => guid.Trim().Trim('{', '}').ToLowerInvariant();

    public static bool IsKnownScheme(string guid) => KnownSchemes.ContainsKey(NormalizeGuid(guid));

    private static readonly Dictionary<string, string> KnownSchemeAliases = new(StringComparer.CurrentCultureIgnoreCase)
    {
        ["平衡"] = "381b4222-f694-41f0-9685-ff5bb260df2e",
        ["Balanced"] = "381b4222-f694-41f0-9685-ff5bb260df2e",
        ["高性能"] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        ["High performance"] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        ["节能"] = "a1841308-3541-4fab-bc81-f71556f20b4a",
        ["Power saver"] = "a1841308-3541-4fab-bc81-f71556f20b4a",
        ["卓越性能"] = "e9a42b02-d5df-448d-aa00-03f14749eb61",
        ["Ultimate Performance"] = "e9a42b02-d5df-448d-aa00-03f14749eb61"
    };

    /// <summary>按显示名查找 Windows 内置电源方案（支持中英文与“卓越性能 (1)”这类重复名）。</summary>
    public static bool TryGetKnownSchemeByName(string? name, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        if (KnownSchemeAliases.TryGetValue(trimmed, out var exact))
        {
            guid = exact;
            return true;
        }

        foreach (var pair in KnownSchemeAliases)
        {
            if (trimmed.Contains(pair.Key, StringComparison.CurrentCultureIgnoreCase))
            {
                guid = pair.Value;
                return true;
            }
        }

        return false;
    }

    public async Task<OperationResult> SetActiveAsync(string guid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(guid)) return OperationResult.Fail("电源计划无效");

        var (success, output) = await RunPowercfgAsync($"/setactive {NormalizeGuid(guid)}", ct).ConfigureAwait(false);
        return success
            ? OperationResult.Ok("电源计划已切换")
            : OperationResult.Fail(string.IsNullOrWhiteSpace(output)
                ? $"powercfg 失败（exit 1）：无法激活电源计划 {guid}"
                : $"powercfg 失败：{output.Trim()}");
    }

    /// <summary>读取当前活动电源计划 GUID；读取失败返回空字符串。</summary>
    public async Task<string> GetActiveGuidAsync(CancellationToken ct)
    {
        var (success, output) = await RunPowercfgAsync("/getactivescheme", ct).ConfigureAwait(false);
        if (!success) return string.Empty;

        var match = Regex.Match(output,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? NormalizeGuid(match.Value) : string.Empty;
    }

    /// <summary>
    /// 激活电源计划；目标不存在但属于 Windows 内置方案（高性能/卓越性能等）时，
    /// 先用 duplicatescheme 创建副本再激活，返回实际激活的 GUID。
    /// </summary>
    public async Task<(OperationResult Result, string ActivatedGuid)> EnsureAndActivateAsync(
        string guid, CancellationToken ct)
    {
        var target = NormalizeGuid(guid);
        var plans = await GetPlansAsync(ct).ConfigureAwait(false);
        if (plans.Any(plan => plan.Guid.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            return (await SetActiveAsync(target, ct).ConfigureAwait(false), target);
        }

        if (!IsKnownScheme(target))
        {
            return (OperationResult.Fail($"电源计划不存在：{guid}"), target);
        }

        var (created, output) = await RunPowercfgAsync($"-duplicatescheme {target}", ct).ConfigureAwait(false);
        var match = Regex.Match(output, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!created || !match.Success)
        {
            return (OperationResult.Fail($"创建电源计划失败：{output.Trim()}"), target);
        }

        var newGuid = match.Value.ToLowerInvariant();
        var activated = await SetActiveAsync(newGuid, ct).ConfigureAwait(false);
        return activated.Success
            ? (OperationResult.Ok($"已创建并应用电源计划「{KnownSchemes[target]}」"), newGuid)
            : (activated, newGuid);
    }

    private static async Task<(bool Success, string Output)> RunPowercfgAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return (false, "无法启动 powercfg");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string ResolveIndirectString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (!raw.StartsWith('@')) return raw;

        // 注册表值格式：@C:\Windows\system32\powrprof.dll,-19,Ultimate Performance
        // SHLoadIndirectString 只接受 @dll,-id，必须去掉最后的英文回退文本。
        var source = raw;
        var firstComma = raw.IndexOf(',');
        var lastComma = raw.LastIndexOf(',');
        if (firstComma > 0 && lastComma > firstComma) source = raw[..lastComma];

        var buffer = new StringBuilder(512);
        var hr = SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero);
        if (hr == 0 && buffer.Length > 0) return buffer.ToString();
        return raw;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source, StringBuilder outBuffer, int outBufferSize, IntPtr reserved);
}

/// <summary>切换电源计划，快照里保存原计划 GUID。</summary>
public sealed class PowerPlanOperation : OperationBase
{
    private readonly PowerService _service;
    private readonly string _guid;
    private readonly string _targetName;

    public PowerPlanOperation(PowerService service, string guid, string targetName)
    {
        _service = service;
        _guid = guid;
        _targetName = targetName;
    }

    public override string Id => "power.plan";
    public override string Name => $"切换电源计划：{_targetName}";
    public override string Description => "通过 powercfg /setactive 切换；失败或撤销时恢复原计划";
    public override RiskLevel RiskLevel => RiskLevel.Safe;

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var plans = await _service.GetPlansAsync(ct).ConfigureAwait(false);
        var active = plans.FirstOrDefault(plan => plan.IsActive);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "PowerPlan",
            Existed = active is not null,
            DataJson = JsonSerializer.Serialize(new PowerPlanSnapshot
            {
                Guid = active?.Guid ?? string.Empty,
                Name = active?.Name ?? string.Empty
            }, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = "当前电源计划",
            Kind = "电源计划",
            Before = "当前活动计划",
            After = _targetName,
            Impact = "影响 CPU 调度、屏幕/睡眠超时等；可随时切回"
        }
    };

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在切换电源计划…", Percent = 50 });
        var (result, activatedGuid) = await _service.EnsureAndActivateAsync(_guid, ct).ConfigureAwait(false);
        if (!result.Success) return result;

        var activeGuid = await _service.GetActiveGuidAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(activeGuid)
            && !string.Equals(activeGuid, activatedGuid, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(
                $"切换后活动电源计划为 {activeGuid}，与目标 {activatedGuid} 不一致");
        }

        return result;
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(item => item.Kind == SnapshotKind.Other);
        if (entry?.DataJson is null) return OperationResult.Fail("快照中缺少原电源计划");
        var snapshot = JsonSerializer.Deserialize<PowerPlanSnapshot>(entry.DataJson, JsonConfig.Options);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Guid)) return OperationResult.Fail("电源计划快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复原电源计划…", Percent = 60 });
        var result = await _service.SetActiveAsync(snapshot.Guid, ct).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok($"已恢复 {snapshot.Name}") : result;
    }

    private sealed class PowerPlanSnapshot
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
