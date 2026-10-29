# 桌面侧栏（DeskWidgets）

Windows 右侧吸附小窗：主题笔记、待办日历、网安热点、川大邮箱、Cursor Agent 大模型。贴右缘展开，移开收起；可钉住。展开后各页签统一宽度。

## 功能

| 页签 | 快捷键 | 说明 |
|------|--------|------|
| 主题笔记 | Alt+1 | 本地主题笔记 |
| 待办事项 | Alt+2 | 日历 + 待办，可同步 Microsoft To Do |
| 收发邮件 | Alt+3 | IMAP/SMTP（默认 mail.scu.edu.cn） |
| 网安热点 | Alt+4 | Solidot / IT之家 / 蓝点网 / 开源中国 / 少数派 + 嘶吼 / FreeBuf / Krebs + 先知 / Netlab / 玄武；本地缓存，约每小时拉取累加未读；点开后从热点消失，可收藏；**↓** /「下一条」打开下一条 |
| 大模型 | Alt+5 | 调用本机 / WSL 的 `cursor-agent`，WebView 时间线展示工具步骤（另：Alt+Space 聚焦输入） |

### 全局 / 通用

| 操作 | 快捷键 |
|------|--------|
| 展开 / 收起侧栏 | 连按两下 Esc |
| 打开大模型并聚焦输入 | Alt+Space |
| 钉住 / 取消钉住 | F1 |
| 收起侧栏 | Esc（输入框内：笔记一次收起；其它多为先失焦再收起） |

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

展开 / 收起 / 大模型输入 / 开机自启 / 退出
