# DeskIT

DeskIT 是一款**免安装、单目录便携**的 Windows 桌面 IT 运维工具：程序放在 U 盘里，插到员工电脑上就地运行（WPF 界面），用于诊断与修复日常 IT 问题，全部数据写在 U 盘上。

## 技术栈

- C# / **.NET 8** + **WPF**（自包含单文件发布，目标机无需预装运行时）
- 脚本引擎：**Windows PowerShell 5.1**（系统内置，不依赖 PowerShell 7+）
- 仅支持 **Windows 10 1809+ / Windows 11（x64）**
- 离线运行：代码中不得存在任何出网调用
- 设备信息采集依赖 `System.Management` 8.0.0（Microsoft 官方包，随程序一并部署，目标机无需额外安装）

## 目录结构

```
DeskIT/
├─ DeskIT.sln             解决方案
├─ global.json            固定 SDK 8.0.425（rollForward: latestFeature）
├─ .gitignore
├─ README.md
├─ actions/               修复动作声明式定义（M4，JSON；随程序一并分发）
├─ packages/              随介质携带的软件包描述文件（M5，JSON；安装程序本体另行同步）
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

- **DeskIT.Core**：风险级别、动作结果、诊断结论四态、会话状态机等枚举；采集分组/快照/档案模型、诊断结论/场景/会话模型、动作定义/执行结果/备份记录模型、软件包描述与安装器归因模型、本地账号与权限快照模型、处理记录/台账/统计与脱敏报告模型；以及“启动自检阈值与能力推导”“机器标识回退与档案归并”“快照保留上限”“按风险排序且需重启动作排最后”“动作风险分级与确认要求、参数绑定转义与结果归类”“诊断结论异常置顶与四态统计”“会话状态机合法迁移”“包完整性判定与版本比较、安装器退出码归因”“账号状态与密码策略解析、UAC 风险描述”“记录关键字段校验与统计、按条件筛选与按机器归并、机器标识冲突检测、姓名遮蔽与凭据掩码、自包含报告构建”等可测试的纯函数规则。
- **DeskIT.Windows**：启动自检能力（权限、提权、单实例、完整性、目录读写、卷空间、系统版本）、设备信息只读采集（WMI / 注册表 / 网络接口 / 卷信息）、档案与快照存储、PowerShell 5.1 执行器、场景化问题诊断（M3）、修复动作库加载 / 前置条件检查 / 统一执行器 / 备份区存储（M4）、软件包库加载与哈希校验 / 安装卸载编排（M5）、账号与权限只读查询（M6），以及台账追加式存储与报告写出（M7）。
- **DeskIT.App**：WPF 界面外壳（M1 便携启动 / M2 设备信息采集 / M3 问题诊断 / M4 修复动作库 / M5 软件安装卸载 / M6 账号与权限 / M7 记录与多机台账），引用 Core 与 Windows。

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
| 3 | M2 设备识别与信息采集 | 已完成 |
| 4 | M3 问题诊断 | 已完成 |
| 5 | M4 修复动作库 | 已完成 |
| 6 | M5 软件安装与卸载 + M6 账号与权限辅助 | 已完成 |
| 7 | M7 记录与多机台账 | 已完成 |
| 8 | M8 介质安全与数据保护 | 已完成 |
| 9 | M9 受控自定义命令与动作沉淀 | 已完成 |

**M1（步骤 2）**：启动页呈现三块内容——启动自检结果（程序完整性、数据目录可写、介质可用空间、目标系统版本、当前权限、并发实例）、当前权限与提权入口（管理员提权 / 只读回退）、本次能力摘要；并实现高级模式的介质级开关（随 `data/operators.json` 保存，开启需二次确认）。
- 判定规则集中在 [StartupRules.cs](src/DeskIT.Core/StartupRules.cs)（纯逻辑、可单测）：空间 200 MB / 500 MB 阈值、系统版本支持范围、完整性结论、能力推导。
- Windows 能力封装见 [src/DeskIT.Windows](src/DeskIT.Windows)：权限检测、提权启动、单实例守卫、完整性校验、目录读写探测、卷空间、系统版本（`RtlGetVersion`）。
- 高级模式开关开启需主密码验证 + 二次确认（M8 落地后接入，见 [MediaVault.cs](src/DeskIT.Windows/MediaVault.cs)）。

**M2（步骤 3）**：设备信息采集页提供“基础采集（≤60 秒，不含软件全量清单）”与“完整采集（含已装软件清单）”两个入口，并按分组展示摘要卡片；采集全程只读，结果写入 U 盘 `data/snapshots/`，不向目标机磁盘写入任何产物。
- 采集分 7 组：系统、硬件、存储、网络、账号与环境、已装软件、安全与更新；其中账号与环境、已装软件、安全与更新可单项关闭（界面取消勾选）。
- 分组并行、组内采集项串行，**每项独立超时（默认 15 秒）**：超时记为“未知（超时）”、权限不足记为“无权限读取”、对象不存在记为“不适用”，均不阻塞其他项。
- 机器标识优先取硬件序列号（`Win32_BIOS.SerialNumber`），自动剔除 `Default string`、`To be filled by O.E.M.` 等占位串；不可用时回退为“主机名 + 首次采集时间”，并在档案中标注标识来源。
- 同一机器多次采集归并到同一档案（提示“该机器此前处理过 N 次”），档案内仅保留最新 5 份快照，历史快照可对比。
- 纯规则见 [CollectionRules.cs](src/DeskIT.Core/CollectionRules.cs) 与 [MachineIdentityRules.cs](src/DeskIT.Core/MachineIdentityRules.cs)；采集实现见 [DeviceInfoCollector.cs](src/DeskIT.Windows/DeviceInfoCollector.cs) 与 [WindowsCollectionItems.cs](src/DeskIT.Windows/WindowsCollectionItems.cs)；落盘见 [SnapshotStore.cs](src/DeskIT.Windows/SnapshotStore.cs)。

**M3（步骤 4）**：问题诊断页把员工口述的模糊现象（“电脑卡”“上不了网”“打印机用不了”）转换成有结论的诊断项，结论只落四态——**正常 / 异常 / 提示 / 未知**；执行状态与结论分离（已执行 / 未执行需管理员 / 不适用），无法判定一律记“未知”并说明原因，绝不误记“异常”。
- 7 个问题场景共 **38 个诊断项**：卡慢、网络、打印与共享、磁盘与存储、启动与系统、软件、账号与权限；可按场景勾选诊断，也可全场景诊断。
- 场景并行、场景内诊断项串行，**每项独立超时（默认 15 秒）**：超时记“未知（超时）”，权限不足的项在只读模式标记“未执行（需要管理员权限）”，均不阻塞其他项。
- 结论按“**异常置顶**”排序（异常 → 提示 → 未知 → 正常），异常与提示项始终可见；每项结论附**原始依据**（数值 / 日志片段）与**建议动作**（关联 M4，需显式勾选才执行）。
- 全部检查均为**本机只读检查**（WMI / 注册表 / 网络接口 / 系统内置只读命令 / PowerShell 5.1 只读查询），不发起外网调用；系统盘空间趋势复用历史快照比较（不足 2 份时如实记“未知”）。
- 诊断结论与依据写入 U 盘 `data/sessions/<会话标识>.json`；会话状态机在编排服务中接线（新建 → 诊断中 → 已结束），非法迁移立即抛错。
- 纯规则见 [DiagnosisRules.cs](src/DeskIT.Core/DiagnosisRules.cs) 与 [SessionStateRules.cs](src/DeskIT.Core/SessionStateRules.cs)；诊断实现见 [ProblemDiagnoser.cs](src/DeskIT.Windows/ProblemDiagnoser.cs) 与 [WindowsDiagnosisItems.cs](src/DeskIT.Windows/WindowsDiagnosisItems.cs)；脚本执行器见 [PowerShell51Executor.cs](src/DeskIT.Windows/PowerShell51Executor.cs)；落盘见 [DiagnosisStore.cs](src/DeskIT.Windows/DiagnosisStore.cs)。

**M4（步骤 5）**：修复动作以**声明式定义**（U 盘 `actions/*.json`）描述“做什么”，由**统一执行器**集中处理参数绑定、权限与前置条件判断、超时 / 取消 / 输出截断（200 行）与结果归类，脚本无需各自实现这些通用逻辑。
- 内置 **29 个动作**，覆盖 7 个场景：网络 6、打印与共享 5、磁盘与存储 6、启动与系统 6、卡慢 2、软件 2、账号与权限 2。
- **风险分级（固有属性，用户不可下调）**：只读（无确认）/ 低（汇总明示）/ 中（汇总确认 + 提示会话可能中断）/ 高（逐项二次确认）/ 不可逆（逐项二次确认 + 输入确认词 `DELETE`）。
- **执行顺序**遵循“按风险升序、需重启的动作排最后”；需管理员的动作在**只读诊断模式下全部禁用**并给出原因。
- 参数以 `{{参数名}}` 引用，绑定时展开为**经转义的 PowerShell 双引号字面量**（转义 `` ` ``、`"`、`$`），防止参数内容被当作脚本执行；缺失必填参数即“已跳过”，不阻断其他动作。
- 前置条件（路径存在 / 服务存在）不满足即“已跳过”并给出原因；执行器统一包装退出码约定（脚本未显式退出时以最后一个外部命令的 `$LASTEXITCODE` 为准，终止性错误记为失败）。
- 每次实际执行的审计与回滚信息写入 U 盘 `data/backup/actions/<执行标识>.json`（含回滚说明与脚本产出的原始数据文件），该目录后续由 M8 统一加密。
- 纯规则见 [ActionRules.cs](src/DeskIT.Core/ActionRules.cs) 与 [ActionOrdering.cs](src/DeskIT.Core/ActionOrdering.cs)；动作定义模型见 [ActionModels.cs](src/DeskIT.Core/ActionModels.cs)；加载 / 检查 / 执行 / 落盘见 [ActionLibrary.cs](src/DeskIT.Windows/ActionLibrary.cs)、[ActionPreconditionChecker.cs](src/DeskIT.Windows/ActionPreconditionChecker.cs)、[ActionExecutor.cs](src/DeskIT.Windows/ActionExecutor.cs)、[ActionBackupStore.cs](src/DeskIT.Windows/ActionBackupStore.cs)；界面见 [MainWindow.xaml](src/DeskIT.App/MainWindow.xaml) 的“修复动作（M4）”页。

**M5（步骤 6）**：软件安装与卸载页把 U 盘 `packages/` 目录里**随介质携带的安装包**部署到目标机，或从目标机卸载；包以 JSON 描述文件声明（安装程序名、静默参数、卸载入口、适配架构、最小可用空间、超时、可选的 SHA-256）。
- **来源必须先验证**：描述文件声明了 `sha256` 且与实际文件一致才允许执行（`校验通过`）；哈希不匹配、文件缺失、未声明哈希三种情况**一律禁止执行**并在界面给出处置说明。
- 首次使用某个包时现算 SHA-256 并提示实际值，便于把哈希补写进描述文件。
- 操作前预检（架构兼容、磁盘空间、与目标机已装清单的版本匹配、管理员权限、启动自检结论）在界面以 `✔ / ▲ / ✘` 分级呈现，阻断项未清除时执行按钮不可用。
- 包的一次操作会被转换为 M4 的**动作定义**交由**同一个执行器**执行，因此天然复用其超时、取消、输出截断、退出码归因、二次确认与备份审计；卸载与强制重装追加**第 2 步高风险确认**。
- 退出码按安装器惯例归因：`0` 成功、`3010 / 1641` 成功但需重启、`1618 / 1641 / 1642` 等映射为“另一安装进行中 / 版本冲突”等类别，未知码如实记录。
- 操作完成后复读目标机版本确认结果，并给出卸载残留说明（目录/注册表项）。
- 纯规则见 [PackageRules.cs](src/DeskIT.Core/PackageRules.cs) 与 [PackageModels.cs](src/DeskIT.Core/PackageModels.cs)；包库加载与哈希校验见 [PackageLibrary.cs](src/DeskIT.Windows/PackageLibrary.cs)；预检与执行编排见 [PackageService.cs](src/DeskIT.Windows/PackageService.cs)；示例描述文件见 [packages/example-7zip.json](packages/example-7zip.json)。

**M6（步骤 6）**：账号与权限辅助页**只读**呈现本地账号与权限现状——账号清单（状态 / 来源 / 最近登录 / 密码到期）、管理员组成员、密码策略、UAC 级别与风险提示、共享凭据缓存、时间源等事实，并把“账号被锁 / 从未登录”等需要关注的情况汇总置顶。
- 查询由**单条 PowerShell 5.1 脚本**一次性输出 JSON（`ConvertTo-Json -Compress`）后由程序解析，查询超时可取消，**不产生任何系统变更**。
- 账号的**变更类操作**（解除锁定 / 启用 / 停用 / 重命名）不新建执行通道，而是作为动作写入 `actions/account.json`，从而自动获得 M4 的逐项二次确认、回滚说明与备份审计。
- 纯规则见 [AccountRules.cs](src/DeskIT.Core/AccountRules.cs) 与 [AccountModels.cs](src/DeskIT.Core/AccountModels.cs)；只读查询见 [AccountDirectoryQuery.cs](src/DeskIT.Windows/AccountDirectoryQuery.cs)；账号变更动作见 [actions/account.json](actions/account.json)；界面见 [MainWindow.xaml](src/DeskIT.App/MainWindow.xaml) 的“账号与权限（M6）”页。

**M7（步骤 7）**：记录与多机台账页把每次会话沉淀为**处理记录**（自动带处理人工号 + 姓名，含问题类别、现象描述、诊断结论、执行的动作与结果、会话结论与结束时间），归并成**单份全量台账**，并在会话正常结束时**静默产出脱敏的自包含报告**（HTML + Markdown）。
- 台账以**仅追加**的 NDJSON（U 盘 `data\ledger.db`）落盘（记录行 + 补录行，逐行独立、单行损坏不影响其余行），加载时按记录标识归并补录；全部数据写在 U 盘，不向目标机磁盘写入任何产物。
- 处理人在 `data\operators.json` 中登记（工号 + 姓名），未登记时在页内就地登记即可继续；记录落盘时**自动带上**当前处理人。
- 会话结束时按“处理人 → 机器标识 → 会话结论”前置校验：未解决必须选择未解决原因；机器标识尚未建立（无采集/诊断档案）时**阻止写入台账**并提示先完成采集或诊断。
- 报告**默认脱敏**：处理人姓名遮蔽（1 字原样、2 字 → `张*`、3 字及以上 → `张*明`）而工号保留；脚本输出与诊断依据中的 `password= / passwd: / pwd= / /pass: / -p / --password` 等凭据值掩码为 `******`。报告为**自包含 HTML**（内联样式、无脚本、无任何出网引用），双击即可离线打开；同名文件按 `-v2 / -v3` 版本化成对追加（HTML 与 Markdown 使用同一版本号）。
- 台账总览提供按**问题类别 / 处理人工号 / 处理时间区间**筛选、按机器归并的档案视图（最近处理时间倒序、显示最近结论）与统计汇总（总数 / 机器数 / 各类别与处理人计数 / 未解决占比），并可把当前筛选结果导出为 **CSV 台账表**（多条）或单条 **HTML / Markdown 报告**；报告量少时按机器标识与会话标识自动命名，写入失败**不抛异常**（如实提示并保留台账记录）。
- 纯规则见 [LedgerModels.cs](src/DeskIT.Core/LedgerModels.cs)、[LedgerRules.cs](src/DeskIT.Core/LedgerRules.cs) 与 [LedgerReportBuilder.cs](src/DeskIT.Core/LedgerReportBuilder.cs)；台账落盘与报告写出见 [LedgerStore.cs](src/DeskIT.Windows/LedgerStore.cs) 与 [ReportWriter.cs](src/DeskIT.Windows/ReportWriter.cs)；界面见 [MainWindow.xaml](src/DeskIT.App/MainWindow.xaml) 的“记录与台账（M7）”页（本次会话 / 目标机档案 / 台账总览三视图）。

**M8（步骤 8）**：介质安全与数据保护——首次使用设置**主密码**（仅内存、绝不落盘），之后每次启动需**解锁**才能查看台账、执行动作与导出；`data\` 下的台账、快照、会话、动作备份与审计全部**认证加密**落盘，未解锁一律拒绝写入。
- **加密实现**：主密码经 **PBKDF2-HMAC-SHA256（200000 次迭代、16 字节随机盐）** 派生 32 字节密钥，数据以 **AES-256-GCM（12 字节 nonce、16 字节 tag）** 加密；加密信封为单行字符串 `DESKIT1|base64(nonce)|base64(tag)|base64(ciphertext)`。**不使用任何机器绑定机制（严禁 DPAPI / ProtectedData）**，介质可跨机、跨启动使用。
- 主密码校验值（`HMAC-SHA256(key, "DESKIT-VERIFIER-v1")`）与盐随 `data\operators.json`（明文介质头文件）保存，**该文件不保存主密码明文**；台账逐行加密写入 NDJSON，**单行损坏不影响其余行**，解密失败区分“介质未解锁”与“校验失败/数据损坏”，从不静默返回空。
- **失败延迟**：连续解锁失败第 5 / 6 / 7 次起分别强制等待 30 / 60 / 120 秒（之后固定 120 秒），**不提供任何绕过方式**，失败计数与冷却落盘（重启后仍生效）。
- **拔出保护**：会话期间每 10 秒探测介质可写性（写入并删除临时探测文件），不可写或介质消失时**立即停止后续动作、保留已完成结果、把会话标记为“已中断”**并明确提示；介质未解锁或不可写时禁止写入。右下角常驻**介质状态指示**与**安全移除**入口（有未结束会话时阻止移除）。
- **自动锁定**：默认无操作 10 分钟锁定（可在“介质与设置（M8）”页调整，0 表示不锁定）；解锁后进入主界面，手动“立即锁定”随时可用。
- **审计**：解锁成功 / 失败、导出、动作执行、报告产出、异常移除 / 会话中断、高级模式开关变化、处理人切换、临时文件清理、介质迁移等均写入 `data\audit\audit.db`（仅追加 NDJSON、经金库加密），默认保留 365 天、到期自动清理；审计事件不含任何凭证明文。
- **介质迁移**：数据目录整体复制到新介质（主密码不变，可继续使用）时按卷序列号生成的**介质标识**差异检测并提示“已更换介质”（介质标识不参与密钥派生、不绑定机器）；目标机临时文件仅写在 `%TEMP%\DeskIT\<runId>`，程序退出时删除，失败时提示残留路径。
- 纯规则见 [MediaModels.cs](src/DeskIT.Core/MediaModels.cs) 与 [MediaRules.cs](src/DeskIT.Core/MediaRules.cs)；加密金库见 [MediaVault.cs](src/DeskIT.Windows/MediaVault.cs)；介质头信息与设置见 [MediaStateStore.cs](src/DeskIT.Windows/MediaStateStore.cs)；拔出保护与探测见 [MediaWatchdog.cs](src/DeskIT.Windows/MediaWatchdog.cs) 与 [MediaWritabilityProbe.cs](src/DeskIT.Windows/MediaWritabilityProbe.cs)；审计见 [AuditStore.cs](src/DeskIT.Windows/AuditStore.cs)；临时目录与迁移见 [TempWorkspace.cs](src/DeskIT.Windows/TempWorkspace.cs) 与 [MediaMigration.cs](src/DeskIT.Windows/MediaMigration.cs)；界面见 [MainWindow.xaml](src/DeskIT.App/MainWindow.xaml) 的解锁 / 初始化门禁与“介质与设置（M8）”页。

**M9（步骤 9）**：受控自定义命令与动作沉淀——内置动作覆盖不到时的**受控后门**：以**管理员 + 高级模式 + 介质已解锁且可写**为前提执行任意 PowerShell 命令，且**每次执行都必须强制记录**，不提供“不记录执行”的路径。
- **入口门禁**：`自定义命令（M9）` 页仅在“管理员模式 + 启动自检通过 + 介质已解锁可写 + 高级模式已开启”四项同时满足时可用，否则横幅明示禁用原因（判定顺序：管理员 → 启动自检 → 解锁 → 可写 → 高级模式）。
- **执行前确认**：每次执行都弹出内联的“影响与身份”确认区，展示**完整命令**、执行身份（管理员 / 标准用户）、会话关联与影响提示，确认后方可执行，**不提供“本次不再确认”**。
- **强制记录**：命令原文与输出在落盘前统一经凭据脱敏（`password= / passwd: / pwd= / /pass: / -p / --password` 值掩码为 `******`），再写入命令历史（`data\custom\commands.db`，加密追加 NDJSON）与审计事件（`AuditEventKind.CustomCommandExecuted`）；**命令历史或审计写入失败即返回失败并停止后续执行**（页面横幅提示，重新加载历史成功后恢复）。
- **持久化变更检测**：执行前后分别只读采集**系统服务 / 计划任务 / 开机自启动项**三类基线并做差异对比（服务取 `HKLM\SYSTEM\CurrentControlSet\Services` 子键名、计划任务取只读 PS 枚举、自启动项取 `Win32_StartupCommand`），任一侧采集失败一律标注“**未知**”并说明原因，**绝不假装“无变化”**；差异显式列入界面与会话台账的“处理后系统持久化变更”。
- **动作沉淀**：命令历史中每条记录可“另存为修复动作”，弹窗填写名称、说明、风险级别（不得低于“高”）、是否需重启、是否可回滚等，**字段不全给出字段级提示**；校验通过后写入 `actions\custom-<slug>-<序号>.json`（JSON 数组、**不覆盖既有文件**、标识形如 `custom.001`），保存成功后自动刷新 M4 动作库即可复用。
- 自定义命令清单进入会话台账（`HandlingRecord.CustomCommands`）并**不计入常规动作成功率**；初次执行会话结束后清空会话级缓存。
- 纯规则见 [CustomCommandModels.cs](src/DeskIT.Core/CustomCommandModels.cs) 与 [CustomCommandRules.cs](src/DeskIT.Core/CustomCommandRules.cs)；执行与强制记录见 [CustomCommandService.cs](src/DeskIT.Windows/CustomCommandService.cs)；命令历史见 [CustomCommandStore.cs](src/DeskIT.Windows/CustomCommandStore.cs)；动作沉淀见 [ActionSedimentStore.cs](src/DeskIT.Windows/ActionSedimentStore.cs)；持久化探测见 [PersistenceProbe.cs](src/DeskIT.Windows/PersistenceProbe.cs)；界面见 [MainWindow.xaml](src/DeskIT.App/MainWindow.xaml) 的“自定义命令（M9）”页。

## 关于需求文档

本项目的需求文档（PRD）**不纳入本仓库**，也不随源码分发。仓库内仅保留实现所需的架构设计与任务分解文档（见 `docs/`）。