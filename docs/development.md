# 开发、动态预览与调试

## 环境准备

项目目标为 .NET 9。开发环境需要 Windows 10/11、.NET 9 SDK，以及 Visual Studio 2022 的“.NET 桌面开发”工作负载或可用的 `dotnet` CLI。

项目根目录的 `global.json` 以 9.0.301 为最低 SDK，并允许向同一 .NET 9 主版本的较新功能带滚动。执行 `dotnet --version` 应显示 9.x；仅安装运行时不能代替 SDK。

.NET 9 的官方支持将在 2026 年 11 月 10 日结束。开发可以继续使用当前环境，但发布维护计划必须包含升级到仍受支持的 .NET 版本。

## 看着窗口修改界面

1. 打开根目录的 `DiaryOfMine.sln`，启动项目选择 `DiaryOfMine.Desktop`。
2. 使用 Debug 配置，按 F5 启动调试。
3. 打开 `src/DiaryOfMine.Desktop/MainWindow.xaml`。
4. 修改文字、颜色、间距和布局，通过 XAML Hot Reload 在运行窗口中查看变化。
5. 使用 Live Visual Tree / Live Property Explorer 检查控件层级、布局与属性；数据绑定问题查看调试输出和绑定诊断。

Visual Studio 的 XAML Hot Reload 支持 WPF 调试会话；具体可更新内容依工具版本而定。[官方热重载说明](https://learn.microsoft.com/en-us/visualstudio/xaml-tools/xaml-hot-reload?view=visualstudio)

新增文件、修改项目引用、初始化路径或遇到不支持的编辑时，停止调试后重新 F5。C# 支持的代码变更可以通过 Hot Reload 应用；它不会自动重新执行已经完成的启动逻辑。[热重载限制](https://learn.microsoft.com/en-us/troubleshoot/developer/visualstudio/ide/xaml-hot-reload-troubleshooting)

## 调试底层逻辑

在 ViewModel、业务用例和基础设施实现中设断点，触发对应操作后查看调用堆栈、局部变量和异步异常。当前项目已经实现 JSON 仓储、提醒、设置、日志、工作模式和 Windows 集成；调试时应优先从 ViewModel 或 Application Service 进入，不要从界面代码直接修改业务 JSON。

调试持久化、迁移、日志或崩溃场景时只使用虚构内容；Debug 与 Release 的数据路径必须分开。不要将真实私人内容、个人路径、密钥、日志或内存转储复制到源码目录和公开问题报告。

## 命令行入口

在仓库根目录执行：

```powershell
dotnet restore .\DiaryOfMine.sln
dotnet build .\DiaryOfMine.sln -c Release --no-restore
dotnet run --project .\src\DiaryOfMine.Desktop\DiaryOfMine.Desktop.csproj
```

上述是分开的手动命令，运行时不必每次先执行 build。命令行启动提供原生窗口；完整的 WPF XAML 动态调试推荐使用 Visual Studio，不能将普通 `dotnet run` 当作 XAML 热重载。

终止运行：托盘菜单选择“退出”，或在 Visual Studio 停止调试。窗口的关闭按钮和 Esc 只隐藏窗口。

## 验证范围

验证应与修改风险相称：文档和仓库元数据修改检查链接、路径及 YAML/Markdown 结构；普通源码修改至少编译受影响项目；数据结构、迁移、安装器及窗口生命周期修改按照 `docs/ai/` 的对应清单检查。UI、托盘、提醒、升级和卸载行为由项目所有者完成最终运行验收。

编译或 CI 通过不等同于产品验收。本仓库当前没有自动化测试项目，新增可独立验证的业务规则时建议同时补充测试。

## 打包

`tools/package_demo.ps1` 用于发布、生成完整软件目录和 IExpress 安装包。脚本当前带有开发机默认输出路径，公开环境中应显式传入 `-OutputRoot`，不要依赖默认值，也不要将生成的 `artifacts/` 或含用户数据的目录提交到版本控制。
