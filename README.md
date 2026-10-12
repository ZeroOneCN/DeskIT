# DeskIT

DeskIT 是一款**免安装、单目录便携**的 Windows 桌面 IT 运维工具：程序放在 U 盘里，插到员工电脑上就地运行（WPF 界面），用于诊断与修复日常 IT 问题，全部数据写在 U 盘上。

## 技术栈

- C# / **.NET 8** + **WPF**（自包含单文件发布，目标机无需预装运行时）
- 脚本引擎：**Windows PowerShell 5.1**（系统内置，不依赖 PowerShell 7+）
- 仅支持 **Windows 10 1809+ / Windows 11（x64）**
- 离线运行：代码中不得存在任何出网调用

## 目录结构

```
DeskIT/
├─ DeskIT.sln             解决方案
├─ global.json            固定 SDK 8.0.425（rollForward: latestFeature）
├─ .gitignore
├─ README.md
├─ docs/
│  ├─ DESIGN_DeskIT.md    总体架构与实现约束
│  └─ TASK_DeskIT.md      原子任务分解（步骤 2 ~ 步骤 9）
├─ tools/
│  └─ Generate-Manifest.ps1  为发布目录生成 manifest.json（完整性自检清单）
├─ src/
│  ├─ DeskIT.App/         WPF 界面外壳（App 层）
│  ├─ DeskIT.Core/        领域模型与纯逻辑规则（不依赖 Windows API）
│  └─ DeskIT.Windows/     Windows 能力封装（接口与占位实现）
└─ tests/
   └─ DeskIT.Core.Tests/  Core 规则的 xUnit 单元测试
```

- **DeskIT.Core**：风险级别、动作结果、诊断结论四态、会话状态机等枚举，目标机与处理记录骨架，以及“按风险排序且需重启动作排最后”“成功但需重启 ⇒ 倾向待重启生效”等可测试的纯函数规则。
- **DeskIT.Windows**：PowerShell 5.1 执行器接口、介质可写性探测接口等 Windows 能力封装的接口与占位实现。
- **DeskIT.App**：最小可运行外壳，引用 Core 与 Windows。

## 构建 / 测试 / 发布

```powershell
# 构建（Release）
dotnet build DeskIT.sln -c Release

# 运行单元测试
dotnet test tests\DeskIT.Core.Tests\DeskIT.Core.Tests.csproj -c Release

# 自包含单文件发布（win-x64）
dotnet publish src\DeskIT.App\DeskIT.App.csproj -c Release -p:DeskITPortable=true
```

> 发布相关的 `RuntimeIdentifier / SelfContained / PublishSingleFile` 仅在传入 `-p:DeskITPortable=true` 时生效，不会影响 `dotnet build` 的行为与输出。

生成完整性自检清单（发布后执行，清单需与可执行文件同目录）：

```powershell
dotnet publish src\DeskIT.App\DeskIT.App.csproj -c Release -p:DeskITPortable=true -o .\artifacts\publish
powershell -ExecutionPolicy Bypass -File tools\Generate-Manifest.ps1 -PublishDirectory .\artifacts\publish -Version 1.0.0
```

## 已实现模块

| 步骤 | 模块 | 状态 |
|------|------|------|
| 1 | 工程骨架（.NET 8 + WPF + xUnit） | 已完成 |
| 2 | M1 便携启动与运行环境 | 已完成 |

**M1（步骤 2）**：启动页呈现三块内容——启动自检结果（程序完整性、数据目录可写、介质可用空间、目标系统版本、当前权限、并发实例）、当前权限与提权入口（管理员提权 / 只读回退）、本次能力摘要；并实现高级模式的介质级开关（随 `data/operators.json` 保存，开启需二次确认）。
- 判定规则集中在 [StartupRules.cs](src/DeskIT.Core/StartupRules.cs)（纯逻辑、可单测）：空间 200 MB / 500 MB 阈值、系统版本支持范围、完整性结论、能力推导。
- Windows 能力封装见 [src/DeskIT.Windows](src/DeskIT.Windows)：权限检测、提权启动、单实例守卫、完整性校验、目录读写探测、卷空间、系统版本（`RtlGetVersion`）。
- 高级模式开关的主密码验证将在介质加密（M8）落地后接入。

## 关于需求文档

本项目的需求文档（PRD）**不纳入本仓库**，也不随源码分发。仓库内仅保留实现所需的架构设计与任务分解文档（见 `docs/`）。