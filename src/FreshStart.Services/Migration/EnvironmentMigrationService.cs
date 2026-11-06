using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Diagnostics;
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
    public required string Name { get; set; }
    public required string Description { get; init; }
    public required MigrationItemKind Kind { get; init; }
    public string WingetId { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public string Version { get; init; } = string.Empty;
    public string ToolRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> DataRoots { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> LocalInstallerPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PathEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string> MachinePathEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UserPathEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> MachineEnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> UserEnvironmentVariables { get; init; } =
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
    // Null means the package was created before scope-aware environment migration.
    public List<string>? MachinePathEntries { get; set; }
    public List<string>? UserPathEntries { get; set; }
    public Dictionary<string, string>? MachineEnvironmentVariables { get; set; }
    public Dictionary<string, string>? UserEnvironmentVariables { get; set; }
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
        var installed = await Task.Run(_programs.List, ct).ConfigureAwait(false);
        var wechatConfiguredRoots = FindWeChatConfiguredRoots();
        var qqConfiguredRoots = FindQQConfiguredRoots();
        var feishuPaths = FindFeishuPaths();
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
                new[] { "微信", "WeChat" },
                wechatConfiguredRoots),
            CreateSocialCandidate(
                "qq", "QQ", "Tencent.QQ.NT", installed,
                new[] { "%USERPROFILE%\\Documents\\Tencent Files" },
                new[] { "QQNT", "QQ" },
                qqConfiguredRoots),
            CreateSocialCandidate(
                "feishu", "飞书", "ByteDance.Feishu", installed,
                new[]
                {
                    "%APPDATA%\\LarkShell",
                    "%APPDATA%\\Feishu",
                    "%APPDATA%\\Lark",
                    "%APPDATA%\\lark_public",
                    "%LOCALAPPDATA%\\Lark\\User Data",
                    "%USERPROFILE%\\Documents\\Feishu",
                    "%USERPROFILE%\\Documents\\Lark"
                },
                new[] { "飞书", "Feishu", "Lark" },
                string.IsNullOrWhiteSpace(feishuPaths.DataDirectory)
                    ? Array.Empty<string>()
                    : new[] { feishuPaths.DataDirectory },
                new[] { "Feishu", "Lark" },
                feishuPaths.InstallDirectory)
        };
        var discoveredRoots = wechatConfiguredRoots.Count > 0 && qqConfiguredRoots.Count > 0
            ? EmptyDiscoveredRoots()
            : await Task.Run(() => FindKnownDataRoots(ct), ct).ConfigureAwait(false);
        AddDiscoveredRoots(candidates[0], discoveredRoots.GetValueOrDefault("wechat"));
        AddDiscoveredRoots(candidates[1], discoveredRoots.GetValueOrDefault("qq"));
        AddDiscoveredRoots(candidates[2], discoveredRoots.GetValueOrDefault("feishu"));

        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "git", "Git", "Git.Git", "git.exe", new[] { "--version" }, ct));
        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "python", "Python", "Python.Python.3.14",
            "python.exe", new[] { "--version" }, ct));
        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "node", "Node.js", "OpenJS.NodeJS.LTS",
            "node.exe", new[] { "--version" }, ct));
        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "java", "Java", "EclipseAdoptium.Temurin.21.JDK",
            "java.exe", new[] { "-version" }, ct));
        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "rust", "Rust", "Rustlang.Rustup",
            "cargo.exe", new[] { "--version" }, ct));
        candidates.AddRange(await CreateDevelopmentCandidatesAsync(
            "dotnet", ".NET SDK", "Microsoft.DotNet.SDK.8",
            "dotnet.exe", new[] { "--version" }, ct));
        return candidates;
    }

    private static IReadOnlyDictionary<string, List<string>> EmptyDiscoveredRoots()
        => new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["wechat"] = new(),
            ["qq"] = new(),
            ["feishu"] = new()
        };

    private static void AddDiscoveredRoots(
        MigrationCandidate candidate,
        IReadOnlyList<string>? discovered)
    {
        if (discovered is null || discovered.Count == 0) return;
        var roots = candidate.DataRoots.ToList();
        foreach (var path in discovered)
        {
            if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(path);
            }
        }
        candidate.DataRoots = roots;
    }

    public Task<string> ExportAsync(
        IReadOnlyList<MigrationCandidate> candidates,
        IReadOnlyDictionary<string, string> manualInstallerDirectories,
        string outputPath,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(
            () => ExportCoreAsync(
                candidates, manualInstallerDirectories, outputPath, progress, ct),
            ct);

    private async Task<string> ExportCoreAsync(
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
                    EnvironmentVariables = new Dictionary<string, string>(candidate.EnvironmentVariables),
                    MachinePathEntries = candidate.MachinePathEntries.ToList(),
                    UserPathEntries = candidate.UserPathEntries.ToList(),
                    MachineEnvironmentVariables =
                        new Dictionary<string, string>(candidate.MachineEnvironmentVariables),
                    UserEnvironmentVariables =
                        new Dictionary<string, string>(candidate.UserEnvironmentVariables)
                };

                item.Installers = await PrepareInstallersAsync(
                    candidate, stage, manualInstallerDirectories, checksums, ct)
                    .ConfigureAwait(false);
                if (item.Installers.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{candidate.Name} 没有可用的原厂安装器。" +
                        "请选择本地安装包后重试；开发环境不再使用目录复制方式部署。");
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

    public Task<MigrationRestoreResult> RestoreAsync(
        string packagePath,
        IReadOnlyDictionary<string, string>? relocationRoots = null,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(
            () => RestoreCoreAsync(packagePath, relocationRoots, progress, ct),
            ct);

    private async Task<MigrationRestoreResult> RestoreCoreAsync(
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
                var itemPercent = 10 + processed * 85d / Math.Max(1, manifest.Items.Count);
                progress?.Report(new MigrationProgress
                {
                    Message = $"正在恢复 {item.Name}…",
                    Percent = itemPercent
                });

                if (item.Kind == MigrationItemKind.Software)
                {
                    if (item.Installers.Count > 0)
                    {
                        await InstallOfflineAsync(
                            item, stage, result.Warnings, progress, itemPercent, ct)
                            .ConfigureAwait(false);
                        result.InstalledApplications++;
                    }
                    else
                    {
                        result.Warnings.Add($"{item.Name} 没有安装介质，仅恢复了用户数据");
                    }
                }
                else
                {
                    await RestoreDevelopmentToolAsync(
                        item, stage, result.Warnings, progress, itemPercent, ct)
                        .ConfigureAwait(false);
                    result.RestoredEnvironments++;
                }

                var restoredDataTargets = new List<(string Original, string Actual)>();
                foreach (var data in item.DataRoots)
                {
                    if (item.Id == "feishu")
                    {
                        await StopFeishuProcessesAsync(
                                result.Warnings, progress, itemPercent, ct)
                            .ConfigureAwait(false);
                    }
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
                        var moved = false;
                        for (var attempt = 0; attempt < 10 && !moved; attempt++)
                        {
                            try
                            {
                                Directory.Move(target, backup);
                                moved = true;
                            }
                            catch (IOException) when (attempt < 9)
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                if (item.Id == "feishu" && attempt == 1)
                                {
                                    await StopFeishuProcessesAsync(
                                            result.Warnings, progress, itemPercent, ct)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (IOException ex)
                            {
                                throw new IOException(
                                    $"无法替换 {item.Name} 数据目录：{target}。" +
                                    "请确认飞书及其后台进程已完全退出后重试。",
                                    ex);
                            }
                        }
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
        => Task.Run(() => InspectCore(packagePath), ct);

    private MigrationPackagePreview InspectCore(string packagePath)
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

        return new MigrationPackagePreview
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
        };
    }

    private static MigrationCandidate CreateSocialCandidate(
        string id,
        string name,
        string wingetId,
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyList<string> dataCandidates,
        IReadOnlyList<string> nameHints,
        IReadOnlyList<string>? configuredRoots = null,
        IReadOnlyList<string>? installerNameHints = null,
        string installDirectory = "")
    {
        var installed = programs.FirstOrDefault(program =>
            nameHints.Any(hint => program.DisplayName.Contains(hint, StringComparison.OrdinalIgnoreCase))
            && !program.DisplayName.Contains("开发者工具", StringComparison.OrdinalIgnoreCase));
        var roots = dataCandidates
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var configured in configuredRoots ?? Array.Empty<string>())
        {
            if (!roots.Contains(configured, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(configured);
            }
        }
        roots = CollapseNestedRoots(roots);
        var localInstallers = FindLocalInstallerFiles(
            installerNameHints ?? Array.Empty<string>(),
            installed?.Version ?? string.Empty,
            installDirectory,
            configuredRoots ?? Array.Empty<string>());

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
            DataRoots = roots,
            LocalInstallerPaths = localInstallers
        };
    }

    private static List<string> CollapseNestedRoots(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path.Length))
        {
            if (result.Any(existing =>
                    root.StartsWith(
                        existing.TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            result.Add(root.TrimEnd(Path.DirectorySeparatorChar));
        }
        return result;
    }

    private static IReadOnlyList<string> FindLocalInstallerFiles(
        IReadOnlyList<string> nameHints,
        string version,
        string installDirectory,
        IReadOnlyList<string> configuredRoots)
    {
        if (nameHints.Count == 0) return Array.Empty<string>();

        var searchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUserDownloadDirectories(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            searchDirectories);
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            AddUserDownloadDirectories(
                InferUserProfileRoot(installDirectory), searchDirectories);
        }
        foreach (var configuredRoot in configuredRoots)
        {
            AddUserDownloadDirectories(
                InferUserProfileRoot(configuredRoot), searchDirectories);
        }

        var candidates = new List<FileInfo>();
        foreach (var directory in searchDirectories)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsInstallerFile(path)) continue;
                    var info = new FileInfo(path);
                    if (info.Length < 20L * 1024 * 1024) continue;
                    var fileName = info.Name;
                    if (fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!nameHints.Any(hint =>
                            fileName.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    candidates.Add(info);
                }
            }
            catch
            {
                // Download folders are optional and may be unavailable.
            }
        }

        var preferred = candidates
            .OrderByDescending(file => !string.IsNullOrWhiteSpace(version)
                                       && file.Name.Contains(version, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Length)
            .FirstOrDefault();
        return preferred is null
            ? Array.Empty<string>()
            : new[] { preferred.FullName };
    }

    private static void AddUserDownloadDirectories(
        string? userProfile,
        HashSet<string> directories)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return;
        foreach (var relative in new[] { "Downloads", "Desktop", @"OneDrive\Desktop", @"OneDrive\Downloads" })
        {
            var path = Path.Combine(userProfile, relative);
            if (Directory.Exists(path)) directories.Add(Path.GetFullPath(path));
        }
    }

    private static string InferUserProfileRoot(string path)
    {
        try
        {
            var current = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
            for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                var name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar));
                if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(current) ?? string.Empty;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // Fall back to the current user's standard folders.
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private sealed record DevelopmentInstallation(string Root, string ExecutablePath);

    private static async Task<IReadOnlyList<MigrationCandidate>> CreateDevelopmentCandidatesAsync(
        string toolId,
        string name,
        string wingetId,
        string executable,
        IReadOnlyList<string> versionArgs,
        CancellationToken ct)
    {
        var installations = await FindDevelopmentInstallationsAsync(toolId, executable, ct)
            .ConfigureAwait(false);
        if (installations.Count == 0)
        {
            return new[]
            {
                await CreateDevelopmentCandidateAsync(
                    toolId, toolId, name, wingetId, versionArgs, ct,
                    installation: null).ConfigureAwait(false)
            };
        }

        var multiple = installations.Count > 1;
        var candidates = new List<MigrationCandidate>();
        for (var index = 0; index < installations.Count; index++)
        {
            var installation = installations[index];
            var candidateId = multiple
                ? $"{toolId}-{StableIdSuffix(installation.Root)}"
                : toolId;
            var candidate = await CreateDevelopmentCandidateAsync(
                candidateId, toolId, name, wingetId, versionArgs, ct,
                installation, includeSharedData: index == 0).ConfigureAwait(false);
            if (multiple)
            {
                var versionToken = ExtractVersionToken(candidate.Version);
                candidate.Name = versionToken.Length == 0
                    ? $"{name} · {candidateId[(candidateId.LastIndexOf('-') + 1)..]}"
                    : $"{name} · {versionToken}";
            }
            candidates.Add(candidate);
        }
        return candidates;
    }

    private static async Task<MigrationCandidate> CreateDevelopmentCandidateAsync(
        string candidateId,
        string toolId,
        string name,
        string wingetId,
        IReadOnlyList<string> versionArgs,
        CancellationToken ct,
        DevelopmentInstallation? installation,
        bool includeSharedData = true)
    {
        var executablePath = installation?.ExecutablePath ?? string.Empty;
        var root = installation?.Root ?? string.Empty;
        var version = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : await RunVersionAsync(executablePath, versionArgs, ct).ConfigureAwait(false);
        var pathScopes = root.Length == 0
            ? (Machine: new List<string>(), User: new List<string>())
            : FindPathEntries(root);
        if (root.Length > 0
            && pathScopes.Machine.Count == 0
            && pathScopes.User.Count == 0)
        {
            var executableDirectory = string.IsNullOrWhiteSpace(executablePath)
                ? root
                : Directory.GetParent(executablePath)?.FullName ?? root;
            if (IsMachineScopedToolRoot(root))
            {
                pathScopes.Machine.Add(executableDirectory);
            }
            else
            {
                pathScopes.User.Add(executableDirectory);
            }
        }

        var envScopes = CollectToolEnvironment(toolId, root);
        var dataRoots = new List<string>();
        if (toolId == "node" && includeSharedData)
        {
            var npmRoot = Environment.ExpandEnvironmentVariables("%APPDATA%\\npm");
            if (Directory.Exists(npmRoot))
            {
                dataRoots.Add(npmRoot);
                if (!pathScopes.User.Contains(npmRoot, StringComparer.OrdinalIgnoreCase))
                {
                    pathScopes.User.Add(npmRoot);
                }
            }
        }
        if (toolId == "python")
        {
            var userSite = FindPythonUserSite(version);
            if (userSite.Length > 0) dataRoots.Add(userSite);
        }
        if (toolId == "rust")
        {
            var rustupHome = FindConfiguredDirectory(
                "RUSTUP_HOME", "%USERPROFILE%\\.rustup");
            if (rustupHome.Length > 0) dataRoots.Add(rustupHome);
            if (root.Length > 0)
            {
                var rustEnvironment = IsMachineScopedToolRoot(root)
                    ? envScopes.Machine
                    : envScopes.User;
                rustEnvironment.TryAdd("CARGO_HOME", root);
                if (rustupHome.Length > 0)
                {
                    rustEnvironment.TryAdd("RUSTUP_HOME", TokenizePath(rustupHome));
                }
            }
        }

        var allPaths = pathScopes.Machine
            .Concat(pathScopes.User)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allEnvironment = new Dictionary<string, string>(
            envScopes.User, StringComparer.OrdinalIgnoreCase);
        foreach (var (variableName, value) in envScopes.Machine)
        {
            allEnvironment[variableName] = value;
        }

        return new MigrationCandidate
        {
            Id = candidateId,
            Name = name,
            Description = root.Length == 0
                ? "未检测到已安装版本"
                : $"安装目录：{root}",
            Kind = MigrationItemKind.Development,
            WingetId = ResolveDevelopmentWingetId(toolId, wingetId, root, version),
            IsInstalled = root.Length > 0,
            Version = version,
            ToolRoot = root,
            DataRoots = dataRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PathEntries = allPaths,
            EnvironmentVariables = allEnvironment,
            MachinePathEntries = pathScopes.Machine,
            UserPathEntries = pathScopes.User,
            MachineEnvironmentVariables = envScopes.Machine,
            UserEnvironmentVariables = envScopes.User
        };
    }

    private static async Task<List<DevelopmentInstallation>> FindDevelopmentInstallationsAsync(
        string toolId,
        string executable,
        CancellationToken ct)
    {
        var installations = new Dictionary<string, DevelopmentInstallation>(
            StringComparer.OrdinalIgnoreCase);

        void Add(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return;
            if (!TryGetDevelopmentRoot(toolId, executablePath, out var root)) return;
            if (!installations.ContainsKey(root))
            {
                installations[root] = new DevelopmentInstallation(root, executablePath);
            }
        }

        AddFromPathScopes(executable, Add);
        switch (toolId)
        {
            case "java":
                AddConfiguredRoot("JAVA_HOME", "bin\\java.exe", Add);
                CollectJavaRegistryInstallations(Add);
                AddJavaInstallationsFromCommonDirectories(Add);
                break;
            case "python":
                CollectPythonRegistryInstallations(Add);
                foreach (var path in await FindPythonLauncherPathsAsync(ct).ConfigureAwait(false))
                {
                    Add(path);
                }
                AddPythonInstallationsFromCommonDirectories(Add);
                break;
            case "node":
                AddConfiguredRoot("NVM_SYMLINK", "node.exe", Add);
                AddNodeManagerInstalls(Add);
                break;
            case "git":
                AddCandidateUnderDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Git"),
                    "cmd\\git.exe", Add);
                AddCandidateUnderDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "Git"),
                    "cmd\\git.exe", Add);
                break;
            case "rust":
                AddConfiguredRoot("CARGO_HOME", "bin\\cargo.exe", Add);
                AddCandidateUnderDirectory(
                    Environment.ExpandEnvironmentVariables("%USERPROFILE%\\.cargo"),
                    "bin\\cargo.exe", Add);
                break;
            case "dotnet":
                AddConfiguredRoot("DOTNET_ROOT", "dotnet.exe", Add);
                AddConfiguredRoot("DOTNET_ROOT_X64", "dotnet.exe", Add);
                AddCandidateUnderDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "dotnet"),
                    "dotnet.exe", Add);
                break;
        }

        Add(FindExecutable(executable));
        return installations.Values
            .OrderBy(item => item.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFromPathScopes(
        string executable,
        Action<string?> add)
    {
        foreach (var scope in new[]
                 {
                     EnvironmentVariableTarget.Machine,
                     EnvironmentVariableTarget.User
                 })
        {
            var path = Environment.GetEnvironmentVariable("Path", scope) ?? string.Empty;
            foreach (var entry in path.Split(
                         ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    add(Path.Combine(Environment.ExpandEnvironmentVariables(entry), executable));
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }
    }

    private static void AddConfiguredRoot(
        string variable,
        string relativeExecutable,
        Action<string?> add)
    {
        foreach (var scope in new[]
                 {
                     EnvironmentVariableTarget.Machine,
                     EnvironmentVariableTarget.User
                 })
        {
            var value = Environment.GetEnvironmentVariable(variable, scope);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var root = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
                add(Path.Combine(root, relativeExecutable));
            }
            catch
            {
                // Ignore malformed install roots.
            }
        }
    }

    private static void AddCandidateUnderDirectory(
        string directory,
        string relativeExecutable,
        Action<string?> add)
    {
        try
        {
            add(Path.Combine(directory, relativeExecutable));
        }
        catch
        {
            // Ignore malformed candidate paths.
        }
    }

    private static bool TryGetDevelopmentRoot(
        string toolId,
        string executablePath,
        out string root)
    {
        root = string.Empty;
        try
        {
            if (!File.Exists(executablePath)) return false;
            var fullExecutable = Path.GetFullPath(executablePath);
            if (fullExecutable.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            root = Directory.GetParent(fullExecutable)?.FullName ?? string.Empty;
            var directoryName = Path.GetFileName(root);
            if ((toolId is "git" && directoryName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
                || (toolId is "rust" or "java" or "python"
                    && directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            {
                root = Directory.GetParent(root)?.FullName ?? root;
            }

            return toolId switch
            {
                "java" => File.Exists(Path.Combine(root, "bin", "java.exe")),
                "python" => File.Exists(Path.Combine(root, "python.exe"))
                            || File.Exists(Path.Combine(root, "bin", "python.exe")),
                "node" => File.Exists(Path.Combine(root, "node.exe")),
                "git" => File.Exists(Path.Combine(root, "cmd", "git.exe"))
                          || File.Exists(Path.Combine(root, "bin", "git.exe")),
                "rust" => File.Exists(Path.Combine(root, "bin", "cargo.exe")),
                "dotnet" => File.Exists(Path.Combine(root, "dotnet.exe")),
                _ => Directory.Exists(root)
            };
        }
        catch
        {
            root = string.Empty;
            return false;
        }
    }

    private static void CollectJavaRegistryInstallations(Action<string?> add)
    {
        var roots = new[]
        {
            @"SOFTWARE\JavaSoft",
            @"SOFTWARE\Eclipse Adoptium",
            @"SOFTWARE\Eclipse Foundation",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Amazon\Corretto",
            @"SOFTWARE\BellSoft",
            @"SOFTWARE\Semeru"
        };
        foreach (var (hive, view) in RegistryScopes())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var rootPath in roots)
                {
                    using var root = baseKey.OpenSubKey(rootPath);
                    if (root is not null)
                    {
                        CollectRegistryExecutablePaths(root, 0, "bin\\java.exe", add);
                    }
                }
            }
            catch
            {
                // Ignore inaccessible registry views.
            }
        }
    }

    private static void CollectPythonRegistryInstallations(Action<string?> add)
    {
        foreach (var (hive, view) in RegistryScopes())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var core = baseKey.OpenSubKey(@"SOFTWARE\Python\PythonCore");
                if (core is null) continue;
                foreach (var version in core.GetSubKeyNames())
                {
                    using var installPath = core.OpenSubKey($@"{version}\InstallPath");
                    if (installPath is null) continue;
                    var path = installPath.GetValue(null) as string
                               ?? installPath.GetValue("ExecutablePath") as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    path = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
                    if (File.Exists(path))
                    {
                        add(path);
                    }
                    else
                    {
                        add(Path.Combine(path, "python.exe"));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible registry views.
            }
        }
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryScopes()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Default);
    }

    private static void CollectRegistryExecutablePaths(
        RegistryKey key,
        int depth,
        string relativeExecutable,
        Action<string?> add)
    {
        if (depth > 4) return;
        foreach (var valueName in key.GetValueNames())
        {
            try
            {
                if (key.GetValue(valueName) is not string value
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                var candidate = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
                if (Directory.Exists(candidate))
                {
                    add(Path.Combine(candidate, relativeExecutable));
                }
            }
            catch
            {
                // Ignore individual malformed registry values.
            }
        }

        foreach (var subKeyName in key.GetSubKeyNames().Take(128))
        {
            try
            {
                using var child = key.OpenSubKey(subKeyName);
                if (child is not null)
                {
                    CollectRegistryExecutablePaths(child, depth + 1, relativeExecutable, add);
                }
            }
            catch
            {
                // Ignore individual inaccessible subkeys.
            }
        }
    }

    private static void AddJavaInstallationsFromCommonDirectories(Action<string?> add)
    {
        var bases = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Eclipse Adoptium"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Amazon Corretto"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Eclipse Adoptium")
        };
        foreach (var baseDirectory in bases)
        {
            AddJavaHomesInTree(baseDirectory, depth: 2, add);
        }
    }

    private static void AddJavaHomesInTree(
        string directory,
        int depth,
        Action<string?> add)
    {
        if (depth < 0 || string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory))
            {
                add(Path.Combine(directory, "bin", "java.exe"));
                foreach (var child in Directory.GetDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    AddJavaHomesInTree(child, depth - 1, add);
                }
            }
        }
        catch
        {
            // Ignore inaccessible install directories.
        }
    }

    private static void AddPythonInstallationsFromCommonDirectories(Action<string?> add)
    {
        var bases = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        foreach (var baseDirectory in bases)
        {
            if (!Directory.Exists(baseDirectory)) continue;
            try
            {
                foreach (var child in Directory.GetDirectories(baseDirectory))
                {
                    var name = Path.GetFileName(child);
                    if (baseDirectory.EndsWith("Program Files", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    add(Path.Combine(child, "python.exe"));
                    add(Path.Combine(child, "bin", "python.exe"));
                }
            }
            catch
            {
                // Ignore inaccessible install directories.
            }
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (var child in Directory.GetDirectories(drive.RootDirectory.FullName))
                {
                    if (Path.GetFileName(child).StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                    {
                        add(Path.Combine(child, "python.exe"));
                    }
                }
            }
            catch
            {
                // Ignore unavailable drives.
            }
        }
    }

    private static async Task<IReadOnlyList<string>> FindPythonLauncherPathsAsync(
        CancellationToken ct)
    {
        var launcher = FindExecutable("py.exe");
        if (launcher.Length == 0) return Array.Empty<string>();
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = launcher,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-0p");
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false) +
                         Environment.NewLine +
                         await stderr.ConfigureAwait(false);
            var results = new List<string>();
            foreach (var line in output.Split(
                         new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var driveIndex = -1;
                for (var index = 1; index < line.Length; index++)
                {
                    if (line[index] == ':' && char.IsLetter(line[index - 1])
                        && (index + 1 >= line.Length
                            || line[index + 1] == '\\' || line[index + 1] == '/'))
                    {
                        driveIndex = index - 1;
                        break;
                    }
                }
                if (driveIndex < 0) continue;
                var path = line[driveIndex..].Trim().Trim('"');
                if (File.Exists(path)) results.Add(path);
            }
            return results;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddNodeManagerInstalls(Action<string?> add)
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.Machine),
            Environment.ExpandEnvironmentVariables("%APPDATA%\\nvm"),
            Environment.ExpandEnvironmentVariables("%APPDATA%\\fnm\\node-versions"),
            Environment.ExpandEnvironmentVariables(
                "%LOCALAPPDATA%\\Volta\\tools\\image\\node")
        };
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                add(Path.Combine(root, "node.exe"));
                foreach (var child in Directory.GetDirectories(root))
                {
                    add(Path.Combine(child, "node.exe"));
                    foreach (var nested in Directory.GetDirectories(child))
                    {
                        add(Path.Combine(nested, "node.exe"));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible version-manager roots.
            }
        }
    }

    private static string FindPythonUserSite(string version)
    {
        var token = ExtractVersionToken(version);
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            return string.Empty;
        }
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Python", $"Python{major}{minor}");
        return Directory.Exists(root) ? root : string.Empty;
    }

    private static string StableIdSuffix(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string BaseToolId(string candidateId)
    {
        foreach (var toolId in new[] { "python", "dotnet", "java", "rust", "node", "git" })
        {
            if (candidateId.Equals(toolId, StringComparison.OrdinalIgnoreCase)
                || candidateId.StartsWith(toolId + "-", StringComparison.OrdinalIgnoreCase))
            {
                return toolId;
            }
        }
        return candidateId;
    }

    private static string ResolveDevelopmentWingetId(
        string toolId,
        string fallback,
        string root,
        string versionText)
    {
        var token = ExtractVersionToken(versionText);
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
        {
            return fallback;
        }

        return toolId switch
        {
            "python" when parts.Length >= 2
                => $"Python.Python.{major}.{parts[1]}",
            "dotnet" => $"Microsoft.DotNet.SDK.{major}",
            "node" => $"OpenJS.NodeJS.{major}",
            "git" => "Git.Git",
            "rust" => "Rustlang.Rustup",
            "java" when root.Contains("Temurin", StringComparison.OrdinalIgnoreCase)
                         || root.Contains("Adoptium", StringComparison.OrdinalIgnoreCase)
                => $"EclipseAdoptium.Temurin.{major}.JDK",
            "java" when root.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                => $"Microsoft.OpenJDK.{major}",
            "java" when root.Contains("Corretto", StringComparison.OrdinalIgnoreCase)
                         || root.Contains("Amazon", StringComparison.OrdinalIgnoreCase)
                => $"Amazon.Corretto.{major}.JDK",
            "java" when root.Contains("Zulu", StringComparison.OrdinalIgnoreCase)
                         || root.Contains("Azul", StringComparison.OrdinalIgnoreCase)
                => $"Azul.Zulu.{major}.JDK",
            "java" when root.Contains("Semeru", StringComparison.OrdinalIgnoreCase)
                         || root.Contains("IBM", StringComparison.OrdinalIgnoreCase)
                => $"IBM.Semeru.{major}.JDK",
            "java" when root.Contains(@"\Java\", StringComparison.OrdinalIgnoreCase)
                => $"Oracle.JDK.{major}",
            _ => fallback
        };
    }

    private static string ExtractVersionToken(string value)
    {
        for (var start = 0; start < value.Length; start++)
        {
            if (!char.IsAsciiDigit(value[start])) continue;
            var end = start;
            while (end < value.Length
                   && (char.IsAsciiDigit(value[end]) || value[end] == '.'))
            {
                end++;
            }
            var token = value[start..end].Trim('.');
            if (token.Contains('.')) return token;
        }
        return string.Empty;
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

    private static async Task<List<MigrationInstaller>> PrepareInstallersAsync(
        MigrationCandidate candidate,
        string stage,
        IReadOnlyDictionary<string, string> manualInstallerDirectories,
        Dictionary<string, string> checksums,
        CancellationToken ct)
    {
        var installerTarget = Path.Combine(
            stage,
            candidate.Kind == MigrationItemKind.Software ? "software" : "development",
            candidate.Id,
            "installers");
        Directory.CreateDirectory(installerTarget);
        if (manualInstallerDirectories.TryGetValue(candidate.Id, out var manualDir)
            && Directory.Exists(manualDir))
        {
            await CopyFilesAsync(manualDir, installerTarget, stage, checksums, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var copiedDetectedInstaller = await CopyLocalInstallerFilesAsync(
                    candidate.LocalInstallerPaths, installerTarget, stage, checksums, ct)
                .ConfigureAwait(false);
            if (!copiedDetectedInstaller && !string.IsNullOrWhiteSpace(candidate.WingetId))
            {
                try
                {
                    await DownloadInstallerAsync(candidate, installerTarget, ct).ConfigureAwait(false);
                    await AddChecksumsAsync(installerTarget, stage, checksums, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (BaseToolId(candidate.Id) == "node")
                    {
                        try
                        {
                            if (Directory.Exists(installerTarget))
                            {
                                Directory.Delete(installerTarget, recursive: true);
                            }
                            Directory.CreateDirectory(installerTarget);
                            await DownloadNodeOfficialInstallerAsync(
                                candidate, installerTarget, ct).ConfigureAwait(false);
                            await AddChecksumsAsync(installerTarget, stage, checksums, ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception fallbackEx)
                        {
                            throw new InvalidOperationException(
                                $"winget 无法获取 Node.js {ExtractVersionToken(candidate.Version)}，" +
                                $"官方安装包回退也失败：{fallbackEx.Message}", fallbackEx);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"无法获取 {candidate.Name} 的原厂离线安装器；" +
                            $"请在源机器选择对应版本的本地安装包后重试。{ex.Message}", ex);
                    }
                }
            }
        }

        return Directory.GetFiles(installerTarget, "*", SearchOption.AllDirectories)
            .Where(IsInstallerFile)
            .Select(path => new MigrationInstaller
            {
                ArchivePath = ToArchivePath(stage, path),
                FileName = Path.GetFileName(path),
                SizeBytes = new FileInfo(path).Length,
                SilentArguments = SilentArgumentsFor(candidate.WingetId, path)
            })
            .ToList();
    }

    private static async Task<bool> CopyLocalInstallerFilesAsync(
        IReadOnlyList<string> installerPaths,
        string destination,
        string stage,
        Dictionary<string, string> checksums,
        CancellationToken ct)
    {
        var copied = false;
        foreach (var source in installerPaths
                     .Where(File.Exists)
                     .Where(IsInstallerFile)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(source);
            var target = Path.Combine(destination, fileName);
            Directory.CreateDirectory(destination);
            await using (var input = new FileStream(
                             source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             target, FileMode.Create, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            checksums[ToArchivePath(stage, target)] = await HashAsync(target, ct)
                .ConfigureAwait(false);
            copied = true;
        }
        return copied;
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
            "--architecture", "x64",
            "--download-directory", destination,
            "--accept-package-agreements", "--accept-source-agreements",
            "--disable-interactivity"
        };
        if (candidate.Kind == MigrationItemKind.Software)
        {
            args.Add("--scope");
            args.Add("machine");
        }
        var toolId = BaseToolId(candidate.Id);
        var version = ExtractVersionToken(candidate.Version);
        if (candidate.Kind == MigrationItemKind.Development
            && toolId != "rust"
            && version.Length > 0)
        {
            args.Add("--version");
            args.Add(version);
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "winget.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = FreshStart.Core.Diagnostics.ConsoleEncoding.OemCodePage,
                StandardErrorEncoding = FreshStart.Core.Diagnostics.ConsoleEncoding.OemCodePage
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        var command = NativeCommandTrace.Format("winget.exe", args);
        NativeCommandTrace.Started(command, Environment.CurrentDirectory);
        var stopwatch = Stopwatch.StartNew();
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
            NativeCommandTrace.Completed(
                command, -1, "winget download 已取消或超时", stopwatch.Elapsed,
                Environment.CurrentDirectory);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("下载安装器超时");
        }
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
                     (await stderr.ConfigureAwait(false));
        NativeCommandTrace.Completed(
            command, process.ExitCode, output, stopwatch.Elapsed, Environment.CurrentDirectory);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"winget download 失败（exit {process.ExitCode}）：{LastLine(output)}");
        }
    }

    private static async Task DownloadNodeOfficialInstallerAsync(
        MigrationCandidate candidate,
        string destination,
        CancellationToken ct)
    {
        var version = ExtractVersionToken(candidate.Version);
        if (version.Length == 0
            || version.Split('.').Any(part => !int.TryParse(part, out _)))
        {
            throw new InvalidDataException($"无法识别 Node.js 版本：{candidate.Version}");
        }

        var fileName = $"node-v{version}-x64.msi";
        var baseUrl = $"https://nodejs.org/dist/v{version}/";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreshStart/0.14");

        var sumsText = await http.GetStringAsync(baseUrl + "SHASUMS256.txt", ct)
            .ConfigureAwait(false);
        var expected = sumsText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2
                            && parts[1].Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidDataException(
                $"Node.js {version} 的官方 SHASUMS256.txt 中没有 {fileName}");
        }

        var target = Path.Combine(destination, fileName);
        var downloadUrl = baseUrl + fileName;
        await using (var source = await http.GetStreamAsync(downloadUrl, ct).ConfigureAwait(false))
        await using (var output = new FileStream(
                         target, FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        var actual = await HashAsync(target, ct).ConfigureAwait(false);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(target); } catch { /* best effort */ }
            throw new InvalidDataException(
                $"Node.js 官方安装包 SHA256 校验失败，期望 {expected}，实际 {actual}");
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
        IProgress<MigrationProgress>? progress,
        double progressPercent,
        CancellationToken ct)
    {
        foreach (var installer in item.Installers.OrderByDescending(file => file.SizeBytes))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MigrationProgress
            {
                Message = $"正在运行 {item.Name} 的原厂安装器：{installer.FileName}…",
                Percent = progressPercent
            });
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
                var silentArguments = item.Id == "feishu"
                    ? "--command=quiet_install"
                    : installer.SilentArguments;
                foreach (var argument in SplitArguments(silentArguments))
                {
                    start.ArgumentList.Add(argument);
                }
            }

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            var command = NativeCommandTrace.Format(start.FileName, start.ArgumentList);
            NativeCommandTrace.Started(command, Environment.CurrentDirectory);
            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(start);
            if (process is null)
            {
                warnings.Add($"{item.Name} 无法启动安装器 {installer.FileName}");
                NativeCommandTrace.Completed(
                    command, -1, "无法启动安装器", stopwatch.Elapsed, Environment.CurrentDirectory);
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
                else
                {
                    progress?.Report(new MigrationProgress
                    {
                        Message = $"{installer.FileName} 已完成，正在校验安装结果…",
                        Percent = progressPercent
                    });
                }
                NativeCommandTrace.Completed(
                    command, process.ExitCode, string.Empty, stopwatch.Elapsed,
                    Environment.CurrentDirectory);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                NativeCommandTrace.Completed(
                    command, -1, "安装器执行已取消或超时", stopwatch.Elapsed,
                    Environment.CurrentDirectory);
                throw;
            }
        }
    }

    private static async Task StopFeishuProcessesAsync(
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        double progressPercent,
        CancellationToken ct)
    {
        var installDirectory = FindFeishuPaths().InstallDirectory;
        var processes = Process.GetProcesses()
            .Where(process => IsFeishuProcess(process, installDirectory))
            .ToList();
        if (processes.Count == 0) return;

        progress?.Report(new MigrationProgress
        {
            Message = "安装器已启动飞书，正在关闭后台进程并恢复聊天数据…",
            Percent = progressPercent
        });
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch
            {
                // Background child processes often have no main window.
            }
        }

        using (var graceful = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            graceful.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(processes.Select(process =>
                        process.HasExited
                            ? Task.CompletedTask
                            : process.WaitForExitAsync(graceful.Token)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Force-close remaining Feishu background processes below.
            }
        }

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // A process may exit between Refresh and Kill.
            }
        }

        await Task.Delay(1000, ct).ConfigureAwait(false);
        var remaining = processes
            .Where(process =>
            {
                try
                {
                    process.Refresh();
                    return !process.HasExited;
                }
                catch
                {
                    return false;
                }
            })
            .Select(process => process.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var process in processes) process.Dispose();

        if (remaining.Count > 0)
        {
            throw new IOException(
                $"无法关闭飞书后台进程：{string.Join("、", remaining)}。请手动退出后重试。");
        }
        warnings.Add("安装器自动启动了飞书，FreshStart 已关闭其后台进程后继续恢复数据。");
    }

    private static bool IsFeishuProcess(Process process, string installDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                var executable = process.MainModule?.FileName ?? string.Empty;
                if (executable.Length > 0)
                {
                    return executable.StartsWith(
                               installDirectory.TrimEnd(Path.DirectorySeparatorChar)
                               + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase)
                           || executable.Contains(
                               @"\Feishu\", StringComparison.OrdinalIgnoreCase);
                }
            }

            return process.ProcessName.Contains("feishu", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return process.ProcessName.Contains("feishu", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task RestoreDevelopmentToolAsync(
        MigrationPackageItem item,
        string stage,
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        double progressPercent,
        CancellationToken ct)
    {
        if (item.ToolRoot.Length == 0) return;
        RemoveLegacyManagedEnvironment(item, warnings);
        if (item.Installers.Count == 0)
        {
            throw new InvalidOperationException(
                $"{item.Name} 的克隆包不包含原厂安装器，无法进行原生部署");
        }
        await InstallOfflineAsync(
            item, stage, warnings, progress, progressPercent, ct).ConfigureAwait(false);
        progress?.Report(new MigrationProgress
        {
            Message = $"正在确认 {item.Name} 的原生安装目录…",
            Percent = progressPercent
        });
        var targetRoot = await ResolveInstalledDevelopmentRootAsync(item, ct)
            .ConfigureAwait(false);
        warnings.Add($"{item.Name} 已通过原厂安装器部署到：{targetRoot}");

        var hasScopedMetadata = item.MachinePathEntries is not null
                                || item.UserPathEntries is not null
                                || item.MachineEnvironmentVariables is not null
                                || item.UserEnvironmentVariables is not null;
        var machinePaths = item.MachinePathEntries?.ToList() ?? new List<string>();
        var userPaths = item.UserPathEntries?.ToList() ?? new List<string>();
        var machineEnvironment = item.MachineEnvironmentVariables is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(
                item.MachineEnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        var userEnvironment = item.UserEnvironmentVariables is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(
                item.UserEnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        if (!hasScopedMetadata)
        {
            var legacyMachineScope = IsMachineScopedToolRoot(item.ToolRoot);
            if (legacyMachineScope)
            {
                machinePaths = item.PathEntries.ToList();
                machineEnvironment = new Dictionary<string, string>(
                    item.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                userPaths = item.PathEntries.ToList();
                userEnvironment = new Dictionary<string, string>(
                    item.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
            }
            if (machinePaths.Count > 0 || userPaths.Count > 0
                || machineEnvironment.Count > 0 || userEnvironment.Count > 0)
            {
                warnings.Add(
                    $"{item.Name} 的旧克隆包未记录环境变量作用域，" +
                    $"已按原安装位置推断为{(legacyMachineScope ? "系统级" : "用户级")}");
            }
        }

        var managedVariables = machineEnvironment.Keys
            .Concat(userEnvironment.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RemoveStaleManagedEnvironment(
            item, managedVariables, EnvironmentVariableTarget.Machine, warnings);
        RemoveStaleManagedEnvironment(
            item, managedVariables, EnvironmentVariableTarget.User, warnings);
        RestoreEnvironmentVariables(
            item.Name, item.ToolRoot, targetRoot, machineEnvironment,
            EnvironmentVariableTarget.Machine, warnings);
        RestoreEnvironmentVariables(
            item.Name, item.ToolRoot, targetRoot, userEnvironment,
            EnvironmentVariableTarget.User, warnings);
        RestorePathEntries(
            item.Name, item.ToolRoot, targetRoot, machinePaths,
            EnvironmentVariableTarget.Machine, warnings);
        RestorePathEntries(
            item.Name, item.ToolRoot, targetRoot, userPaths,
            EnvironmentVariableTarget.User, warnings);
    }

    private static async Task<string> ResolveInstalledDevelopmentRootAsync(
        MigrationPackageItem item,
        CancellationToken ct)
    {
        var toolId = BaseToolId(item.Id);
        var executable = toolId switch
        {
            "java" => "java.exe",
            "python" => "python.exe",
            "node" => "node.exe",
            "git" => "git.exe",
            "rust" => "cargo.exe",
            "dotnet" => "dotnet.exe",
            _ => string.Empty
        };
        var expectedVersion = ExtractVersionToken(item.Version);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var installations = await FindDevelopmentInstallationsAsync(
                toolId, executable, ct).ConfigureAwait(false);
            foreach (var installation in installations)
            {
                var version = await RunVersionAsync(
                    installation.ExecutablePath,
                    toolId == "java" ? new[] { "-version" } : new[] { "--version" },
                    ct).ConfigureAwait(false);
                var actualVersion = ExtractVersionToken(version);
                if (expectedVersion.Length > 0
                    && actualVersion.Equals(
                        expectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return installation.Root;
                }
            }
            if (toolId == "rust" && installations.Count == 1)
            {
                return installations[0].Root;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            $"{item.Name} 安装器已结束，但未能确认原生安装目录或版本");
    }

    private static void RemoveLegacyManagedEnvironment(
        MigrationPackageItem item,
        List<string> warnings)
    {
        var safeId = SafeName(item.Id);
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreshStart", "Environments", safeId),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FreshStart", "Environments", safeId)
        };
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                Directory.Delete(root, recursive: true);
                warnings.Add($"{item.Name} 旧的 FreshStart 接管环境已移除：{root}");
            }
            catch (Exception ex)
            {
                warnings.Add($"{item.Name} 旧环境目录清理失败：{root}；{ex.Message}");
            }
        }

        var keyName = $"FreshStart.Environment.{safeId}";
        var scopes = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        };
        foreach (var (hive, view) in scopes)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                baseKey.DeleteSubKeyTree(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}",
                    throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                warnings.Add($"{item.Name} 旧控制面板接管项清理失败：{ex.Message}");
            }
        }
    }

    private static void RemoveStaleManagedEnvironment(
        MigrationPackageItem item,
        IReadOnlyList<string> variableNames,
        EnvironmentVariableTarget scope,
        List<string> warnings)
    {
        try
        {
            var environmentRoots = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FreshStart", "Environments", SafeName(item.Id)),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "FreshStart", "Environments", SafeName(item.Id))
            };

            var path = Environment.GetEnvironmentVariable("Path", scope) ?? string.Empty;
            var entries = path
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !IsManagedEnvironmentPath(entry, environmentRoots))
                .ToList();
            var cleanedPath = string.Join(';', entries);
            if (!string.Equals(path, cleanedPath, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("Path", cleanedPath, scope);
            }

            foreach (var name in variableNames)
            {
                var value = Environment.GetEnvironmentVariable(name, scope);
                if (!string.IsNullOrWhiteSpace(value)
                    && IsManagedEnvironmentPath(value, environmentRoots))
                {
                    Environment.SetEnvironmentVariable(name, null, scope);
                }
            }
        }
        catch (Exception ex)
        {
            var scopeName = scope == EnvironmentVariableTarget.Machine ? "系统" : "用户";
            warnings.Add($"{item.Name} 的旧{scopeName}环境记录清理失败：{ex.Message}");
        }
    }

    private static bool IsManagedEnvironmentPath(
        string value,
        IReadOnlyList<string> environmentRoots)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value)
            .Trim()
            .Trim('"')
            .TrimEnd(Path.DirectorySeparatorChar);
        return environmentRoots.Any(root =>
            string.Equals(
                expanded, root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || expanded.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMachineScopedToolRoot(string toolRoot)
    {
        try
        {
            var root = Path.GetFullPath(toolRoot).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return !root.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void RestoreEnvironmentVariables(
        string itemName,
        string sourceRoot,
        string targetRoot,
        IReadOnlyDictionary<string, string> variables,
        EnvironmentVariableTarget scope,
        List<string> warnings)
    {
        var scopeName = scope == EnvironmentVariableTarget.Machine ? "系统" : "用户";
        foreach (var (name, value) in variables)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            var restored = expanded.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase)
                ? ReplaceRoot(expanded, sourceRoot, targetRoot)
                : value;
            if (restored.Length == 0) continue;
            try
            {
                Environment.SetEnvironmentVariable(name, restored, scope);
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"{itemName} 的{scopeName}环境变量 {name} 恢复失败：{ex.Message}");
            }
        }
    }

    private static void RestorePathEntries(
        string itemName,
        string sourceRoot,
        string targetRoot,
        IReadOnlyList<string> pathEntries,
        EnvironmentVariableTarget scope,
        List<string> warnings)
    {
        if (pathEntries.Count == 0) return;
        var scopeName = scope == EnvironmentVariableTarget.Machine ? "系统" : "用户";
        try
        {
            var current = Environment.GetEnvironmentVariable("Path", scope) ?? string.Empty;
            var entries = current
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var entry in pathEntries)
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry);
                var restored = ReplaceRoot(expanded, sourceRoot, targetRoot);
                if (restored.Length > 0
                    && !entries.Contains(restored, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(restored);
                }
            }
            Environment.SetEnvironmentVariable("Path", string.Join(';', entries), scope);
        }
        catch (Exception ex)
        {
            warnings.Add($"{itemName} 的{scopeName} PATH 恢复失败：{ex.Message}");
        }
    }

    private static string FindConfiguredDirectory(string variable, string fallback)
    {
        foreach (var scope in new[]
                 {
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine
                 })
        {
            var value = Environment.GetEnvironmentVariable(variable, scope);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var candidate = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(value).Trim().Trim('"'));
                if (Directory.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed environment paths.
            }
        }

        try
        {
            var candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(fallback));
            return Directory.Exists(candidate) ? candidate : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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

    private static (List<string> Machine, List<string> User) FindPathEntries(string root)
    {
        var machine = FindPathEntries(root, EnvironmentVariableTarget.Machine);
        var user = FindPathEntries(root, EnvironmentVariableTarget.User);
        return (machine, user);
    }

    private static List<string> FindPathEntries(
        string root,
        EnvironmentVariableTarget scope)
    {
        var results = new List<string>();
        var path = Environment.GetEnvironmentVariable("Path", scope) ?? string.Empty;
        foreach (var entry in path.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\');
            if (expanded.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && !results.Contains(expanded, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(expanded);
            }
        }
        return results;
    }

    private static (
        Dictionary<string, string> Machine,
        Dictionary<string, string> User) CollectToolEnvironment(string id, string root)
    {
        var allow = id switch
        {
            "git" => new[] { "GIT_EXEC_PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM" },
            "python" => new[] { "PYTHONHOME", "PYTHONPATH" },
            "node" => new[] { "NODE_PATH", "NPM_CONFIG_PREFIX", "NPM_CONFIG_CACHE" },
            "java" => new[] { "JAVA_HOME", "JDK_HOME", "CLASSPATH" },
            "rust" => new[] { "CARGO_HOME", "RUSTUP_HOME", "RUSTUP_TOOLCHAIN" },
            "dotnet" => new[]
            {
                "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86",
                "DOTNET_HOST_PATH", "NUGET_PACKAGES", "DOTNET_CLI_HOME"
            },
            _ => Array.Empty<string>()
        };
        var allowOutsideToolRoot = id == "rust";
        return (
            CollectToolEnvironment(
                allow, root, EnvironmentVariableTarget.Machine, allowOutsideToolRoot),
            CollectToolEnvironment(
                allow, root, EnvironmentVariableTarget.User, allowOutsideToolRoot));
    }

    private static Dictionary<string, string> CollectToolEnvironment(
        IReadOnlyList<string> allow,
        string root,
        EnvironmentVariableTarget scope,
        bool allowOutsideToolRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allow)
        {
            var value = Environment.GetEnvironmentVariable(name, scope);
            if (!string.IsNullOrWhiteSpace(value)
                && (allowOutsideToolRoot
                    || root.Length == 0
                    || value.Contains(root, StringComparison.OrdinalIgnoreCase))
                && !ContainsSecretMarker(name))
            {
                var expanded = Environment.ExpandEnvironmentVariables(value);
                result[name] = root.Length > 0
                                && expanded.Contains(root, StringComparison.OrdinalIgnoreCase)
                    ? expanded
                    : TokenizePath(expanded);
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
            "rustlang.rustup" => "-y --default-toolchain stable --profile default",
            "microsoft.dotnet.sdk.8" => "/install /quiet /norestart",
            "tencent.wechat" or "tencent.wechat.universal" => "/S",
            "tencent.qq.nt" => "/S",
            "bytedance.feishu" => "--command=quiet_install",
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

    private static IReadOnlyDictionary<string, List<string>> FindKnownDataRoots(
        CancellationToken ct)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["wechat"] = new(),
            ["qq"] = new(),
            ["feishu"] = new()
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    queue.Enqueue((drive.RootDirectory.FullName, 0));
                }
            }
            catch { /* ignore unavailable drives */ }
        }

        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Windows", "ProgramData",
            "node_modules", ".git", "AppData", "nt_qq", "nt_db", "nt_data"
        };
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path) || depth > 5) continue;

            string[] children;
            try
            {
                // Materialize here so access errors during enumeration are handled here,
                // rather than escaping later from the foreach on the UI thread.
                children = Directory.GetDirectories(path);
            }
            catch
            {
                continue;
            }

            if (IsQQDataRoot(path) || children.Any(IsQQAccountDirectory))
            {
                AddRoot(found["qq"], path);
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                string name;
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    name = Path.GetFileName(child);
                }
                catch
                {
                    continue;
                }

                if (name.Equals("Tencent Files", StringComparison.OrdinalIgnoreCase))
                {
                    AddRoot(found["qq"], child);
                    continue;
                }
                if (IsQQDataRoot(child))
                {
                    AddRoot(found["qq"], child);
                    continue;
                }
                if (name.Equals("xwechat_files", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Path.GetDirectoryName(child);
                    if (!string.IsNullOrWhiteSpace(parent)) AddRoot(found["wechat"], parent);
                    continue;
                }
                if (depth < 5 && !skipped.Contains(name))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return found;
    }

    private static bool IsQQDataRoot(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "nt_qq", "global"));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsQQAccountDirectory(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return name.Length > 0
                   && name.All(char.IsAsciiDigit)
                   && Directory.Exists(Path.Combine(path, "nt_qq"));
        }
        catch
        {
            return false;
        }
    }

    private static void AddRoot(List<string> roots, string path)
    {
        var full = Path.GetFullPath(path);
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(full);
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

    private static FeishuPaths FindFeishuPaths()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Feishu");
            if (key is null) return new FeishuPaths(string.Empty, string.Empty);

            var installDirectory = NormalizeExistingDirectory(key.GetValue("InstallDir") as string);
            var dataDirectory = NormalizeExistingDirectory(key.GetValue("DataDir") as string);
            return new FeishuPaths(installDirectory, dataDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取飞书安装与数据目录失败: {ex.Message}");
            return new FeishuPaths(string.Empty, string.Empty);
        }
    }

    private static string NormalizeExistingDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Directory.Exists(expanded)
                ? Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record FeishuPaths(string InstallDirectory, string DataDirectory);

    private static IReadOnlyList<string> FindQQConfiguredRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var authRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QQ", "auth");
            if (!Directory.Exists(authRoot)) return Array.Empty<string>();

            foreach (var file in Directory.GetFiles(authRoot))
            {
                try
                {
                    // QQ may hold this file open while running. Only path-bearing
                    // JSON values are inspected; account/profile fields are ignored.
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    CollectQQConfiguredRoots(document.RootElement, roots, 0);
                }
                catch
                {
                    // Older or encrypted QQ builds are handled by the disk fallback.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 QQ 数据目录配置失败: {ex.Message}");
        }

        return roots.ToList();
    }

    private static void CollectQQConfiguredRoots(
        JsonElement element,
        HashSet<string> roots,
        int depth)
    {
        if (depth > 12) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CollectQQConfiguredRoots(property.Value, roots, depth + 1);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectQQConfiguredRoots(item, roots, depth + 1);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.String) return;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("appimg://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var path = Uri.UnescapeDataString(value["appimg://".Length..])
                .Replace('/', Path.DirectorySeparatorChar);
            var current = Path.IsPathRooted(path) ? path : Path.GetDirectoryName(path);
            for (var level = 0; level < 12 && !string.IsNullOrWhiteSpace(current); level++)
            {
                var name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar));
                if (name.Equals("Tencent Files", StringComparison.OrdinalIgnoreCase)
                    || IsQQDataRoot(current))
                {
                    if (!IsProtectedTarget(current) && Directory.Exists(current))
                    {
                        roots.Add(Path.GetFullPath(current));
                    }
                    return;
                }
                if (name.Length > 0
                    && name.All(char.IsAsciiDigit)
                    && Directory.Exists(Path.Combine(current, "nt_qq")))
                {
                    var dataRoot = Path.GetDirectoryName(current);
                    if (!string.IsNullOrWhiteSpace(dataRoot)
                        && !IsProtectedTarget(dataRoot)
                        && Directory.Exists(dataRoot))
                    {
                        roots.Add(Path.GetFullPath(dataRoot));
                    }
                    return;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // Ignore malformed or stale path values.
        }
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
