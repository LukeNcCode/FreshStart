using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Defender;

internal enum DefenderSetting
{
    RealTime,
    Cloud
}

public sealed class DefenderSettingOperation : OperationBase
{
    private readonly DefenderService _service;
    private readonly DefenderSetting _setting;
    private readonly bool _enable;

    internal DefenderSettingOperation(DefenderService service, DefenderSetting setting, bool enable)
    {
        _service = service;
        _setting = setting;
        _enable = enable;
    }

    public override string Id => $"defender.{_setting.ToString().ToLowerInvariant()}.{(_enable ? "on" : "off")}";
    public override string Name => _setting == DefenderSetting.RealTime
        ? $"{(_enable ? "开启" : "关闭")} Defender 实时防护"
        : $"{(_enable ? "开启" : "关闭")} Defender 云保护";

    public override string Description => _setting == DefenderSetting.RealTime
        ? "通过 Set-MpPreference 修改实时防护；若篡改保护开启，Windows 会拒绝修改"
        : "通过 Set-MpPreference 修改 MAPSReporting 云保护级别";

    public override RiskLevel RiskLevel => _setting == DefenderSetting.RealTime ? RiskLevel.High : RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _setting == DefenderSetting.RealTime ? "Defender 实时防护" : "Defender 云保护",
            Kind = "Microsoft Defender",
            Before = _enable ? "关闭" : "开启",
            After = _enable ? "开启" : "关闭",
            Impact = _setting == DefenderSetting.RealTime
                ? "关闭后实时拦截停止；篡改保护开启时通常会被系统拒绝"
                : "影响可疑文件样本上传与云端判定"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = await _service.GetPreferenceStateAsync(ct).ConfigureAwait(false);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "DefenderPreference",
            Existed = true,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在修改 Defender 设置…", Percent = 60 });
        return _setting == DefenderSetting.RealTime
            ? await _service.SetRealTimeAsync(_enable, ct).ConfigureAwait(false)
            : await _service.SetCloudAsync(_enable, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.Other);
        if (entry?.DataJson is null) return OperationResult.Fail("Defender 快照损坏");
        var original = JsonSerializer.Deserialize<DefenderPreferenceState>(entry.DataJson, JsonConfig.Options);
        if (original is null) return OperationResult.Fail("Defender 快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复 Defender 设置…", Percent = 60 });
        return _setting == DefenderSetting.RealTime
            ? await _service.SetRealTimeAsync(!original.DisableRealTime, ct).ConfigureAwait(false)
            : await _service.SetCloudAsync(original.MapsReporting > 0, ct).ConfigureAwait(false);
    }
}

public sealed class DefenderExclusionOperation : OperationBase
{
    private readonly DefenderService _service;
    private readonly DefenderExclusion _exclusion;
    private readonly bool _add;
    private bool _existedBefore;

    public DefenderExclusionOperation(DefenderService service, DefenderExclusion exclusion, bool add)
    {
        _service = service;
        _exclusion = exclusion;
        _add = add;
    }

    public override string Id => $"defender.exclusion.{(_add ? "add" : "remove")}";
    public override string Name => $"{(_add ? "添加" : "移除")} Defender 排除项";
    public override string Description => "通过 Add-MpPreference / Remove-MpPreference 修改排除项，写入前保存完整列表";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = $"{_exclusion.KindText}：{_exclusion.Value}",
            Kind = "Defender 排除项",
            Before = _add ? "不在列表中" : "在列表中",
            After = _add ? "加入排除列表" : "从排除列表移除",
            Impact = "排除项会让 Defender 跳过对应路径/进程/扩展名的扫描"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = await _service.GetExclusionsAsync(ct).ConfigureAwait(false);
        _existedBefore = Contains(state, _exclusion);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "DefenderExclusions",
            Existed = true,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (_add == _existedBefore)
        {
            return OperationResult.Ok(_add ? "排除项已存在" : "排除项不存在，无需移除");
        }

        progress?.Report(new OperationStep { Message = "正在修改 Defender 排除项…", Percent = 60 });
        return await _service.SetExclusionAsync(_exclusion, _add, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (_add == _existedBefore) return OperationResult.Ok("无需回滚");

        progress?.Report(new OperationStep { Message = "正在恢复 Defender 排除项…", Percent = 60 });
        return await _service.SetExclusionAsync(_exclusion, !_add, ct).ConfigureAwait(false);
    }

    private static bool Contains(DefenderExclusionState state, DefenderExclusion exclusion)
    {
        var list = exclusion.Kind switch
        {
            DefenderExclusionKind.Path => state.Paths,
            DefenderExclusionKind.Process => state.Processes,
            DefenderExclusionKind.Extension => state.Extensions,
            _ => new List<string>()
        };
        return list.Any(value => string.Equals(value, exclusion.Value, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DefenderTimedDisableOperation : OperationBase
{
    private readonly DefenderService _service;
    private readonly int _minutes;
    private string _createdTaskName = string.Empty;

    public DefenderTimedDisableOperation(DefenderService service, int minutes)
    {
        _service = service;
        _minutes = Math.Max(1, minutes);
    }

    public override string Id => "defender.timeddisable";
    public override string Name => $"临时关闭 Defender 实时防护 {_minutes} 分钟";
    public override string Description => "关闭实时防护并创建计划任务，在到期后自动恢复；需要篡改保护允许修改";
    public override RiskLevel RiskLevel => RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = "Microsoft Defender 实时防护",
            Kind = "Defender",
            Before = "开启",
            After = $"关闭 {_minutes} 分钟，然后自动恢复",
            Impact = "关闭期间不会实时拦截恶意文件；若计划任务失败，可能保持关闭，请谨慎"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = await _service.GetPreferenceStateAsync(ct).ConfigureAwait(false);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "DefenderTimedDisable",
            Existed = true,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在关闭实时防护并创建恢复任务…", Percent = 60 });
        var (result, taskName) = await _service.EnableTimedDisableAsync(_minutes, ct).ConfigureAwait(false);
        _createdTaskName = taskName;
        return result;
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在恢复实时防护…", Percent = 50 });
        await _service.RemoveRestoreTaskAsync(_createdTaskName, ct).ConfigureAwait(false);

        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.Other);
        if (entry?.DataJson is null) return OperationResult.Fail("Defender 快照损坏");
        var original = JsonSerializer.Deserialize<DefenderPreferenceState>(entry.DataJson, JsonConfig.Options);
        if (original is null) return OperationResult.Fail("Defender 快照损坏");
        return await _service.SetRealTimeAsync(!original.DisableRealTime, ct).ConfigureAwait(false);
    }
}
