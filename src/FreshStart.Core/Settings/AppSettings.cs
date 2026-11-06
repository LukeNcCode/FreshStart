namespace FreshStart.Core.Settings;

/// <summary>
/// 用户设置。字段保持扁平，便于导出/导入和向前兼容。
/// 新增字段必须给默认值，旧配置文件缺失字段时自动使用默认值。
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Light / Dark</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>主题强调色（#RRGGBB）。</summary>
    public string AccentColor { get; set; } = "#007AFF";

    /// <summary>关闭后所有代码动画时长为 0（无障碍要求）。</summary>
    public bool AnimationsEnabled { get; set; } = true;

    public bool SidebarCollapsed { get; set; }

    /// <summary>每次启动后第一次写入前，询问是否创建系统还原点。</summary>
    public bool AskRestorePointOnFirstWrite { get; set; } = true;

    /// <summary>在独立窗口中实时显示后台任务控制台。</summary>
    public bool TaskConsoleEnabled { get; set; }

    /// <summary>高风险操作前创建系统还原点（会话内选择开启后生效）。</summary>
    public bool CreateRestorePointForHighRisk { get; set; } = true;

    public string LastPage { get; set; } = "home";

    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }

    /// <summary>是否已经保存过窗口位置。false 时窗口居中，避免把 0,0 当成有效位置。</summary>
    public bool WindowPositionSaved { get; set; }

    public bool WindowMaximized { get; set; }

    public int BackupRetentionDays { get; set; } = 60;

    /// <summary>是否已看过启动品牌动画。</summary>
    public bool HasSeenIntro { get; set; }

    /// <summary>已看过的启动动画版本；产品更新后可重新展示一次。</summary>
    public int IntroRevision { get; set; }

    /// <summary>已应用的内置桌面壁纸键（空 = 未用内置壁纸）。</summary>
    public string DesktopWallpaper { get; set; } = string.Empty;

    /// <summary>应用内置壁纸前的原始壁纸路径（用于「恢复原壁纸」）。</summary>
    public string OriginalWallpaperPath { get; set; } = string.Empty;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
