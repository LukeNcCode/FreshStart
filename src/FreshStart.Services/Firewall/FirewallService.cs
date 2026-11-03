using System.Text.Json;
using System.Text.RegularExpressions;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using FreshStart.Services.System;

namespace FreshStart.Services.Firewall;

public sealed class FirewallRuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Profile { get; set; } = string.Empty;

    public string DirectionText => Direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase) ? "入站" : "出站";
    public string ActionText => Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? "允许" : "阻止";
    public string EnabledText => Enabled ? "启用" : "禁用";
    public string Summary => $"{DirectionText} · {ActionText} · {EnabledText}";
}

public sealed class FirewallRuleSpec
{
    public string DisplayName { get; set; } = string.Empty;
    public string Direction { get; set; } = "Inbound";
    public string Action { get; set; } = "Allow";
    public string Protocol { get; set; } = "TCP";
    public string LocalPort { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public string Description { get; set; } = "FreshStart 方案创建";
}

/// <summary>
/// 防火墙：读取/开关配置文件，添加/删除规则。
/// 全部通过 NetSecurity 模块，写入前由调用方做快照。
/// </summary>
public sealed class FirewallService
{
    private readonly PowerShellRunner _powershell = new();

    public async Task<(bool Domain, bool Private, bool Public)> GetProfilesAsync(CancellationToken ct = default)
    {
        var result = await _powershell.RunAsync(
            "Get-NetFirewallProfile | Select-Object " +
            "@{n='Name';e={[string]$_.Name}}, @{n='Enabled';e={([string]$_.Enabled) -eq 'True'}} | ConvertTo-Json -Compress",
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        var domain = true;
        var privateProfile = true;
        var publicProfile = true;

        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return (domain, privateProfile, publicProfile);

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var item in items)
            {
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var enabled = item.TryGetProperty("Enabled", out var e) && e.ValueKind == JsonValueKind.True;
                if (string.Equals(name, "Domain", StringComparison.OrdinalIgnoreCase)) domain = enabled;
                else if (string.Equals(name, "Private", StringComparison.OrdinalIgnoreCase)) privateProfile = enabled;
                else if (string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase)) publicProfile = enabled;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析防火墙状态失败: {ex.Message}");
        }

        return (domain, privateProfile, publicProfile);
    }

    public async Task<OperationResult> SetProfileAsync(string profiles, bool enable, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profiles)) return OperationResult.Fail("未指定防火墙配置文件");
        var script =
            $"Set-NetFirewallProfile -Profile {profiles} -Enabled {(enable ? "True" : "False")} -ErrorAction Stop";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok("防火墙配置已更新")
            : OperationResult.Fail($"设置防火墙失败：{Error(result)}");
    }

    public async Task<IReadOnlyList<FirewallRuleInfo>> ListRulesAsync(string keyword, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(keyword)
            ? string.Empty
            : $" -DisplayName '*{Escape(keyword)}*'";

        var script =
            $"Get-NetFirewallRule{filter} -ErrorAction SilentlyContinue | " +
            "Select-Object @{n='Name';e={[string]$_.Name}}, @{n='DisplayName';e={[string]$_.DisplayName}}, " +
            "@{n='Direction';e={[string]$_.Direction}}, @{n='Action';e={[string]$_.Action}}, " +
            "@{n='Enabled';e={([string]$_.Enabled) -eq 'True'}}, @{n='Profile';e={[string]$_.Profile}} | " +
            "ConvertTo-Json -Compress";

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var list = new List<FirewallRuleInfo>();
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return list;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var item in items)
            {
                list.Add(new FirewallRuleInfo
                {
                    Name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    DisplayName = item.TryGetProperty("DisplayName", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    Direction = item.TryGetProperty("Direction", out var dir) ? dir.GetString() ?? string.Empty : string.Empty,
                    Action = item.TryGetProperty("Action", out var act) ? act.GetString() ?? string.Empty : string.Empty,
                    Enabled = item.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.True,
                    Profile = item.TryGetProperty("Profile", out var p) ? p.GetString() ?? string.Empty : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析防火墙规则失败: {ex.Message}");
        }

        return list.OrderBy(rule => rule.DisplayName).ToList();
    }

    public async Task<bool> RuleExistsAsync(string displayName, CancellationToken ct = default)
    {
        var result = await _powershell.RunAsync(
            $"if (Get-NetFirewallRule -DisplayName '{Escape(displayName)}' -ErrorAction SilentlyContinue) {{ 'exists' }} else {{ 'gone' }}",
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return result.Success && result.StdOut.Contains("exists", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<OperationResult> AddRuleAsync(FirewallRuleSpec spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec.DisplayName)) return OperationResult.Fail("规则名称不能为空");

        // 参数可能来自方案 JSON（可被分享/编辑），先按枚举白名单收口，不能直接拼进脚本
        var direction = NormalizeChoice(spec.Direction, s_directions);
        if (direction is null) return OperationResult.Fail($"方向无效：{spec.Direction}");

        var action = NormalizeChoice(spec.Action, s_actions);
        if (action is null) return OperationResult.Fail($"动作无效：{spec.Action}");

        var protocol = NormalizeChoice(spec.Protocol, s_protocols);
        if (protocol is null) return OperationResult.Fail($"协议无效：{spec.Protocol}");

        var localPort = string.IsNullOrWhiteSpace(spec.LocalPort) ? string.Empty : spec.LocalPort.Trim();
        if (localPort.Length > 0 && !IsValidPortSpec(localPort))
        {
            return OperationResult.Fail($"端口格式无效：{spec.LocalPort}（示例 80 / 80,443 / 8000-8100）");
        }

        if (await RuleExistsAsync(spec.DisplayName, ct).ConfigureAwait(false))
        {
            return OperationResult.Fail($"同名防火墙规则已存在：{spec.DisplayName}");
        }

        var parts = new List<string>
        {
            $"New-NetFirewallRule -DisplayName '{Escape(spec.DisplayName)}'",
            $"-Direction {direction}",
            $"-Action {action}",
            $"-Protocol {protocol}"
        };

        if (localPort.Length > 0) parts.Add($"-LocalPort {localPort}");
        if (!string.IsNullOrWhiteSpace(spec.Program)) parts.Add($"-Program '{Escape(spec.Program)}'");
        if (!string.IsNullOrWhiteSpace(spec.Description)) parts.Add($"-Description '{Escape(spec.Description)}'");

        var script = string.Join(" ", parts) + " -ErrorAction Stop | Out-Null";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail($"创建防火墙规则失败：{Error(result)}");
        }

        return await RuleExistsAsync(spec.DisplayName, ct).ConfigureAwait(false)
            ? OperationResult.Ok("防火墙规则已创建")
            : OperationResult.Fail("创建后未检测到规则生效");
    }

    public async Task<OperationResult> RemoveRuleAsync(string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return OperationResult.Fail("规则名称不能为空");

        var script = $"Remove-NetFirewallRule -DisplayName '{Escape(displayName)}' -ErrorAction Stop";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success) return OperationResult.Fail($"删除防火墙规则失败：{Error(result)}");

        return await RuleExistsAsync(displayName, ct).ConfigureAwait(false)
            ? OperationResult.Fail("删除后规则仍然存在")
            : OperationResult.Ok("防火墙规则已删除");
    }

    /// <summary>读取一条规则的完整定义，用于删除后的回滚。</summary>
    public async Task<FirewallRuleSnapshot?> CaptureRuleAsync(string displayName, CancellationToken ct = default)
    {
        var script =
            $"$r = Get-NetFirewallRule -DisplayName '{Escape(displayName)}' -ErrorAction Stop; " +
            "$pf = $r | Get-NetFirewallPortFilter; $af = $r | Get-NetFirewallApplicationFilter; " +
            "[pscustomobject]@{ DisplayName=[string]$r.DisplayName; Direction=[string]$r.Direction; " +
            "Action=[string]$r.Action; Enabled=(([string]$r.Enabled) -eq 'True'); Profile=[string]$r.Profile; " +
            "Protocol=[string]$pf.Protocol; LocalPort=([string]$pf.LocalPort -replace 'Any',''); " +
            "RemotePort=([string]$pf.RemotePort -replace 'Any',''); Program=[string]$af.Program } | ConvertTo-Json -Compress";

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return null;

        try
        {
            return JsonSerializer.Deserialize<FirewallRuleSnapshot>(result.StdOut, JsonConfig.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析防火墙规则快照失败: {ex.Message}");
            return null;
        }
    }

    public async Task<OperationResult> RestoreRuleAsync(FirewallRuleSnapshot snapshot, CancellationToken ct = default)
    {
        // 快照里的协议可能是数字或本地化名称，归一化失败时退回 Any，保证回滚不因校验失败而丢规则
        var protocol = NormalizeChoice(snapshot.Protocol, s_protocols) ?? "Any";
        var spec = new FirewallRuleSpec
        {
            DisplayName = snapshot.DisplayName,
            Direction = snapshot.Direction,
            Action = snapshot.Action,
            Protocol = protocol,
            LocalPort = snapshot.LocalPort,
            Program = string.IsNullOrWhiteSpace(snapshot.Program) || snapshot.Program == "Any" ? string.Empty : snapshot.Program
        };

        var add = await AddRuleAsync(spec, ct).ConfigureAwait(false);
        if (!add.Success) return add;

        if (!snapshot.Enabled)
        {
            await _powershell.RunAsync(
                $"Disable-NetFirewallRule -DisplayName '{Escape(snapshot.DisplayName)}' -ErrorAction SilentlyContinue",
                TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }

        return OperationResult.Ok("防火墙规则已恢复");
    }

    private static string Escape(string value) => value.Replace("'", "''");

    // 只允许这些取值进入脚本；大小写不敏感，输出统一用规范写法
    private static readonly string[] s_directions = { "Inbound", "Outbound" };
    private static readonly string[] s_actions = { "Allow", "Block" };
    private static readonly string[] s_protocols = { "TCP", "UDP", "ICMPv4", "ICMPv6", "Any" };
    private static readonly Regex s_portSpec = new(
        @"^\d{1,5}(-\d{1,5})?(,\s*\d{1,5}(-\d{1,5})?)*$", RegexOptions.Compiled);

    private static string? NormalizeChoice(string? value, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        foreach (var candidate in allowed)
        {
            if (candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    /// <summary>校验端口表达式：单个端口、逗号列表或区间，且每段 1–65535。</summary>
    private static bool IsValidPortSpec(string value)
    {
        if (!s_portSpec.IsMatch(value)) return false;
        foreach (var part in value.Split(','))
        {
            foreach (var bound in part.Split('-'))
            {
                if (!int.TryParse(bound.Trim(), out var port) || port is < 1 or > 65535) return false;
            }
        }
        return true;
    }

    private static string Error(PowerShellResult result)
        => !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr.Trim() : result.StdOut.Trim();
}

public sealed class FirewallRuleSnapshot
{
    public string DisplayName { get; set; } = string.Empty;
    public string Direction { get; set; } = "Inbound";
    public string Action { get; set; } = "Allow";
    public bool Enabled { get; set; } = true;
    public string Profile { get; set; } = "Any";
    public string Protocol { get; set; } = "Any";
    public string LocalPort { get; set; } = string.Empty;
    public string RemotePort { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
}

// ---------------- 操作 ----------------

public sealed class FirewallProfileOperation : OperationBase
{
    private readonly FirewallService _service;
    private readonly string _profiles;
    private readonly bool _enable;
    private readonly (bool Domain, bool Private, bool Public)? _knownProfiles;

    /// <param name="knownProfiles">
    /// 页面加载时已经读到的三个配置文件状态。传入后 DescribeChanges 不再访问系统
    /// （该方法在 UI 线程同步执行，等待 PowerShell 会冻结界面）。
    /// </param>
    public FirewallProfileOperation(
        FirewallService service,
        string profiles,
        bool enable,
        (bool Domain, bool Private, bool Public)? knownProfiles = null)
    {
        _service = service;
        _profiles = profiles;
        _enable = enable;
        _knownProfiles = knownProfiles;
    }

    public override string Id => $"firewall.profile.{_profiles.ToLowerInvariant()}";
    public override string Name => $"防火墙 {_profiles}：{(_enable ? "开启" : "关闭")}";
    public override string Description => "通过 Set-NetFirewallProfile 修改配置文件状态；失败可回滚";
    public override RiskLevel RiskLevel => _enable ? RiskLevel.Caution : RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges()
    {
        var before = _knownProfiles is { } known
            ? $"{Bool(known.Domain)} / {Bool(known.Private)} / {Bool(known.Public)}"
            : "（执行时读取当前状态）";
        return new[]
        {
            new ChangeDescriptor
            {
                Target = $"防火墙 · {_profiles}",
                Kind = "防火墙配置",
                Before = before,
                After = _enable ? "开启" : "关闭",
                Impact = _enable ? "恢复系统拦截" : "关闭后入站/出站可能不再被拦截"
            }
        };
    }

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var (domain, privateProfile, publicProfile) = await _service.GetProfilesAsync(ct).ConfigureAwait(false);
        return CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = "FirewallProfiles",
                Existed = true,
                DataJson = JsonSerializer.Serialize(new { domain, privateProfile, publicProfile }, JsonConfig.Options)
            }
        });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在修改防火墙配置…", Percent = 50 });
        return await _service.SetProfileAsync(_profiles, _enable, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault();
        if (entry?.DataJson is null) return OperationResult.Fail("缺少防火墙快照");

        using var doc = JsonDocument.Parse(entry.DataJson);
        await _service.SetProfileAsync("Domain", doc.RootElement.GetProperty("domain").GetBoolean(), ct).ConfigureAwait(false);
        await _service.SetProfileAsync("Private", doc.RootElement.GetProperty("privateProfile").GetBoolean(), ct).ConfigureAwait(false);
        await _service.SetProfileAsync("Public", doc.RootElement.GetProperty("publicProfile").GetBoolean(), ct).ConfigureAwait(false);
        return OperationResult.Ok("防火墙配置已恢复");
    }

    private static string Bool(bool value) => value ? "开启" : "关闭";
}

/// <summary>添加防火墙规则；回滚时删除本次新增的规则。</summary>
public sealed class FirewallRuleAddOperation : OperationBase
{
    private readonly FirewallService _service;
    private readonly FirewallRuleSpec _spec;

    public FirewallRuleAddOperation(FirewallService service, FirewallRuleSpec spec)
    {
        _service = service;
        _spec = spec;
    }

    public override string Id => "firewall.rule.add";
    public override string Name => $"添加防火墙规则「{_spec.DisplayName}」";
    public override string Description => $"{_spec.Direction} / {_spec.Action} / {_spec.Protocol} {_spec.LocalPort}".Trim();
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _spec.DisplayName,
            Kind = "防火墙规则",
            Before = "（不存在）",
            After = $"{_spec.Direction} {_spec.Action} {_spec.Protocol} {_spec.LocalPort}",
            Impact = "仅新增规则，不影响已有规则"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = _spec.DisplayName,
                Existed = false,
                DataJson = JsonSerializer.Serialize(_spec, JsonConfig.Options)
            }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在创建防火墙规则…", Percent = 50 });
        return await _service.AddRuleAsync(_spec, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => await _service.RemoveRuleAsync(_spec.DisplayName, ct).ConfigureAwait(false);
}

/// <summary>删除防火墙规则；回滚时按快照重建。</summary>
public sealed class FirewallRuleRemoveOperation : OperationBase
{
    private readonly FirewallService _service;
    private readonly string _displayName;

    public FirewallRuleRemoveOperation(FirewallService service, string displayName)
    {
        _service = service;
        _displayName = displayName;
    }

    public override string Id => "firewall.rule.remove";
    public override string Name => $"删除防火墙规则「{_displayName}」";
    public override string Description => "删除前读取规则定义，回滚时重建";
    public override RiskLevel RiskLevel => RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _displayName,
            Kind = "防火墙规则",
            Before = "存在",
            After = "删除",
            Impact = "删除后对应程序/端口不再有该规则"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var snapshot = await _service.CaptureRuleAsync(_displayName, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"找不到防火墙规则：{_displayName}");
        return CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = _displayName,
                Existed = true,
                DataJson = JsonSerializer.Serialize(snapshot, JsonConfig.Options)
            }
        });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在删除防火墙规则…", Percent = 50 });
        return await _service.RemoveRuleAsync(_displayName, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault();
        if (entry?.DataJson is null) return OperationResult.Fail("缺少防火墙规则快照");
        var snapshot = JsonSerializer.Deserialize<FirewallRuleSnapshot>(entry.DataJson, JsonConfig.Options);
        return snapshot is null
            ? OperationResult.Fail("防火墙规则快照损坏")
            : await _service.RestoreRuleAsync(snapshot, ct).ConfigureAwait(false);
    }
}
