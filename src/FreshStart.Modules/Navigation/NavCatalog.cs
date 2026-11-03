namespace FreshStart.Modules.Navigation;

/// <summary>
/// 导航清单是编译期常量，启动时不扫描程序集、不读数据库。
/// IsAvailable=false 的条目会在侧边栏置灰，提示对应的交付批次。
/// </summary>
public sealed record NavItemDefinition(
    string Key,
    string Title,
    string Group,
    string IconKey,
    bool IsAvailable,
    int Batch,
    string Hint)
{
    public string IconData => Visual.AppIcons.Get(IconKey);

    public string UnavailableText => $"该模块计划第 {Batch} 批交付";
}

public static class NavCatalog
{
    public static IReadOnlyList<NavItemDefinition> Items { get; } = new List<NavItemDefinition>
    {
        new("home", "开荒工作台", "总览", "home", true, 1, "首页与快捷操作"),
        new("system-info", "系统信息", "总览", "info", true, 1, "硬件、系统版本与激活状态"),

        new("software", "推荐软件", "常用", "download", true, 3, "7 个高频软件的静默安装"),
        new("presets", "一键开荒", "常用", "preset", true, 4, "四套预设批量优化"),
        new("cleanup", "系统清理", "常用", "cleanup", true, 4, "临时文件与缩略图缓存"),

        new("execution", "执行中心", "优化", "terminal", true, 9, "配置工作流、预检计划、批量执行并生成报告"),
        new("context-menu", "右键菜单", "优化", "context-menu", true, 2, "新版/经典右键菜单管理"),
        new("ui-tuning", "界面调优", "优化", "sliders", true, 4, "任务栏、资源管理器与深色模式"),
        new("privacy", "隐私清理", "优化", "privacy", true, 4, "遥测、广告 ID 与推荐内容"),
        new("windows-update", "Windows 更新", "优化", "update", true, 2, "暂停、关闭与更新历史"),
        new("defender", "安全防护", "优化", "shield", true, 2, "Defender 引擎、实时防护与防火墙"),
        new("features", "Windows 功能", "优化", "apps", true, 8, "启用 / 禁用可选功能"),
        new("game-optimize", "游戏优化", "优化", "game", true, 4, "Game DVR、游戏模式与 GPU 调度"),

        new("appx", "原生应用", "应用", "apps", true, 3, "Appx 包卸载与重新注册"),
        new("uninstall", "软件卸载", "应用", "uninstall", true, 4, "第三方程序卸载与残留清理"),
        new("startup", "自启动", "应用", "startup", true, 2, "注册表、启动文件夹、任务与服务"),

        new("performance", "资源占用", "工具", "activity", true, 3, "CPU/内存/磁盘/网络/GPU 实时曲线"),
        new("network", "网络工具", "工具", "network", true, 4, "DNS、IPv4、Ping 与局域网互传"),
        new("service-tasks", "服务与任务", "工具", "gear", true, 4, "服务启动类型与计划任务"),
        new("power", "电源管理", "工具", "power", true, 4, "电源计划、合盖与快速启动"),
        new("driver", "驱动与备份", "工具", "chip", true, 4, "驱动安装、删除与导出备份"),
        new("restore", "全局还原", "工具", "sync", true, 4, "恢复到首次基准"),
        new("history", "操作历史", "工具", "activity", true, 4, "查看并回滚任意历史操作"),

        new("settings", "设置", "其他", "settings", true, 1, "外观、动效、备份与关于")
    };

    public static NavItemDefinition? Find(string key)
        => Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IGrouping<string, NavItemDefinition>> Grouped()
        => Items.GroupBy(item => item.Group).ToList();
}
