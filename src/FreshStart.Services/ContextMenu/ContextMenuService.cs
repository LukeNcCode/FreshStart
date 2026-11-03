using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Services.ContextMenu;

public enum ContextMenuKind
{
    ClassicVerb,
    ShellExtension,
    SendTo,
    NewMenu
}

public sealed class ContextMenuEntry
{
    public ContextMenuKind Kind { get; set; }
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string IconKey { get; set; } = "context-menu";
    public bool IsEnabled { get; set; }
    public bool CanToggle { get; set; }
    public bool CanDelete { get; set; } = true;

    public string Hive { get; set; } = "HKCU";
    public string View { get; set; } = "64";
    public string SubKey { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? ToggleParent { get; set; }
    public string? KeyName { get; set; }

    public string ToggleText => IsEnabled ? "禁用" : "启用";
    public string KindText => Kind switch
    {
        ContextMenuKind.ClassicVerb => "经典菜单",
        ContextMenuKind.ShellExtension => "Shell 扩展",
        ContextMenuKind.SendTo => "发送到",
        ContextMenuKind.NewMenu => "新建菜单",
        _ => "菜单项"
    };
}

public sealed class ContextMenuSnapshot
{
    public List<ContextMenuEntry> ShellVerbs { get; set; } = new();
    public List<ContextMenuEntry> ShellExtensions { get; set; } = new();
    public List<ContextMenuEntry> SendToItems { get; set; } = new();
    public List<ContextMenuEntry> NewMenuItems { get; set; } = new();
    public bool IsClassicStyle { get; set; }
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 右键菜单枚举与操作工厂。所有写操作都返回 ISystemOperation，交给 OperationRunner 备份/回滚。
/// 说明：Windows 11 紧凑菜单中的打包应用入口由 AppX/COM 注册，本模块管理注册表 shell / shellex /
/// 发送到 / ShellNew；紧凑菜单本身通过“经典/新版风格”切换。
/// </summary>
public sealed class ContextMenuService
{
    public const string ClassicStyleClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string ClassicStylePath = @"Software\Classes\CLSID\" + ClassicStyleClsid;

    private static readonly (string Key, string Display)[] Scopes =
    {
        ("*", "所有文件"),
        ("AllFilesystemObjects", "所有文件系统对象"),
        ("Directory", "文件夹"),
        ("Directory\\Background", "文件夹背景"),
        ("Drive", "驱动器"),
        ("Folder", "文件夹（系统）"),
        ("DesktopBackground", "桌面背景")
    };

    private readonly RegistryService _registry;
    private readonly ContextMenuQuarantine _quarantine = new();

    public ContextMenuService(RegistryService registry) => _registry = registry;

    public ContextMenuSnapshot Load()
    {
        var snapshot = new ContextMenuSnapshot { IsClassicStyle = IsClassicStyle() };
        foreach (var (hive, registryHive) in new[] { ("HKCU", RegistryHive.CurrentUser), ("HKLM", RegistryHive.LocalMachine) })
        {
            var classes = hive == "HKCU" ? @"Software\Classes" : @"SOFTWARE\Classes";
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
            foreach (var (scope, scopeDisplay) in Scopes)
            {
                ReadShellVerbs(baseKey, classes, scope, scopeDisplay, hive, snapshot);
                ReadShellExtensions(baseKey, classes, scope, scopeDisplay, hive, snapshot);
            }
            ReadNewMenu(baseKey, classes, hive, snapshot);
        }

        snapshot.SendToItems.AddRange(ReadSendTo());
        snapshot.ShellVerbs = snapshot.ShellVerbs
            .OrderBy(e => e.Scope).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        snapshot.ShellExtensions = snapshot.ShellExtensions
            .OrderBy(e => e.Scope).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        snapshot.NewMenuItems = snapshot.NewMenuItems
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return snapshot;
    }

    public IReadOnlyList<QuarantineEntry> LoadTrash()
    {
        return _quarantine.List().Where(IsStillQuarantined).ToList();
    }

    private bool IsStillQuarantined(QuarantineEntry entry)
    {
        try
        {
            if (entry.IsRegistry)
            {
                if (string.IsNullOrEmpty(entry.QuarantineSubKey)) return false;
                return _registry.CaptureKey(entry.QuarantineHive ?? "HKCU", entry.QuarantineView ?? "64", entry.QuarantineSubKey).Existed;
            }

            var path = entry.QuarantineFilePath;
            return !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        }
        catch
        {
            return false;
        }
    }

    public bool IsClassicStyle()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ClassicStylePath);
            if (key is null) return false;
            using var inproc = key.OpenSubKey("InprocServer32");
            if (inproc is null) return false;
            var value = inproc.GetValue(null) as string;
            return string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    public ISystemOperation CreateStyleOperation(bool classic)
    {
        return new ContextMenuStyleOperation(_registry, classic);
    }

    public ISystemOperation CreateVerbToggleOperation(ContextMenuEntry entry, bool enable)
    {
        var verb = enable ? "启用" : "禁用";
        return new RegistryValueOperation(
            _registry,
            $"ctxmenu.verb.{(enable ? "enable" : "disable")}",
            $"{verb}经典菜单项「{entry.DisplayName}」",
            "通过 LegacyDisable 标记启用/禁用 shell 菜单项，不删除注册表数据",
            RiskLevel.Caution,
            entry.Hive,
            entry.View,
            entry.SubKey,
            "LegacyDisable",
            RegistryValueKind.String,
            enable ? null : string.Empty);
    }

    public ISystemOperation CreateHandlerToggleOperation(ContextMenuEntry entry, bool enable)
    {
        if (entry.ToggleParent is null || entry.KeyName is null)
            throw new InvalidOperationException("菜单扩展缺少切换路径");

        var cleanName = entry.KeyName.TrimStart('-');
        var targetName = enable ? cleanName : "-" + cleanName;
        var destination = $@"{entry.ToggleParent}\{targetName}";

        return new RegistryKeyMoveOperation(
            _registry,
            $"ctxmenu.handler.{(enable ? "enable" : "disable")}",
            $"{(enable ? "启用" : "禁用")} Shell 扩展「{entry.DisplayName}」",
            "把注册表键重命名为 -名称 来禁用扩展，数据保留在原位置，可随时恢复",
            RiskLevel.Caution,
            entry.Hive,
            entry.View,
            entry.SubKey,
            entry.Hive,
            entry.View,
            destination);
    }

    public (ISystemOperation Operation, QuarantineEntry Quarantine) CreateDeleteOperation(ContextMenuEntry entry)
    {
        if (entry.Kind == ContextMenuKind.SendTo && entry.FilePath is not null)
        {
            var q = _quarantine.CreateFileEntry("SendTo", entry.DisplayName, entry.FilePath);
            var op = new FileSystem.FileMoveOperation(
                "ctxmenu.delete.sendto",
                $"移除发送到项「{entry.DisplayName}」",
                "把发送到快捷方式移入 FreshStart 隔离区，可从回收站恢复",
                RiskLevel.Caution,
                entry.FilePath,
                q.QuarantineFilePath!,
                entry.DisplayName);
            return (op, q);
        }

        var originalHive = entry.Hive;
        var originalSubKey = entry.SubKey;
        var kind = entry.Kind switch
        {
            ContextMenuKind.ClassicVerb => "ShellVerb",
            ContextMenuKind.ShellExtension => "ShellExtension",
            ContextMenuKind.NewMenu => "NewMenu",
            _ => "ContextMenu"
        };
        var quarantine = _quarantine.CreateRegistryEntry(kind, entry.DisplayName, originalHive, entry.View, originalSubKey);
        var registryOp = new RegistryKeyMoveOperation(
            _registry,
            $"ctxmenu.delete.{kind.ToLowerInvariant()}",
            $"移除「{entry.DisplayName}」",
            "把注册表键移入 FreshStart 隔离区，可从回收站恢复",
            RiskLevel.Caution,
            originalHive,
            entry.View,
            originalSubKey,
            quarantine.QuarantineHive!,
            quarantine.QuarantineView!,
            quarantine.QuarantineSubKey!);
        return (registryOp, quarantine);
    }

    public ISystemOperation CreateRestoreOperation(QuarantineEntry entry)
    {
        if (entry.IsRegistry)
        {
            return new RegistryKeyMoveOperation(
                _registry,
                "ctxmenu.restore",
                $"恢复「{entry.DisplayName}」",
                "把隔离区中的注册表项移回原位置",
                RiskLevel.Caution,
                entry.QuarantineHive!,
                entry.QuarantineView!,
                entry.QuarantineSubKey!,
                entry.OriginalHive!,
                entry.OriginalView!,
                entry.OriginalSubKey!);
        }

        return new FileSystem.FileMoveOperation(
            "ctxmenu.restore.file",
            $"恢复「{entry.DisplayName}」",
            "把隔离区中的文件移回原位置",
            RiskLevel.Caution,
            entry.QuarantineFilePath!,
            entry.OriginalFilePath!,
            entry.DisplayName);
    }

    public void RemoveQuarantineIndex(QuarantineEntry entry) => _quarantine.Remove(entry);

    private static void ReadShellVerbs(
        RegistryKey baseKey, string classes, string scope, string scopeDisplay, string hive, ContextMenuSnapshot snapshot)
    {
        using var shell = baseKey.OpenSubKey($@"{classes}\{scope}\shell");
        if (shell is null) return;

        foreach (var name in shell.GetSubKeyNames())
        {
            if (string.Equals(name, "ShellNew", StringComparison.OrdinalIgnoreCase)) continue;
            using var verb = shell.OpenSubKey(name);
            if (verb is null) continue;

            // 系统内部/程序化动词不在真实右键菜单中显示
            if (verb.GetValueNames().Any(v => string.Equals(v, "ProgrammaticAccessOnly", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawDisplay = ReadString(verb, "MUIVerb") ?? ReadString(verb, null) ?? name;
            var display = ShellResourceResolver.Resolve(rawDisplay);
            if (string.IsNullOrWhiteSpace(display)) display = name;

            using var commandKey = verb.OpenSubKey("command");
            var command = ReadString(commandKey, null);
            if (string.IsNullOrWhiteSpace(command) && commandKey?.GetValue("DelegateExecute") is not null)
            {
                command = "系统命令（DelegateExecute）";
            }
            command ??= string.Empty;
            var disabled = verb.GetValueNames().Any(v => string.Equals(v, "LegacyDisable", StringComparison.OrdinalIgnoreCase));

            snapshot.ShellVerbs.Add(new ContextMenuEntry
            {
                Kind = ContextMenuKind.ClassicVerb,
                Id = $"verb:{hive}:{scope}:{name}",
                DisplayName = display,
                Detail = command,
                Source = hive,
                Scope = scopeDisplay,
                IconKey = "context-menu",
                IsEnabled = !disabled,
                CanToggle = true,
                CanDelete = true,
                Hive = hive,
                View = "64",
                SubKey = $@"{classes}\{scope}\shell\{name}"
            });
        }
    }

    private static void ReadShellExtensions(
        RegistryKey baseKey, string classes, string scope, string scopeDisplay, string hive, ContextMenuSnapshot snapshot)
    {
        var parentPath = $@"{classes}\{scope}\shellex\ContextMenuHandlers";
        using var parent = baseKey.OpenSubKey(parentPath);
        if (parent is null) return;

        foreach (var rawName in parent.GetSubKeyNames())
        {
            var enabled = !rawName.StartsWith('-');
            var name = enabled ? rawName : rawName.TrimStart('-');
            if (string.IsNullOrWhiteSpace(name)) continue;

            using var handler = parent.OpenSubKey(rawName);
            var clsid = ReadString(handler, null) ?? string.Empty;
            snapshot.ShellExtensions.Add(new ContextMenuEntry
            {
                Kind = ContextMenuKind.ShellExtension,
                Id = $"handler:{hive}:{scope}:{rawName}",
                DisplayName = name,
                Detail = string.IsNullOrEmpty(clsid) ? "ContextMenuHandlers" : clsid,
                Source = hive,
                Scope = scopeDisplay,
                IconKey = "apps",
                IsEnabled = enabled,
                CanToggle = true,
                CanDelete = true,
                Hive = hive,
                View = "64",
                SubKey = $@"{parentPath}\{rawName}",
                ToggleParent = parentPath,
                KeyName = rawName
            });
        }
    }

    private static IEnumerable<ContextMenuEntry> ReadSendTo()
    {
        foreach (var (path, owner) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "当前用户"),
                     (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         @"Microsoft\Windows\SendTo"), "所有用户")
                 })
        {
            if (!Directory.Exists(path)) continue;
            IEnumerable<string> items;
            try { items = Directory.EnumerateFileSystemEntries(path); }
            catch { continue; }

            foreach (var item in items)
            {
                var name = Path.GetFileNameWithoutExtension(item);
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(item);
                yield return new ContextMenuEntry
                {
                    Kind = ContextMenuKind.SendTo,
                    Id = $"sendto:{item}",
                    DisplayName = name,
                    Detail = item,
                    Source = owner,
                    Scope = "发送到",
                    IconKey = "folder",
                    IsEnabled = true,
                    CanToggle = false,
                    CanDelete = true,
                    FilePath = item
                };
            }
        }
    }

    private static void ReadNewMenu(RegistryKey baseKey, string classes, string hive, ContextMenuSnapshot snapshot)
    {
        using var classesKey = baseKey.OpenSubKey(classes);
        if (classesKey is null) return;

        foreach (var ext in classesKey.GetSubKeyNames())
        {
            if (!ext.StartsWith('.')) continue;
            using var extKey = classesKey.OpenSubKey(ext);
            using var shellNew = extKey?.OpenSubKey("ShellNew");
            if (shellNew is null) continue;

            var detail = ReadString(shellNew, "FileName")
                         ?? ReadString(shellNew, "Command")
                         ?? (shellNew.GetValue("NullFile") is not null ? "空文件" : "ShellNew");
            snapshot.NewMenuItems.Add(new ContextMenuEntry
            {
                Kind = ContextMenuKind.NewMenu,
                Id = $"new:{hive}:{ext}",
                DisplayName = ext,
                Detail = detail,
                Source = hive,
                Scope = "新建菜单",
                IconKey = "list",
                IsEnabled = true,
                CanToggle = false,
                CanDelete = true,
                Hive = hive,
                View = "64",
                SubKey = $@"{classes}\{ext}\ShellNew"
            });
        }
    }

    private static string? ReadString(RegistryKey? key, string? valueName)
    {
        if (key is null) return null;
        try
        {
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value switch
            {
                string text => text.Trim(),
                string[] multi => string.Join(" ", multi),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
