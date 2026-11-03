using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Software;

/// <summary>
/// Chocolatey 批量安装（**当前未启用**，0.29.0-beta 的备选方案）。
/// 主线安装链路是 winget + 官方直链兜底；保留本类只为将来可切。
/// 与 winget 链路一致的语义：逐个安装、单个失败继续、已成功的保留、不整批回滚。
/// </summary>
public sealed class ChocoInstallBatchOperation : OperationBase
{
    private readonly ChocolateyService _choco;
    private readonly IReadOnlyList<SoftwareDefinition> _packages;
    private readonly List<SoftwareDefinition> _installedNow = new();
    private readonly List<SoftwareDefinition> _failedPackages = new();
    private readonly List<string> _failures = new();

    public ChocoInstallBatchOperation(ChocolateyService choco, IReadOnlyList<SoftwareDefinition> packages)
    {
        _choco = choco;
        _packages = packages;
    }

    public IReadOnlyList<SoftwareDefinition> InstalledPackages => _installedNow;
    public IReadOnlyList<SoftwareDefinition> FailedPackages => _failedPackages;
    public IReadOnlyList<string> Failures => _failures;

    public override string Id => "software.choco.install";
    public override string Name => _packages.Count == 1
        ? $"安装「{_packages[0].Name}」（Chocolatey）"
        : $"批量安装 {_packages.Count} 个软件（Chocolatey）";
    public override string Description =>
        "通过 Chocolatey 官方源 community.chocolatey.org 静默安装；单个失败会记录并继续，已成功的软件保留，不自动卸载";
    public override RiskLevel RiskLevel => RiskLevel.Caution;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => _packages.Select(package => new ChangeDescriptor
    {
        Target = package.Name,
        Kind = "软件安装",
        Before = "未安装",
        After = $"通过 Chocolatey 安装 {ChocolateyService.ResolvePackageName(package.Id) ?? "（无对应包）"}",
        Impact = ChocolateyService.IsKnownUnavailable(package.Id)
            ? "Chocolatey 官方源没有该软件的包，将跳过"
            : "安装过程不可自动回滚"
    }).ToList();

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var installed = await _choco.GetInstalledAsync(ct).ConfigureAwait(false);
        return CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = "chocolatey-installed",
                Existed = true,
                DataJson = global::System.Text.Json.JsonSerializer.Serialize(
                    installed, JsonConfig.Compact)
            }
        });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在检查 Chocolatey 环境…", Percent = 1 });
        var ready = await _choco.EnsureInstalledAsync(
            new Progress<string>(line => progress?.Report(new OperationStep { Message = line, Percent = 2 })),
            ct).ConfigureAwait(false);
        if (!ready.Success)
        {
            return OperationResult.Fail(ready.Message);
        }

        var source = await _choco.EnsureSourceAsync(ct).ConfigureAwait(false);
        if (!source.Success)
        {
            Log.Warn("Chocolatey 源检查失败，继续使用当前源：" + source.Message);
        }

        var installed = await _choco.GetInstalledAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < _packages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var package = _packages[i];
            var percent = (int)(i * 100.0 / _packages.Count);

            if (ChocolateyService.IsKnownUnavailable(package.Id))
            {
                _failures.Add($"{package.Name}：Chocolatey 官方源没有该软件的包");
                _failedPackages.Add(package);
                progress?.Report(new OperationStep
                {
                    Message = $"{package.Name}：Chocolatey 官方源没有对应包，跳过",
                    Percent = percent,
                    IsError = true
                });
                continue;
            }

            var chocoName = ChocolateyService.ResolvePackageName(package.Id);
            if (string.IsNullOrWhiteSpace(chocoName))
            {
                _failures.Add($"{package.Name}：无法解析巧克力包名");
                _failedPackages.Add(package);
                continue;
            }

            if (installed.ContainsKey(chocoName))
            {
                progress?.Report(new OperationStep
                {
                    Message = $"{package.Name} 已安装（{chocoName}），跳过",
                    Percent = (int)((i + 1) * 100.0 / _packages.Count)
                });
                _installedNow.Add(package);
                continue;
            }

            progress?.Report(new OperationStep
            {
                Message = $"正在安装 {package.Name}（{chocoName}，{i + 1}/{_packages.Count}）…",
                Percent = percent
            });

            var result = await _choco.InstallAsync(chocoName, line =>
            {
                progress?.Report(new OperationStep { Message = line, Percent = percent });
            }, ct).ConfigureAwait(false);

            if (result.Canceled) throw new OperationCanceledException(ct);

            if (result.Success)
            {
                _installedNow.Add(package);
                _choco.InvalidateInstalledCache();
                continue;
            }

            var reason = LastLine(result.Output);
            _failures.Add($"{package.Name}（{chocoName}，exit {result.ExitCode}）：{reason}");
            _failedPackages.Add(package);
            Log.Warn($"Chocolatey 安装失败: {chocoName} exit={result.ExitCode} - {reason}");
        }

        if (_installedNow.Count == 0 && _failedPackages.Count > 0)
        {
            return OperationResult.Fail($"全部 {_failedPackages.Count} 个软件安装失败：" + _failures[0]);
        }

        var message = $"已安装 {_installedNow.Count} 个软件";
        if (_failedPackages.Count > 0)
        {
            message += $"，{_failedPackages.Count} 个失败（可点「重试失败项」）";
        }

        return OperationResult.Ok(message);
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("Chocolatey 安装不支持自动回滚"));

    private static string LastLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无输出";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return line ?? "无输出";
    }
}
