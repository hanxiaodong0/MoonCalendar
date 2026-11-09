# 拾光日历

参考你提供的界面制作的原生 Windows 桌面日历：保留深色背景、蓝色今日圆点，当月日期白色高亮，相邻月份日期灰色，默认显示天津天气。当前版本 1.1.1。

<img src="docs/screenshot.png" width="434" alt="拾光日历：固定六行的深色日历界面" />

## 使用

从源码运行下方构建命令后，打开生成的 `dist/MoonCalendar.exe` 即可使用。建议保留同目录的配置文件和许可证。源码仓库不包含生成的 EXE 和 ZIP。

适用 Windows 10 / 11，需要 .NET Framework 4.8；已在 Windows 11 build 22631 上验证可运行。无需安装 Node.js、Python 或另外的 .NET SDK，不需要管理员权限。

- 时间：每秒更新，使用北京时间 UTC+8，可切换 12 / 24 小时制。
- 天气：默认天津，约每 20 分钟更新；点击天气区域可刷新，悬停可查看体感温度、湿度、风速、当日温度范围和数据时间。
- 农历：显示农历月日、干支生肖、闰月、传统节日及二十四节气。
- 放假调休：用绿色“休”标记假期、橙色“班”标记补班；内置 2025—2027 年数据，联网时尝试更新。
- 月份：固定显示六行、42 个连续日期，周一为第一列；上、下月补齐的日期使用灰色，当月所有日期（含周末）使用白色。点击灰色日期切换至该月，点击年月可跳转，支持 1901—2099 年。
- 小窗：拖动顶部或时间区域移动；图钉按钮切换置顶，位置和设置自动保存。
- 托盘：默认关闭窗口后继续运行。双击任务栏右下角托盘图标重新显示，右键可退出；设置中可改为关闭即退出。
- 自启动：在设置中启用“开机自启动”后，登录 Windows 自动驻留托盘。
- 任务栏入口：启用“点击任务栏时间打开拾光日历”，点击任务栏日期/时间会在该屏任务栏上方打开日历；再点一次、点击别处或按 `Esc` 收起。拖动顶部后切换为普通小窗。
- 快捷键：`← / →` 或 `PageUp / PageDown` 切换月份；`Home` 返回今天；`Esc` 关闭设置、月份对话框或任务栏弹窗。

## Windows 日历入口与自启动

设置中的自启动与任务栏入口可以独立开关。退出拾光日历，或者取消任务栏入口选项后，Windows 自带的日历入口会恢复。程序接管鼠标点击任务栏日期/时间的入口，Windows 的其他通知入口和快捷键保留原有行为。

自启动使用当前用户的注册表 `Run` 启动项，符合 [Microsoft 的启动项说明](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)，不需要管理员权限。任务栏入口使用运行期间的鼠标钩子，仅在识别到真实任务栏时钟按钮后拦截该按钮的左键点击；其他区域和右键操作正常传递。识别会定期重做，以适应 Explorer 重启或任务栏变化。钩子按 [Microsoft 的接口约定](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)实现。

已在 Windows 11 build 22631 的双屏环境中识别主屏和副屏的任务栏时间按钮，并实际点击确认能够弹出拾光日历。其他 Windows 版本保留原生时钟 HWND 与常见时钟自动化标识的识别；未识别时不会拦截整个任务栏，仍可从托盘使用。

启用自启动后请保留 EXE 所在目录；移动程序后，在新位置重新保存自启动设置即可更新路径。若 Windows 启动应用设置中人为禁用了此项，需要在那里重新启用。已验证启动项登记、读回校验和 `--startup` 命令；尚未通过重启整台电脑进行验证。

城市设置提供常用城市及城市搜索。搜索得到多个同名地点时，可在下拉框中核对行政区后保存。

断网时，时间和农历可正常使用；天气显示缓存或“暂无天气”，不会用示例温度代替实际数据。未取得年度放假数据时会明确提示，普通周末、纪念日和节气不会自动当成国务院公布的假期。

## 数据与许可

- 天气与城市搜索：[Open-Meteo](https://open-meteo.com/)，[天气接口文档](https://open-meteo.com/en/docs)、[城市搜索文档](https://open-meteo.com/en/docs/geocoding-api)，天气数据标注 CC BY 4.0。当前接入其个人非商业免费接口。
- 农历及节气：[6tail/lunar-csharp](https://github.com/6tail/lunar-csharp)，固定使用 1.6.9，MIT 许可；仅调用历法、节气与节日功能。
- 放假调休：[NateScarlet/holiday-cn](https://github.com/NateScarlet/holiday-cn)，MIT 许可，是整理国务院公告的第三方开源数据源；程序并非直接连接政府 API。原始公告链接保留于 `data/*.json` 的 `papers` 字段。
- [2026 年国务院放假调休通知（政府网站转载）](https://www.beijing.gov.cn/zhengce/zhengcefagui/202511/t20251104_4258873.html)。2027 年数据来源公告见 `data/2027.json`。

初始数据于 2026-11-09 下载，文件校验值和下载地址在 `dependencies.json` 中。每个数据年份更新后，本地缓存保留其原始公告链接。实际放假以国务院最终公告为准。

运行时仅向天气、城市搜索和节假日数据服务发送获取数据所需的请求。城市通过手动选择设定，不调用系统定位，不包含登录或联网遥测。任务栏识别在本机进行，不收集或发送鼠标轨迹。

## 保存位置

用户设置、天气缓存、节假日缓存、接管状态和错误日志位于：

```text
%LOCALAPPDATA%\ShiguangCalendar\
```

配置写入使用临时文件和原子替换。目录无写入权限时，当前会话仍可使用，但可能无法保存设置。

## 开发与构建

源码位于 `src/`，界面使用 C# + WPF。`lib/lunar.dll` 已随源码保存并嵌入最终 EXE，无运行时 NuGet 下载。

在 Windows PowerShell 或 PowerShell 7 中运行：

```powershell
.\build.ps1
```

构建脚本使用 Windows 自带 .NET Framework 编译器，不要求安装 SDK。`MoonCalendar.csproj` 也可用安装了 .NET Framework 4.8 开发工具的 Visual Studio 打开。

自动检查（等待进程完成后读取结果文件）：

```powershell
Start-Process .\dist\MoonCalendar.exe -ArgumentList '--self-test', "$PWD\verification\self-test.json" -Wait
Start-Process .\dist\MoonCalendar.exe -ArgumentList '--ui-self-test', "$PWD\verification\ui-self-test.json" -Wait
Start-Process .\dist\MoonCalendar.exe -ArgumentList '--network-test', "$PWD\verification\network-test.json" -Wait
```

`--self-test` 检查农历、节气、闰月、放假补班、天气缓存隔离及 2388 个月的布局。`--ui-self-test` 会短暂显示测试窗口，验证原生窗口的实际隐藏、反复弹出、遮挡和收起；测试不接管任务栏、不修改个人设置。`--network-test` 使用程序自身的网络实现获取天津天气和搜索城市，需要联网。

`--startup` 从托盘模式启动；重复启动 EXE 会唤回现有窗口。`--integration-status <结果.json>` 可只读检查自启动登记及已识别的时钟范围。`--enable-windows-integration <结果.json>` 启用自启动并保存任务栏接管选项；`--disable-windows-integration <结果.json>` 撤销这两项登记，运行中的实例应随后重启以应用。

`verification/` 保存测试结果，输出文件不纳入版本管理。当前版本的 39 项数据检查和 28 项窗口回归检查已通过。`--capture <绝对路径.png>` 可启动程序并在五秒后导出自身 WPF 界面；使用时先退出已运行的日历。

排查窗口不显示时，可运行 `--window-status <结果.json>` 读取日历窗口的原生可见性、前台状态和遮蔽状态。退出已有实例后，使用 `--startup --trace-window` 启动可将最近 32 条窗口状态变化保存在本机的 `window-status.json`；这些诊断不会上传。普通启动默认不记录此文件。

## 维护

GitHub 仓库：[hanxiaodong0/MoonCalendar](https://github.com/hanxiaodong0/MoonCalendar)。日常更新步骤见 [维护说明](docs/maintenance.md)。
