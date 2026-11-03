using System.Text.Json;
using FreshStart.Core.Operations;
using FreshStart.Core.Security;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Services.ContextMenu;

/// <summary>
/// Win11 新版右键菜单 ↔ Win10 经典菜单。
/// 经典：HKCU\Software\Classes\CLSID\{86ca1aa0-...}\InprocServer32 默认值设为空字符串。
/// 新版：删除该 CLSID 键。切换后需要重启资源管理器。
/// </summary>
public sealed class ContextMenuStyleOperation : OperationBase
{
    private readonly Registry.RegistryService _registry;
    private readonly bool _classic;
    private const string Path = @"Software\Classes\CLSID\" + ContextMenuService.ClassicStyleClsid;

    public ContextMenuStyleOperation(Registry.RegistryService registry, bool classic)
    {
        _registry = registry;
        _classic = classic;
        RegistryPathGuard.EnsureWritable("HKCU", Path);
    }

    public override string Id => _classic ? "ctxmenu.style.classic" : "ctxmenu.style.win11";
    public override string Name => _classic ? "切换到 Win10 经典右键菜单" : "切换回 Win11 新版右键菜单";
    public override string Description => "修改当前用户的 CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32，切换后需重启资源管理器";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32",
            Kind = "注册表键",
            Before = _classic ? "Win11 新版" : "Win10 经典",
            After = _classic ? "Win10 经典（默认值设为空）" : "Win11 新版（删除覆盖键）",
            Impact = "只影响当前用户；重启资源管理器后生效；可随时切回"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = _registry.CaptureKey("HKCU", "64", Path);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.RegistryKey,
            Target = "HKCU\\" + Path,
            Existed = state.Existed,
            DataJson = JsonSerializer.Serialize(state, FreshStart.Core.JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override Task<OperationResult> ApplyCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在切换右键菜单风格…", Percent = 60 });
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        if (_classic)
        {
            using var key = baseKey.CreateSubKey(Path, writable: true)
                ?? throw new IOException("无法创建经典菜单 CLSID 键");
            using var inproc = key.CreateSubKey("InprocServer32", writable: true)
                ?? throw new IOException("无法创建 InprocServer32 键");
            inproc.SetValue(null, string.Empty, RegistryValueKind.String);
        }
        else
        {
            baseKey.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        }

        using var verify = baseKey.OpenSubKey(Path);
        var classicNow = verify is not null && string.IsNullOrEmpty(verify.OpenSubKey("InprocServer32")?.GetValue(null) as string);
        Ensure(classicNow == _classic, "切换后状态校验失败");
        return Task.FromResult(OperationResult.Ok("右键菜单风格已切换，重启资源管理器后生效"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.RegistryKey)
            ?? throw new InvalidOperationException("快照缺少注册表键");
        var original = JsonSerializer.Deserialize<Registry.RegistryKeyState>(entry.DataJson!, FreshStart.Core.JsonConfig.Options)
            ?? throw new InvalidOperationException("快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复右键菜单风格…", Percent = 60 });
        if (original.Existed) _registry.RestoreKey(original);
        else _registry.DeleteKey("HKCU", "64", Path);

        return Task.FromResult(OperationResult.Ok("右键菜单风格已恢复"));
    }
}
