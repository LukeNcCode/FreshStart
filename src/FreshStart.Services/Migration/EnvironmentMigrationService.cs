using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Services.Software;
using FreshStart.Services.Uninstall;
using Microsoft.Win32;

namespace FreshStart.Services.Migration;

public enum MigrationItemKind
{
    Software,
    Development
}

public sealed class MigrationCandidate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required MigrationItemKind Kind { get; init; }
    public string WingetId { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public string Version { get; init; } = string.Empty;
    public string ToolRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> DataRoots { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> PathEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public bool HasPayload => IsInstalled
        || DataRoots.Count > 0
        || ToolRoot.Length > 0;
}

public sealed class MigrationPackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string SourceMachine { get; set; } = Environment.MachineName;
    public string WindowsVersion { get; set; } = Environment.OSVersion.VersionString;
    public List<MigrationPackageItem> Items { get; set; } = new();
}

public sealed class MigrationPackageItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public MigrationItemKind Kind { get; set; }
    public string WingetId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool InstalledOnSource { get; set; }
    public string ToolRoot { get; set; } = string.Empty;
    public string RestoreRoot { get; set; } = string.Empty;
    public List<MigrationDataRoot> DataRoots { get; set; } = new();
    public List<MigrationInstaller> Installers { get; set; } = new();
    public List<string> PathEntries { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

public sealed class MigrationDataRoot
{
    public string TargetPath { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class MigrationInstaller
{
    public string ArchivePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SilentArguments { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class MigrationProgress
{
    public string Message { get; init; } = string.Empty;
    public double Percent { get; init; }
}

public sealed class MigrationRestoreResult
{
    public int InstalledApplications { get; set; }
    public int RestoredApplications { get; set; }
    public int RestoredDataSets { get; set; }
    public int RestoredEnvironments { get; set; }
    public List<string> Warnings { get; init; } = new();
}

public sealed class MigrationPackagePreview
{
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MigrationTargetPreview> Targets { get; init; } = Array.Empty<MigrationTargetPreview>();
}

public sealed class MigrationTargetPreview
{
    public string ItemId { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public string SuggestedPath { get; init; } = string.Empty;
    public bool RequiresRelocation { get; init; }
}

public sealed class EnvironmentMigrationService
{
    private static readonly JsonSerializerOptions PackageJson = new(JsonConfig.Options)
    {
        WriteIndented = true
    };

    private readonly WingetService _winget;
    private readonly InstalledProgramService _programs = new();

    public EnvironmentMigrationService(WingetService winget)
    {
        _winget = winget;
    }

    public async Task<IReadOnlyList<MigrationCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var installed = _programs.List();
        var candidates = new List<MigrationCandidate>
        {
            CreateSocialCandidate(
                "wechat", "微信", "Tencent.WeChat.Universal", installed,
                new[]
                {
                    "%USERPROFILE%\\Documents\\WeChat Files",
                    "%USERPROFILE%\\Documents\\xwechat_files",
                    "%APPDATA%\\Tencent\\xwechat\\config"
                },
                new[] { "微信", "WeChat" }),
            CreateSocialCandidate(
                "qq", "QQ", "Tencent.QQ.NT", installed,
                new[] { "%USERPROFILE%\\Documents\\Tencent Files" },
                new[] { "QQNT", "QQ" }),
            CreateSocialCandidate(
                "feishu", "飞书", "ByteDance.Feishu", installed,
                new[] { "%USERPROFILE%\\Documents\\Feishu", "%USERPROFILE%\\Documents\\Lark" },
                new[] { "飞书", "Feishu", "Lark" })
        };

        candidates.Add(await CreateDevelopmentCandidateAsync(
            "git", "Git", "Git.Git", "git.exe", new[] { "--version" }, ct));
        candidates.Add(await CreateDevelopmentCandidateAsync(
            "python", "Python", "Python.Python.3.14", "python.exe", new[] { "--version" }, ct));
        var node = await CreateDevelopmentCandidateAsync(
            "node", "Node.js", "OpenJS.NodeJS.LTS", "node.exe", new[] { "--version" }, ct);
        if (node.ToolRoot.Length > 0)
        {
            var npmRoot = Environment.ExpandEnvironmentVariables("%APPDATA%\\npm");
            var paths = node.PathEntries.ToList();
            if (Directory.Exists(npmRoot) && !paths.Contains(npmRoot, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(npmRoot);
            }
            node = new MigrationCandidate
            {
                Id = node.Id,
                Name = node.Name,
                Description = node.Description,
                Kind = node.Kind,
                WingetId = node.WingetId,
                IsInstalled = node.IsInstalled,
                Version = node.Version,
                ToolRoot = node.ToolRoot,
                DataRoots = Directory.Exists(npmRoot) ? new[] { npmRoot } : Array.Empty<string>(),
                PathEntries = paths,
                EnvironmentVariables = node.EnvironmentVariables
            };
        }
        candidates.Add(node);
        return candidates;
    }

    public async Task<string> ExportAsync(
        IReadOnlyList<MigrationCandidate> candidates,
        IReadOnlyDictionary<string, string> manualInstallerDirectories,
        string outputPath,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) throw new InvalidOperationException("至少选择一项迁移内容");

        var stage = Path.Combine(AppPaths.CacheRoot, "migration-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new MigrationPackageManifest();

        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                var basePercent = index * 100d / candidates.Count;
                progress?.Report(new MigrationProgress
                {
                    Message = $"正在提取 {candidate.Name}…",
                    Percent = basePercent
                });

                var item = new MigrationPackageItem
                {
                    Id = candidate.Id,
                    Name = candidate.Name,
                    Kind = candidate.Kind,
                    WingetId = candidate.WingetId,
                    Version = candidate.Version,
                    InstalledOnSource = candidate.IsInstalled,
                    ToolRoot = candidate.ToolRoot,
                    PathEntries = candidate.PathEntries.ToList(),
                    EnvironmentVariables = new Dictionary<string, string>(candidate.EnvironmentVariables)
                };

                if (candidate.Kind == MigrationItemKind.Software)
                {
                    var installerTarget = Path.Combine(stage, "software", candidate.Id, "installers");
                    Directory.CreateDirectory(installerTarget);
                    if (manualInstallerDirectories.TryGetValue(candidate.Id, out var manualDir)
                        && Directory.Exists(manualDir))
                    {
                        await CopyFilesAsync(manualDir, installerTarget, stage, checksums, ct)
                            .ConfigureAwait(false);
                    }
                    else if (!string.IsNullOrWhiteSpace(candidate.WingetId))
                    {
                        try
                        {
                            await DownloadInstallerAsync(candidate, installerTarget, ct).ConfigureAwait(false);
                            await AddChecksumsAsync(installerTarget, stage, checksums, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"无法获取 {candidate.Name} 的离线安装器；请选择本地安装包后重试。{ex.Message}", ex);
                        }
                    }

                    item.Installers = Directory.Exists(installerTarget)
                        ? Directory.GetFiles(installerTarget, "*", SearchOption.AllDirectories)
                            .Where(IsInstallerFile)
                            .Select(path => new MigrationInstaller
                            {
                                ArchivePath = ToArchivePath(stage, path),
                                FileName = Path.GetFileName(path),
                                SizeBytes = new FileInfo(path).Length,
                                SilentArguments = SilentArgumentsFor(candidate.WingetId, path)
                            }).ToList()
                        : new List<MigrationInstaller>();
                    if (item.Installers.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"{candidate.Name} 没有可用的离线安装器。请在源机器选择安装包后再导出。");
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(candidate.ToolRoot)
                        || !Directory.Exists(candidate.ToolRoot))
                    {
                        throw new InvalidOperationException($"{candidate.Name} 的安装目录不存在");
                    }

                    var archiveRoot = Path.Combine(stage, "development", candidate.Id, "root");
                    await CopyFilesAsync(candidate.ToolRoot, archiveRoot, stage, checksums, ct)
                        .ConfigureAwait(false);
                    item.RestoreRoot = candidate.ToolRoot;
                }

                item.DataRoots.AddRange(await CopyDataRootsAsync(
                    candidate, stage, checksums, ct).ConfigureAwait(false));
                manifest.Items.Add(item);
            }

            var manifestPath = Path.Combine(stage, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, PackageJson),
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);
            checksums["manifest.json"] = await HashAsync(manifestPath, ct).ConfigureAwait(false);

            var checksumPath = Path.Combine(stage, "checksums.json");
            await File.WriteAllTextAsync(
                checksumPath,
                JsonSerializer.Serialize(checksums, PackageJson),
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            progress?.Report(new MigrationProgress { Message = "正在压缩克隆包…", Percent = 96 });
            await Task.Run(() =>
                ZipFile.CreateFromDirectory(stage, outputPath, CompressionLevel.Optimal, false),
                ct).ConfigureAwait(false);

            var packageHash = await HashAsync(outputPath, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                outputPath + ".sha256",
                $"{packageHash}  {Path.GetFileName(outputPath)}{Environment.NewLine}",
                Encoding.ASCII,
                ct).ConfigureAwait(false);
            progress?.Report(new MigrationProgress { Message = "克隆包已生成", Percent = 100 });
            return outputPath;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* cache cleanup is best effort */ }
        }
    }

    public async Task<MigrationRestoreResult> RestoreAsync(
        string packagePath,
        IReadOnlyDictionary<string, string>? relocationRoots = null,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到克隆包", packagePath);
        var stage = Path.Combine(AppPaths.CacheRoot, "migration-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var result = new MigrationRestoreResult();
        try
        {
            progress?.Report(new MigrationProgress { Message = "正在校验克隆包…", Percent = 1 });
            ZipFile.ExtractToDirectory(packagePath, stage, overwriteFiles: true);
            var manifestPath = Path.Combine(stage, "manifest.json");
            var checksumPath = Path.Combine(stage, "checksums.json");
            if (!File.Exists(manifestPath) || !File.Exists(checksumPath))
            {
                throw new InvalidDataException("克隆包缺少 manifest.json 或 checksums.json");
            }

            var manifest = JsonSerializer.Deserialize<MigrationPackageManifest>(
                await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), PackageJson)
                ?? throw new InvalidDataException("无法读取克隆包清单");
            if (manifest.SchemaVersion != 1) throw new InvalidDataException("不支持的克隆包版本");

            var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(checksumPath, ct).ConfigureAwait(false), PackageJson)
                ?? throw new InvalidDataException("无法读取校验清单");
            var verified = 0;
            foreach (var (relativePath, expected) in checksums)
            {
                ct.ThrowIfCancellationRequested();
                var path = ResolveInside(stage, relativePath);
                if (!File.Exists(path)) throw new InvalidDataException($"克隆包文件缺失：{relativePath}");
                var actual = await HashAsync(path, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"克隆包校验失败：{relativePath}");
                }
                verified++;
                progress?.Report(new MigrationProgress
                {
                    Message = $"校验文件 {verified}/{checksums.Count}…",
                    Percent = 2 + verified * 8d / Math.Max(1, checksums.Count)
                });
            }

            var processed = 0;
            foreach (var item in manifest.Items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new MigrationProgress
                {
                    Message = $"正在恢复 {item.Name}…",
                    Percent = 10 + processed * 85d / Math.Max(1, manifest.Items.Count)
                });

                if (item.Kind == MigrationItemKind.Software)
                {
                    if (item.Installers.Count > 0)
                    {
                        await InstallOfflineAsync(item, stage, result.Warnings, ct).ConfigureAwait(false);
                        result.InstalledApplications++;
                    }
                    else
                    {
                        result.Warnings.Add($"{item.Name} 没有安装介质，仅恢复了用户数据");
                    }
                }
                else
                {
                    await RestoreDevelopmentToolAsync(item, stage, result.Warnings, ct).ConfigureAwait(false);
                    result.RestoredEnvironments++;
                }

                var restoredDataTargets = new List<(string Original, string Actual)>();
                foreach (var data in item.DataRoots)
                {
                    var originalTarget = Environment.ExpandEnvironmentVariables(data.TargetPath);
                    if (IsProtectedTarget(originalTarget))
                    {
                        result.Warnings.Add($"{item.Name} 的数据目标位于受保护位置，已跳过：{originalTarget}");
                        continue;
                    }
                    string? relocationRoot = null;
                    relocationRoots?.TryGetValue(originalTarget, out relocationRoot);
                    var target = ResolveAvailableDataTarget(item.Id, originalTarget, relocationRoot);
                    if (!string.Equals(target, originalTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add(
                            $"{item.Name} 的目标盘不存在，已改写到：{target}");
                    }
                    var source = ResolveInside(stage, data.ArchivePath);
                    if (!Directory.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? target);
                    if (Directory.Exists(target))
                    {
                        var backup = $"{target}.freshstart-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                        Directory.Move(target, backup);
                        result.Warnings.Add($"{item.Name} 原数据已备份到 {backup}");
                    }
                    await CopyFilesAsync(source, target, null, null, ct).ConfigureAwait(false);
                    restoredDataTargets.Add((originalTarget, target));
                    result.RestoredDataSets++;
                }
                if (item.Id == "wechat" && restoredDataTargets.Count > 0)
                {
                    RewriteWeChatDataPaths(restoredDataTargets, result.Warnings);
                }
                processed++;
            }

            progress?.Report(new MigrationProgress { Message = "迁移完成", Percent = 100 });
            return result;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* best effort */ }
        }
    }

    public Task<MigrationPackagePreview> InspectAsync(
        string packagePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到克隆包", packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("克隆包缺少 manifest.json");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var manifest = JsonSerializer.Deserialize<MigrationPackageManifest>(reader.ReadToEnd(), PackageJson)
                       ?? throw new InvalidDataException("无法读取克隆包清单");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("不支持的克隆包版本");
        if (manifest.Items.Count is < 1 or > 64)
        {
            throw new InvalidDataException("克隆包项目数量不合法");
        }

        return Task.FromResult(new MigrationPackagePreview
        {
            Items = manifest.Items.Select(item =>
                $"{item.Name}：安装器 {item.Installers.Count} 个，数据目录 {item.DataRoots.Count} 个").ToList(),
            Targets = manifest.Items
                .SelectMany(item => item.DataRoots.Select(data =>
                {
                    var original = Environment.ExpandEnvironmentVariables(data.TargetPath);
                    var requiresRelocation = TargetRequiresRelocation(original);
                    var resolved = requiresRelocation
                        ? BuildRelocationTarget(
                            item.Id, original,
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "FreshStart", "MigrationData"))
                        : Path.GetFullPath(original);
                    return new MigrationTargetPreview
                    {
                        ItemId = item.Id,
                        ItemName = item.Name,
                        OriginalPath = original,
                        SuggestedPath = resolved,
                        RequiresRelocation = requiresRelocation
                    };
                }))
                .ToList()
        });
    }

    private static MigrationCandidate CreateSocialCandidate(
        string id,
        string name,
        string wingetId,
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyList<string> dataCandidates,
        IReadOnlyList<string> nameHints)
    {
        var installed = programs.FirstOrDefault(program =>
            nameHints.Any(hint => program.DisplayName.Contains(hint, StringComparison.OrdinalIgnoreCase))
            && !program.DisplayName.Contains("开发者工具", StringComparison.OrdinalIgnoreCase));
        var roots = dataCandidates
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (id == "wechat")
        {
            foreach (var configured in FindWeChatConfiguredRoots())
            {
                if (!roots.Contains(configured, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(configured);
                }
            }
        }

        return new MigrationCandidate
        {
            Id = id,
            Name = name,
            Description = roots.Count == 0
                ? "未发现默认聊天数据目录"
                : $"发现 {roots.Count} 个聊天数据目录，可离线迁移",
            Kind = MigrationItemKind.Software,
            WingetId = wingetId,
            IsInstalled = installed is not null,
            Version = installed?.Version ?? string.Empty,
            DataRoots = roots
        };
    }

    private static async Task<MigrationCandidate> CreateDevelopmentCandidateAsync(
        string id,
        string name,
        string wingetId,
        string executable,
        IReadOnlyList<string> versionArgs,
        CancellationToken ct)
    {
        var executablePath = FindExecutable(executable);
        var root = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Directory.GetParent(executablePath)?.FullName ?? string.Empty;
        if (id == "git" && root.EndsWith("\\cmd", StringComparison.OrdinalIgnoreCase))
        {
            root = Directory.GetParent(root)?.FullName ?? root;
        }

        var version = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : await RunVersionAsync(executablePath, versionArgs, ct).ConfigureAwait(false);
        var pathEntries = root.Length == 0
            ? new List<string>()
            : FindPathEntries(root);
        var env = CollectToolEnvironment(id, root);

        return new MigrationCandidate
        {
            Id = id,
            Name = name,
            Description = root.Length == 0
                ? "未在 PATH 中检测到可执行文件"
                : $"安装目录：{root}",
            Kind = MigrationItemKind.Development,
            WingetId = wingetId,
            IsInstalled = root.Length > 0,
            Version = version,
            ToolRoot = root,
            PathEntries = pathEntries,
            EnvironmentVariables = env
        };
    }

    private static async Task<List<MigrationDataRoot>> CopyDataRootsAsync(
        MigrationCandidate candidate,
        string stage,
        Dictionary<string, string> checksums,
        CancellationToken ct)
    {
        var results = new List<MigrationDataRoot>();
        for (var index = 0; index < candidate.DataRoots.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = Environment.ExpandEnvironmentVariables(candidate.DataRoots[index]);
            if (!Directory.Exists(source)) continue;
            if (IsProtectedTarget(source))
            {
                throw new InvalidOperationException(
                    $"{candidate.Name} 的数据目录指向受保护位置：{source}");
            }
            var relative = $"data/{candidate.Id}/{index}";
            var destination = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            var stats = await CopyFilesAsync(source, destination, stage, checksums, ct)
                .ConfigureAwait(false);
            results.Add(new MigrationDataRoot
            {
                TargetPath = TokenizePath(source),
                ArchivePath = relative,
                FileCount = stats.FileCount,
                SizeBytes = stats.SizeBytes
            });
        }
        return results;
    }

    private static async Task DownloadInstallerAsync(
        MigrationCandidate candidate,
        string destination,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var args = new List<string>
        {
            "download", "--id", candidate.WingetId, "--exact",
            "--scope", "machine", "--architecture", "x64",
            "--download-directory", destination,
            "--accept-package-agreements", "--accept-source-agreements",
            "--disable-interactivity"
        };
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "winget.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("下载安装器超时");
        }
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
                     (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"winget download 失败（exit {process.ExitCode}）：{LastLine(output)}");
        }
    }

    private static async Task<(int FileCount, long SizeBytes)> CopyFilesAsync(
        string source,
        string destination,
        string? stage,
        Dictionary<string, string>? checksums,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var total = files.Sum(path => new FileInfo(path).Length);
        var count = 0;
        long copied = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            count++;
            copied += new FileInfo(target).Length;
            if (stage is not null && checksums is not null)
            {
                checksums[ToArchivePath(stage, target)] = await HashAsync(target, ct).ConfigureAwait(false);
            }
        }
        return (count, copied);
    }

    private static async Task AddChecksumsAsync(
        string directory,
        string stage,
        Dictionary<string, string> checksums,
        CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            checksums[ToArchivePath(stage, file)] = await HashAsync(file, ct).ConfigureAwait(false);
        }
    }

    private static async Task InstallOfflineAsync(
        MigrationPackageItem item,
        string stage,
        List<string> warnings,
        CancellationToken ct)
    {
        foreach (var installer in item.Installers.OrderByDescending(file => file.SizeBytes))
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveInside(stage, installer.ArchivePath);
            if (!File.Exists(path)) continue;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            ProcessStartInfo start;
            if (extension == ".msi")
            {
                start = new ProcessStartInfo("msiexec.exe");
                start.ArgumentList.Add("/i");
                start.ArgumentList.Add(path);
                start.ArgumentList.Add("/qn");
                start.ArgumentList.Add("/norestart");
            }
            else if (extension is ".msix" or ".msixbundle" or ".appx" or ".appxbundle")
            {
                start = new ProcessStartInfo("powershell.exe");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"Add-AppxPackage -Path '{path.Replace("'", "''")}'");
            }
            else
            {
                start = new ProcessStartInfo(path) { UseShellExecute = false };
                foreach (var argument in SplitArguments(installer.SilentArguments))
                {
                    start.ArgumentList.Add(argument);
                }
            }

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using var process = Process.Start(start);
            if (process is null)
            {
                warnings.Add($"{item.Name} 无法启动安装器 {installer.FileName}");
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    warnings.Add($"{item.Name} 安装器 {installer.FileName} 返回 {process.ExitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }
        }
    }

    private static async Task RestoreDevelopmentToolAsync(
        MigrationPackageItem item,
        string stage,
        List<string> warnings,
        CancellationToken ct)
    {
        if (item.ToolRoot.Length == 0) return;
        var source = ResolveInside(stage, $"development/{SafeName(item.Id)}/root");
        if (!Directory.Exists(source))
        {
            warnings.Add($"{item.Name} 的目录快照不存在");
            return;
        }

        var targetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreshStart", "Environments", SafeName(item.Id));
        warnings.Add($"{item.Name} 已恢复到独立目录：{targetRoot}");
        await CopyFilesAsync(source, targetRoot, null, null, ct).ConfigureAwait(false);

        foreach (var (name, value) in item.EnvironmentVariables)
        {
            var restored = ReplaceRoot(value, item.ToolRoot, targetRoot);
            if (restored.Length == 0) continue;
            Environment.SetEnvironmentVariable(name, restored, EnvironmentVariableTarget.User);
        }

        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)
                       ?? string.Empty;
        var entries = userPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var entry in item.PathEntries)
        {
            var restored = ReplaceRoot(entry, item.ToolRoot, targetRoot);
            if (restored.Length > 0
                && !entries.Contains(restored, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(restored);
            }
        }
        Environment.SetEnvironmentVariable(
            "Path", string.Join(';', entries), EnvironmentVariableTarget.User);
    }

    private static string FindExecutable(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (directory.Contains(@"\.cache\codex", StringComparison.OrdinalIgnoreCase)
                || directory.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid PATH entry */ }
        }
        return string.Empty;
    }

    private static List<string> FindPathEntries(string root)
    {
        var results = new List<string>();
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = Environment.GetEnvironmentVariable("Path", scope) ?? string.Empty;
            foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\');
                if (expanded.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    && !results.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                }
            }
        }
        return results;
    }

    private static Dictionary<string, string> CollectToolEnvironment(string id, string root)
    {
        var allow = id switch
        {
            "git" => new[] { "GIT_EXEC_PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM" },
            "python" => new[] { "PYTHONHOME", "PYTHONPATH" },
            "node" => new[] { "NODE_PATH", "NPM_CONFIG_PREFIX", "NPM_CONFIG_CACHE" },
            _ => Array.Empty<string>()
        };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var name in allow)
            {
                var value = Environment.GetEnvironmentVariable(name, scope);
                if (!string.IsNullOrWhiteSpace(value)
                    && (root.Length == 0 || value.Contains(root, StringComparison.OrdinalIgnoreCase))
                    && !ContainsSecretMarker(name))
                {
                    result[name] = TokenizePath(Environment.ExpandEnvironmentVariables(value));
                }
            }
        }
        return result;
    }

    private static bool ContainsSecretMarker(string name)
        => name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
           || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
           || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
           || name.Contains("KEY", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> RunVersionAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)
                          ?? await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)
                          ?? string.Empty;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return output.Trim();
        }
        catch
        {
            return "版本未知";
        }
    }

    private static string SilentArgumentsFor(string wingetId, string path)
    {
        if (Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return wingetId.ToLowerInvariant() switch
        {
            "git.git" => "/VERYSILENT /NORESTART /NOCANCEL /SP-",
            "python.python.3.14" => "/quiet InstallAllUsers=1 PrependPath=1 Include_test=0",
            "tencent.wechat" or "tencent.wechat.universal" => "/S",
            "tencent.qq.nt" => "/S",
            "bytedance.feishu" => "/S",
            _ => "/S"
        };
    }

    private static bool IsInstallerFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is
            ".exe" or ".msi" or ".msix" or ".msixbundle" or ".appx" or ".appxbundle";

    private static IEnumerable<string> SplitArguments(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string TokenizePath(string path)
        => path.Replace(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "%USERPROFILE%",
            StringComparison.OrdinalIgnoreCase);

    private static string ReplaceRoot(string value, string oldRoot, string newRoot)
        => value.Replace(oldRoot.TrimEnd('\\'), newRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string ToArchivePath(string stage, string path)
        => Path.GetRelativePath(stage, path).Replace('\\', '/');

    private static string ResolveInside(string root, string relativePath)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"克隆包包含越界路径：{relativePath}");
        }
        return target;
    }

    private static bool IsProtectedTarget(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(
                full,
                Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TargetRequiresRelocation(string path)
    {
        if (IsProtectedTarget(path)) return true;
        try
        {
            var full = Path.GetFullPath(path);
            var drive = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(drive) || !Directory.Exists(drive)) return true;

            var probeRoot = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            while (!string.IsNullOrWhiteSpace(probeRoot) && !Directory.Exists(probeRoot))
            {
                probeRoot = Path.GetDirectoryName(probeRoot);
            }
            if (string.IsNullOrWhiteSpace(probeRoot)) return true;

            var probe = Path.Combine(
                probeRoot, $".freshstart-write-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string ResolveAvailableDataTarget(
        string itemId,
        string originalTarget,
        string? relocationRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(relocationRoot))
        {
            return BuildRelocationTarget(itemId, originalTarget, relocationRoot);
        }

        if (!TargetRequiresRelocation(originalTarget))
        {
            return Path.GetFullPath(originalTarget);
        }

        throw new InvalidOperationException(
            $"目标目录不可写，必须选择替代位置：{originalTarget}");
    }

    public static string BuildRelocationTarget(
        string itemId,
        string originalTarget,
        string selectedRoot)
    {
        var driveRoot = Path.GetPathRoot(originalTarget)?.TrimEnd(Path.DirectorySeparatorChar)
                        ?? string.Empty;
        var relative = originalTarget.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase)
            ? originalTarget[driveRoot.Length..].TrimStart(Path.DirectorySeparatorChar)
            : Path.GetFileName(originalTarget.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(relative)) relative = "data";
        return Path.Combine(selectedRoot, SafeName(itemId), relative);
    }

    private static void RewriteWeChatDataPaths(
        IReadOnlyList<(string Original, string Actual)> restoredTargets,
        List<string> warnings)
    {
        try
        {
            var configMapping = restoredTargets.FirstOrDefault(pair =>
                pair.Original.TrimEnd('\\').EndsWith(
                    @"\Tencent\xwechat\config", StringComparison.OrdinalIgnoreCase));
            if (configMapping.Original is null) return;

            var dataMappings = restoredTargets
                .Where(pair => !ReferenceEquals(pair.Original, configMapping.Original)
                               && !string.Equals(pair.Original, configMapping.Original,
                                   StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dataMappings.Count == 0) return;

            foreach (var file in Directory.GetFiles(configMapping.Actual, "*.ini"))
            {
                var text = File.ReadAllText(file).Trim().Trim('"');
                var mapping = dataMappings.FirstOrDefault(pair =>
                    string.Equals(text, pair.Original.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith(pair.Original.TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase));
                if (mapping.Original is null) continue;
                File.WriteAllText(
                    file,
                    text.Replace(mapping.Original.TrimEnd('\\'), mapping.Actual.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase),
                    new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"微信数据路径改写失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<string> FindWeChatConfiguredRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tencent", "xwechat", "config");
            if (Directory.Exists(configRoot))
            {
                foreach (var file in Directory.GetFiles(configRoot, "*.ini"))
                {
                    var value = File.ReadLines(file).FirstOrDefault()?.Trim('"', ' ');
                    if (string.IsNullOrWhiteSpace(value) || IsProtectedTarget(value)) continue;
                    if (Directory.Exists(value))
                    {
                        roots.Add(Path.GetFullPath(value));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取微信数据目录配置失败: {ex.Message}");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat");
            foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
            {
                if (key?.GetValue(name) is not string value || string.IsNullOrWhiteSpace(value)) continue;
                if (!name.Contains("path", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("save", StringComparison.OrdinalIgnoreCase)) continue;
                value = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                if (!IsProtectedTarget(value) && Directory.Exists(value))
                {
                    roots.Add(Path.GetFullPath(value));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取微信注册表数据目录失败: {ex.Message}");
        }

        return roots.ToList();
    }

    private static string SafeName(string value)
    {
        var safe = new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        return safe.Length == 0 ? "environment" : safe;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string LastLine(string value)
        => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault() ?? "无输出";
}
