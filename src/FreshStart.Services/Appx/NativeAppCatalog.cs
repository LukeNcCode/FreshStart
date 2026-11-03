namespace FreshStart.Services.Appx;

/// <summary>Windows 11 常见的系统原生应用（Appx）。清单是静态的，不依赖本机是否还装着。</summary>
public sealed class NativeAppDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>面向「纯净系统开荒」是否建议移除（仅用于排序/提示，不自动勾选）。</summary>
    public bool RecommendedRemove { get; init; }

    /// <summary>受系统保护、需要走强制卸载（takeown / 删除目录）的组件。</summary>
    public bool ForceRemove { get; init; }

    /// <summary>剧本步骤类型：appx / appxforce / onedrive。</summary>
    public string StepKind { get; init; } = "appx";
}

/// <summary>
/// 静态原生应用清单：按 Windows 11 出厂预装整理，包含常见但可能被本机提前删除的条目，
/// 这样剧本里仍然可以预先写好「移除某某应用」，执行时匹配不到就自动跳过。
/// </summary>
public static class NativeAppCatalog
{
    public static IReadOnlyList<NativeAppDefinition> All { get; } = new List<NativeAppDefinition>
    {
        new() { Name = "Microsoft.BingWeather", DisplayName = "天气", Category = "资讯", Description = "MSN 天气", RecommendedRemove = true },
        new() { Name = "Microsoft.BingNews", DisplayName = "资讯", Category = "资讯", Description = "MSN 新闻聚合", RecommendedRemove = true },
        new() { Name = "Microsoft.MicrosoftSolitaireCollection", DisplayName = "纸牌合集", Category = "游戏", Description = "Microsoft Solitaire Collection", RecommendedRemove = true },
        new() { Name = "Microsoft.GamingApp", DisplayName = "Xbox", Category = "游戏", Description = "Xbox 应用与游戏库", RecommendedRemove = true },
        new() { Name = "Microsoft.XboxGamingOverlay", DisplayName = "Game Bar", Category = "游戏", Description = "Win+G 游戏栏覆盖层", RecommendedRemove = true },
        new() { Name = "Microsoft.MicrosoftStickyNotes", DisplayName = "便笺", Category = "效率", Description = "桌面便笺" },
        new() { Name = "Microsoft.Office.OneNote", DisplayName = "OneNote", Category = "效率", Description = "笔记应用", RecommendedRemove = true },
        new() { Name = "Microsoft.MicrosoftOfficeHub", DisplayName = "Office 中心", Category = "效率", Description = "Office 推广入口", RecommendedRemove = true },
        new() { Name = "Microsoft.OutlookForWindows", DisplayName = "Outlook（新版）", Category = "效率", Description = "Windows 邮件新版客户端" },
        new() { Name = "Microsoft.Todos", DisplayName = "Microsoft To Do", Category = "效率", Description = "待办清单" },
        new() { Name = "Microsoft.PowerAutomateDesktop", DisplayName = "Power Automate", Category = "效率", Description = "桌面自动化", RecommendedRemove = true },
        new() { Name = "Microsoft.WindowsFeedbackHub", DisplayName = "反馈中心", Category = "系统", Description = "Windows 反馈", RecommendedRemove = true },
        new() { Name = "Microsoft.GetHelp", DisplayName = "获取帮助", Category = "系统", Description = "微软支持入口", RecommendedRemove = true },
        new() { Name = "Microsoft.Getstarted", DisplayName = "提示", Category = "系统", Description = "Windows 使用提示", RecommendedRemove = true },
        new() { Name = "Microsoft.WindowsStore", DisplayName = "Microsoft Store", Category = "系统", Description = "应用商店" },
        new() { Name = "Microsoft.WindowsTerminal", DisplayName = "Windows Terminal", Category = "系统", Description = "现代终端" },
        new() { Name = "Microsoft.Windows.DevHome", DisplayName = "开发主页", Category = "系统", Description = "Dev Home", RecommendedRemove = true },
        new() { Name = "Microsoft.SecHealthUI", DisplayName = "Windows 安全中心", Category = "系统", Description = "彻底移除 Defender 引擎与安全中心（不可回滚，需重启）", ForceRemove = true, StepKind = "defender-remove" },
        new() { Name = "OneDrive", DisplayName = "OneDrive", Category = "系统", Description = "OneDrive 同步客户端（Win32，运行卸载程序移除）", StepKind = "onedrive" },
        new() { Name = "Microsoft.Copilot", DisplayName = "Copilot", Category = "系统", Description = "Windows Copilot 侧边栏", RecommendedRemove = true },
        new() { Name = "Microsoft.Windows.Photos", DisplayName = "照片", Category = "媒体", Description = "图片查看与编辑" },
        new() { Name = "Microsoft.ZuneMusic", DisplayName = "媒体播放器", Category = "媒体", Description = "Windows Media Player 新版" },
        new() { Name = "Microsoft.ZuneVideo", DisplayName = "电影和电视", Category = "媒体", Description = "视频播放", RecommendedRemove = true },
        new() { Name = "Microsoft.WindowsCamera", DisplayName = "相机", Category = "媒体", Description = "摄像头应用" },
        new() { Name = "Microsoft.WindowsSoundRecorder", DisplayName = "录音机", Category = "媒体", Description = "录音" },
        new() { Name = "Microsoft.ScreenSketch", DisplayName = "截图工具", Category = "媒体", Description = "截图与录屏" },
        new() { Name = "Microsoft.Paint", DisplayName = "画图", Category = "媒体", Description = "Paint" },
        new() { Name = "Microsoft.MSPaint", DisplayName = "画图 3D", Category = "媒体", Description = "Paint 3D", RecommendedRemove = true },
        new() { Name = "Microsoft.Microsoft3DViewer", DisplayName = "3D 查看器", Category = "媒体", Description = "3D 模型查看", RecommendedRemove = true },
        new() { Name = "Microsoft.MixedReality.Portal", DisplayName = "混合现实门户", Category = "媒体", Description = "MR Portal", RecommendedRemove = true },
        new() { Name = "Microsoft.WindowsMaps", DisplayName = "地图", Category = "工具", Description = "Windows 地图", RecommendedRemove = true },
        new() { Name = "Microsoft.WindowsAlarms", DisplayName = "时钟", Category = "工具", Description = "闹钟与计时器" },
        new() { Name = "Microsoft.WindowsCalculator", DisplayName = "计算器", Category = "工具", Description = "计算器" },
        new() { Name = "Microsoft.WindowsNotepad", DisplayName = "记事本", Category = "工具", Description = "Notepad" },
        new() { Name = "microsoft.windowscommunicationsapps", DisplayName = "邮件和日历", Category = "工具", Description = "Windows 邮件/日历（旧版）" },
        new() { Name = "Microsoft.YourPhone", DisplayName = "手机连接", Category = "工具", Description = "Phone Link", RecommendedRemove = true },
        new() { Name = "Microsoft.People", DisplayName = "人脉", Category = "工具", Description = "联系人应用", RecommendedRemove = true },
        new() { Name = "Microsoft.SkypeApp", DisplayName = "Skype", Category = "社交", Description = "Skype", RecommendedRemove = true },
        new() { Name = "MSTeams", DisplayName = "Microsoft Teams", Category = "社交", Description = "Teams 个人版", RecommendedRemove = true },
        new() { Name = "Microsoft.549981C3F5F10", DisplayName = "Cortana", Category = "社交", Description = "Cortana 语音助手", RecommendedRemove = true },
        new() { Name = "Clipchamp.Clipchamp", DisplayName = "Clipchamp", Category = "媒体", Description = "视频编辑器", RecommendedRemove = true }
    };

    public static int Count => All.Count;
}
