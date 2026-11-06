# Codex Auto Continue（Codex 自动续接）

面向 Windows 的 Codex 桌面辅助程序，提供暗黑会话面板、可恢复异常的续接调度和 Webhook 通知。

**当前版本：0.1.0-preview，可运行的原生 WPF 测试版。** 已实现本机 Codex Desktop 会话读取、恢复策略、消息发送器、通知、持久化与 EXE 打包。**当前机器的桌面后端没有暴露已验证的控制端点，因此尚未验证桌面任务能够自动恢复，不能保证无人值守续跑。** CLI 接受消息只会显示“待确认”，观察到真实执行后才确认恢复。

## 运行与构建

[下载便携 ZIP](https://github.com/tvcmall-dev/codex-auto-continue/releases/download/v0.1.0-preview/CodexAutoContinue-0.1.0-preview-win-x64.zip) · [单独下载 EXE](https://github.com/tvcmall-dev/codex-auto-continue/releases/download/v0.1.0-preview/CodexAutoContinue.exe) · [版本说明与校验值](https://github.com/tvcmall-dev/codex-auto-continue/releases/tag/v0.1.0-preview)

- Windows 10/11 x64，.NET Framework 4.8。
- 便携包解压后运行 `CodexAutoContinue.exe`，无需管理员权限。
- 从源码构建需要 .NET SDK 与 .NET Framework 4.8 Developer Pack，在仓库根目录运行 `./build.ps1`。
- 构建脚本先运行测试，再在 `dist` 中生成 EXE 便携目录与 ZIP。
- 无第三方运行时包依赖；SQLite 使用系统自带的 `winsqlite3.dll`，无需 Node.js、Python 或单独的 API Key。

完整操作步骤、状态说明及排查方式见[使用说明](docs/使用说明.md)。

## 已实现功能

以下为原生 WPF 界面截图，图中使用明确标记的虚构测试数据；正式启动不会加载示例会话。

![原生 WPF 暗黑面板](docs/images/dashboard-wpf.png)

- 原生 WPF 暗黑三栏面板、矢量状态插画、搜索筛选、会话详情和事件记录。
- 读取最近 120 个未归档的本机桌面主会话；排除子会话与扩展来源。
- 单会话监控默认关闭，可分别开启；提供全局暂停、关闭到托盘和可选开机启动。
- 中文（默认 `继续`）、英文（`continue`）、自定义续接消息；保存后统一用于手动与自动续接。
- 仅对监测后新确认的可恢复终态失败调度；默认 30 秒起指数等待，最多 5 次，后续自动失败沿用同一次异常的次数。
- 发送前持久化并重新检查目标；不确定的投递保持待确认，重启后核对实际终态，防止重复消息。
- 钉钉、飞书、企业微信和通用 Webhook；支持平台签名、真实手动测试、事件订阅、去重与最多 3 次投递。
- SQLite 保存状态与历史；完整 Webhook URL 和密钥使用 Windows 当前用户 DPAPI 加密。

![续接消息设置](docs/images/settings-wpf.png)

## 真实接入边界

当前适配器只读 Codex 自己维护的 `state_*.sqlite` 和 `thread_history_*.sqlite` 事件投影。启动先建立基线，历史失败不会自动重发。历史 `inProgress` 不直接视为正在执行；需观察到新的输出或工具进展。超过 2 分钟没有新执行证据时，状态转为待确认，不因此触发续接。

续接使用现有认证的 `codex queue --thread <ID> --message <内容> --resume`，保持原会话身份。当前 Windows 桌面后端以 stdio 运行，未找到可连接的现有控制端点；CLI 可能提示恢复失败但仍返回 0。因此，入队、退出码和新轮次开始均不代表已恢复，程序必须等到实际执行证据。

额度或速率限制如果没有可靠重置时间，会转为人工关注。内部重试、审批等待、主动停止、正常完成、鉴权或余额等异常不会触发重复续接。程序不会修改 Codex 数据库、认证或全局钩子配置。

## 验证

```powershell
dotnet build tests/CodexAutoContinue.Tests/CodexAutoContinue.Tests.csproj -c Release
.\tests\CodexAutoContinue.Tests\bin\Release\net48\CodexAutoContinue.Tests.exe
.\src\CodexAutoContinue\bin\Release\net48\CodexAutoContinue.exe --smoke-test
```

测试包括恢复状态机、真实格式数据库读取、发送前重查、防重复与重启核对、消息指纹、DPAPI、SQLite、平台签名和 HTTP 200 业务失败。Webhook 自动测试使用内存 HTTP 处理器，不向真实机器人投递；真实测试通知由用户在设置中主动触发。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/CodexAutoContinue/Desktop` | 原生界面与托盘 |
| `src/CodexAutoContinue/Core` | 恢复状态机与失败分类 |
| `src/CodexAutoContinue/Integration` | 本地读取与 CLI 投递 |
| `src/CodexAutoContinue/Infrastructure` | SQLite、DPAPI 设置与状态持久化 |
| `src/CodexAutoContinue/Notifications` | 平台消息与签名 |
| `tests/CodexAutoContinue.Tests` | 无外部服务依赖的行为测试 |

最初的[交互原型](docs/prototypes/dashboard.html)保留为设计参考，使用虚构数据，正式程序不依赖浏览器。[设计方案](docs/superpowers/specs/2026-11-06-codex-auto-continue-design.md)记录了策略与接入验收要求。

## 官方参考资料

- [Codex app-server 文档](https://learn.chatgpt.com/docs/app-server)
- [Codex 生命周期钩子](https://learn.chatgpt.com/docs/hooks)
- [Codex 命令行选项](https://learn.chatgpt.com/docs/developer-commands?surface=cli)
- [钉钉自定义机器人安全设置](https://open.dingtalk.com/document/dingstart/customize-robot-security-settings)
