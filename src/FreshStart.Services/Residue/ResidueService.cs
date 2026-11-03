using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Services.Registry;
using FreshStart.Services.Uninstall;
using Microsoft.Win32;

namespace FreshStart.Services.Residue;

public sealed class ResidueItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string Risk { get; init; } = "Safe";
    public bool DefaultSelected { get; init; }
    public bool Reversible { get; init; } = true;
    public long SizeBytes { get; init; }

    public string SizeText => SizeBytes <= 0 ? "—" : Format(SizeBytes);
    public string RiskText => Risk switch { "Dangerous" => "危险", "Caution" => "谨慎", _ => "安全" };
    public bool CanClean => Reversible;

    private static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}

public sealed class ResidueScanResult
{
    public InstalledProgram Target { get; init; } = new();
    public List<ResidueItem> Items { get; init; } = new();
}

/// <summary>
/// 卸载残留扫描：安装目录、快捷方式、遗留卸载键、引用安装路径的服务。
/// 清理时文件移入隔离区，注册表键先快照再删除，并生成 HTML/JSON 报告。
/// </summary>
public sealed class ResidueService
{
    private readonly InstalledProgramService _programs;
    private readonly RegistryService _registry;
    private readonly FreshStart.Core.Abstractions.IWriteLock _writeLock;

    public ResidueService(
        InstalledProgramService programs,
        RegistryService registry,
        FreshStart.Core.Abstractions.IWriteLock? writeLock = null)
    {
        _programs = programs;
        _registry = registry;
        _writeLock = writeLock ?? new FreshStart.Core.Threading.WriteLock();
    }

    public IReadOnlyList<InstalledProgram> GetTargets() => _programs.LoadUninstalled();

    public Task<ResidueScanResult> ScanAsync(InstalledProgram target, CancellationToken ct = default)
        => Task.Run(() => Scan(target, ct), ct);

    private ResidueScanResult Scan(InstalledProgram target, CancellationToken ct)
    {
        var items = new List<ResidueItem>();

        if (!string.IsNullOrWhiteSpace(target.InstallLocation) && Directory.Exists(target.InstallLocation))
        {
            items.Add(new ResidueItem
            {
                Kind = "目录",
                Path = target.InstallLocation,
                Evidence = "卸载后安装目录仍然存在",
                Risk = "Safe",
                DefaultSelected = true,
                SizeBytes = DirectorySize(target.InstallLocation, ct)
            });
        }

        foreach (var shortcut in FindShortcuts(target.DisplayName, ct))
        {
            items.Add(new ResidueItem
            {
                Kind = "快捷方式",
                Path = shortcut,
                Evidence = "开始菜单/桌面中存在同名快捷方式",
                Risk = "Safe",
                DefaultSelected = true
            });
        }

        foreach (var key in FindUninstallKeys(target, ct))
        {
            items.Add(new ResidueItem
            {
                Kind = "注册表",
                Path = key,
                Evidence = "卸载后仍存在 ARP 卸载项",
                Risk = "Caution",
                DefaultSelected = true
            });
        }

        foreach (var service in FindServices(target, ct))
        {
            items.Add(new ResidueItem
            {
                Kind = "服务",
                Path = service,
                Evidence = "服务 ImagePath 引用了残留安装目录",
                Risk = "Dangerous",
                DefaultSelected = false,
                Reversible = true
            });
        }

        return new ResidueScanResult { Target = target, Items = items };
    }

    public async Task<(string HtmlPath, string JsonPath)> CleanAsync(
        ResidueScanResult result, IReadOnlyList<ResidueItem> selected, CancellationToken ct)
    {
        // 残留清理会删注册表键、移动目录，必须与其它写入串行
        using var lease = await _writeLock.EnterAsync(ct).ConfigureAwait(false);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var quarantine = Path.Combine(AppPaths.Root, "quarantine", $"residue-{stamp}");
        Directory.CreateDirectory(quarantine);
        var reports = AppPaths.ReportsRoot;
        Directory.CreateDirectory(reports);
        var moved = new List<object>();

        foreach (var item in selected.Where(item => item.CanClean))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (item.Kind == "注册表")
                {
                    var parts = item.Path.Split(new[] { "::" }, 4, StringSplitOptions.None);
                    if (parts.Length != 4) continue;
                    var hive = parts[1];
                    var view = parts[2];
                    var subKey = parts[3].Trim();
                    var state = _registry.CaptureKey(hive, view, subKey);
                    _registry.DeleteKey(hive, view, subKey);
                    moved.Add(new { kind = "registry", path = subKey, snapshot = state });
                }
                else if (item.Kind == "服务")
                {
                    var serviceName = item.Path;
                    const string serviceRoot = @"SYSTEM\CurrentControlSet\Services\";
                    var state = _registry.CaptureKey("HKLM", "64", serviceRoot + serviceName);
                    RunProcess("sc.exe", $"stop \"{serviceName}\"");
                    var exit = RunProcess("sc.exe", $"delete \"{serviceName}\"");
                    moved.Add(new { kind = "service", name = serviceName, exit, snapshot = state });
                }
                else if (File.Exists(item.Path))
                {
                    var target = Path.Combine(quarantine, $"{Guid.NewGuid():N}{Path.GetExtension(item.Path)}");
                    MoveFile(item.Path, target, ct);
                    moved.Add(new { kind = "file", original = item.Path, quarantined = target });
                }
                else if (Directory.Exists(item.Path))
                {
                    if (!IsSafeToMove(item.Path))
                    {
                        moved.Add(new
                        {
                            kind = "skipped",
                            path = item.Path,
                            reason = "目录过于靠近系统根（盘符根、Windows/Program Files 或层级不足），已跳过"
                        });
                        continue;
                    }

                    var target = Path.Combine(quarantine, Guid.NewGuid().ToString("N"));
                    MoveDirectory(item.Path, target, ct);
                    moved.Add(new { kind = "directory", original = item.Path, quarantined = target });
                }
            }
            catch (Exception ex)
            {
                moved.Add(new { kind = "error", path = item.Path, message = ex.Message });
            }
        }

        var jsonPath = Path.Combine(reports, $"residue-{stamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new
        {
            target = result.Target.DisplayName,
            cleanedAt = DateTimeOffset.Now,
            items = selected.Select(item => new { item.Kind, item.Path, item.Evidence, item.Risk }),
            moved
        }, JsonConfig.Options), ct).ConfigureAwait(false);

        var htmlPath = Path.Combine(reports, $"residue-{stamp}.html");
        var html = new StringBuilder()
            .AppendLine("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">")
            .AppendLine("<title>FreshStart 残留清理报告</title>")
            .AppendLine("<style>body{font-family:Segoe UI,sans-serif;max-width:820px;margin:40px auto;color:#222}" +
                        "table{border-collapse:collapse;width:100%}td,th{padding:8px;border-bottom:1px solid #eee;text-align:left}</style>")
            .AppendLine($"<h1>卸载残留清理：{Encode(result.Target.DisplayName)}</h1>")
            .AppendLine($"<p>清理时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>")
            .AppendLine("<table><tr><th>类型</th><th>路径</th><th>风险</th></tr>");
        foreach (var item in selected)
        {
            html.AppendLine($"<tr><td>{Encode(item.Kind)}</td><td>{Encode(item.Path)}</td><td>{Encode(item.RiskText)}</td></tr>");
        }
        html.AppendLine("</table><p>文件已移入隔离区，可在备份目录中恢复。</p></html>");
        await File.WriteAllTextAsync(htmlPath, html.ToString(), ct).ConfigureAwait(false);

        Log.Info($"残留清理完成: {result.Target.DisplayName}，{selected.Count} 项");
        return (htmlPath, jsonPath);
    }

    private static string Encode(string? value) => global::System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// 只允许移动"看起来像程序安装目录"的路径：
    /// 盘符根、系统目录本身或其祖先、层级不足 3 段的一律拒绝。
    /// </summary>
    private static bool IsSafeToMove(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        if (full.Length == 0) return false;
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;

        var depth = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3) return false;

        var guarded = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var candidate in guarded)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var protectedRoot = candidate.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, protectedRoot, StringComparison.OrdinalIgnoreCase)) return false;
            // full 是受保护目录的祖先（例如 C:\Program Files 的上级）同样拒绝
            if (protectedRoot.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static long DirectorySize(string path, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    private static List<string> FindShortcuts(string displayName, CancellationToken ct)
    {
        var results = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        var expectedName = displayName.Trim();

        foreach (var root in roots.Where(Directory.Exists))
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current, "*.lnk", SearchOption.TopDirectoryOnly))
                    {
                        if (IsLikelyShortcut(file, expectedName))
                        {
                            results.Add(file);
                        }
                    }
                }
                catch { /* 某些开始菜单目录不可读，跳过 */ }

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                            {
                                stack.Push(directory);
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* 目录不可读，跳过 */ }
            }
        }
        return results;
    }

    private static List<string> FindUninstallKeys(InstalledProgram target, CancellationToken ct)
    {
        var results = new List<string>();
        (RegistryHive Hive, RegistryView View, string HiveText)[] roots =
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
            (RegistryHive.CurrentUser, RegistryView.Default, "HKCU")
        };
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var (hive, view, hiveText) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var key = root.OpenSubKey(name);
                    var display = key?.GetValue("DisplayName") as string;
                    if (string.Equals(display?.Trim(), target.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        var viewText = view == RegistryView.Registry32 ? "32" : "64";
                        results.Add($"REG::{hiveText}::{viewText}::{path}\\{name}");
                    }
                }
            }
            catch { /* ignore */ }
        }
        return results;
    }

    private static List<string> FindServices(InstalledProgram target, CancellationToken ct)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(target.InstallLocation)) return results;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root is null) return results;
            foreach (var name in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(name);
                var image = key?.GetValue("ImagePath") as string;
                if (image is not null && ImageBelongsToInstallLocation(image, target.InstallLocation))
                {
                    results.Add(name);
                }
            }
        }
        catch { /* ignore */ }
        return results;
    }

    private static bool IsLikelyShortcut(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        return stem.Equals(displayName, StringComparison.OrdinalIgnoreCase)
               || (displayName.Length >= 4
                   && stem.StartsWith(displayName + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ImageBelongsToInstallLocation(string imagePath, string installLocation)
    {
        try
        {
            var image = imagePath.Trim();
            if (image.StartsWith('"'))
            {
                var end = image.IndexOf('"', 1);
                if (end > 1) image = image[1..end];
            }
            else
            {
                var comma = image.IndexOf(',');
                var space = image.IndexOf(' ');
                var cut = comma < 0 ? space : space < 0 ? comma : Math.Min(comma, space);
                if (cut > 0) image = image[..cut];
            }

            var executable = Path.GetFullPath(Environment.ExpandEnvironmentVariables(image));
            var root = Path.GetFullPath(installLocation).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void MoveFile(string source, string destination, CancellationToken ct)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            using var input = File.OpenRead(source);
            using var output = File.Create(destination);
            CopyStream(input, output, ct);
            output.Flush(flushToDisk: true);
            if (new FileInfo(destination).Length != new FileInfo(source).Length)
            {
                throw new IOException($"跨卷复制文件校验失败：{source}");
            }
            File.Delete(source);
        }
    }

    private static void MoveDirectory(string source, string destination, CancellationToken ct)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDirectory(source, destination, ct);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            using var input = File.OpenRead(file);
            using var output = File.Create(Path.Combine(destination, Path.GetFileName(file)));
            CopyStream(input, output, ct);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), ct);
        }
    }

    private static void CopyStream(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    private static int RunProcess(string fileName, string arguments)
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
            if (process is null) return -1;
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return -1;
            }
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
