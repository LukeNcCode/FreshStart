using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Defender;

/// <summary>彻底移除 Defender 引擎与安全组件；不可自动回滚，必须重启。</summary>
public sealed class DefenderRemovalOperation : OperationBase
{
    private readonly DefenderRemovalService _service;

    public DefenderRemovalOperation(DefenderRemovalService service) => _service = service;

    public override string Id => "defender.remove-engine";
    public override string Name => "彻底移除 Defender 引擎与安全组件";
    public override string Description =>
        "按 windows-defender-remover 13 的范围移除 Defender 服务/驱动、安全中心、SmartScreen、WebThreat、" +
        "VBS/凭据保护、驱动阻止列表、系统缓解、计划任务、启动项、右键菜单、策略与安装目录；不可自动回滚，必须重启";
    public override RiskLevel RiskLevel => RiskLevel.High;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = "WinDefend / WdNisSvc / WdFilter / WdBoot / MsSecCore / SecurityHealthService / wscsvc 等",
            Kind = "Defender 服务与驱动",
            Before = "运行 / 自动启动",
            After = "停止并删除服务注册",
            Impact = "Defender 实时防护、云保护、网络检查与安全中心服务停止工作"
        },
        new ChangeDescriptor
        {
            Target = "Microsoft.SecHealthUI + SecurityHealthSystray",
            Kind = "Windows 安全中心",
            Before = "已安装",
            After = "移除应用、托盘自启动与设置页入口",
            Impact = "开始菜单与设置中的 Windows 安全中心消失"
        },
        new ChangeDescriptor
        {
            Target = "VBS / HVCI / Credential Guard / LSA 保护 / 驱动阻止列表 / 系统缓解",
            Kind = "系统安全缓解",
            Before = "按系统默认",
            After = "关闭",
            Impact = "降低内核与凭据保护；WSL2 / Hyper-V / Windows 沙盒可能无法使用"
        },
        new ChangeDescriptor
        {
            Target = "SmartScreen / WebThreat / Defender 策略与通知",
            Kind = "安全策略",
            Before = "启用",
            After = "禁用并清除策略",
            Impact = "下载与安装不再经过 SmartScreen 检查，密码泄露检测停止"
        },
        new ChangeDescriptor
        {
            Target = @"C:\ProgramData\Microsoft\Windows Defender 等目录 + smartscreen.exe",
            Kind = "Defender 文件",
            Before = "存在",
            After = "删除（受保护文件可能需重启后重试）",
            Impact = "不可恢复；Windows 更新/商店修复可能需要重新安装组件"
        },
        new ChangeDescriptor
        {
            Target = "Windows Defender 计划任务 / 启动项 / 右键菜单",
            Kind = "集成入口",
            Before = "存在",
            After = "删除",
            Impact = "扫描与安全中心入口消失"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = new
        {
            CapturedAt = DateTimeOffset.Now,
            Services = new[]
            {
                "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot",
                "MsSecCore", "MsSecFlt", "MsSecWfp", "SecurityHealthService",
                "SgrmAgent", "SgrmBroker", "wscsvc", "whesvc",
                "webthreatdefsvc", "webthreatdefusersvc", "PlutonHsp2", "PlutonHeci", "Hsp"
            },
            Directories = new[]
            {
                @"C:\ProgramData\Microsoft\Windows Defender",
                @"C:\Program Files\Windows Defender",
                @"C:\Program Files (x86)\Windows Defender",
                @"C:\Program Files\Windows Defender Advanced Threat Protection"
            },
            SecurityCenterPackage = "Microsoft.SecHealthUI",
            Note = "该操作不可自动回滚；快照仅用于审计"
        };

        return Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = "DefenderRemoval",
                Existed = true,
                DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
            }
        }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在彻底移除 Defender 引擎…", Percent = 1 });
        return await _service.RemoveAsync(progress, ct).ConfigureAwait(false);
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("彻底移除 Defender 不可自动回滚；请使用系统还原点或就地修复安装"));
}
