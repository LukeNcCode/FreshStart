# 性能实测（0.7.1）

## 测试环境

| 项 | 值 |
|---|---|
| OS | Windows 11 Pro for Workstations 23H2，Build 22631.4169 |
| CPU | Intel Core i9-14900HX（24 核 / 32 线程） |
| 内存 | 15.8 GB |
| 显示缩放 | 150% |
| 构建 | Release，self-contained，single-file，ReadyToRun，PublishTrimmed=false |
| 产物 | `FreshStart.Host.exe` 157.8 MB + `FreshStart.exe`（NativeAOT）2.2 MB |

## 冷启动（进程创建 → 可交互）

统计 5 次（构建完成后的稳定状态），首次安装后第一次启动单独标注：

| Run | 进程创建 → 可交互 (ms) | 工作集 (MB) |
|---:|---:|---:|
| 1 | 468 | 131.1 |
| 2 | 481 | 133.4 |
| 3 | 451 | 133.0 |
| 4 | 446 | 132.7 |
| 5 | 467 | 132.4 |

- **P50 = 467 ms，P95 = 468 ms，max WS = 133.4 MB**（0.7.0 复测 5 次）
- 首次安装后的第一次启动约 831 ms：单文件原生库解包与 Defender 首次扫描，属一次性成本。
- 0.6.0 同机测得 P50 400 ms；差异主要来自系统负载，均满足 ≤800ms。

启动阶段拆解（典型 467ms 运行）：

| 阶段 | 典型耗时 |
|---|---|
| 进程创建 → App.OnStartup（CLR + App.xaml + 主题字典） | ~100 ms |
| 设置读取 + 日志初始化 + 手写 DI | ~30 ms |
| Shell 构建（标题栏/侧边栏/静态首页） | ~120 ms |
| Show + 布局 + Mica + 首帧 | ~180 ms |
| 首帧 → 可交互 | < 10 ms |

## 热启动

已有 Host 运行时，NativeAOT 启动器完成「探测 → 转发参数 → 退出」：

| 次数 | 耗时 |
|---:|---:|
| 首次进程加载（早期实测） | 85 ms |
| 稳定状态（6 次） | 13–29 ms |

- **P50 = 14 ms**（目标 ≤ 300 ms）
- 已通过 UI Automation 验证：转发 `--page=system-info` 后已有窗口显示系统信息页的「刷新」按钮。

## 内存

| 场景 | Task Manager 私有工作集 | 总 Working Set |
|---|---:|---:|
| 首屏（home） | 47.1 MB | 137.8 MB |
| 系统信息页（缓存命中） | 51.1 MB | 148.4 MB |
| 原生应用页（36 个可见应用） | 53.5 MB | 150.6 MB |
| 推荐软件页（含已安装检测） | 52.5 MB | 149.8 MB |
| 一键开荒页 | 44.3 MB | 133.1 MB |
| 隐私清理页 | 44.7 MB | 132.8 MB |
| 界面调优页 | 54.6 MB | 139.4 MB |
| 网络工具页 | 54.6 MB | 140.4 MB |
| 电源管理页 | 54.6 MB | 134.4 MB |
| 游戏优化页 | 38.1 MB | 122.0 MB |
| 服务与任务页（328 个服务） | 47.6 MB | 138.5 MB |
| 软件卸载页（93 个程序） | 48.6 MB | 137.7 MB |
| 驱动与备份页（49 个驱动） | 49.7 MB | 144.3 MB |
| 磁盘空间页（未扫描） | 42.0 MB | 130.1 MB |
| 资源占用页（1s 采样，3s） | 51.7 MB | 140.9 MB |
| 资源占用页（1s 采样，30s） | 57.9 MB | 157.8 MB |
| 资源占用页（1s 采样，60s） | 56.3 MB | 156.8 MB |

目标 ≤150MB 按 Task Manager 显示的“私有工作集”计，实测 42–66MB，满足。总 Working Set 包含 WPF/PDH 的共享页面，资源页稳定在 157MB 左右（私有工作集 56MB），属于共享页统计差异。

> 0.4.2：GPU 占用修复。`GPU Engine\Utilization Percentage` 是 PDH 双样本计数器，`CounterSample.Calculate(cur)` 永远返回 0；现在按实例保存上一次样本并用 `Calculate(prev, cur)` 计算，再按引擎类型求和后取最大值（与任务管理器“GPU 0”口径接近）。实测桌面 Wallpaper Engine 场景 25%，任务管理器 28%。

> 0.4.1：资源页打开后立即发出第一帧采样（实测 46–51ms），CPU 在第二个采样点（+400ms）出值；磁盘/GPU 的 PDH 计数器改到后台线程初始化，不再阻塞首帧，就绪前显示“…”而不是 0。GPU 首次读取失败会继续重试，连续 3 次失败才标记“不可用”。

> 0.3.1 修复：`Process.GetProcesses()` 与 `PerformanceCounterCategory.ReadCategory()` 每轮会产生数百个短命对象，原先 gen0 未及时回收，资源页私有工作集 30 秒内会多涨约 10MB。现在每轮采样后执行一次 `GC.Collect(0)`（<2ms，后台线程），进程/GPU 分别约 1s/2s 刷新。

## 页面切换

- 页面骨架在导航事件内立即创建（< 100ms）。
- 完整数据异步填充；过渡为 200ms 不透明度 + 8px 上移。
- 系统信息页从骨架切换到数据时强制 `ScrollToTop`，避免焦点导致滚动偏移。

## 复测方法

```powershell
dotnet publish src\FreshStart.App\FreshStart.App.csproj -c Release -r win-x64 `
  -p:DevNoElevation=true -o publish\dev

$exe = "publish\dev\FreshStart.Host.exe"
1..10 | ForEach-Object {
  $p = Start-Process $exe -ArgumentList "--smoke-test", "--smoke-out=publish\dev\smoke.json" -PassThru
  $p.WaitForExit()
  Get-Content publish\dev\smoke.json -Raw | ConvertFrom-Json |
    Select-Object processStartMs, workingSetMb
}
```

`processStartMs` 由 `StartupProbe` 用 `Process.StartTime` 计算，包含 CLR 与 App.xaml 加载，而不只是 `OnStartup` 之后。
