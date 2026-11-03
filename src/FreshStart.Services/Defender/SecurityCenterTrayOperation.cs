using System.Diagnostics;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;

namespace FreshStart.Services.Defender;

/// <summary>
/// 移除 Windows 安全中心的托盘图标：删除 SecurityHealthSystray 的登录自启动项并结束进程。
/// 只处理托盘入口，不改动 WinDefend 服务、实时防护或安全中心策略；删除的 Run 值会进入快照，可回滚。
/// </summary>
public sealed class SecurityCenterTrayOperation : OperationBase
{
    private const string Hive = "HKLM";
    private const string View = "64";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SecurityHealth";
    private const string TrayProcess = "SecurityHealthSystray";

    private readonly RegistryService _registry = new();
    private bool _wasRunning;

    public override string Id => "securitycenter.tray.remove";
    public override string Name => "移除安全中心托盘图标";
    public override string Description =>
        "删除 HKLM\\...\\Run 下的 SecurityHealth 自启动项并结束 SecurityHealthSystray；" +
        "不会关闭 Defender 引擎或实时防护，托盘图标可通过回滚恢复";

    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\SecurityHealth",
            Kind = "登录自启动",
            Before = "SecurityHealthSystray.exe 随登录启动",
            After = "删除自启动项并结束当前托盘进程",
            Impact = "安全中心托盘图标消失；Defender 引擎与实时防护仍在后台运行"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var current = _registry.CaptureValue(Hive, View, RunKey, ValueName);
        _wasRunning = IsTrayRunning();

        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.RegistryValue,
            Target = RegistryService.Normalize(Hive, RunKey, ValueName),
            ValueName = ValueName,
            Existed = current.Existed,
            ValueType = current.ValueType,
            DataJson = JsonSerializer.Serialize(current, JsonConfig.Options),
            Aux = _wasRunning ? "running" : "stopped"
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在删除安全中心托盘自启动项…", Percent = 55 });

        var remove = new RegistryValueState
        {
            Hive = Hive,
            View = View,
            SubKey = RunKey,
            Name = ValueName,
            Existed = false
        };
        _registry.ApplyValue(remove);

        progress?.Report(new OperationStep { Message = "正在结束安全中心托盘进程…", Percent = 80 });
        KillTrayProcesses();

        if (_registry.CaptureValue(Hive, View, RunKey, ValueName).Existed)
        {
            return Task.FromResult(OperationResult.Fail("自启动项删除后仍存在，可能被系统策略或安全软件恢复"));
        }

        var message = IsTrayRunning()
            ? "自启动项已删除，但托盘进程仍在运行；请注销或重启后再确认"
            : "已移除安全中心托盘图标与自启动（Defender 引擎仍在后台运行）";
        return Task.FromResult(OperationResult.Ok(message));
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.RegistryValue)
            ?? throw new InvalidOperationException("快照中缺少托盘自启动项记录");
        var original = JsonSerializer.Deserialize<RegistryValueState>(entry.DataJson!, JsonConfig.Options)
            ?? throw new InvalidOperationException("托盘自启动项快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复托盘自启动项…", Percent = 60 });
        _registry.ApplyValue(original);

        if (entry.Aux == "running") TryStartTray();
        return Task.FromResult(OperationResult.Ok("安全中心托盘自启动项已恢复"));
    }

    private static bool IsTrayRunning()
    {
        try
        {
            return Process.GetProcessesByName(TrayProcess).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void KillTrayProcesses()
    {
        foreach (var process in Process.GetProcessesByName(TrayProcess))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log.Warn($"结束 {TrayProcess} 失败: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryStartTray()
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, $"{TrayProcess}.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复安全中心托盘失败: {ex.Message}");
        }
    }
}
