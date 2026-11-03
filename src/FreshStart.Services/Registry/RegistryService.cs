using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Core.Security;

namespace FreshStart.Services.Registry;

/// <summary>一个注册表值的完整状态（含 ACL 的 SDDL），可直接 JSON 序列化后回放。</summary>
public sealed class RegistryValueState
{
    public string Hive { get; set; } = "HKCU";
    public string View { get; set; } = "64";
    public string SubKey { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Existed { get; set; }
    public string? ValueType { get; set; }
    public string? StringData { get; set; }
    public string[]? MultiStringData { get; set; }
    public byte[]? BinaryData { get; set; }
    public int? DwordData { get; set; }
    public long? QwordData { get; set; }
    public string? Sddl { get; set; }

    public string FullPath => RegistryService.Normalize(Hive, SubKey, Name);
}

/// <summary>递归键状态，用于右键菜单这种"整棵子树删除/恢复"的场景。</summary>
public sealed class RegistryKeyState
{
    public string Hive { get; set; } = "HKCU";
    public string View { get; set; } = "64";
    public string SubKey { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public string? Sddl { get; set; }
    public List<RegistryValueState> Values { get; set; } = new();
    public List<RegistryKeyState> SubKeys { get; set; } = new();
}

/// <summary>
/// 注册表读写封装。所有写入都经过 RegistryPathGuard 白名单，
/// 所有读取都使用显式 RegistryView，避免 32/64 位视图错乱。
/// </summary>
public sealed class RegistryService
{
    public RegistryValueState CaptureValue(string hive, string? view, string subKey, string? name)
    {
        var state = new RegistryValueState { Hive = hive, View = view ?? "64", SubKey = subKey, Name = name };
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(hive), ParseView(view));
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        if (key is null) return state;

        state.Existed = true;
        try { state.Sddl = TryGetSddl(baseKey, subKey); } catch { /* ACL 读取失败不阻断 */ }

        if (name is null) return state; // 仅捕获键存在性

        try
        {
            var kind = key.GetValueKind(name);
            state.ValueType = kind.ToString();
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    state.StringData = raw as string;
                    break;
                case RegistryValueKind.MultiString:
                    state.MultiStringData = raw as string[];
                    break;
                case RegistryValueKind.Binary:
                    state.BinaryData = raw as byte[];
                    break;
                case RegistryValueKind.DWord:
                    state.DwordData = raw is int i ? i : Convert.ToInt32(raw);
                    break;
                case RegistryValueKind.QWord:
                    state.QwordData = raw is long l ? l : Convert.ToInt64(raw);
                    break;
            }
        }
        catch (IOException)
        {
            // 值不存在
            state.Existed = false;
            state.ValueType = null;
        }
        catch (UnauthorizedAccessException)
        {
            state.Existed = false;
        }

        return state;
    }

    public void ApplyValue(RegistryValueState state)
    {
        RegistryPathGuard.EnsureWritable(state.Hive, state.SubKey);
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(state.Hive), ParseView(state.View));
        using var key = baseKey.CreateSubKey(state.SubKey, writable: true)
            ?? throw new IOException($"无法创建注册表键: {state.SubKey}");

        if (state.Name is null) return;

        if (!state.Existed)
        {
            key.DeleteValue(state.Name, throwOnMissingValue: false);
            return;
        }

        var kind = string.IsNullOrEmpty(state.ValueType)
            ? RegistryValueKind.String
            : Enum.Parse<RegistryValueKind>(state.ValueType);

        object? data = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => state.StringData ?? string.Empty,
            RegistryValueKind.MultiString => state.MultiStringData ?? Array.Empty<string>(),
            RegistryValueKind.Binary => state.BinaryData ?? Array.Empty<byte>(),
            RegistryValueKind.DWord => state.DwordData ?? 0,
            RegistryValueKind.QWord => state.QwordData ?? 0L,
            _ => state.StringData
        };

        key.SetValue(state.Name, data!, kind);
    }

    public void DeleteValue(string hive, string? view, string subKey, string name)
    {
        RegistryPathGuard.EnsureWritable(hive, subKey);
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(hive), ParseView(view));
        using var key = baseKey.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>读回校验：用于写入后确认系统真的接受了变更。</summary>
    public bool ValueEquals(RegistryValueState expected)
    {
        var actual = CaptureValue(expected.Hive, expected.View, expected.SubKey, expected.Name);
        if (actual.Existed != expected.Existed) return false;
        if (!expected.Existed) return true;
        if (!string.Equals(actual.ValueType, expected.ValueType, StringComparison.OrdinalIgnoreCase)) return false;
        return expected.ValueType switch
        {
            nameof(RegistryValueKind.String) or nameof(RegistryValueKind.ExpandString) =>
                string.Equals(actual.StringData, expected.StringData, StringComparison.Ordinal),
            nameof(RegistryValueKind.MultiString) =>
                (actual.MultiStringData ?? Array.Empty<string>()).SequenceEqual(expected.MultiStringData ?? Array.Empty<string>()),
            nameof(RegistryValueKind.Binary) =>
                (actual.BinaryData ?? Array.Empty<byte>()).SequenceEqual(expected.BinaryData ?? Array.Empty<byte>()),
            nameof(RegistryValueKind.DWord) => actual.DwordData == expected.DwordData,
            nameof(RegistryValueKind.QWord) => actual.QwordData == expected.QwordData,
            _ => true
        };
    }

    public RegistryKeyState CaptureKey(string hive, string? view, string subKey, bool recursive = true)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(hive), ParseView(view));
        return CaptureKeyInternal(baseKey, hive, view ?? "64", subKey, recursive, 0, new Counter());
    }

    public void DeleteKey(string hive, string? view, string subKey, bool recursive = true)
    {
        RegistryPathGuard.EnsureWritable(hive, subKey);
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(hive), ParseView(view));
        if (recursive) baseKey.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        else baseKey.DeleteSubKey(subKey, throwOnMissingSubKey: false);
    }

    public void RestoreKey(RegistryKeyState state)
    {
        if (!state.Existed)
        {
            DeleteKey(state.Hive, state.View, state.SubKey);
            return;
        }

        RegistryPathGuard.EnsureWritable(state.Hive, state.SubKey);
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(state.Hive), ParseView(state.View));
        using var key = baseKey.CreateSubKey(state.SubKey, writable: true)
            ?? throw new IOException($"无法恢复注册表键: {state.SubKey}");

        foreach (var value in state.Values) ApplyValue(value);

        foreach (var sub in state.SubKeys)
        {
            using var child = key.CreateSubKey(StripParent(sub.SubKey, state.SubKey), writable: true);
            if (child is null) continue;
            foreach (var value in sub.Values) ApplyValue(value);
            foreach (var grandChild in sub.SubKeys) RestoreChild(child, grandChild, sub.SubKey);
        }

        if (!string.IsNullOrEmpty(state.Sddl))
        {
            try { ApplySddl(baseKey, state.SubKey, state.Sddl); }
            catch (Exception ex) { Log.Warn($"恢复注册表 ACL 失败: {state.SubKey} - {ex.Message}"); }
        }

        Log.Info($"注册表键已恢复: {state.SubKey}（{state.Values.Count} 个值，{state.SubKeys.Count} 个子键）");
    }

    private static void RestoreChild(RegistryKey parent, RegistryKeyState child, string parentPath)
    {
        var name = StripParent(child.SubKey, parentPath);
        using var key = parent.CreateSubKey(name, writable: true);
        if (key is null) return;
        foreach (var value in child.Values)
        {
            var svc = new RegistryService();
            svc.ApplyValue(value);
        }
        foreach (var grandChild in child.SubKeys) RestoreChild(key, grandChild, child.SubKey);
    }

    private static RegistryKeyState CaptureKeyInternal(
        RegistryKey baseKey, string hive, string view, string subKey, bool recursive, int depth, Counter counter)
    {
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        var state = new RegistryKeyState { Hive = hive, View = view, SubKey = subKey, Existed = key is not null };
        if (key is null) return state;

        if (++counter.Count > 2000) throw new InvalidOperationException("注册表键过大，已中止备份（>2000 项）");
        if (depth > 8 && recursive) throw new InvalidOperationException("注册表层级过深，已中止备份");

        try { state.Sddl = TryGetSddl(baseKey, subKey); } catch { /* ignore */ }

        foreach (var valueName in key.GetValueNames())
        {
            var temp = new RegistryService();
            var valueState = temp.CaptureValue(hive, view, subKey, valueName);
            state.Values.Add(valueState);
        }

        if (!recursive) return state;

        foreach (var childName in key.GetSubKeyNames())
        {
            var childPath = string.IsNullOrEmpty(subKey) ? childName : $"{subKey}\\{childName}";
            state.SubKeys.Add(CaptureKeyInternal(baseKey, hive, view, childPath, true, depth + 1, counter));
        }

        return state;
    }

    private static string StripParent(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return path;
        return path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)
            ? path[(parent.Length + 1)..]
            : path;
    }

    private static string? TryGetSddl(RegistryKey baseKey, string subKey)
    {
        using var key = baseKey.OpenSubKey(subKey, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions);
        if (key is null) return null;
        var security = key.GetAccessControl(AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    private static void ApplySddl(RegistryKey baseKey, string subKey, string sddl)
    {
        using var key = baseKey.OpenSubKey(subKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
        if (key is null) return;
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        key.SetAccessControl(security);
    }

    /// <summary>把一棵已捕获的键树复制到新的根路径（用于“禁用即改名为 -Name”和隔离区恢复）。</summary>
    public RegistryKeyState CloneKeyForPath(RegistryKeyState source, string hive, string view, string targetSubKey)
    {
        return CloneNode(source, hive, view, targetSubKey);
    }

    private static RegistryKeyState CloneNode(RegistryKeyState source, string hive, string view, string targetSubKey)
    {
        var clone = new RegistryKeyState
        {
            Hive = hive,
            View = view,
            SubKey = targetSubKey,
            Existed = source.Existed,
            Sddl = source.Sddl
        };

        foreach (var value in source.Values)
        {
            clone.Values.Add(new RegistryValueState
            {
                Hive = hive,
                View = view,
                SubKey = targetSubKey,
                Name = value.Name,
                Existed = value.Existed,
                ValueType = value.ValueType,
                StringData = value.StringData,
                MultiStringData = value.MultiStringData,
                BinaryData = value.BinaryData,
                DwordData = value.DwordData,
                QwordData = value.QwordData,
                Sddl = value.Sddl
            });
        }

        foreach (var child in source.SubKeys)
        {
            var relative = child.SubKey.Length > source.SubKey.Length
                ? child.SubKey[(source.SubKey.Length + 1)..]
                : child.SubKey;
            var childPath = string.IsNullOrEmpty(relative) ? targetSubKey : $"{targetSubKey}\\{relative}";
            clone.SubKeys.Add(CloneNode(child, hive, view, childPath));
        }

        return clone;
    }

    public static RegistryHive ParseHive(string hive) => hive.Trim().ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
        "HKU" or "HKEY_USERS" => RegistryHive.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
        _ => throw new ArgumentException($"未知注册表根键: {hive}")
    };

    public static RegistryView ParseView(string? view) => view switch
    {
        "32" => RegistryView.Registry32,
        _ => RegistryView.Registry64
    };

    public static string Normalize(string hive, string subKey, string? name)
    {
        var basePath = RegistryPathGuard.Normalize(hive, subKey);
        return name is null ? basePath : $"{basePath} :: {name}";
    }

    private sealed class Counter { public int Count; }
}

/// <summary>
/// 通用注册表值写入操作。上下文菜单、界面调优、隐私清理都复用它。
/// </summary>
public sealed class RegistryValueOperation : OperationBase
{
    private readonly RegistryService _registry;
    private readonly string _hive;
    private readonly string? _view;
    private readonly string _subKey;
    private readonly string? _valueName;
    private readonly RegistryValueKind _kind;
    private readonly object? _newValue;

    public RegistryValueOperation(
        RegistryService registry,
        string id,
        string name,
        string description,
        RiskLevel risk,
        string hive,
        string? view,
        string subKey,
        string? valueName,
        RegistryValueKind kind,
        object? newValue)
    {
        _registry = registry;
        Id = id;
        Name = name;
        Description = description;
        RiskLevel = risk;
        _hive = hive;
        _view = view;
        _subKey = subKey;
        _valueName = valueName;
        _kind = kind;
        _newValue = newValue;
        RegistryPathGuard.EnsureWritable(hive, subKey); // 构造即校验，UI 无法绕过白名单
    }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel RiskLevel { get; }

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges()
    {
        var current = _registry.CaptureValue(_hive, _view, _subKey, _valueName);
        return new[]
        {
            new ChangeDescriptor
            {
                Target = current.FullPath,
                Kind = "注册表值",
                Before = current.Existed ? Format(current) : "（不存在）",
                After = _newValue is null ? "（删除）" : FormatNew(),
                Impact = Description
            }
        };
    }

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = _registry.CaptureValue(_hive, _view, _subKey, _valueName);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.RegistryValue,
            Target = RegistryService.Normalize(_hive, _subKey, _valueName),
            ValueName = _valueName,
            Existed = state.Existed,
            ValueType = state.ValueType,
            Sddl = state.Sddl,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override Task<OperationResult> ApplyCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在写入注册表…", Percent = 60 });

        var target = new RegistryValueState
        {
            Hive = _hive,
            View = _view ?? "64",
            SubKey = _subKey,
            Name = _valueName,
            Existed = _newValue is not null,
            ValueType = _newValue is null ? null : _kind.ToString(),
            StringData = _newValue as string,
            MultiStringData = _newValue as string[],
            BinaryData = _newValue as byte[],
            DwordData = _newValue is int i ? i : null,
            QwordData = _newValue is long l ? l : null
        };

        _registry.ApplyValue(target);

        progress?.Report(new OperationStep { Message = "正在校验写入结果…", Percent = 90 });
        Ensure(_registry.ValueEquals(target), $"写入后校验失败：{target.FullPath}");
        return Task.FromResult(OperationResult.Ok($"已写入 {target.FullPath}"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.RegistryValue)
            ?? throw new InvalidOperationException("快照中缺少注册表值记录");
        var original = JsonSerializer.Deserialize<RegistryValueState>(entry.DataJson!, JsonConfig.Options)
            ?? throw new InvalidOperationException("快照内容损坏");

        progress?.Report(new OperationStep { Message = "正在回滚注册表…", Percent = 70 });
        _registry.ApplyValue(original);
        Ensure(_registry.ValueEquals(original), $"回滚后校验失败：{original.FullPath}");
        return Task.FromResult(OperationResult.Ok("注册表已恢复"));
    }

    private string Format(RegistryValueState state) => state.ValueType switch
    {
        nameof(RegistryValueKind.DWord) => state.DwordData?.ToString() ?? "0",
        nameof(RegistryValueKind.QWord) => state.QwordData?.ToString() ?? "0",
        nameof(RegistryValueKind.Binary) => $"二进制（{state.BinaryData?.Length ?? 0} 字节）",
        nameof(RegistryValueKind.MultiString) => string.Join(" | ", state.MultiStringData ?? Array.Empty<string>()),
        _ => state.StringData ?? string.Empty
    };

    private string FormatNew() => _kind switch
    {
        RegistryValueKind.DWord => _newValue?.ToString() ?? "0",
        RegistryValueKind.QWord => _newValue?.ToString() ?? "0",
        RegistryValueKind.Binary => $"二进制（{(_newValue as byte[])?.Length ?? 0} 字节）",
        RegistryValueKind.MultiString => string.Join(" | ", _newValue as string[] ?? Array.Empty<string>()),
        _ => _newValue?.ToString() ?? "0"
    };
}
