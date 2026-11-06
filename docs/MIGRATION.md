# Environment migration

环境克隆是实验功能，用于把选定软件与开发环境封装为本地 `.fclone` 包，并在另一台
Windows 11 设备上离线恢复。

## Supported items

| 项目 | 迁移内容 |
|---|---|
| 微信 | 默认 `Documents\WeChat Files` 或 `Documents\xwechat_files`；可添加自定义数据目录 |
| QQ | 优先读取 `%APPDATA%\QQ\auth` 中的实际数据路径；配置不可用时回退扫描固定盘中的 `Tencent Files` / `nt_qq` 数据根目录 |
| 飞书 | 默认 `Documents\Feishu`、`Documents\Lark`；可添加自定义下载目录 |
| Git | Git 安装目录、PATH 中相关条目和安全的 Git 环境变量 |
| Python | Python 安装目录、PATH 中相关条目和安全的 Python 环境变量 |
| Node.js | Node.js 安装目录、npm 全局目录、PATH 中相关条目和安全的环境变量 |
| Java | `JAVA_HOME` 指向的 JDK、PATH、`JAVA_HOME` / `JDK_HOME` 和相关环境变量 |
| Rust | `.cargo` 工具链、`.rustup` 数据、PATH、`CARGO_HOME` / `RUSTUP_HOME` |
| .NET SDK | `dotnet` 安装目录、SDK 版本、PATH 和 `DOTNET_ROOT` 等安全环境变量 |

开发工具会枚举本机的全部已安装版本，而不是只取 PATH 中第一个可执行文件。Java
会合并 JavaSoft / Eclipse Adoptium / Microsoft / Amazon / Azul 等注册表和常见安装
目录；Python 会合并 PythonCore 注册表、`py -0p`、PATH 与常见安装根。每个不同安装根
都会生成独立的版本化克隆项，可以在页面上一键选择全部开发环境后批量生成同一个包。
Node.js 的 `winget` 精确版本不可用时会回退到 `nodejs.org` 对应版本 MSI，并使用官方
`SHASUMS256.txt` 校验后再写入克隆包。

生成克隆包前会重新扫描并执行链路预检，按 Winget 原厂、本地原厂安装器和待准备项目
分组。弹窗会说明导出阶段是否联网、安装器来源、数据安全边界和无法打包的项目；只有
用户确认后才会选择保存位置并开始提取。

社交软件安装器优先从源机器下载并写入克隆包；也可以手动指定本地安装器目录。目标机器
恢复时不访问软件官网。

微信使用 `Tencent.WeChat.Universal` 的当前安装介质，避免误装旧版
`Tencent.WeChat` 导致新数据无法打开。

## Explicit exclusions

- 聊天软件登录状态、Cookie 和认证令牌
- 浏览器密码、Token 和凭据
- 驱动、内核组件与机器绑定许可证
- 数据库实例、Docker 虚拟磁盘和其他大型机器状态
- 未提供离线安装介质的商店应用

社交软件记录能否直接打开还取决于软件版本、账号加密和厂商兼容策略。登录状态不会迁移。
克隆包本身不加密，可能包含私人聊天记录与下载文件，请存放在受保护的磁盘或加密容器中。

## Package layout

```text
manifest.json
checksums.json
software/<item>/installers/*
development/<item>/installers/*
data/<item>/<n>/*
```

`manifest.json` 记录版本、安装参数、原始数据路径，以及分别按系统级和用户级保存的
PATH 条目与安全环境变量。
`checksums.json` 为恢复前完整性校验提供 SHA256。

## Restore safety

- 恢复前校验全部文件。
- 已存在的聊天数据目录会移动到同级 `.freshstart-backup-*` 目录。
- 原数据盘不存在时，恢复前会要求用户选择替代部署目录；默认建议使用
  `%LocalAppData%\FreshStart\MigrationData`，微信配置中的自定义数据路径会同步改写。
- 开发环境不再复制到 FreshStart 自有目录，而是运行克隆包内对应版本的原厂安装器。
  控制面板中的修复、更改和卸载由原厂安装器提供。
- 原厂安装完成后，FreshStart 再恢复 `.rustup`、Python 用户级包、PATH 和环境变量。
- 上一版遗留的 `FreshStart\Environments` 目录和 `FreshStart.Environment.*`
  接管式控制面板项会在新原生部署时清理。
- 安装器失败不会删除已经恢复的数据。
- 恢复完成后需要用户自行登录聊天软件。
