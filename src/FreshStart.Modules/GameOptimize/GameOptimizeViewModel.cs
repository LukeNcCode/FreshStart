using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Modules.UiTuning;
using FreshStart.Services.Tuning;

namespace FreshStart.Modules.GameOptimize;

/// <summary>游戏优化复用界面调优的页面与批量管线，只切换分类为“游戏”。</summary>
public sealed class GameOptimizeViewModel : UiTuningViewModel
{
    public GameOptimizeViewModel(TuningService tuning, OperationRunner runner, IToastService toast)
        : base(tuning, runner, toast, "游戏", "游戏优化",
            "Game DVR、游戏模式与 GPU 调度；草稿式修改，一次备份、失败回滚",
            "已启用游戏优化", "正在读取游戏设置…")
    {
    }
}
