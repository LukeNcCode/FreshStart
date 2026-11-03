using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FreshStart.Core;

namespace FreshStart.Services.System;

/// <summary>
/// 为"稍后由 SYSTEM/管理员执行的脚本"创建目录。
/// 目录名随机、ACL 只允许 SYSTEM 与 Administrators 写入，
/// 避免普通用户提前占位或替换脚本（TOCTOU → 本地提权）。
/// </summary>
public static class SecureWorkDirectory
{
    /// <summary>创建并收紧 ACL 的工作目录；返回其绝对路径。</summary>
    public static string Create(string label)
    {
        if (!IsElevated())
        {
            throw new UnauthorizedAccessException("创建立即由 SYSTEM 执行的脚本目录需要管理员权限");
        }

        var baseDir = ResolveBaseDirectory();
        var workDir = Path.Combine(baseDir, $"FreshStart-{SanitizeLabel(label)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        if (!LockDown(workDir))
        {
            TryDelete(workDir);
            throw new UnauthorizedAccessException("无法为 SYSTEM 脚本目录设置可信 ACL，已中止");
        }

        return workDir;
    }

    /// <summary>当前进程是否已提权（UAC 之后）。未提权时无法创建 SYSTEM 任务。</summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn($"判断提权状态失败: {ex.Message}");
            return false;
        }
    }

    private static bool IsSystemTemp(string baseDir)
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows)) return false;
            var expected = Path.Combine(windows, "Temp");
            return string.Equals(
                Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>尽力清理工作目录；失败只记日志（可能是 SYSTEM 任务仍持有文件）。</summary>
    public static void TryDelete(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时脚本目录失败: {directory} - {ex.Message}");
        }
    }

    private static string ResolveBaseDirectory()
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windows))
            {
                var candidate = Path.Combine(windows, "Temp");
                if (Directory.Exists(candidate) && IsSystemTemp(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"定位系统临时目录失败: {ex.Message}");
        }

        throw new IOException("系统临时目录不可用，拒绝在用户可写目录创建 SYSTEM 执行脚本");
    }

    /// <summary>去掉继承的 ACL，只保留 SYSTEM 与 Administrators 的完全控制。</summary>
    private static bool LockDown(string directory)
    {
        // *S-1-5-18 = SYSTEM，*S-1-5-32-544 = Administrators（用 SID 避免系统语言差异）
        var arguments = new StringBuilder()
            .Append($"\"{directory}\" /inheritance:r /grant:r ")
            .Append("*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /C /Q")
            .ToString();

        try
        {
            using var process = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                Log.Warn($"无法启动 icacls，脚本目录 ACL 未收紧：{directory}");
                return false;
            }

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                Log.Warn($"icacls 超时，脚本目录 ACL 未收紧：{directory}");
                return false;
            }

            if (process.ExitCode != 0)
            {
                Log.Warn($"icacls 失败（exit {process.ExitCode}），脚本目录 ACL 未收紧：{directory}");
                return false;
            }

            return HasTrustedAcl(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"收紧脚本目录 ACL 失败: {directory} - {ex.Message}");
            return false;
        }
    }

    private static bool HasTrustedAcl(string directory)
    {
        try
        {
            const string systemSid = "S-1-5-18";
            const string administratorsSid = "S-1-5-32-544";
            var security = new DirectoryInfo(directory).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is not (systemSid or administratorsSid))
            {
                Log.Warn($"脚本目录所有者不可信：{directory} -> {owner ?? "未知"}");
                return false;
            }

            const FileSystemRights writeRights =
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;

            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & writeRights) == 0) continue;
                var sid = rule.IdentityReference.Value;
                if (sid is systemSid or administratorsSid) continue;

                Log.Warn($"脚本目录存在不可信写权限：{directory} -> {sid}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"校验脚本目录 ACL 失败: {directory} - {ex.Message}");
            return false;
        }
    }

    private static string SanitizeLabel(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        var text = builder.ToString().Trim('-');
        if (text.Length == 0) text = "work";
        return text.Length > 24 ? text[..24] : text;
    }
}
