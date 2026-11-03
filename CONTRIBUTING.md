# Contributing

感谢参与 FreshStart。这个项目会修改 Windows 系统设置，因此提交必须优先保证可回滚、可审计和最小权限。

## 开发流程

1. Fork 仓库并从 `main` 创建主题分支。
2. 安装 .NET SDK 8.0.426 与 Windows 11 SDK。
3. 运行 `dotnet build FreshStart.sln -c Release`。
4. UI 改动请使用 `-p:DevNoElevation=true` 构建，并附上浅色、深色及最小窗口尺寸截图。
5. 提交 Pull Request，描述改动、风险、回滚方式和验证步骤。

详细说明见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

## 代码约束

- 所有持久化系统修改必须经过统一操作管线，并说明是否支持回滚。
- 注册表访问必须经过路径白名单和边界校验。
- 禁止通过外部输入拼接 PowerShell 命令；参数必须使用结构化 `ArgumentList`。
- 新功能不得引入遥测、后台上报或未经说明的网络请求。
- 不要提交 `bin/`、`obj/`、`publish/`、`shots/`、node_modules 或微软 winget 安装包。
- 不要提交个人路径、令牌、密码、用户日志或其他敏感数据。

## UI 验收

界面改动至少检查：

- 浅色和深色主题
- 980×640 最小逻辑尺寸
- 侧边栏展开与收起
- 搜索框占位文字和输入文字
- Toast、模态框和底部操作区无遮挡

## Commit 建议

使用清晰的提交前缀，例如：

```text
feat: add workflow schedule preview
fix: prevent text clipping in navigation search
docs: clarify release asset preparation
```
