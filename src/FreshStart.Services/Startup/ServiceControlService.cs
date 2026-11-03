using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;

namespace FreshStart.Services.Startup;

public sealed class ServiceState
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int StartType { get; set; }
    public bool DelayedAutoStart { get; set; }
    public string ImagePath { get; set; } = string.Empty;
}

/// <summary>
/// 服务启动类型读写。注册表是状态源，sc.exe 负责通知 SCM。
/// 写后读回校验，失败由 OperationRunner 回滚。
/// </summary>
public sealed class ServiceControlService
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    public ServiceState Capture(string name)
    {
        var state = new ServiceState { Name = name };
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey($@"{ServicesPath}\{name}");
        if (key is null) return state;

        state.Exists = true;
        state.DisplayName = ResolveDisplayName(key.GetValue("DisplayName") as string) is { Length: > 0 } display ? display : name;
        state.StartType = key.GetValue("Start") is int start ? start : 3;
        state.DelayedAutoStart = key.GetValue("DelayedAutoStart") is int delayed && delayed == 1;
        state.ImagePath = key.GetValue("ImagePath") as string ?? string.Empty;
        return state;
    }

    /// <summary>枚举 Win32 服务（Type 0x10/0x20），不含内核驱动；按显示名排序。</summary>
    public List<ServiceState> List()
    {
        var services = new List<ServiceState>();
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(ServicesPath);
            if (root is null) return services;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;
                    var type = key.GetValue("Type") is int value ? value : 0;
                    if ((type & 0x30) == 0) continue; // 驱动服务不在此页展示

                    services.Add(new ServiceState
                    {
                        Name = name,
                        DisplayName = ResolveDisplayName(key.GetValue("DisplayName") as string) is { Length: > 0 } display ? display : name,
                        Exists = true,
                        StartType = key.GetValue("Start") is int start ? start : 3,
                        DelayedAutoStart = key.GetValue("DelayedAutoStart") is int delayed && delayed == 1,
                        ImagePath = key.GetValue("ImagePath") as string ?? string.Empty
                    });
                }
                catch
                {
                    // 单个服务不可读时跳过
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举服务失败: {ex.Message}");
        }

        return services
            .OrderBy(service => service.StartType == 4 ? 1 : 0)
            .ThenBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult> SetStartModeAsync(string name, int startType, bool delayed, CancellationToken ct)
    {
        var mode = startType switch
        {
            2 => delayed ? "delayed-auto" : "auto",
            4 => "disabled",
            _ => "demand"
        };

        var (exitCode, output) = await RunScAsync($@"config ""{name}"" start= {mode}", ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return OperationResult.Fail(exitCode == 5
                ? $"无法修改服务「{name}」的启动类型：拒绝访问（该服务受系统保护或 ACL 限制，请用系统设置或组策略处理）"
                : $"sc config 失败（exit {exitCode}）：{output.Trim()}");
        }

        // sc 对 delayed-auto 的处理依赖版本，注册表兜底确保状态一致；
        // 受保护服务可能拒绝直接写注册表，此时以 sc 之后的读回校验为准。
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{ServicesPath}\{name}", writable: true);
            if (key is null) return OperationResult.Fail($"服务不存在：{name}");
            key.SetValue("Start", startType, Microsoft.Win32.RegistryValueKind.DWord);
            if (startType == 2 && delayed) key.SetValue("DelayedAutoStart", 1, Microsoft.Win32.RegistryValueKind.DWord);
            else key.DeleteValue("DelayedAutoStart", throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException)
        {
            // 交给下面的读回校验判断 sc 是否已经完成修改
        }
        catch (IOException)
        {
            // 同上
        }

        var actual = Capture(name);
        if (!actual.Exists || actual.StartType != startType || actual.DelayedAutoStart != (startType == 2 && delayed))
        {
            return OperationResult.Fail($"服务状态校验失败：{name}");
        }

        return OperationResult.Ok($"服务 {name} 启动类型已设置为 {ModeText(startType, delayed)}");
    }

    private static string ModeText(int startType, bool delayed) => startType switch
    {
        2 => delayed ? "自动（延迟）" : "自动",
        4 => "禁用",
        _ => "手动"
    };

    /// <summary>
    /// 服务显示名可能是 @%SystemRoot%\...\file.dll,-100 或 @oem.inf,%Name%;Fallback 形式的资源字符串。
    /// 展开环境变量后调用 SHLoadIndirectString；失败时退回 ; 或 , 后面的英文回退文本。
    /// </summary>
    private static string ResolveDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (!raw.StartsWith('@')) return raw;

        var source = raw;
        var fallback = string.Empty;

        var semi = raw.IndexOf(';');
        if (semi > 0)
        {
            fallback = raw[(semi + 1)..].Trim();
            source = raw[..semi];
        }
        else
        {
            var firstComma = raw.IndexOf(',');
            var lastComma = raw.LastIndexOf(',');
            if (firstComma > 0 && lastComma > firstComma)
            {
                fallback = raw[(lastComma + 1)..].Trim();
                source = raw[..lastComma];
            }
        }

        source = Environment.ExpandEnvironmentVariables(source);

        var buffer = new StringBuilder(512);
        var hr = SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero);
        if (hr == 0 && buffer.Length > 0) return buffer.ToString();
        return fallback;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source, StringBuilder outBuffer, int outBufferSize, IntPtr reserved);

    private static async Task<(int ExitCode, string Output)> RunScAsync(string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var output = (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false));
        return (process.ExitCode, output);
    }
}

/// <summary>单个服务的启停/延迟操作。禁用运行中的服务需要重启后完全生效，UI 会提示。</summary>
public sealed class ServiceStartModeOperation : OperationBase
{
    private readonly ServiceControlService _services;
    private readonly string _name;
    private readonly int _targetStart;
    private readonly bool _targetDelayed;
    private readonly RiskLevel _risk;

    public ServiceStartModeOperation(
        ServiceControlService services, string name, int targetStart, bool targetDelayed, RiskLevel risk)
    {
        _services = services;
        _name = name;
        _targetStart = targetStart;
        _targetDelayed = targetDelayed;
        _risk = risk;
    }

    public override string Id => "startup.service.startmode";
    public override string Name => $"设置服务「{_name}」启动类型";
    public override string Description => "通过 SCM 修改服务启动类型；禁用运行中的服务通常需要重启后完全生效";
    public override RiskLevel RiskLevel => _risk;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges()
    {
        var current = _services.Capture(_name);
        return new[]
        {
            new ChangeDescriptor
            {
                Target = $@"HKLM\SYSTEM\CurrentControlSet\Services\{_name}\Start",
                Kind = "系统服务",
                Before = current.Exists ? ModeText(current.StartType, current.DelayedAutoStart) : "（不存在）",
                After = ModeText(_targetStart, _targetDelayed),
                Impact = "影响开机启动与服务可用性；服务不会立即停止，重启后生效"
            }
        };
    }

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = _services.Capture(_name);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Service,
            Target = _name,
            Existed = state.Exists,
            DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var snapshot = Read(state);
        if (!snapshot.Exists) return OperationResult.Fail($"服务不存在：{_name}");
        progress?.Report(new OperationStep { Message = "正在修改服务启动类型…", Percent = 60 });
        return await _services.SetStartModeAsync(_name, _targetStart, _targetDelayed, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var snapshot = Read(state);
        progress?.Report(new OperationStep { Message = "正在恢复服务启动类型…", Percent = 60 });
        return await _services.SetStartModeAsync(_name, snapshot.StartType, snapshot.DelayedAutoStart, ct).ConfigureAwait(false);
    }

    private static ServiceState Read(OperationSnapshot state)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.Service)
            ?? throw new InvalidOperationException("快照缺少服务状态");
        return JsonSerializer.Deserialize<ServiceState>(entry.DataJson!, JsonConfig.Options)
            ?? throw new InvalidOperationException("服务快照损坏");
    }

    private static string ModeText(int startType, bool delayed) => startType switch
    {
        2 => delayed ? "自动（延迟）" : "自动",
        4 => "禁用",
        _ => "手动"
    };
}
