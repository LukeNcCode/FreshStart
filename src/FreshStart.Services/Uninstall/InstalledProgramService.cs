using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Services.Uninstall;

public sealed class InstalledProgram
{
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string KeyName { get; set; } = string.Empty;
    public string Hive { get; set; } = "HKLM";
    public string View { get; set; } = "64";
    public DateTimeOffset? UninstalledAt { get; set; }

    public string SizeText => SizeBytes <= 0 ? "—" : FormatSize(SizeBytes);
    public string Subtitle => string.IsNullOrWhiteSpace(Publisher) ? Version : $"{Publisher} · {Version}";
    public bool CanUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString)
                                || !string.IsNullOrWhiteSpace(UninstallString);

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}

/// <summary>
/// 枚举注册表卸载项（ARP）。只保留有卸载命令、非系统组件、非补丁的程序。
/// 卸载通过 UninstallString/QuietUninstallString 或 msiexec 静默参数执行。
/// </summary>
public sealed class InstalledProgramService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(10);
    private readonly string _uninstalledPath = Path.Combine(AppPaths.Root, "uninstalled.json");

    public IReadOnlyList<InstalledProgram> List()
    {
        var programs = new List<InstalledProgram>();
        Collect(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", programs);
        Collect(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", programs);
        Collect(RegistryHive.CurrentUser, RegistryView.Default, "HKCU", programs);

        return programs
            .GroupBy(program => $"{program.DisplayName}|{program.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(program => program.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void RecordUninstalled(InstalledProgram program)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var list = LoadUninstalled().ToList();
            list.RemoveAll(item => string.Equals(item.DisplayName, program.DisplayName, StringComparison.OrdinalIgnoreCase));
            program.UninstalledAt = DateTimeOffset.Now;
            list.Add(program);
            File.WriteAllText(_uninstalledPath, JsonSerializer.Serialize(list, JsonConfig.Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"记录已卸载程序失败: {ex.Message}");
        }
    }

    public IReadOnlyList<InstalledProgram> LoadUninstalled()
    {
        try
        {
            if (!File.Exists(_uninstalledPath)) return Array.Empty<InstalledProgram>();
            return JsonSerializer.Deserialize<List<InstalledProgram>>(
                File.ReadAllText(_uninstalledPath), JsonConfig.Options) ?? new List<InstalledProgram>();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已卸载程序记录失败: {ex.Message}");
            return Array.Empty<InstalledProgram>();
        }
    }

    public bool KeyExists(InstalledProgram program)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryService.ParseHive(program.Hive), RegistryService.ParseView(program.View));
            return baseKey.OpenSubKey(program.KeyName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OperationResult> UninstallAsync(InstalledProgram program, CancellationToken ct)
    {
        var command = BuildCommand(program);
        if (string.IsNullOrWhiteSpace(command)) return OperationResult.Fail("该程序没有可用的卸载命令");

        // 卸载命令来自注册表（第三方程序写入），可能被改成命令链。
        // cmd /c 无法参数化，因此拒绝含命令分隔符/重定向的值。
        if (!IsSafeUninstallCommand(command))
        {
            Log.Warn($"卸载命令含可疑字符，已拒绝执行：{program.DisplayName} - {command}");
            return OperationResult.Fail("该程序的卸载命令包含可疑字符（& | > < ^ 或换行），出于安全考虑已拒绝执行");
        }

        try
        {
            // 这里刻意拼命令行而不用 ArgumentList：cmd.exe 有自己的引号规则，
            // .NET 的标准参数转义会改变 `cmd /c "带空格的路径" /S` 的语义
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return OperationResult.Fail("无法启动卸载程序");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UninstallTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                ct.ThrowIfCancellationRequested();
                return OperationResult.Fail("卸载超时，请检查程序是否仍在运行");
            }

            var output = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var error = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            // 卸载器可能异步写入注册表；轮询等待最多 20 秒
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (!KeyExists(program))
                {
                    return OperationResult.Ok("已卸载");
                }
            }

            var installGone = string.IsNullOrWhiteSpace(program.InstallLocation)
                              || !Directory.Exists(program.InstallLocation);
            if (installGone)
            {
                // 目录已经消失但 ARP 项仍在：视为已卸载，遗留键交给残留清理
                return OperationResult.Ok("卸载程序已结束，注册表项可能为残留");
            }

            return OperationResult.Fail(
                "卸载程序已结束，但安装目录与注册表项仍然存在。可能仍需交互完成或需要重启，稍后可在“软件卸载”里重试。" +
                ShortOutput(output, error));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    internal static string BuildCommand(InstalledProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.QuietUninstallString)) return program.QuietUninstallString.Trim();

        var raw = program.UninstallString.Trim();
        if (raw.Length == 0) return string.Empty;

        var msi = Regex.Match(raw, @"\{[0-9A-Fa-f-]{36}\}");
        if (raw.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && msi.Success)
        {
            return $"msiexec /x {msi.Value} /passive /norestart";
        }
        return raw;
    }

    /// <summary>cmd.exe 会解释这些字符，卸载命令里出现即视为被篡改。</summary>
    private static bool IsSafeUninstallCommand(string command)
    {
        foreach (var ch in command)
        {
            if (ch is '&' or '|' or '>' or '<' or '^' or '\r' or '\n') return false;
        }
        return true;
    }

    private static string ShortOutput(string stdout, string stderr)
    {
        var line = (stderr + Environment.NewLine + stdout)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return string.IsNullOrWhiteSpace(line) ? string.Empty : $" 卸载器输出：{line}";
    }

    private static void Collect(RegistryHive hive, RegistryView view, string hiveText, List<InstalledProgram> programs)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallPath);
            if (root is null) return;

            foreach (var keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key is null) continue;
                    if (key.GetValue("SystemComponent") is int system && system == 1) continue;
                    if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) continue;
                    var releaseType = key.GetValue("ReleaseType") as string ?? string.Empty;
                    if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                        || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase) && name.Length < 12) continue;

                    var uninstall = key.GetValue("UninstallString") as string ?? string.Empty;
                    var quiet = key.GetValue("QuietUninstallString") as string ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(uninstall) && string.IsNullOrWhiteSpace(quiet)) continue;

                    var size = key.GetValue("EstimatedSize") is int kb ? Math.Max(0, (long)kb * 1024) : 0;
                    programs.Add(new InstalledProgram
                    {
                        DisplayName = name.Trim(),
                        Publisher = (key.GetValue("Publisher") as string ?? string.Empty).Trim(),
                        Version = (key.GetValue("DisplayVersion") as string ?? string.Empty).Trim(),
                        InstallDate = (key.GetValue("InstallDate") as string ?? string.Empty).Trim(),
                        SizeBytes = size,
                        UninstallString = uninstall,
                        QuietUninstallString = quiet,
                        InstallLocation = (key.GetValue("InstallLocation") as string ?? string.Empty).Trim(),
                        KeyName = $@"{UninstallPath}\{keyName}",
                        Hive = hiveText,
                        View = view == RegistryView.Registry32 ? "32" : "64"
                    });
                }
                catch
                {
                    // 单个卸载项不可读时跳过
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已安装程序失败: {ex.Message}");
        }
    }
}

/// <summary>卸载程序：不可回滚，但会保存 ARP 快照与审计记录。</summary>
public sealed class UninstallProgramOperation : OperationBase
{
    private readonly InstalledProgramService _service;
    private readonly InstalledProgram _program;

    public UninstallProgramOperation(InstalledProgramService service, InstalledProgram program)
    {
        _service = service;
        _program = program;
    }

    public override string Id => "uninstall.program";
    public override string Name => $"卸载「{_program.DisplayName}」";
    public override string Description => "调用程序自带的卸载命令；卸载不可自动回滚";
    public override RiskLevel RiskLevel => RiskLevel.High;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _program.DisplayName,
            Kind = "已安装程序",
            Before = $"{_program.Version} · {_program.SizeText}",
            After = "卸载",
            Impact = "卸载后程序文件与注册表项会被删除，FreshStart 无法自动恢复；请确认已保存工作"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = _program.DisplayName,
            Existed = true,
            DataJson = JsonSerializer.Serialize(_program, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = $"正在卸载 {_program.DisplayName}…", Percent = 50 });
        return await _service.UninstallAsync(_program, ct).ConfigureAwait(false);
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Ok("卸载操作不支持自动回滚"));
}
