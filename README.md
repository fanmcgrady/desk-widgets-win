# 桌面侧栏（Windows 原生 WPF）

右侧吸附的日历 + 待办。隐藏时完全移出屏幕（无色条）。鼠标贴右缘展开，移开收起。

## 技术

- .NET 8 + WPF（Windows 原生窗口）
- SQLite（`Microsoft.Data.Sqlite`）
- 托盘：WinForms `NotifyIcon`

数据目录与旧版共用：`%APPDATA%\DeskWidgets\desk.db`

## 运行

```bat
dotnet run
```

或发布后双击 exe：

```bat
dotnet publish -c Release -r win-x64 --self-contained false -o publish
publish\DeskWidgets.exe
```

## 托盘

展开 / 收起 / 开机自启 / 退出
