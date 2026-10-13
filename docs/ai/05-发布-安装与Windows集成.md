# 发布、安装与 Windows 集成

> 维护提示：未来修改功能时候，更新此处详细信息。

- 最后核对日期：2026-09-14
- 当前结论：v1.1.1 安装器支持根据 HKCU 卸载注册或 App Paths 自动识别上一版安装目录并原地更新；升级不接触用户数据与设置。2026-09-14 已用完整的失焦/最小化/关闭转悬浮及编辑保护逻辑重新生成 win-x64 安装包与完整软件，并覆盖既定交付目录，尚待项目所有者安装验收。

## 1. 应用发布形态

Desktop 项目：

- AssemblyName / exe：`DiaryOfMine.exe`
- Product：`Diary of Mine`
- Version：`1.1.1`
- Framework：`net9.0-windows`
- 发布脚本默认 Runtime：`win-x64`
- 目标：self-contained、single-file 的 Release 应用

`tools/package_demo.ps1` 的职责：

1. 清理脚本专用 staging 和输出子目录。
2. `dotnet publish` 生成应用 exe。
3. 使用系统 .NET Framework C# 编译器编译 WinForms `InstallerHost.cs` 和 `UninstallerHost.cs`。
4. 生成“完整软件”目录（应用、卸载器、说明）。
5. 用 IExpress 将安装宿主、卸载宿主和应用打为单个 Demo Setup。

除非用户明确要求打包，本流程不得因为普通源码修改自动运行。

## 2. 安装器行为

`packaging/InstallerHost.cs` 提供 WinForms 安装向导：

- 用户可编辑安装目录，且禁止直接安装到盘符根目录。
- 检测到上一版安装时，优先读取卸载注册的 `InstallLocation`，App Paths 作为后备；确认目标 exe 存在后锁定原安装路径并显示更新模式。
- 更新时先在安装目录暂存新版应用与卸载器，再替换旧文件；替换失败则回滚已提交的二进制。该流程不读取、复制或删除用户数据与设置。
- 安装 `DiaryOfMine.exe` 与 `Uninstall-DiaryOfMine.exe`。
- 桌面快捷方式：可选，默认勾选。
- 任务栏：可选，默认勾选；通过快捷方式和系统 Pin verb 尽力处理。
- 完成后启动：可选，默认不勾选；安装前捕获勾选值，只有为 true 才 `Process.Start`。
- 开始菜单：无论桌面选项如何都创建 `Diary of Mine.lnk`，用于 Windows 搜索发现。
- 注册 `HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\DiaryOfMine.exe`，默认值指向 exe，`Path` 指向安装目录。
- 注册 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DiaryOfMine`，包含 DisplayName、版本、安装位置、图标、卸载命令等。
- 安装完成后释放工作目录，并调度脚本等待安装器进程退出后删除 IExpress 临时解包目录和清理脚本本身。

注意：Windows 任务栏“固定”动作可能被系统版本或策略限制；代码应把它视为尽力操作，不能影响主体安装成功。

## 3. 卸载器行为

`packaging/UninstallerHost.cs`：

1. 请求用户确认（`/quiet` 可跳过交互）。
2. 结束/等待应用进程退出。
3. 删除 HKCU Run、Uninstall、App Paths 注册。
4. 删除桌面、任务栏、开始菜单快捷方式。
5. 删除应用 exe 和安装目录中除当前卸载器外的内容。
6. 在 `%TEMP%` 生成一次性 cleanup cmd，等待卸载器 PID 退出后删除卸载器自身、空安装目录和脚本自身。

卸载器默认**不删除** `%LOCALAPPDATA%\DiaryOfMine`，用户日程、日记、工作记录和设置可保留供重装使用。若未来提供“同时删除个人数据”，必须单独复选确认，且不能默认勾选。

## 4. 应用自身 Windows 注册

`Desktop/Services/WindowsRegistrationService.cs` 在启动/设置保存时运行：

- 根据 `StartWithWindows` 写/删 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DiaryOfMine`，命令含 `--startup`。
- 为直接运行版本维护基本卸载/显示信息。
- 安装器写入托管标记后，应用自身注册不得覆盖真实 `Uninstall-DiaryOfMine.exe` 卸载命令。
- `--unregister` 启动参数只执行注销后退出。

Debug 和 Release 应避免互相污染注册。检查注册逻辑时同时关注安装版、自运行版和开发版。

## 5. 2026-09-12 源码修复记录

针对 Demo 测试报告，源码已处理：

| 问题 | 源码处理 | 发布物状态 |
| --- | --- | --- |
| 卸载器 exe 残留约 64 KB 和空目录 | 卸载器退出后由临时 cmd 等待 PID、自删、删目录、自删脚本。 | 未重打包验证 |
| 安装临时解包目录残留约 171 MB | Installer Host 启动即识别解包目录并调度退出后递归清理。 | 未重打包验证 |
| 未勾选仍自动启动 | 在安装前保存 `_launch.Checked`，仅 true 时启动。 | 未重打包验证 |
| 任务栏图标点击过度敏感隐藏 | `WindowBehaviorCoordinator` 自动悬浮检查跳过 `MainWindow.WindowState == Minimized`。 | 应用源码完成，待 UI 验收 |
| Windows 搜索找不到 | 始终写开始菜单快捷方式，并注册 `App Paths`；卸载时同步移除。 | 未重打包验证 |

只有重新执行打包脚本并安装新产物后，才能把状态改为“已打包”；只有在干净/目标 Windows 环境完成测试后，才能改为“已验收”。

## 6. 隐私发布检查

发布前应只做有针对性的检查：

- staging 输入只来自 `dotnet publish`、`packaging` 编译产物和静态说明。
- 输出中不得出现 `userdata`、`cache`、`logs`、`DiaryOfMine.*.json`、`calendar.dat`、用户图片或开发截图。
- 不从 `%LOCALAPPDATA%\DiaryOfMine`、`%LOCALAPPDATA%\DiaryOfMine.Dev` 或用户自定义缓存目录复制任何内容。
- 发布说明不得包含实际日程标题、日记正文、工作目标等测试输入。
- 完整软件目录和安装包应来自同一次 publish，避免 exe 与卸载器/说明版本错配。

## 7. 下一次明确打包任务的最小流程

1. 确认根 README 更新日志中的版本与源码状态。
2. 运行 `tools/package_demo.ps1` 生成新输出；不要复用旧 staging。
3. 静态检查输出文件名和目录，不做过度模拟。
4. 由项目所有者在目标机验收：自定义路径、桌面选项、任务栏选项、开始菜单搜索、未勾选不启动、勾选启动、临时目录清理、卸载自删除、用户数据保留。
5. 验收后把 README 和本文件状态从“源码完成”更新为“已打包/已验收”，并记录产物版本或文件名。
