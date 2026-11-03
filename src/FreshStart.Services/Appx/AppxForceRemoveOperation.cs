using System.Diagnostics;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Appx;

/// <summary>高级强制卸载系统组件：删除包注册、预置包与安装目录（不可回滚）。</summary>
public sealed class AppxForceRemoveOperation : OperationBase
{
    private readonly AppxService _service;
    private readonly AppxPackageInfo _package;

    public AppxForceRemoveOperation(AppxService service, AppxPackageInfo package)
    {
        _service = service;
        _package = package;
    }

    public override string Id => "appx.force-remove";
    public override string Name => $"强制卸载系统组件「{_package.LocalizedName}」";
    public override string Description => _package.Name.Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
        ? "删除包注册、预置包、安装目录与用户数据，并清理安全中心托盘自启动；官方不支持，可能导致安全中心/商店/更新异常"
        : "删除 WindowsApps 安装目录、当前用户包数据与预置包；官方不支持，可能导致安全中心/商店/更新异常";
    public override RiskLevel RiskLevel => RiskLevel.High;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _package.Name,
            Kind = "系统组件（强制）",
            Before = "已安装",
            After = _package.Name.Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
                ? "删除注册 + 安装目录 + 用户包数据 + 托盘自启动"
                : "删除注册 + 安装目录 + 用户包数据",
            Impact = "不可回滚；Windows 更新可能重新安装；安全中心/商店功能可能异常"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = _package.Name,
            Existed = true,
            DataJson = JsonSerializer.Serialize(_package, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在强制卸载系统组件…", Percent = 30 });
        return await _service.ForceRemoveAsync(_package, progress, ct).ConfigureAwait(false);
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Ok("强制卸载不支持自动回滚"));
}
