# 贡献指南

感谢你关注 Diary of Mine。本项目处理日记、日程和工作记录，任何贡献都应优先保护用户数据与已有行为。

## 开始之前

1. 阅读 `README.md`、`docs/architecture.md` 和与修改范围对应的 `docs/ai/` 文档。
2. 对较大的功能、数据格式变化或界面重构，先创建 Issue 说明目标、兼容影响和验收方式。
3. 不要提交真实日记、缓存、日志、转储、绝对个人路径、凭据或其他私人内容。

## 本地开发

项目需要 Windows 和 .NET 9 SDK。可在仓库根目录运行：

```powershell
dotnet restore .\DiaryOfMine.sln
dotnet build .\DiaryOfMine.sln -c Release --no-restore
```

运行桌面端：

```powershell
dotnet run --project .\src\DiaryOfMine.Desktop\DiaryOfMine.Desktop.csproj
```

## 修改原则

- 保持 `Desktop -> Application + Infrastructure`、`Infrastructure -> Application`、`Application -> Domain` 的依赖方向。
- 持久化变更通过 ViewModel、Application Service 和 Repository 完成，不从界面代码直接修改业务 JSON。
- 修改领域字段时同步检查反序列化兼容、仓储、编辑窗口、跨页面刷新和旧版本数据。
- 保留未知 JSON 字段和现有原子写入策略；涉及格式升级时提供明确迁移路径。
- 不把 UI 自动化、一次编译成功或 CI 通过描述为已经完成产品验收。
- 修改功能、数据结构、窗口行为或发布流程时，同步更新受影响文档。

## 提交与 Pull Request

- 每个 Pull Request 聚焦一个清晰目标，避免混入无关格式化或重构。
- 描述用户可见变化、数据兼容影响、验证范围和仍需项目所有者验收的内容。
- 对界面变化附截图；截图必须使用虚构数据并清理个人路径。
- 如果没有适合的自动化测试，请说明完成了哪些静态或编译检查，以及哪些运行检查留给项目所有者。

## 缺陷与安全问题

普通缺陷使用 Bug Issue 模板。可能泄露个人数据、绕过安全边界或造成数据损坏的问题，请遵循 `SECURITY.md`，不要在公开 Issue 中披露敏感细节。
