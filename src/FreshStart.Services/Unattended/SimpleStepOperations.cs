using FreshStart.Core.Operations;
using FreshStart.Services.Cleanup;
using FreshStart.Services.Driver;

namespace FreshStart.Services.Unattended;

/// <summary>把「系统清理」包装成剧本步骤。清理不可回滚。</summary>
public sealed class CleanupStepOperation : OperationBase
{
    private readonly CleanupService _service;
    private readonly CleanupCategory _category;

    public CleanupStepOperation(CleanupService service, CleanupCategory category)
    {
        _service = service;
        _category = category;
    }

    public override string Id => $"unattended.cleanup.{_category.Key}";
    public override string Name => $"清理「{_category.Title}」";
    public override string Description => $"{_category.Description}；删除后无法恢复";
    public override RiskLevel RiskLevel => RiskLevel.Caution;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _category.Title,
            Kind = "缓存文件",
            Before = "存在可清理的临时文件",
            After = "删除",
            Impact = "不可回滚；正在被占用的文件会自动跳过"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = _category.Key,
                Existed = true
            }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = $"正在清理「{_category.Title}」…", Percent = 60 });
        // 复合操作已持有写锁，这里用"不再取锁"的版本，避免自锁
        var result = await _service.CleanInsideWriteLockAsync(_category, ct).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok(result.Message) : OperationResult.Fail(result.Message);
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("清理操作不可回滚"));
}

/// <summary>把「驱动导出」包装成剧本步骤；只复制驱动包，不修改系统。</summary>
public sealed class DriverExportOperation : OperationBase
{
    private readonly DriverService _service;
    private readonly string _destination;

    public DriverExportOperation(DriverService service, string destination)
    {
        _service = service;
        _destination = destination;
    }

    public override string Id => "unattended.driver.export";
    public override string Name => "导出第三方驱动";
    public override string Description => $"把当前第三方驱动包复制到：{_destination}";
    public override RiskLevel RiskLevel => RiskLevel.Safe;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _destination,
            Kind = "驱动导出",
            Before = "—",
            After = "复制驱动包到目标目录",
            Impact = "只读系统驱动信息，不安装/修改驱动"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = _destination,
                Existed = Directory.Exists(_destination)
            }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在导出驱动…", Percent = 50 });
        var result = await _service.ExportAsync(_destination, ct).ConfigureAwait(false);
        return result;
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("驱动导出不涉及系统修改，无需回滚"));
}
