# FreshStart · 当前功能 / 实现链路 / 架构 / 技术路线

版本 **0.2.3**（环境克隆实验版）；5 个工程、4 层、25 个导航页面、24 种执行步骤类型、26 项注册表调优 + 4 套预设、8 个推荐软件、41 个内置原生应用条目、黑白单色界面、1 段首次启动品牌动画、3 张可设为系统壁纸的内置图。

---

## 一、进程与分层架构

```
FreshStart.exe  (Launcher, NativeAOT, asInvoker, ~2.2 MB)
   │ 命名管道探测/转发（热启动不弹 UAC）
   ├─ Host 已在跑 → 转发参数 → 激活窗口 → 退出
   └─ 未运行 → 启动 FreshStart.Host.exe（app.manifest: requireAdministrator，整个生命周期只弹一次 UAC）
                        │
                        ├─ 命名 Mutex（名字带版本号）单实例
                        ├─ 命名管道监听后续激活
                        └─ WPF 主体
```

| 工程 | 目标框架 / 产物 | 职责 |
|---|---|---|
| `FreshStart.Core` | `net8.0`，无 WPF 依赖 | `ISystemOperation` 契约、`OperationRunner`、JSON 备份、备份保留策略、历史审计、日志、设置、路径、注册表白名单、启动探针 |
| `FreshStart.Services` | `net8.0-windows`，Registry/WMI/PowerShell 封装 | 所有系统能力：注册表、Appx、winget、Defender、网络、电源、防火墙、功能、驱动、还原点… |
| `FreshStart.Modules` | `net8.0-windows` + WPF | 页面 View + ViewModel（CommunityToolkit.Mvvm）、自绘控件、导航清单 |
| `FreshStart.App` | `win-x64` 单文件 + ReadyToRun | 外壳：主题、自绘标题栏、Modal 确认、Toast、导航与组合根 |
| `FreshStart.Launcher` | `win-x64` NativeAOT | 极轻启动器，只做单实例与参数转发 |

依赖单向：`App → Modules → Services → Core`；Core 不引用 WPF、不直接碰注册表类型。

**组合方式**：`CompositionRoot` 手写 DI（无容器、无反射扫描），所有服务单例在此创建；页面 ViewModel 由 `NavigationService` 的工厂字典懒加载并**常驻缓存**，`--page=xxx` 直接定位。导航是编译期常量 `NavCatalog`（`IsAvailable` 控制占位批次）。

---

## 二、写操作统一管线（`OperationRunner`）

```
ISystemOperation.DescribeChanges()        ← 只读预演，供弹窗；禁止在此方法里同步等 I/O
      ↓
IOperationGate.RequestConsentAsync()      ← 确认弹窗 + 本次会话还原点三选一
      ↓
CaptureCurrentStateAsync()                ← 读当前值，失败则零写入
      ↓
JsonBackupStore.SaveAsync()               ← snapshot.json + manifest.json，原子写 + SHA256 + 读回校验
      ↓
[High 风险 + 会话内开启] RestorePointService.CreateAsync()
      ↓
ApplyAsync()                              ← 必须幂等，写后读回校验
      ├─ 成功 → 撤销栈（上限 20）+ operations.jsonl 审计 → HistoryChanged
      ├─ 失败 → RollbackAsync(CancellationToken.None)  ← 回滚不受取消影响
      └─ 取消 → 同样回滚已应用步骤（0.27.3 起），结果标记 Cancelled + RolledBack
```

- 写锁 `IWriteLock`（`WriteLock`，进程内 `SemaphoreSlim(1,1)` + `%ProgramData%\FreshStart\locks\write.lock` 跨进程独占文件锁）：`OperationRunner`、执行中心与 4 条不经过 Runner 的写路径共用同一实例，GUI/命令行/多进程写入全局串行；实现**不可重入**，复合操作内部调用 `CleanupService.CleanInsideWriteLockAsync` 这类"已持锁"版本。
- 备份目录：`%AppData%\FreshStart\backup\{yyyyMMdd-HHmmss}-{operationId}-{guid8}\`（`snapshot.json` / `manifest.json`）。
- 审计：`history\operations.jsonl` 逐行追加。
- 保留策略：启动时后台按 `BackupRetentionDays` 清理旧备份（至少保留最近 20 份），日志保留 14 天。
- 全局基准：首次启动在**线程池**采集 26 项调优键原值 → `state\baseline\baseline.json`（旧位置 `baseline.json` 会自动迁移）。
- `RegistryPathGuard` 白名单在操作**构造期**校验，按「前缀 + 分隔符」边界匹配，UI 无法绕过（**仅覆盖 FreshStart 自身的注册表写入**；Defender 移除脚本、winget source 配置、Appx 托盘清理不走该白名单）。
- `CanRollback=false` 的操作必须在 UI 明示：Defender 完整移除、Appx 强制卸载、OneDrive 移除、winget 批量安装、软件卸载、清理/驱动导出步骤。

**不经过 `OperationRunner` 的 4 条写路径**（有二次确认 + 已纳入全局写锁，但没有独立备份目录、不写 `operations.jsonl`）：
`CleanupService.CleanAsync`（临时文件）、`ResidueService.CleanAsync`（残留清理，移入 quarantine 并写报告）、`HistoryRestoreService.RestoreAsync`（历史回滚）、`AppxService` 的托盘自启动清理（在强制卸载操作内部，随该操作持锁）。

---

## 三、功能清单与实现链路

### 总览
| 页面 | 实现链路 |
|---|---|
| 开荒工作台 | 快捷操作目录 `QuickActionCatalog` → `QuickActionService`（启动控制面板/终端/设置页等 shell 命名空间）；开荒评分 `ScoreService`（**7 项加权：启动项 25 / 自动服务 15 / 登录开机任务 10 / 系统盘空间 15 / 内存 10 / 隐私开关 15 / 已安装软件数 10，合计 100**）→ `ScoreRing` 自绘控件；报告写入 `reports\score-*.{html,json}` |
| 系统信息 | `SystemInfoService`：快速字段同步 + WMI 慢字段 `Task.Run` + 每项 3s/整体 6s 超时 + 24h 磁盘缓存（`cache\systeminfo.json`）；`WmiQuery` 封装 |

### 优化
| 页面 | 实现链路 |
|---|---|
| 一键开荒 | `TuningPreset`（4 套）→ `TuningBatchOperation` → 逐项 `RegistryValueOperation` 快照+写入，失败整体回滚 |
| 执行中心 | `ExecutionCenterViewModel` 把 `ScriptEditorViewModel` 与执行计划/报告页合并为一个连续工作流；支持四套起始模板、配置直接发送执行、旧方案 JSON 导入/导出、依赖排序、预检、执行、取消与报告 |
| 右键菜单 | `ContextMenuService` 枚举 shell 动词/ContextMenuHandlers/SendTo/ShellNew → `RegistryKeyMoveOperation` 移入隔离区（`ContextMenuQuarantine` 索引，跨重启恢复）+ `ContextMenuStyleOperation`（Win10 经典菜单） |
| 界面调优 | 调优目录子集 + `ExplorerService.RestartAsync()` 让资源管理器/右键/任务栏生效 |
| 隐私清理 | 遥测、广告 ID、活动历史、推荐内容、位置等的注册表/策略键 → 批量 `RegistryValueOperation` |
| Windows 更新 | `WindowsUpdateService`：状态/历史读取 + 三模式（暂停/关闭/恢复）= 服务启动类型 + 策略键 + 三处更新计划任务，统一快照 |
| 安全防护 | 合并页：Defender 标签（实时防护/云保护/排除项/临时关闭）+ 防火墙标签（三配置文件开关、规则枚举/增删，先快照；方向/动作/协议走枚举白名单，端口走格式校验）+「彻底移除 Defender 引擎」 |
| Windows 功能 | `OptionalFeatureService`：DISM 接口枚举 + 启用/禁用 + 状态读回校验 |
| 游戏优化 | 继承 `UiTuningViewModel`：GameDVR/Game Bar/捕获/HAGS（需重启）等 |

### 应用
| 页面 | 实现链路 |
|---|---|
| 原生应用 | `NativeAppCatalog`（41 条静态清单）→ `AppxRemoveBatchOperation`：先记录 `RemovedAppxInfo` → `Remove-AppxPackage`；受保护组件走 `AppxForceRemoveOperation`（DISM 解除策略 → 必要时 takeown/删目录，**仅限 `WindowsApps` / `SystemApps` 等合法安装目录**）；`AppxRestoreOperation` 重新注册 |
| 推荐软件 | `SoftwareCatalog`（8 条：解压缩 ×2 / 浏览器 ×2 / Everything / PotPlayer / OBS / UU远程）→ `WingetService`：按需检测/在线更新/离线包导入 → `EnsureOfficialSourceAsync`（只用微软官方源）→ `WingetInstallBatchOperation` 逐个 `install --id …`，失败且有直链条目时走官方直链兜底；单失败继续、成功保留、重试只重失败项（详见第五节） |
| 软件卸载 | `InstalledProgramService`（注册表 Uninstall 键，WOW6432 双视图）→ `UninstallProgramOperation`；卸载后 `ResidueService` 扫残留目录/注册表，可清理（拒绝搬迁盘符根与系统目录） |
| 自启动 | `StartupService` 合并四来源（Run/RunOnce、启动文件夹、计划任务、第三方自动服务）+ `StartupApproved` 状态；启停分别落到 `RegistryValueOperation` / 文件改名 / `ScheduledTaskToggleOperation` / `ServiceStartModeOperation` |

### 工具
| 页面 | 实现链路 |
|---|---|
| 资源占用 | `PerformanceSampler`：PDH `ReadCategory` 采 CPU/内存/磁盘/网络/GPU + 进程差值；`Sparkline`/`ScoreRing` 自绘；页面 `Loaded/Unloaded` 触发 `Start/Stop` |
| 系统清理 | `CleanupService`（**3 类：用户临时、Windows 临时、缩略图/图标缓存**）→ 统计后删除，占用文件自动跳过；执行中心里对应 `CleanupStepOperation` |
| 网络工具 | `NetworkService`：适配器/DNS/IPv4 读取 → `NetworkDnsOperation`/`NetworkIpOperation`（静态 IP 快照回滚）+ `netsh` 刷缓存 + Ping；局域网互传 `LanShareService`（绑定局域网 IPv4、token 鉴权、并发上限 8、单请求 1 小时超时、路径必须落在共享根内） |
| 服务与任务 | `ServiceControlService`（sc.exe + 注册表读回校验，含 DelayedAutoStart）、`ScheduledTaskService`（PowerShell 隐藏窗口）→ 各自 `OperationBase` |
| 电源管理 | `PowerService`：内置四套主流方案（平衡/高性能/节能/卓越性能），缺失时 `powercfg -duplicatescheme` 创建；切换前快照、切换后 `getactivescheme` 校验；快速启动写 `HiberbootEnabled` |
| 驱动与备份 | `DriverService`（第三方驱动枚举）→ `DriverInstallOperation`（INF/目录 pnputil）、`DriverRemoveOperation`（`pnputil /export-driver` 导出备份后可回滚） |
| 全局还原 | `RestoreBaseline`（26 项调优键原值）→ `GlobalRestoreOperation` 批量反演 |
| 操作历史 | `OperationHistoryStore`（jsonl）→ `HistoryViewModel` → `HistoryRestoreService` 按快照回滚**指定一条**历史 |

> **历史回滚的能力边界**：只有 `RegistryValue`、`RegistryKey`、`Service` 三类快照条目能回放；文件移动、计划任务、Appx、驱动、winget 等条目会**跳过并计数**，结果里 `Restored=0` 时按失败处理。回滚前会校验「备份目录在 `backup\` 之下」且「`snapshot.json` 的 SHA256 与 `manifest.json` 一致」，不通过直接拒绝。

### 其他
设置页：主题（浅色/深色/跟随系统）、强调色、动效强度（`MotionPreferences` 全局时长）、备份保留天数、首次写入是否询问还原点、日志/备份目录、关于。

---

## 四、执行中心执行链路

```
① UI: RunJobAsync → BuildPlan(job) → 逐条 UnattendedStep 变成 UnattendedPlanItem（可执行/跳过+原因）
② PrepareAsync(plan, ct, progress)     ← 只读预检
     ├─ 缓存：Appx 包列表 / 网卡列表 / 电源计划 各只枚举一次
     ├─ 逐项校验：服务是否存在、任务是否存在、包是否安装、目标是否解析得到
     ├─ 进度：Percent 1–8%，消息「正在预检第 i/n 步：kind」
     └─ 产出：List<ISystemOperation> + Notes + 跳过项
③ CompositeOperation(ContinueOnError)   ← 组合成一个可审计操作
④ OperationRunner.ExecuteAsync           ← 走统一管线（确认弹窗在这里，不是预检时）
     ├─ ContinueOnError=true → 失败记入 Failures 并继续；全部失败时整体判失败
     ├─ ContinueOnError=false → 任一步失败即逆序回滚已应用部分
     └─ 用户取消 → 回滚已应用的步骤（0.27.3 起）
⑤ 成功后按需 ExplorerService.RestartAsync（右键菜单/任务栏类步骤需要）
⑥ WriteReport → reports\unattended-{时间}-{key}.{html,json}
     （状态/耗时/变更表/备注/失败项/备份目录）
```

命令行等价入口：`FreshStart.exe --execute=方案.json --yes`（headless 用 `AutoApproveGate`，不发界面内弹窗）；旧参数 `--unattended` 继续兼容。

**退出码**：`0` 全部成功 · `1` 执行失败或有步骤失败 · `2` 缺少 `--yes`/`--silent` 或已有实例冲突 · `3` 方案文件不存在/无效/无可执行步骤 · `4` 未处理异常。

---

## 五、winget 安装链路

```
按需初始化（WingetService）
  ├─ 应用启动不调用 winget，不阻塞主界面
  ├─ 进入「推荐软件」页后才检测 winget
  ├─ MinimumSourceCapableVersion = 1.11     ← 现代源索引安装能力下限
  └─ MinimumSupportedVersion = 1.6          ← 历史下限，仅作兜底

winget 缺失或过旧：
  ├─ 在线：从 aka.ms/getwinget 安装微软官方 App Installer
  ├─ 商店：打开 Microsoft Store 的 App Installer 页面
  └─ 离线：导入独立 winget-offline Release 附加包中的 msixbundle

主发布包不携带 winget 二进制；可选附加包包含 App Installer、VCLibs 与
Windows App Runtime installer，仅供无法联网的干净系统使用。

安装前：EnsureOfficialSourceAsync()      ← 只用微软官方源（0.28.1 起不再切国内镜像）
          ├─ source list 里没有镜像地址 → 直接继续
          └─ 检测到旧版本遗留的 USTC 镜像 → source reset 迁回官方源 + 刷新索引
             （非管理员时只提示、不阻断）

安装前能力检查：版本 <1.11 → 直接给结论（软件页提示 + 批量安装前置拦截），
                 不再逐包跑十几分钟后全部失败于 0x8A15005E

      → WingetInstallBatchOperation
          ├─ 逐个：已完成索引 + 别名判重 → InstallAsync（带 --source winget，可选 --location）
          ├─ 单失败：继续下一个，记 FailedPackages（不整批回滚、不停）
          ├─ 错误码分流：
          │   0x8A150002 → 交互安装
          │   0x8A150012 → 补 msstore 源后重试（先不带 --source，再带 --source winget）
          │   0x8A150011 → 清缓存重试
          │   0x8A150014 → 源里找不到该包：刷新索引重试 → 仍无则 source reset 后再试
          │   0x8A15005E → 源索引校验失败：source reset + update 后重试；仍失败给出升级指引
          └─ 结束：已装 N 个 / 失败 M 个，失败项可一键只重试失败
```

> **source 子命令写法（易踩坑）**：新版 winget 不接受 `source update winget`（位置参数）与 `source update/reset --accept-source-agreements`，正确写法是 `source update --name winget --disable-interactivity`、`source reset --name winget --disable-interactivity`；写错会静默失败并表现为"源已切换但搜不到包"（`0x8A150014`）。`source reset` 还需要管理员权限（FreshStart 主程序是 requireAdministrator，正常路径没问题）。软件页的「更新源索引」按钮走 `source update --disable-interactivity`（刷新全部源）。

> **自定义安装路径**：软件页可填/选一个目录，非空时透传 `winget install --location <路径>`；仅对支持该参数的安装器生效（MSI / Inno / ZIP），多数 EXE bootstrapper 会忽略或报错。无人值守 `install` 步骤用 `Value` 字段传路径。

> 源策略：只用**微软官方源**（0.28.1 起移除国内镜像）。国内镜像只镜像索引、安装包仍走厂商原始地址，对下载速度无帮助，反而会因索引滞后导致 `0x8A150014`。

> **直链兜底（0.34.9）**：`DirectInstallerCatalog` 登记了 5 个高频软件的厂商官方安装包 —— Chrome / 7-Zip / Everything / Bandizip / PotPlayer。`WingetInstallBatchOperation` 在 winget 装某个包失败时，若该包有直链条目就自动改用直链：**强制 HTTPS** → 下载到随机、仅 SYSTEM/Administrators 可写的可信临时目录 → 512 MB 上限 → 可选 SHA256 + **强制 Authenticode 签名校验** → 用登记好的静默参数执行 → 20 分钟超时 → 按 ARP 显示名确认安装结果；无法确认时按失败处理。失败会保留安装器路径供手动运行。Firefox / OBS 没有稳定的国内直链（mozilla CDN 与 GitHub 不可达或过慢），继续依赖 winget。Chocolatey 后端（`ChocolateyService` / `ChocoInstallBatchOperation`）代码保留但未注册。

> **0x8A15005E 的真相**：它表示 `SOURCE_DATA_INTEGRITY_FAILURE`（源数据完整性/签名校验失败），不是"证书过期"。现代 winget 源索引换了格式，App Installer **1.6.3482**（旧内置包）无法校验，因此任何安装都会失败。0.27.5 已把内置包换成 **1.29.290**，并内置 `windowsappruntimeinstall-x64.exe`（Windows App Runtime 1.8，提供 `VCLibs.140.00.UWPDesktop ≥ 14.0.33728`；1.29 已不再需要 UI.Xaml）。点「安装 / 更新 winget」即可离线升级。

> 内置包依赖口径（读自 1.29 的 AppxManifest）：`Microsoft.WindowsAppRuntime.1.8 ≥ 8000.616.304.0`、`Microsoft.VCLibs.140.00 ≥ 14.0.33519.0`、`Microsoft.VCLibs.140.00.UWPDesktop ≥ 14.0.33728.0`。安装前会先做依赖预检：不足时用内置 Runtime 安装器 `--force` 补齐并复检，仍不足则立即失败并提示走 Store / Windows 更新（这两个 framework 包无法离线补齐；`aka.ms` 的 VCLibs 直链仍是 14.0.33321）。

**Defender 完整移除**（不可回滚，必须重启）：`DefenderRemovalService` 编排 → 脚本写入 `%SystemRoot%\Temp\FreshStart-defender-removal-<guid>`（随机名 + 仅 SYSTEM/Administrators 可写）→ 一次性 SYSTEM 计划任务（`SystemProcessRunner`）导入 `DefenderRemovalRegistryData` → 停服务/删驱动键/删目录/安全中心/SmartScreen/VBS/任务/启动项 → 残留检查报告；`allowFileFallback` 控制是否 takeown 删文件。

---

## 六、UI 基础设施

- 自绘标题栏 + DWM Mica（`WindowBackdrop`）、`WindowMaximizeFix` 修最大化覆盖任务栏。
- 主题：`Light.xaml` / `Dark.xaml` + `Common.xaml`，强调色与动效时长走 `MotionPreferences`（支持「关闭动画」全链路 0 时长）。
- `ModalHost`：单弹窗、自绘卡片；**点遮罩不取消**（只抖动提示），Esc/按钮取消。
- `ToastHost`：非阻塞提示；`WindowsNotifier` 系统通知（执行开始/完成/失败；命令行模式退出前等待最多 3 秒，避免通知被进程退出吞掉）。
- 分页：`PagedList<T>` 全量加载 + 每次 30 条「加载更多」，筛选变化回第一页。

---

## 七、技术栈与工程路线

| 维度 | 选择 |
|---|---|
| 运行时 | .NET 8（`net8.0` / `net8.0-windows`），C# 12，`Nullable=enable`，隐式 using |
| UI | WPF，无第三方 UI 库；自绘控件（ToggleSwitch/ScoreRing/Sparkline），CommunityToolkit.Mvvm 源生成 |
| 系统集成 | Registry API、WMI、`netsh`/`sc`/`powercfg`/`pnputil`/`dism`/`winget` 子进程、`PowerShellRunner`（隐藏、超时、异步、区分取消与超时）、一次性 SYSTEM 计划任务 |
| 分发 | Launcher NativeAOT + Host 单文件 ReadyToRun、自包含 x64、单一 UAC |
| 发布 | `build\publish.ps1`：clean→publish Host→publish Launcher(AOT，失败降级单文件)→拷文档/壁纸→可选 Authenticode 签名→生成主 ZIP；存在 winget 资源时额外生成独立离线附加包与 `.sha256` |
| 版本 | 单一来源 `Directory.Build.props`；侧边栏动态读 Host 产品版本 |
| 可观测 | `Log`（按天滚动，保留 14 天）、`StartupProbe`（启动各阶段埋点）、操作审计 jsonl、执行中心/评分/残留清理 HTML+JSON 报告 |
| 数据目录 | `%AppData%\FreshStart\`：`backup\`、`reports\`、`history\operations.jsonl`、`presets\`、`state\baseline\baseline.json`、`cache\`、`logs\`、`settings.json`；提权脚本与 Defender 移除日志在 `%SystemRoot%\Temp\FreshStart-*` |
| 隐私 | 完全离线，不采集不上传；外联仅：用户显式触发的 winget（官方源 + 依赖下载）、Ping、局域网互传 |

**约束（工程红线）**
1. 任何写入必须经 `OperationRunner`；新注册表根必须同步 `RegistryPathGuard`。例外见第二节末尾（清理/残留/历史回滚/托盘清理）。
2. 不可回滚操作必须 `CanRollback=false` 并在 UI 明示风险 + 二次确认。
3. 启动路径禁止：注册表批量遍历、WMI、进程全量枚举、磁盘扫描、反射扫描（首次基准采集已放线程池）。
4. 备份失败 → 中止，不允许「先写后补备份」。
5. 回滚用 `CancellationToken.None`，不允许被取消打断。
6. `DescribeChanges()` 只读且快速：禁止在 UI 线程同步等 PowerShell/WMI。

**扩展新模块的标准 5 步**：`Modules/<X>/` 建 ViewModel+View+Operation → `NavCatalog` 打开条目 → `CompositionRoot` 工厂注册 → 写操作走 `OperationBase` + `OperationRunner` → 需要新注册表根则改 `RegistryPathGuard` 并写风险说明。

---

## 八、当前边界 / 待办

- **QQ（NT）安装**：`0x8A150012` → `0x8A15005E` 未闭环（见第五节计划）。
- **Defender 完整移除**：服务/驱动键与目录需重启后复查清理，报告已明确残留项。
- **不可回滚项**：Defender 移除、Appx 强制卸载、OneDrive 移除、批量 winget 安装、软件卸载、清理/驱动导出——只做「成功保留 + 失败重试」，不整批回滚。
- **历史回滚**：只覆盖注册表值/键与服务启动类型；文件、任务、Appx、驱动条目跳过且仅计数。
- **不走 `OperationRunner` 的 4 条路径**：清理、残留清理、历史回滚、托盘清理——有二次确认 + 共用全局写锁，但没有独立备份快照、不写 `operations.jsonl`。
- **跨机方案**：电源计划按 GUID→名称→标准 GUID 创建；驱动/软件步骤找不到时跳过并写报告。
- **分发**：无代码签名时 SmartScreen/企业分发会提示；winget 离线组件通过独立 Release 附加包分发，不进入源代码仓库。
