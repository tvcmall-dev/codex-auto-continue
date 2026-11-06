# Codex Auto Continue（Codex 自动续接）

面向 Windows 的 Codex 桌面辅助工具，用于监控 Codex 会话，在可恢复的网络故障或额度限制导致任务中断后，按策略发送续接消息，帮助任务继续执行。

**当前阶段：方案设计与可交互界面预览。** 原生 WPF 应用及真实 Codex 接入尚未实现。预览使用虚构会话，不会向 Codex 发送消息。

## 桌面应用规划

- 支持 Windows 10/11，x64。
- 使用 C# 和 .NET Framework 4.8 开发。
- 采用原生 WPF 界面与 MVVM 架构，提供状态矢量插画和系统托盘。
- 提供多会话总览、单会话监控开关和事件时间线。
- 使用本地 SQLite 保存重试计划与恢复记录。
- 支持钉钉、飞书、企业微信及通用 Webhook 通知，可在设置中配置接收地址。
- 仅在确认当前执行已因可恢复异常而失败后，触发自动续接。
- 续接消息支持中文（默认 `继续`）、英文（`continue`）和自定义内容。
- 明确区分“消息已入队”和“已确认恢复执行”两种状态。

## 界面预览

在浏览器中打开[可交互面板](docs/prototypes/dashboard.html)即可体验，无需安装依赖或连接外部服务。该页面用于展示原生 WPF 应用的视觉与交互方案，正式桌面程序不依赖此网页运行。

暗黑主题预览支持会话选择、搜索、筛选、监控开关、通知设置和续接消息选择，并演示“发送续接 → 等待确认 → 恢复执行”的状态变化。

Webhook 测试仅为模拟，不会发送网络请求。输入的设置仅保存在当前页面内存中，刷新后清除。状态插画采用内联矢量绘制，可在 WPF 中使用原生几何图形与绘图资源实现。

## 接入方案

计划组合使用 Codex app-server 适配器、可选的生命周期钩子、重试状态机和 CLI 消息发送器。可选的钩子中继程序也使用 .NET Framework 4.8 编写，无需 Python。

真实接入的首个里程碑，需要针对 **Codex 桌面端正在使用的同一后端**，验证以下三项能力：

1. 读取多个现有桌面会话的真实运行状态。
2. 向选定会话的消息队列加入续接消息，保持原会话身份不变。
3. 观察到新的执行轮次与正常执行后，再将会话标记为已恢复。

另行启动 app-server 进程并读取已保存的会话历史，不能证明已接入桌面端的实时运行状态。仅凭 `codex queue --resume` 返回成功退出码，也不能确认任务已经恢复执行。

状态转换、界面交互、重试规则和验收标准详见[设计方案](docs/superpowers/specs/2026-11-06-codex-auto-continue-design.md)。

## 官方参考资料

- [Codex app-server 文档](https://learn.chatgpt.com/docs/app-server)
- [Codex 生命周期钩子](https://learn.chatgpt.com/docs/hooks)
- [Codex 命令行选项](https://learn.chatgpt.com/docs/developer-commands?surface=cli)
- [插件打包与生命周期钩子](https://developers.openai.com/plugins/build/plugins)
