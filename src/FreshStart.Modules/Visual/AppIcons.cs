namespace FreshStart.Modules.Visual;

/// <summary>
/// 统一线性图标：24×24 画布、1.5px 描边、圆角端点。
/// 直接用 Path Data 字符串，避免位图与图标字体在启动期增加开销。
/// </summary>
public static class AppIcons
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = "M3.5 10.5 L12 3.5 L20.5 10.5 M5.5 9.5 V20.5 H18.5 V9.5 M9.5 20.5 V14.5 H14.5 V20.5",
        ["info"] = "M12 21 A9 9 0 1 1 12 3 A9 9 0 1 1 12 21 M12 11 V16.5 M12 7.5 V8.2",
        ["context-menu"] = "M4 6.5 H12 M4 12 H12 M4 17.5 H12 M16.5 6.5 H20 M16.5 12 H20 M16.5 17.5 H20",
        ["palette"] = "M14 4 L20 10 L11 19 L5 21 L6 14 Z M11 8 L16 13",
        ["preset"] = "M12 2.5 C15.5 6 16 10.5 12 14.5 C8 10.5 8.5 6 12 2.5 Z M12 14.5 V21 M8.5 17.5 C7 18.5 6.2 19.7 6 21 M15.5 17.5 C17 18.5 17.8 19.7 18 21",
        ["update"] = "M20 12 A8 8 0 1 1 12 4 M20 12 V6.5 M20 12 H14.5",
        ["shield"] = "M12 3 L20 6.2 V11 C20 16 16.5 19.6 12 21 C7.5 19.6 4 16 4 11 V6.2 Z",
        ["activity"] = "M3 12.5 H7 L10 5.5 L14 19 L17 12.5 H21",
        ["apps"] = "M4 4 H9.5 V9.5 H4 Z M14.5 4 H20 V9.5 H14.5 Z M4 14.5 H9.5 V20 H4 Z M14.5 14.5 H20 V20 H14.5 Z",
        ["download"] = "M12 3.5 V15 M7 10.5 L12 15.5 L17 10.5 M4.5 20.5 H19.5",
        ["uninstall"] = "M4 8 L12 3 L20 8 V17 L12 22 L4 17 Z M4 8 L12 13 L20 8 M12 13 V22",
        ["startup"] = "M12 2.5 C15.5 6 16 10.5 12 14.5 C8 10.5 8.5 6 12 2.5 Z M12 14.5 V21 M8.5 17.5 C7 18.5 6.2 19.7 6 21 M15.5 17.5 C17 18.5 17.8 19.7 18 21",
        ["cleanup"] = "M12 3 L13.8 10.2 L21 12 L13.8 13.8 L12 21 L10.2 13.8 L3 12 L10.2 10.2 Z",
        ["disk"] = "M12 21 A9 9 0 1 1 12 3 A9 9 0 1 1 12 21 M12 16 A4 4 0 1 1 12 8 A4 4 0 1 1 12 16 M12 12.8 V13.2",
        ["network"] = "M3 12 H21 M12 21 C16 17 16 7 12 3 C8 7 8 17 12 21 M4.2 7.5 H19.8 M4.2 16.5 H19.8",
        ["gear"] = "M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M12 15.5 A3.5 3.5 0 1 1 12 8.5 A3.5 3.5 0 1 1 12 15.5",
        ["power"] = "M12 3.5 V11.5 M6.8 6.6 A8 8 0 1 0 17.2 6.6",
        ["chip"] = "M7 7 H17 V17 H7 Z M9.5 4 V7 M14.5 4 V7 M9.5 17 V20 M14.5 17 V20 M4 9.5 H7 M4 14.5 H7 M17 9.5 H20 M17 14.5 H20",
        ["hardware"] = "M3 5 H21 V16 H3 Z M9 20.5 H15 M12 16 V20.5",
        ["game"] = "M7.5 8.5 H16.5 C19.5 8.5 20.5 10.5 20 15 C19.7 17.3 17.5 18 16 16 L14.5 14.5 H9.5 L8 16 C6.5 18 4.3 17.3 4 15 C3.5 10.5 4.5 8.5 7.5 8.5 Z M8 11 V13.5 M6.8 12.2 H9.2 M15.5 11.3 V11.7 M17.4 13.2 V13.6",
        ["sync"] = "M4 8 H17 M14 5 L17 8 L14 11 M20 16 H7 M10 13 L7 16 L10 19",
        ["terminal"] = "M3 5 H21 V19 H3 Z M7 9.5 L10 12 L7 14.5 M12.5 14.5 H17",
        ["filter"] = "M4 5.5 H20 M7 11.5 H17 M10 17.5 H14",
        ["privacy"] = "M2.5 12 C5.5 6.8 18.5 6.8 21.5 12 C18.5 17.2 5.5 17.2 2.5 12 Z M12 15 A3 3 0 1 1 12 9 A3 3 0 1 1 12 15 M4 3.5 L20 20.5",
        ["code"] = "M8 8 L3 12 L8 16 M16 8 L21 12 L16 16 M14 4.5 L10 19.5",
        ["sliders"] = "M4 7 H20 M4 12 H20 M4 17 H20 M9 4.8 V9.2 M16 9.8 V14.2 M7 14.8 V19.2",
        ["explorer"] = "M3 6.5 H9.5 L11.5 8.5 H21 V19.5 H3 Z",
        ["settings"] = "M4 7 H20 M4 12 H20 M4 17 H20 M9 4.8 V9.2 M16 9.8 V14.2 M7 14.8 V19.2",
        ["clock"] = "M12 21 A9 9 0 1 1 12 3 A9 9 0 1 1 12 21 M12 7 V12.5 L15.5 14.5",
        ["moon"] = "M20 14.5 A8.5 8.5 0 1 1 9.5 4 A7 7 0 0 0 20 14.5 Z",
        ["sun"] = "M12 16.5 A4.5 4.5 0 1 1 12 7.5 A4.5 4.5 0 1 1 12 16.5 M12 2.5 V5 M12 19 V21.5 M2.5 12 H5 M19 12 H21.5 M5.3 5.3 L7 7 M17 17 L18.7 18.7 M18.7 5.3 L17 7 M7 17 L5.3 18.7",
        ["chevron-left"] = "M14.5 6 L8.5 12 L14.5 18",
        ["chevron-right"] = "M9.5 6 L15.5 12 L9.5 18",
        ["close"] = "M6 6 L18 18 M18 6 L6 18",
        ["minimize"] = "M6 12 H18",
        ["maximize"] = "M6 6 H18 V18 H6 Z",
        ["restore"] = "M8 8 H18 V18 H8 Z M5 16 V5 H16",
        ["check"] = "M4 12.5 L9.5 18 L20 6.5",
        ["alert"] = "M12 3.5 L21 20 H3 Z M12 9.5 V14.5 M12 17 V17.5",
        ["folder"] = "M3 6.5 H9.5 L11.5 8.5 H21 V19.5 H3 Z",
        ["list"] = "M4 6.5 H20 M4 12 H20 M4 17.5 H14",
        ["refresh"] = "M20 12 A8 8 0 1 1 12 4 M20 12 V6.5 M20 12 H14.5",
        ["open"] = "M14 4 H20 V10 M20 4 L11 13 M18 14 V20 H4 V6 H10",
        ["copy"] = "M9 9 H20 V20 H9 Z M5 15 H4 V4 H15 V5"
    };

    public static string Get(string key) => Map.TryGetValue(key, out var data) ? data : Map["info"];

    public static IReadOnlyDictionary<string, string> All => Map;
}
