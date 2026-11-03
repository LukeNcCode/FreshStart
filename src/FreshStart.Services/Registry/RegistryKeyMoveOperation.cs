using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Core.Security;

namespace FreshStart.Services.Registry;

/// <summary>
/// 注册表键移动：禁用 Shell 扩展（改名为 -Name）、把菜单项移入隔离区、恢复隔离项。
/// 移动前同时捕获源和目标，任何一步失败都会回滚。
/// </summary>
public sealed class RegistryKeyMoveOperation : OperationBase
{
    private readonly RegistryService _registry;
    private readonly string _sourceHive;
    private readonly string _sourceView;
    private readonly string _sourceSubKey;
    private readonly string _destinationHive;
    private readonly string _destinationView;
    private readonly string _destinationSubKey;
    private readonly RiskLevel _risk;

    public RegistryKeyMoveOperation(
        RegistryService registry,
        string id,
        string name,
        string description,
        RiskLevel risk,
        string sourceHive,
        string sourceView,
        string sourceSubKey,
        string destinationHive,
        string destinationView,
        string destinationSubKey)
    {
        _registry = registry;
        Id = id;
        Name = name;
        Description = description;
        _risk = risk;
        _sourceHive = sourceHive;
        _sourceView = sourceView;
        _sourceSubKey = sourceSubKey;
        _destinationHive = destinationHive;
        _destinationView = destinationView;
        _destinationSubKey = destinationSubKey;

        RegistryPathGuard.EnsureWritable(sourceHive, sourceSubKey);
        RegistryPathGuard.EnsureWritable(destinationHive, destinationSubKey);
    }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel RiskLevel => _risk;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = RegistryService.Normalize(_sourceHive, _sourceSubKey, null),
            Kind = "注册表键",
            Before = $"{RegistryService.Normalize(_sourceHive, _sourceSubKey, null)}",
            After = $"{RegistryService.Normalize(_destinationHive, _destinationSubKey, null)}",
            Impact = Description
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var source = _registry.CaptureKey(_sourceHive, _sourceView, _sourceSubKey);
        var destination = _registry.CaptureKey(_destinationHive, _destinationView, _destinationSubKey);

        var entries = new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.RegistryKey,
                Target = RegistryService.Normalize(_sourceHive, _sourceSubKey, null),
                Aux = "source",
                Existed = source.Existed,
                DataJson = JsonSerializer.Serialize(source, JsonConfig.Options)
            },
            new SnapshotEntry
            {
                Kind = SnapshotKind.RegistryKey,
                Target = RegistryService.Normalize(_destinationHive, _destinationSubKey, null),
                Aux = "destination",
                Existed = destination.Existed,
                DataJson = JsonSerializer.Serialize(destination, JsonConfig.Options)
            }
        };

        return Task.FromResult(CreateSnapshot(entries));
    }

    protected override Task<OperationResult> ApplyCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var source = ReadState(state, "source");
        var destination = ReadState(state, "destination");
        if (source is null || !source.Existed) return Task.FromResult(OperationResult.Fail("源注册表键不存在"));
        if (destination is { Existed: true }) return Task.FromResult(OperationResult.Fail("目标注册表键已存在"));

        progress?.Report(new OperationStep { Message = "正在移动注册表键…", Percent = 60 });
        var clone = _registry.CloneKeyForPath(source, _destinationHive, _destinationView, _destinationSubKey);
        _registry.RestoreKey(clone);
        _registry.DeleteKey(_sourceHive, _sourceView, _sourceSubKey, recursive: true);

        Ensure(!_registry.CaptureKey(_sourceHive, _sourceView, _sourceSubKey).Existed, "移动后源键仍存在");
        Ensure(_registry.CaptureKey(_destinationHive, _destinationView, _destinationSubKey).Existed, "移动后目标键不存在");
        return Task.FromResult(OperationResult.Ok("已移动注册表键"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var source = ReadState(state, "source");
        if (source is null) return Task.FromResult(OperationResult.Fail("快照损坏"));

        progress?.Report(new OperationStep { Message = "正在恢复注册表键…", Percent = 60 });
        if (source.Existed) _registry.RestoreKey(source);
        else _registry.DeleteKey(_sourceHive, _sourceView, _sourceSubKey, recursive: true);

        _registry.DeleteKey(_destinationHive, _destinationView, _destinationSubKey, recursive: true);
        return Task.FromResult(OperationResult.Ok("注册表键已恢复"));
    }

    private static RegistryKeyState? ReadState(OperationSnapshot state, string aux)
    {
        var entry = state.Entries.FirstOrDefault(item => item.Aux == aux);
        if (entry?.DataJson is null) return null;
        return JsonSerializer.Deserialize<RegistryKeyState>(entry.DataJson, JsonConfig.Options);
    }
}
