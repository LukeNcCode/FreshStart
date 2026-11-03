using System.Diagnostics;
using System.Text.RegularExpressions;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Driver;

/// <summary>通过 pnputil 安装驱动包；回滚时删除本次发布的 oemXX.inf。</summary>
public sealed class DriverInstallOperation : OperationBase
{
    private static readonly Regex InfPattern = new(@"([A-Za-z0-9_.\-]+\.inf)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _path;
    private readonly List<string> _addedInfs = new();

    public DriverInstallOperation(string path) => _path = path;

    public override string Id => "driver.install";
    public override string Name => $"安装驱动：{Path.GetFileName(_path.TrimEnd('\\'))}";
    public override string Description => "pnputil /add-driver /install；回滚时删除本次新增的驱动包";
    public override RiskLevel RiskLevel => RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _path,
            Kind = "驱动包",
            Before = "未安装",
            After = "安装并尝试匹配设备",
            Impact = "可能需要重启；不兼容驱动可能导致蓝屏，请先在虚拟机验证"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry { Kind = SnapshotKind.Other, Target = _path, Existed = Directory.Exists(_path) || File.Exists(_path) }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (!File.Exists(_path) && !Directory.Exists(_path))
        {
            return OperationResult.Fail($"驱动路径不存在：{_path}");
        }

        progress?.Report(new OperationStep { Message = "正在安装驱动包…", Percent = 40 });
        var arguments = Directory.Exists(_path)
            ? $"/add-driver \"{_path}\" /subdirs /install"
            : $"/add-driver \"{_path}\" /install";

        var (code, output) = await DriverProcess.RunAsync("pnputil.exe", arguments, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (code is not (0 or 3010))
        {
            return OperationResult.Fail($"pnputil 安装失败（exit {code}）：{DriverProcess.LastLine(output)}");
        }

        _addedInfs.Clear();
        foreach (Match match in InfPattern.Matches(output))
        {
            var inf = match.Groups[1].Value;
            if (inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && !_addedInfs.Contains(inf, StringComparer.OrdinalIgnoreCase))
            {
                _addedInfs.Add(inf);
            }
        }

        Log.Info($"驱动安装完成: {_path}；新增 {_addedInfs.Count} 个驱动包");
        return OperationResult.Ok(code == 3010
            ? "驱动已安装，需要重启后生效"
            : $"驱动已安装（新增 {_addedInfs.Count} 个驱动包）");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (_addedInfs.Count == 0) return OperationResult.Ok("没有可回滚的驱动包");

        var failed = new List<string>();
        foreach (var inf in _addedInfs)
        {
            var (code, output) = await DriverProcess.RunAsync("pnputil.exe", $"/delete-driver {inf} /uninstall /force",
                TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (code != 0) failed.Add($"{inf}（exit {code}: {DriverProcess.LastLine(output)}）");
        }

        return failed.Count == 0
            ? OperationResult.Ok($"已删除 {_addedInfs.Count} 个新装驱动包")
            : OperationResult.Fail("部分驱动包删除失败：" + string.Join("；", failed));
    }


}

/// <summary>删除驱动包；删除前导出备份，回滚时重新安装。</summary>
public sealed class DriverRemoveOperation : OperationBase
{
    private readonly string _infName;
    private readonly string _displayName;
    private string? _backupFolder;

    public DriverRemoveOperation(string infName, string displayName)
    {
        _infName = infName;
        _displayName = displayName;
    }

    public override string Id => "driver.remove";
    public override string Name => $"删除驱动包「{_displayName}」";
    public override string Description => "pnputil /export-driver 备份后删除；回滚时重新安装备份中的驱动";
    public override RiskLevel RiskLevel => RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _infName,
            Kind = "驱动包",
            Before = "已安装",
            After = "删除（先导出备份）",
            Impact = "依赖该驱动的设备可能失效；可能需要重启"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry { Kind = SnapshotKind.Other, Target = _infName, Existed = true }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_infName)) return OperationResult.Fail("驱动包名称无效");

        _backupFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FreshStart", "backup", "drivers", $"{DateTime.Now:yyyyMMdd-HHmmss}-{_infName}");

        progress?.Report(new OperationStep { Message = "正在备份驱动包…", Percent = 30 });
        Directory.CreateDirectory(_backupFolder);
        var (exportCode, exportOutput) = await DriverProcess.RunAsync(
            "pnputil.exe", $"/export-driver {_infName} \"{_backupFolder}\"",
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        if (exportCode != 0 && !Directory.EnumerateFiles(_backupFolder, "*.inf", SearchOption.AllDirectories).Any())
        {
            return OperationResult.Fail($"驱动备份失败，已中止删除（exit {exportCode}）：{DriverProcess.LastLine(exportOutput)}");
        }

        progress?.Report(new OperationStep { Message = "正在删除驱动包…", Percent = 70 });
        var (code, output) = await DriverProcess.RunAsync(
            "pnputil.exe", $"/delete-driver {_infName} /uninstall /force",
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        return code == 0
            ? OperationResult.Ok("驱动包已删除（备份已保留）")
            : OperationResult.Fail($"删除驱动失败（exit {code}）：{DriverProcess.LastLine(output)}");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_backupFolder) || !Directory.Exists(_backupFolder))
        {
            return OperationResult.Fail("找不到驱动备份，无法回滚");
        }

        var inf = Directory.EnumerateFiles(_backupFolder, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (inf is null) return OperationResult.Fail("驱动备份里没有 .inf 文件");

        progress?.Report(new OperationStep { Message = "正在重新安装驱动…", Percent = 60 });
        var (code, output) = await DriverProcess.RunAsync(
            "pnputil.exe", $"/add-driver \"{inf}\" /install",
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        return code is 0 or 3010
            ? OperationResult.Ok("驱动已重新安装")
            : OperationResult.Fail($"驱动回滚失败（exit {code}）：{DriverProcess.LastLine(output)}");
    }
}

internal static class DriverProcess
{
    public static async Task<(int Code, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return (-1, "无法启动进程");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "命令超时");
            }

            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static string LastLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        return lines.Count == 0 ? "无输出" : lines[^1];
    }
}
