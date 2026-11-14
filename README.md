# 桌面侧栏（DeskWidgets）

Windows 左侧吸附小窗：主题笔记、待办日历、网安热点、川大邮箱、Cursor Agent 大模型、Everything 文件搜索。贴左缘展开，移开收起；可钉住。待办为窄栏，笔记、邮件、大模型和文件搜索同宽，网安满屏。

## 功能

| 页签 | 快捷键 | 说明 |
|------|--------|------|
| 主题笔记 | Ctrl+1 | 本地纯文本笔记（.txt），可用记事本打开 |
| 待办事项 | Ctrl+2 | 日历 + 待办，可同步 Microsoft To Do |
| 收发邮件 | Ctrl+3 | IMAP/SMTP（默认 mail.scu.edu.cn） |
| 网安热点 | Ctrl+4 | 热点 / 收藏 / 源。源可以添加、删除，名单保存在本机；本地缓存，约每 15 分钟拉取；默认源含安全客、绿盟、FreeBuf、先知、Seebug、补天等；点开后从热点消失，可收藏；**→** 下一条，**←** 刷新，**↓**/空格下翻，**↑**/Shift+空格上翻 |
| 大模型 | Ctrl+5 | 调用本机 / WSL 的 `cursor-agent`，WebView 时间线展示工具步骤（另：Ctrl+空格 聚焦输入） |
| 文件 | Ctrl+6 | 左：Everything 搜文件；右：桌面分区（DesktopBox）配置。围栏随本程序一起启动（另：Alt+空格） |

### 全局 / 通用

| 操作 | 快捷键 |
|------|--------|
| 展开侧栏 | 连按两下 Ctrl |
| 打开文件搜索 | Alt+空格 |
| 打开大模型并聚焦输入 | Ctrl+空格 |
| 钉住 / 取消钉住 | F1 |
| 收起侧栏 | Esc |

数据目录：`%APPDATA%\DeskWidgets\desk.db`  
Agent 工作区：桌面 `智能体空间`（欢迎语会列出 `~\.cursor\skills`）

## 目录

| 路径 | 说明 |
|------|------|
| `Services/` | 侧栏业务（笔记、邮件、待办同步、Everything 等） |
| `DesktopBox/` | 桌面四分区围栏（库项目，随主程序启动） |
| `Assets/` | 静态资源 |

## 技术

- .NET 8 + WPF
- SQLite、WebView2、MailKit
- 托盘：WinForms `NotifyIcon`

## 运行

```bat
cd desk-widgets-win
dotnet run -c Release
```

发布：

```bat
dotnet publish -c Release -r win-x64 --self-contained false -o publish
publish\DeskWidgets.exe
```

## 托盘

展开 / 收起 / 大模型输入 / 开机自启 / 退出
