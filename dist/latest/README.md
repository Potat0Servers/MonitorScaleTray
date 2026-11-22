# 屏幕缩放 · MonitorScaleTray

Windows 10/11 x64 多显示器缩放自动控制工具。使用系统自带的 .NET Framework 4.x，无需 Python 或管理员权限。

## 下载与运行

下载仓库中的 `MonitorScaleTray-win-x64.zip`，解压后双击 `MonitorScaleTray.exe`。也可以直接运行 `dist/latest/MonitorScaleTray.exe`。

左键点击任务栏右侧的显示器图标打开管理窗口；图标可能位于隐藏图标区域。关闭窗口后继续在托盘运行。右键菜单提供：

- 开机启动：勾选表示已启用，每次打开菜单都会同步实际设置。
- 打开窗口。
- 退出程序。

替换旧版前先保存编辑并退出旧程序。开启自启动后请保持 EXE 路径不变；移动后应重新关闭并开启自启动。

## 配置显示器

1. 点击“刷新检测”，勾选要添加的活动显示器。
2. 双击列表中的名称或缩放值进行编辑，也可以手动新建条目。
3. 点击“保存并应用”，保存全部条目并应用到匹配的活动显示器。
4. 使用 Ctrl / Shift 多选，批量设置缩放或删除条目。离线显示器的配置会保留。

支持标准档位：100、125、150、175、200、225、250、300、350、400、450、500%。实际可用范围由 Windows、显示器和分辨率决定；不支持的值会提示错误。

## 自动纠正

- 启动时立即对已连接且匹配配置的显示器强制应用一次。
- 收到显示器插拔、显示设置切换、系统解锁或睡眠恢复事件后立即检查；至少一个匹配显示器不符合配置时，强制应用当前配置。
- 全部匹配时不写入。事件后约 2 秒和 6 秒再次复查，用于处理显示器尚未就绪或系统稍后重置缩放的情况。
- 合并短时重复通知，防止自身修改缩放产生循环；同时每两秒检查一次活动显示器拓扑，补充漏报。

自动应用使用已保存的配置，窗口中未保存的编辑不会被自动应用或覆盖。程序不会修改显示分辨率、排列或输出模式。

## 外观

“外观”菜单支持浅色、深色、自动（跟随 Windows 应用模式），选择会单独保存。采用 Windows 11 风格配色，切换时更新窗口、列表、菜单和标题栏。系统消息框和通知仍由 Windows 绘制。

## 本地数据

数据保存在 `%LOCALAPPDATA%\MonitorScaleTray`，不随软件上传：

| 文件 | 内容 |
| --- | --- |
| `config.json` | 显示器名称、百分比及设备路径绑定 |
| `appearance.json` | 深浅或自动模式偏好 |
| `*.bak` | 上一版配置备份 |
| `activity.log` | 自动触发与应用结果，约 1 MB 后清理 |

配置示例：

```json
{
  "displays": { "显示器名称": 150 },
  "devicePaths": {}
}
```

`displays` 保存名称与百分比。检测添加的显示器还会写入 `devicePaths`，用于区分同型号设备。手动新建时名称必须与检测名称一致；同名且未绑定的设备不会被自动应用。端口或驱动改变导致设备路径变化时，请重新检测并清理旧条目。

写入采用临时文件替换和备份。损坏配置会阻止启动并保留原文件。手动编辑文件前请退出程序。删除条目不会恢复该显示器原来的 Windows 缩放值。

## 兼容性与验证范围

- 使用前关闭 Windows **自定义缩放**，按系统提示注销并重新登录。系统下拉列表中的 200% 可以使用；全局“自定义缩放”输入的 200% 属于不同模式。
- 复制模式下共享同一个显示源的屏幕无法设置相互冲突的比例，程序会提示并跳过冲突配置。
- 逐屏缩放依赖未公开的 Windows DisplayConfig 请求类型；Windows 更新、驱动和远程桌面环境可能影响兼容性。程序会保留错误原因。
- 已验证配置读写、设备匹配、深浅外观，以及模拟事件下的自动应用、重试和重复通知处理。
- 真实 HDMI 插拔、锁屏解锁、多个活动屏幕、仅外接屏幕切换、实际比例变更和登录后自启动仍需在目标设备验证。

程序无网络通信。

## 构建

在 Windows 项目目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

使用系统自带的 .NET Framework C# 编译器生成 `dist/latest/MonitorScaleTray.exe`。可通过 `-OutputPath` 指定其他输出路径。构建前退出正在使用该输出文件的程序。

只读诊断与自检（将示例输出路径替换为存在的目录）：

```text
MonitorScaleTray.exe --diagnose C:\path\diagnostics.txt
MonitorScaleTray.exe --self-test C:\path\test-results.txt
MonitorScaleTray.exe --show
```

源码位于 `src`；自检逻辑位于 `src/SelfTest.cs`；图标位于 `assets`。

## 实现参考

- [Microsoft QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- [Microsoft DPI 与注册表设置](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dpi-related-apis-and-registry-settings?view=windows-11)
- [逐屏缩放请求结构研究](https://github.com/lihas/windows-DPI-scaling-sample)：DisplayConfigGetDeviceInfo / DisplayConfigSetDeviceInfo 的 -3 / -4 请求类型。
- [Microsoft Win32 深浅主题](https://learn.microsoft.com/windows/apps/desktop/modernize/apply-windows-themes)
- [Microsoft SystemEvents](https://learn.microsoft.com/dotnet/api/microsoft.win32.systemevents)
