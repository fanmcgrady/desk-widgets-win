# DesktopBox（桌面分区）

DeskWidgets 的子库：三分区桌面围栏（左上角留空放桌面图标），用 Windows Shell 视图（`IShellView`）显示真实文件夹。

随 `DeskWidgets.exe` 一起启动；配置在「文件」页右侧面板，也读写 `%APPDATA%\DesktopBox\config.json`。

## 默认文件夹

| 区 | 位置 | 路径 |
|----|------|------|
| （空） | 左上 | 桌面图标 |
| 项目区 | 右上 | `桌面\桌面分区\项目区` |
| 待办区 | 左下 | `桌面\桌面分区\待办区` |
| 内存 | 右下 | `X:\` |

自动归类默认关闭（省资源，桌面粘贴不会被搬走）。在设置勾选「新文件归入内存」后，才监视桌面并移入内存区（`autoSort.targetZoneId = todo` → `X:\`）。
