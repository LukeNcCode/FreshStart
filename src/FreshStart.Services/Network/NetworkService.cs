using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Network;

public sealed class AdapterIpConfig
{
    public bool Dhcp { get; set; } = true;
    public string Address { get; set; } = string.Empty;
    public int Prefix { get; set; } = 24;
    public string Gateway { get; set; } = string.Empty;

    public string ModeText => Dhcp ? "自动（DHCP）" : "静态";
    public string Summary => Dhcp
        ? "自动（DHCP）"
        : $"{Address}/{Prefix}" + (string.IsNullOrWhiteSpace(Gateway) ? string.Empty : $" · 网关 {Gateway}");
}

public sealed class NetworkAdapterInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public List<string> DnsServers { get; init; } = new();

    public string DnsText => DnsServers.Count == 0 ? "自动（DHCP）" : string.Join(", ", DnsServers);
}

public sealed class DnsPreset
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] Servers { get; init; } = Array.Empty<string>();
}

public sealed class PingResult
{
    public bool Success { get; init; }
    public string Address { get; init; } = string.Empty;
    public long LatencyMs { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 网络工具：适配器/DNS 读取、DNS 切换（可回滚）、刷新缓存、Ping 测试。
/// DNS 写入用 PowerShell Cmdlet，因为 netsh 输出受系统语言影响，难以稳定解析。
/// </summary>
public sealed class NetworkService
{
    private readonly PowerShellRunner _powershell = new();

    public static IReadOnlyList<DnsPreset> Presets { get; } = new List<DnsPreset>
    {
        new() { Key = "auto", Name = "自动（DHCP）", Description = "由路由器/运营商下发", Servers = Array.Empty<string>() },
        new() { Key = "ali", Name = "阿里 DNS", Description = "国内低延迟", Servers = new[] { "223.5.5.5", "223.6.6.6" } },
        new() { Key = "tencent", Name = "腾讯 DNS", Description = "国内低延迟", Servers = new[] { "119.29.29.29", "182.254.116.116" } },
        new() { Key = "cloudflare", Name = "Cloudflare", Description = "隐私优先，支持 DoH", Servers = new[] { "1.1.1.1", "1.0.0.1" } },
        new() { Key = "google", Name = "Google DNS", Description = "国际通用", Servers = new[] { "8.8.8.8", "8.8.4.4" } }
    };

    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            if (nic.Name.Contains("本地连接*", StringComparison.Ordinal)) continue;
            if (nic.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)) continue;
            if (nic.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) continue;

            int index;
            try { index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
            catch { index = 0; }

            var dns = new List<string>();
            try
            {
                dns.AddRange(nic.GetIPProperties().DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString()));
            }
            catch { /* 某些适配器可能拒绝访问 */ }

            adapters.Add(new NetworkAdapterInfo
            {
                Index = index,
                Name = nic.Name,
                Description = nic.Description,
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
                StatusText = nic.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接",
                DnsServers = dns
            });
        }

        return await Task.FromResult(adapters
            .OrderByDescending(adapter => adapter.IsUp)
            .ThenByDescending(IsPrimary)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    /// <summary>WLAN/以太网优先于 VPN 与虚拟网卡，作为默认选中项。</summary>
    public static bool IsPrimary(NetworkAdapterInfo adapter)
    {
        var name = adapter.Name;
        return name.Contains("WLAN", StringComparison.OrdinalIgnoreCase)
               || name.Contains("以太网", StringComparison.Ordinal)
               || name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AdapterIpConfig> GetIpConfigAsync(int interfaceIndex, CancellationToken ct = default)
    {
        if (interfaceIndex <= 0) return new AdapterIpConfig();

        var script =
            "$if = Get-NetIPInterface -InterfaceIndex " + interfaceIndex + " -AddressFamily IPv4 -ErrorAction SilentlyContinue" + Environment.NewLine +
            "$addr = Get-NetIPAddress -InterfaceIndex " + interfaceIndex + " -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1" + Environment.NewLine +
            "$gw = (Get-NetRoute -InterfaceIndex " + interfaceIndex + " -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | " +
            "Select-Object -First 1).NextHop" + Environment.NewLine +
            "[pscustomobject]@{ Dhcp = ([string]$if.Dhcp -eq 'Enabled'); Address = [string]$addr.IPAddress; " +
            "Prefix = [int]$addr.PrefixLength; Gateway = [string]$gw } | ConvertTo-Json -Compress";

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return new AdapterIpConfig();

        try
        {
            return JsonSerializer.Deserialize<AdapterIpConfig>(result.StdOut, JsonConfig.Options) ?? new AdapterIpConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 IP 配置失败: {ex.Message}");
            return new AdapterIpConfig();
        }
    }

    public async Task<OperationResult> SetIpConfigAsync(
        string adapterName, bool dhcp, string address, int prefix, string gateway, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return OperationResult.Fail("适配器名称无效");
        if (!IsSafeAdapterName(adapterName)) return OperationResult.Fail($"适配器名称包含非法字符：{adapterName}");

        string script;
        if (dhcp)
        {
            script = $"netsh interface ipv4 set address name=\"{adapterName}\" source=dhcp";
        }
        else
        {
            if (!global::System.Net.IPAddress.TryParse(address, out _)) return OperationResult.Fail("静态 IP 地址格式无效");
            if (prefix is < 1 or > 32) return OperationResult.Fail("子网前缀长度无效（1–32）");
            if (!string.IsNullOrWhiteSpace(gateway) && !global::System.Net.IPAddress.TryParse(gateway, out _))
            {
                return OperationResult.Fail("网关格式无效");
            }

            var mask = PrefixToMask(prefix);
            script = string.IsNullOrWhiteSpace(gateway)
                ? $"netsh interface ipv4 set address name=\"{adapterName}\" static {address} {mask}"
                : $"netsh interface ipv4 set address name=\"{adapterName}\" static {address} {mask} {gateway}";
        }

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok(dhcp ? "已切换为自动获取 IP（DHCP）" : "已设置静态 IP")
            : OperationResult.Fail($"设置 IP 失败：{(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim())}");
    }

    /// <summary>适配器名会进入 netsh 命令行，含引号/重定向符号等一律拒绝。</summary>
    private static bool IsSafeAdapterName(string name)
    {
        foreach (var ch in name)
        {
            if (ch is '"' or '\'' or '&' or '|' or '<' or '>' or '^' or '%' or '`' or ';' or '\r' or '\n') return false;
        }
        return true;
    }

    private static string PrefixToMask(int prefix)    {
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        var bytes = BitConverter.GetBytes(mask);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return string.Join(".", bytes);
    }

    public async Task<List<string>> GetDnsAsync(int interfaceIndex, CancellationToken ct = default)
    {
        if (interfaceIndex <= 0) return new List<string>();
        var script = $"""
            $servers = @((Get-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
            ConvertTo-Json -InputObject $servers -Compress
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(result.StdOut, JsonConfig.Options) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<OperationResult> SetDnsAsync(int interfaceIndex, IReadOnlyList<string> servers, CancellationToken ct)
    {
        if (interfaceIndex <= 0) return OperationResult.Fail("无效的网络适配器");

        // 服务器地址会拼进 PowerShell 脚本，先按 IP 格式校验，非 IP 一律拒绝
        foreach (var server in servers)
        {
            if (!global::System.Net.IPAddress.TryParse(server?.Trim(), out _))
            {
                return OperationResult.Fail($"DNS 服务器地址无效：{server}");
            }
        }

        var script = servers.Count == 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop"
            : $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({string.Join(",", servers.Select(s => $"'{s.Trim()}'"))}) -ErrorAction Stop";

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"设置 DNS 失败（exit {result.ExitCode}）"
                : result.StdErr.Trim());
        }
        return OperationResult.Ok("DNS 已更新");
    }

    /// <summary>刷新 DNS 解析缓存；不修改任何持久化设置。</summary>
    public async Task<OperationResult> FlushDnsAsync(CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var process = Process.Start(startInfo);
            if (process is null) return OperationResult.Fail("无法启动 ipconfig");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0
                ? OperationResult.Ok("DNS 缓存已刷新")
                : OperationResult.Fail($"刷新失败（exit {process.ExitCode}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新 DNS 失败: {ex.Message}");
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<PingResult> PingAsync(string host, CancellationToken ct)
    {
        var address = host.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return new PingResult { Success = false, Message = "请输入域名或 IP" };

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(3)).WaitAsync(ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new PingResult { Success = true, Address = reply.Address.ToString(), LatencyMs = reply.RoundtripTime }
                : new PingResult { Success = false, Address = address, Message = reply.Status.ToString() };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PingResult { Success = false, Address = address, Message = ex.Message };
        }
    }
}

/// <summary>切换 DNS：先快照当前服务器，写入失败或用户撤销时可恢复原值。</summary>
public sealed class NetworkDnsOperation : OperationBase
{
    private readonly NetworkService _service;
    private readonly NetworkAdapterInfo _adapter;
    private readonly IReadOnlyList<string> _servers;
    private readonly string _presetName;

    public NetworkDnsOperation(
        NetworkService service,
        NetworkAdapterInfo adapter,
        IReadOnlyList<string> servers,
        string presetName)
    {
        _service = service;
        _adapter = adapter;
        _servers = servers;
        _presetName = presetName;
    }

    public override string Id => "network.dns";
    public override string Name => $"切换 DNS：{_presetName}";
    public override string Description => $"把「{_adapter.Name}」的 IPv4 DNS 改为 {_presetName}；失败或撤销时恢复原服务器";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = $"{_adapter.Name}（接口 {_adapter.Index}）",
            Kind = "DNS 服务器",
            Before = _adapter.DnsServers.Count == 0 ? "自动（DHCP）" : string.Join(", ", _adapter.DnsServers),
            After = _servers.Count == 0 ? "自动（DHCP）" : string.Join(", ", _servers),
            Impact = "只影响 IPv4 DNS；IPv6 与 VPN 的 DNS 不受影响"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var current = await _service.GetDnsAsync(_adapter.Index, ct).ConfigureAwait(false);
        var state = new DnsSnapshot
        {
            InterfaceIndex = _adapter.Index,
            InterfaceName = _adapter.Name,
            Servers = current
        };
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = $"DNS:{_adapter.Index}",
            Existed = current.Count > 0,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在设置 DNS…", Percent = 40 });
        var result = await _service.SetDnsAsync(_adapter.Index, _servers, ct).ConfigureAwait(false);
        if (!result.Success) return result;

        // 自动（DHCP）：具体 DNS 由网络下发，不能拿空列表去比对
        if (_servers.Count == 0)
        {
            return OperationResult.Ok("DNS 已恢复为自动（DHCP）");
        }

        progress?.Report(new OperationStep { Message = "正在校验 DNS…", Percent = 85 });
        var actual = await _service.GetDnsAsync(_adapter.Index, ct).ConfigureAwait(false);
        if (!actual.SequenceEqual(_servers, StringComparer.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"DNS 写入后校验失败（当前：{string.Join(", ", actual)}）");
        }
        return OperationResult.Ok("DNS 已更新");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(item => item.Kind == SnapshotKind.Other)
            ?? throw new InvalidOperationException("快照中缺少 DNS 记录");
        var snapshot = JsonSerializer.Deserialize<DnsSnapshot>(entry.DataJson!, JsonConfig.Options)
            ?? throw new InvalidOperationException("DNS 快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复原 DNS…", Percent = 60 });
        var result = await _service.SetDnsAsync(snapshot.InterfaceIndex, snapshot.Servers, ct).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok("已恢复原 DNS") : result;
    }

    private sealed class DnsSnapshot
    {
        public int InterfaceIndex { get; set; }
        public string InterfaceName { get; set; } = string.Empty;
        public List<string> Servers { get; set; } = new();
    }
}

/// <summary>切换适配器的 IPv4 配置（DHCP / 静态），失败或撤销时恢复原配置。</summary>
public sealed class NetworkIpOperation : OperationBase
{
    private readonly NetworkService _service;
    private readonly NetworkAdapterInfo _adapter;
    private readonly bool _dhcp;
    private readonly string _address;
    private readonly int _prefix;
    private readonly string _gateway;

    public NetworkIpOperation(
        NetworkService service,
        NetworkAdapterInfo adapter,
        bool dhcp,
        string address,
        int prefix,
        string gateway)
    {
        _service = service;
        _adapter = adapter;
        _dhcp = dhcp;
        _address = address;
        _prefix = prefix;
        _gateway = gateway;
    }

    public override string Id => "network.ip";
    public override string Name => $"IP 配置：{_adapter.Name} → {(_dhcp ? "自动（DHCP）" : "静态")}";
    public override string Description => "通过 netsh 修改 IPv4 地址；失败或撤销时恢复原配置";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = $"{_adapter.Name} · IPv4",
            Kind = "IP 配置",
            Before = "见执行前快照",
            After = _dhcp ? "自动（DHCP）" : $"{_address}/{_prefix}" + (string.IsNullOrWhiteSpace(_gateway) ? string.Empty : $" · 网关 {_gateway}"),
            Impact = "修改后网络会短暂中断；静态 IP 需要与所在网段匹配"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var config = await _service.GetIpConfigAsync(_adapter.Index, ct).ConfigureAwait(false);
        return CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = $"IP:{_adapter.Index}",
                Existed = true,
                DataJson = JsonSerializer.Serialize(config, JsonConfig.Options)
            }
        });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在修改 IP 配置…", Percent = 50 });
        var result = await _service.SetIpConfigAsync(_adapter.Name, _dhcp, _address, _prefix, _gateway, ct).ConfigureAwait(false);
        if (!result.Success) return result;

        var actual = await _service.GetIpConfigAsync(_adapter.Index, ct).ConfigureAwait(false);
        if (actual.Dhcp != _dhcp) return OperationResult.Fail("写入后 IP 模式校验失败");

        if (!_dhcp && !string.Equals(actual.Address, _address, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"写入后 IP 地址校验失败（当前 {actual.Address}）");
        }

        return OperationResult.Ok(_dhcp ? "已切换为自动获取 IP" : "已设置静态 IP");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault();
        if (entry?.DataJson is null) return OperationResult.Fail("缺少 IP 配置快照");

        var original = JsonSerializer.Deserialize<AdapterIpConfig>(entry.DataJson, JsonConfig.Options);
        if (original is null) return OperationResult.Fail("IP 配置快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复原 IP 配置…", Percent = 60 });
        return await _service.SetIpConfigAsync(
            _adapter.Name, original.Dhcp, original.Address, original.Prefix, original.Gateway, ct).ConfigureAwait(false);
    }
}
