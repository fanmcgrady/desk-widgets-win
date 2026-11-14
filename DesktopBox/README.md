# DesktopBox（桌面分区）

DeskWidgets 的子库：四分区桌面围栏，用 Windows Shell 视图（`IShellView`）显示真实文件夹。

随 `DeskWidgets.exe` 一起启动；配置在「文件」页右侧面板，也读写 `%APPDATA%\DesktopBox\config.json`。

## 默认文件夹

| 区 | 路径 |
|----|------|
| 项目区 | `桌面\桌面分区\项目区` |
| 教学区 | `桌面\桌面分区\教学区` |
| 待办区 | `桌面\桌面分区\待办区` |
| 内存 | `X:\` |

桌面新文件默认移入内存区（`autoSort.targetZoneId = todo`）。
