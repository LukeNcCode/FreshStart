using FreshStart.Core.Operations;

namespace FreshStart.App.Dialogs;

public sealed class DialogAction
{
    public DialogAction(string key, string label, bool isPrimary = false, bool isDestructive = false, bool isCancel = false)
    {
        Key = key;
        Label = label;
        IsPrimary = isPrimary;
        IsDestructive = isDestructive;
        IsCancel = isCancel;
    }

    public string Key { get; }
    public string Label { get; }
    public bool IsPrimary { get; }
    public bool IsDestructive { get; }
    public bool IsCancel { get; }
}

public sealed class DialogRequest
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Eyebrow { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; } = RiskLevel.Caution;
    public IReadOnlyList<string> Details { get; set; } = Array.Empty<string>();
    public IReadOnlyList<DialogAction> Actions { get; set; } = Array.Empty<DialogAction>();
    public string? Footer { get; set; }

    public string RiskText => Risk switch
    {
        RiskLevel.High => "高风险",
        RiskLevel.Caution => "谨慎",
        _ => "安全"
    };
}

public sealed class DialogResult
{
    public DialogResult(string? key) => Key = key;

    public string? Key { get; }
    public bool Cancelled => Key is null || Key == "cancel";
    public bool Is(string key) => string.Equals(Key, key, StringComparison.Ordinal);
}
