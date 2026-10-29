# 桌面侧栏（DeskWidgets）

Windows 右侧吸附小窗：主题笔记、待办日历、川大邮箱、Cursor Agent 大模型。贴右缘展开，移开收起；可钉住。

## 功能

| 页签 | 快捷键 | 说明 |
|------|--------|------|
| 主题笔记 | Alt+1 | 本地主题笔记 |
| 待办事项 | Alt+2 | 日历 + 待办，可同步 Microsoft To Do |
| 收发邮件 | Alt+3 | IMAP/SMTP（默认 mail.scu.edu.cn） |
| 大模型 | Alt+4 | 调用本机 / WSL 的 `cursor-agent`，WebView 时间线展示工具步骤 |

数据目录：`%APPDATA%\DeskWidgets\desk.db`  
Agent 工作区：桌面 `智能体空间`（欢迎语会列出 `~\.cursor\skills`）

## 技术

- .NET 8 + WPF
- SQLite、WebView2、MailKit
- 托盘：WinForms `NotifyIcon`

## 运行

```bat
dotnet run
```

发布：

```bat
dotnet publish -c Release -r win-x64 --self-contained false -o publish
publish\DeskWidgets.exe
```

## 托盘

展开 / 收起 / 开机自启 / 退出
