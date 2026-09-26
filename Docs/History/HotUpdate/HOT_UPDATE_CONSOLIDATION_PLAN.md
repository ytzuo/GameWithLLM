# 热更新开发收束计划

> 状态：已完成（C0-C6，2026-09-27）
> 创建日期：2026-09-26  
> 适用项目：`unity-NPC-agent-client`  
> 前置条件：HybridCLR H0-H7、Addressables A0-A7 已完成  
> 架构事实源：`ARCHITECTURE.md`

## 1. 背景与目标

HybridCLR H0-H7 与 Addressables A0-A7 已完成从本地工具包验证、原子 ToolSet 激活、
远端内容加载，到生产候选、差量更新和回滚的完整开发链路。开发过程中为降低单阶段
风险，仓库保留了大量按 H/A 阶段命名的 Editor 菜单、批处理入口、Smoke Runner、构建
目录、日志标记和基线文档。

这些阶段性资产在开发期有价值，但不应继续成为生产系统的主要组织方式。本计划的目标
是将热更新实现从“按开发阶段划分”收束为“按长期模块职责划分”，同时保持现有协议、
工具契约、Addressables 地址、业务 ID 和已发布 Release 不变。

收束完成后应达到以下结果：

1. 生产发布只有一条 `ContentReleasePipeline`，不再由 H7 和 A7 分别生成生产候选。
2. Editor 只暴露少量统一入口；具体检查仍按模块独立实现和报告。
3. 项目配置、内容验证、Smoke、Player 构建和生产提升相互分层。
4. 生产代码、日志、环境变量和新 Release 不再携带 A0-A7/H0-H7 开发阶段标识。
5. 历史文档、已发布 Release、归档证据和外部稳定标识保持不变。
6. Unity 运行时只保留 Addressables 交付路径，不保留第二条本地热更新 fallback。

## 2. 固定边界

收束不得改变以下边界：

- LLM、完整对话历史和 tool loop 仍只存在于 Go。
- Unity Runtime Bridge、A2A、MCP 和 Save Coordination 协议不变。
- Unity API 仍只能在主线程调用。
- 工具参数仍以 JSON 对象跨越网络边界。
- 工具 Schema 仍只由 Unity Runtime 生成。
- `GameWithLLM.AgentRuntime` 仍是 AOT 公共契约程序集。
- 热更新工具包仍不得覆盖 AOT BuiltinTool 或引用平行 SDK 契约。
- ToolSet、工具元数据和客户端文本仍以完整候选原子激活。
- Addressables 逻辑地址、工具名、`packageId`、业务 ID 和 `.meta` GUID 不得因本次
  整理而改变。
- 已发布 Release 和归档证据是不可变历史，不得重写阶段名称。

本计划只描述后续重构目标。在实际代码完成、测试通过并同步更新 `ARCHITECTURE.md`
以前，当前 A/H 实现仍是有效事实。

## 3. 当前问题

### 3.1 Editor 入口过多

当前 A1-A6 普遍同时暴露：

- `Configure`；
- `Verify`；
- `ConfigureFromCommandLine`；
- `VerifyFromCommandLine`；
- `BuildLocalDevelopmentFromCommandLine`；
- `BuildWindowsPlayerFromCommandLine`；
- 每个文件独立的 `RunCommand`。

H2-H5、H7 和 A7 还分别保留 Smoke Player、Production Player、Addressables Build、
候选清单与提升逻辑。多个入口能够构建 Player，但它们不一定执行相同的最终门禁，容易
造成“从不同菜单得到不同生产结果”。

### 3.2 阶段名称进入长期代码

当前类名、菜单、环境变量、构建目录和日志中存在：

```text
AddressablesA1ProjectSetup ... AddressablesA7ReleasePipeline
HybridClrH7ReleasePipeline
A2 release was rejected
H2_SMOKE_SUCCESS ... H7_PLAYER_SMOKE_SUCCESS
A7_CONTENT_STATE_PATH
Builds/AddressablesA6
```

这些名称描述的是实现历史，不描述当前模块职责。随着后续内容类型增加，它们会继续扩大
维护成本。

### 3.3 生产与测试路径未完全分离

- `HybridClrBootstrap` 仍包含 `StreamingAssets/HotUpdate` 本地加载能力。
- `AgentHostClient` 在关闭内容 bootstrap 时仍能进入本地热更新路径。
- H7 smoke plan 位于 `StreamingAssets`，会进入普通 Production Player。
- A1 delivery probe 仍在启动必需下载列表中，尽管真实 release manifest 和内容已经
  能验证远端链路。

### 3.4 构建基础设施重复

多个 Editor 文件分别实现 Profile 切换、Addressables Build、Windows Player Build、
SHA-256、候选文件枚举和命令行退出处理。H7 与 A7 还分别拥有提升脚本和生产指针逻辑。

## 4. 入口保留与删除策略

| 当前能力 | 目标处理 | 说明 |
|---|---|---|
| A0-A6 `Verify()` | 保留验证逻辑，迁入模块规则 | 继续参与统一生产门禁 |
| A1-A6 `Configure()` | 移入 `Migrations` / `Project Setup` | 仅首次建库、升级或修复时使用 |
| A2-A6 LocalDevelopment 构建入口 | 删除 | 由统一 Local Smoke Pipeline 接管 |
| A2-A6 Windows Player 构建入口 | 删除 | 生产 Player 只能由 Release Pipeline 构建 |
| A1 多 Profile 批量构建 | 删除或降为诊断工具 | 不是生产发布步骤 |
| H2-H5 独立 Smoke Player 构建 | 删除 | 由通用 Tool Package smoke 覆盖 |
| H7 工具包生产门禁 | 保留逻辑，移除 H7 命名 | 长期属于 Tool Packages 模块 |
| Packed Play smoke | 保留测试，统一入口 | UI、Inventory、Scene handle 生命周期仍需覆盖 |
| A7 发布事务与回滚测试 | 保留 | 长期属于 Release 模块 |
| 历史 Baseline 文档 | 归档，不删除 | 用于审计和问题追溯 |

原则是“保留能力，删除阶段入口”。验证实现可以继续拆分，但生产入口必须唯一。

## 5. 目标目录与职责

建议将 Editor 侧代码收束为以下结构：

```text
Assets/Editor/HotUpdate/
├─ Core/
│  ├─ HotUpdateEditorCommand.cs
│  ├─ ContentBuildContext.cs
│  ├─ AddressablesProfileScope.cs
│  ├─ WindowsPlayerBuilder.cs
│  └─ ArtifactHash.cs
├─ Validation/
│  ├─ ContentValidationRunner.cs
│  ├─ ProjectConfigurationValidator.cs
│  ├─ ContentOwnershipValidator.cs
│  ├─ ContentCatalogValidator.cs
│  ├─ ToolPackageValidator.cs
│  ├─ UiContentValidator.cs
│  ├─ ItemContentValidator.cs
│  ├─ CharacterContentValidator.cs
│  ├─ SceneContentValidator.cs
│  └─ ReleaseBudgetValidator.cs
├─ Tests/
│  ├─ ContentBootstrapSmoke.cs
│  ├─ ToolPackageSmoke.cs
│  ├─ UiInventoryPackedPlaySmoke.cs
│  ├─ RemoteScenePackedPlaySmoke.cs
│  └─ ReleaseTransactionTests.cs
├─ Release/
│  ├─ ContentReleasePipeline.cs
│  ├─ ToolPackageReleaseGate.cs
│  ├─ CandidateManifestWriter.cs
│  └─ ContentStateArchive.cs
└─ Migrations/
   ├─ ContentDeliveryProjectSetup.cs
   ├─ UiContentSetup.cs
   ├─ ItemContentSetup.cs
   ├─ CharacterContentSetup.cs
   └─ SceneContentSetup.cs
```

PowerShell 和 CI 目标结构：

```text
Scripts/
├─ Build-ContentRelease.ps1
├─ Publish-ContentRelease.ps1
└─ Test-ContentReleaseTransaction.ps1

.github/workflows/
├─ content-validation.yml
├─ content-release-candidate.yml
└─ content-release-promote.yml
```

## 6. 通用验证框架

### 6.1 验证规则

验证规则按模块拆分，使用统一输入和结构化结果：

```csharp
public interface IContentValidationRule
{
    string RuleId { get; }
    string Module { get; }
    ValidationResult Validate(ContentValidationContext context);
}
```

推荐显式注册规则，不使用反射自动发现。显式列表能够保证执行顺序、依赖关系和 CI 结果
稳定，也避免 Editor 中意外加载测试或迁移类型。

```csharp
private static readonly IContentValidationRule[] Rules =
{
    new ProjectConfigurationValidator(),
    new ContentOwnershipValidator(),
    new ContentCatalogValidator(),
    new ToolPackageValidator(),
    new UiContentValidator(),
    new ItemContentValidator(),
    new CharacterContentValidator(),
    new SceneContentValidator(),
    new ReleaseBudgetValidator()
};
```

### 6.2 验证级别

| Profile | 内容 | 使用位置 |
|---|---|---|
| `Fast` | 配置、JSON、Address、业务 ID、Schema、引用边界 | 本地频繁验证、普通 PR |
| `Candidate` | Fast + AOT、程序集白名单、Missing Script、Analyze、Build Layout | 候选构建 |
| `Release` | Candidate + Player、真实 smoke、升级/断网/缓存/磁盘场景 | 受保护发布环境 |
| `Module` | 只执行指定模块规则 | 内容开发与故障定位 |

统一入口只负责选择 Profile、执行规则和生成报告，不应把所有测试代码复制到一个类中。

### 6.3 统一报告

Editor、CI 和本地命令使用相同报告格式：

```json
{
  "schemaVersion": 1,
  "profile": "candidate",
  "succeeded": false,
  "modules": [
    {
      "module": "Scenes",
      "rule": "MissingScripts",
      "succeeded": false,
      "message": "scene/warehouse/main contains a missing script"
    }
  ]
}
```

报告不得包含玩家正文、模型输出、完整工具参数、Prompt、历史、密钥或 token。

## 7. 测试分层

“通用测试”表示统一编排和结果，不表示把不同运行环境合成一个测试方法。测试仍按以下
层级独立执行：

```text
静态 Editor Validation
→ EditMode Tests
→ Packed Play Mode Smoke
→ Windows IL2CPP Player Smoke
→ Existing Install Upgrade / Offline / Cache / Low Disk
→ Publish / Retry / Rollback Transaction Tests
```

各层失败应保留独立结果。CI 可以并行执行无依赖层，但生产提升必须等待全部要求的证据。

长期测试模块：

- `ContentBootstrapSmoke`：初始化、Catalog、缓存和离线状态。
- `ToolPackageSmoke`：metadata、程序集、Schema、ToolSet、tombstone 和调用。
- `UiInventoryPackedPlaySmoke`：UI lease、物品 Catalog、Inventory 与资源释放。
- `RemoteScenePackedPlaySmoke`：加载、绑定、切换、卸载和 Bootstrap 回退。
- `ReleaseTransactionTests`：不可变上传、篡改拒绝、断点重试、指针保护和回滚。

## 8. 最终 Editor 与命令行入口

### 8.1 Editor 菜单

```text
GameWithLLM/Content/Validate Project
GameWithLLM/Content/Validate Selected Module
GameWithLLM/Content/Run Packed Play Smoke
GameWithLLM/Content/Build Local Smoke Player
GameWithLLM/Content/Build Full Release Candidate
GameWithLLM/Content/Build Content Update Candidate
GameWithLLM/Content/Project Setup/Repair Configuration
```

`Project Setup` 菜单必须明确标注为迁移/修复操作，不得成为正常构建的隐式前置步骤。

### 8.2 命令行入口

```text
ContentValidationRunner.ValidateFromCommandLine
ContentSmokeRunner.RunFromCommandLine
ContentReleasePipeline.BuildFullFromCommandLine
ContentReleasePipeline.BuildUpdateFromCommandLine
```

所有入口共享 `HotUpdateEditorCommand.Run`，统一异常日志、退出码和报告路径。

## 9. 阶段名称迁移

### 9.1 类名

| 当前名称 | 目标名称 |
|---|---|
| `AddressablesA0InventoryValidator` | `ContentOwnershipValidator` |
| `AddressablesA1ProjectSetup` | `ContentDeliveryProjectSetup` |
| `AddressablesA2ProjectSetup` | `HotUpdateArtifactStager` |
| `AddressablesA3ProjectSetup` | `UiContentSetup` / `UiContentValidator` |
| `AddressablesA4ProjectSetup` | `ItemContentSetup` / `ItemContentValidator` |
| `AddressablesA5ProjectSetup` | `CharacterContentSetup` / `CharacterContentValidator` |
| `AddressablesA6ProjectSetup` | `SceneContentSetup` / `SceneContentValidator` |
| `AddressablesA7ReleasePipeline` | `ContentReleasePipeline` |
| `HybridClrH7ReleasePipeline` | `ToolPackageReleaseGate` |
| `AddressablesA1PlayModeSmoke` | `ContentBootstrapSmoke` |
| `AddressablesA3PackedPlaySmoke` | `UiInventoryPackedPlaySmoke` |
| `AddressablesA6PackedPlaySmoke` | `RemoteScenePackedPlaySmoke` |

`Setup` 与 `Validator` 必须分离：Validator 只读并返回结果，不能在验证过程中修改资产、
场景、Profile 或 Addressables Settings。

### 9.2 文件、配置和 CI

| 当前名称 | 目标名称 |
|---|---|
| `h7-release-policy.json` | `tool-package-release-policy.json` |
| `a7-release-policy.json` | `content-release-policy.json` |
| `h7-smoke-plan.json` | `tool-package-smoke-plan.json` |
| `H7_RELEASE_ID` | `CONTENT_RELEASE_ID` |
| `A7_CONTENT_STATE_PATH` | `CONTENT_BASELINE_STATE_PATH` |
| `Build-H7Release.ps1` + `Build-A7Release.ps1` | `Build-ContentRelease.ps1` |
| `Promote-A7Release.ps1` | `Publish-ContentRelease.ps1` |
| `hybridclr-h7.yml` | `tool-package-validation.yml` |
| `addressables-a7.yml` | `content-release-candidate.yml` |
| `addressables-a7-promote.yml` | `content-release-promote.yml` |

### 9.3 日志与成功标记

| 当前标记 | 目标语义标记 |
|---|---|
| `A2 release was rejected` | `content release was rejected` |
| `H2_SMOKE_SUCCESS` | `TOOL_PACKAGE_LOAD_SUCCESS` |
| `H3_ATOMIC_PACK_SUCCESS` | `TOOL_PACKAGE_ATOMICITY_SUCCESS` |
| `H4_TOOLSET_SUCCESS` | `TOOLSET_ACTIVATION_SUCCESS` |
| `H5_CATALOG_SUCCESS` | `CONTENT_CATALOG_ACTIVATION_SUCCESS` |
| `H7_PLAYER_SMOKE_SUCCESS` | `TOOL_PACKAGE_SMOKE_SUCCESS` |
| `A7_SMOKE_SUCCESS` | `CONTENT_RELEASE_SMOKE_SUCCESS` |

新日志按模块命名：`[Content]`、`[ToolPackages]`、`[Catalogs]`、`[Scenes]`、
`[Release]`。生产错误码描述失败语义，不包含开发阶段编号。

## 10. 不得重命名的稳定标识

以下内容不得为去除阶段标识而改写：

- 已发布 Release ID，例如 `h7-production-1.3.0`；
- 已发布 Addressables 地址和版本化工具二进制地址；
- 工具名、`toolIdentity`、`packageId`、业务 ID；
- JSON Schema version 和工具契约版本；
- Runtime、MCP、A2A、Save Coordination 方法名；
- `.meta` GUID；
- 已归档 smoke 证据中的成功标记；
- 历史 Baseline 文档中的 A/H 名称。

新 Release 使用中性版本，例如：

```text
releaseId: content-2026.09.26.1
toolSetVersion: 1.4.0
contentVersion: 2026.09.26.1
playerBuildId: windows-x64-0.1.0
```

本次内部入口迁移应在一个受控变更中原子完成，不长期保留 A/H 与模块名称双入口；已发布
外部标识则永久保留。

## 11. 生产与测试路径清理

### 11.1 移除本地运行时 fallback

生产运行时最终只保留：

```text
ClientContentBootstrap
→ AddressableHotUpdateReleaseLoader
→ HybridClrToolPackageLoader
→ ToolsRegistry.Prepare/Activate
```

执行步骤：

1. 将 Editor 本地工具发现移入 Editor/Tests 程序集。
2. 将 H7 smoke 改为使用 Addressables 候选。
3. 将 `SmokePackageId` 等构建常量移入 Editor 配置。
4. 删除 `AgentHostClient.enableHybridClrBootstrap`。
5. 删除 `AgentHostClient` 中 `!enableContentBootstrap` 的本地 release 加载分支。
6. 删除 `HybridClrBootstrap` 中 `StreamingAssets/HotUpdate` manifest、metadata、DLL、PDB
   和 Catalog 读取逻辑。
7. 保证关闭内容 bootstrap 时只运行 AOT BuiltinTools，不进入第二条热更新交付链。

### 11.2 移除 A1 delivery probe

真实 release manifest 已能验证远端内容链路，因此：

1. 从 required download labels 删除 `content.a1-required`。
2. 删除 `a1-bootstrap-probe.json` 及其 `.meta`。
3. 删除 A1 Setup 中 probe 创建和验证逻辑。
4. 删除 `content.a1-required` Label。
5. 由 release manifest 和真实必需内容承担启动健康检查。

### 11.3 Smoke plan 不进入 Production Player

将 `Assets/StreamingAssets/H7/h7-smoke-plan.json` 移入非 StreamingAssets 测试目录，
同时移动 `.meta` 保持 GUID。Local Smoke Player 构建前临时复制，构建完成后清除；正式
Player 构建必须验证不存在测试计划和本地热更新 staging。

## 12. 生成产物与版本库策略

当前以下内容由构建流程生成，但仍在 Git 中：

```text
Assets/Content/HotUpdate/Metadata/*.dll.bytes
Assets/Content/HotUpdate/ToolPacks/*.dll.bytes
Assets/Content/HotUpdate/release-manifest.json
```

近期继续保留，以保证新克隆项目的 Addressables GUID 和 Editor 引用完整。只有在统一
`Prepare Workspace` 能稳定生成全部文件、保留 `.meta` GUID、恢复 Addressable entry
并提供明确错误提示后，才评估取消跟踪二进制。不得先删除文件再补生成流程。

本地可再生成目录不进入版本库：

```text
Builds/
HybridCLRData/
ServerData/
Logs/
Temp/
Artifacts/
```

发布和 smoke 证据必须先上传或归档，再清理本地 `Artifacts`。

## 13. 文档收束

历史 Baseline 移入：

```text
Docs/History/HotUpdate/
```

历史内容不改写。活跃文档建议保留：

```text
ARCHITECTURE.md
Docs/HOT_UPDATE_OPERATIONS.md
Docs/HOT_UPDATE_RELEASE_CHECKLIST.md
Docs/HOT_UPDATE_CONSOLIDATION_PLAN.md
```

- `ARCHITECTURE.md`：唯一当前事实源。
- `HOT_UPDATE_OPERATIONS.md`：本地构建、候选生成、提升、回滚和故障处理。
- `HOT_UPDATE_RELEASE_CHECKLIST.md`：生产人工检查项和 smoke 证据要求。
- 本文：收束迁移目标，完成后转入 History。

## 14. 实施顺序

### C0：冻结与基线

> 完成记录（2026-09-26）：工作树在 `f1a6cbe1a828639cab2be08ea49e5720c3f6fa92`
> 冻结；H7 候选、Player smoke、Packed Play、33/33 EditMode、Addressables content
> state 与发布事务结果已记录在
> `Docs/Baselines/hot-update-consolidation-c0.json`。协议和稳定外部标识未改变。
> 冻结点没有 A7 Production 候选；重建尝试因未提升 packageVersion 的新 DLL hash 被
> H4 不可变历史台账正确拒绝，未伪造 smoke 证据且未改写生产指针。

1. 提交或暂存当前 A7 修改。
2. 归档 H7/A7 候选、content state 和 smoke 证据。
3. 记录当前 Editor、EditMode、Packed Play、Player smoke 和发布事务结果。
4. 更新 `ARCHITECTURE.md`，声明即将进行内部模块化迁移，协议不变。

### C1：公共构建基础设施

> 完成记录（2026-09-26）：已建立 `Assets/Editor/HotUpdate/Core`，统一命令行退出、
> Addressables Profile scope、Windows Player 构建、SHA-256 和候选文件清单；H7/A7
> 已切换到公共实现，旧入口暂保留到 C3/C4 满足删除条件。

1. 抽取 `HotUpdateEditorCommand`、Profile scope、Player builder、Hash 和文件清单工具。
2. H7/A7 改用公共实现。
3. 保持现有行为不变，通过原有测试后再继续。

### C2：模块化验证

> 完成记录（2026-09-26）：已建立显式规则列表、`Fast`/`Candidate`/`Release`/`Module`
> Profile 和统一 JSON 报告。A0-A6/H7 验证由模块规则编排，A7 只调用 Candidate
> Profile；每条规则返回独立 Module/RuleId/失败信息，并在验证后恢复 Editor Scene setup。

1. 建立 `ContentValidationRunner` 和结构化报告。
2. 按模块迁移 A0-A6/H7 `Verify()` 内容。
3. A7 改为调用 `Candidate`/`Release` Profile，不再直接调用阶段类。
4. 为每条规则保留可独立测试的输入和失败信息。

### C3：测试入口统一

> 完成记录（2026-09-26）：Bootstrap、Tool Package、UI/Inventory、Scene 和 Release
> smoke 已切换到模块命名；`ContentSmokeRunner` 统一选择独立运行层，CI 分开执行静态验证、
> EditMode 和两类 Packed Play smoke 并分别保存证据。H2-H5 独立 Smoke Player 构建入口、
> 旧命令行 flag 和阶段成功标记已删除。

1. 重命名 Bootstrap、Tool Package、UI/Inventory、Scene 和 Release smoke。
2. 建立统一 `ContentSmokeRunner`。
3. CI 按层执行并汇总报告，不将不同运行环境合并为单一测试方法。
4. 移除 H2-H5 历史 Smoke Player 构建入口。

### C4：唯一生产发布链

> 完成记录（2026-09-26）：`ToolPackageReleaseGate` 只返回门禁结果和工具包候选信息；
> `ContentReleasePipeline` 是唯一 Production Addressables/Player 候选构建入口。本地工具包
> Player smoke 先产生与 release manifest 绑定的证据，生产候选随后才可构建。构建、发布、
> 事务测试脚本和三条 CI workflow 已统一为 Content 命名，旧 H7/A7 workflow 与提升脚本已删除。

1. `ToolPackageReleaseGate` 只返回验证结果和工具候选信息。
2. `ContentReleasePipeline` 成为唯一 Production Player/Addressables 候选构建入口。
3. 合并 H7/A7 PowerShell 构建脚本。
4. 生产提升只保留 `Publish-ContentRelease.ps1`。
5. 更新受保护 CI 环境后删除旧 workflow 和提升脚本。

### C5：生产路径清理

> 完成记录（2026-09-27）：生产运行时已删除本地 HybridCLR loader 与场景开关；禁用
> 内容 bootstrap 时仅注册 AOT BuiltinTools。A1 delivery probe 和 label 已删除；工具包
> smoke plan 移入 Editor 测试数据并只在本地 smoke Player 构建期间临时 staging。
> 阶段类、菜单、命令行入口、生产日志和错误已改为模块语义，Production 构建会拒绝
> `StreamingAssets/HotUpdate` 与 smoke staging。

1. 移除本地 HybridCLR runtime fallback。
2. 移除 A1 probe。
3. 将 smoke plan 移出 Production StreamingAssets。
4. 将阶段标记替换为模块语义标记。
5. 删除旧 A/H 菜单和命令行入口。

### C6：文档与磁盘清理

> 完成记录（2026-09-27）：已新增 Operations 与 Release Checklist，同步
> `ARCHITECTURE.md`，阶段计划、Baseline 与冻结证据已归档到
> `Docs/History/HotUpdate`。本地可再生成的 Builds、HybridCLRData、ServerData、Logs、
> Temp 与 Artifacts 已在验证证据确认后清理；收束计划随历史资料归档。

1. 编写 Operations 和 Release Checklist。
2. 将阶段 Baseline 移入 History。
3. 清理已归档的 Builds、HybridCLRData、ServerData、Logs、Temp 和 Artifacts。
4. 完成最终全链路验证后，将本文状态改为“已完成”并归档。

## 15. 每阶段删除条件

| 待删除内容 | 删除条件 |
|---|---|
| A2-A6 Build 入口 | 统一 Pipeline 已构建同等 Player，且 CI 不再引用 |
| H2-H5 Smoke 入口 | ToolPackageSmoke 覆盖加载、Schema、原子 ToolSet、Catalog |
| H7 Production Candidate | A7/ContentRelease 已包含 ToolPackage gate 和真实 Player smoke |
| H7 Promote 脚本 | 唯一 Content Release 提升与回滚测试通过 |
| 本地 HybridCLR runtime loader | Editor/Player smoke 均通过 Addressables 候选 |
| A1 probe | manifest 和真实 required labels 已覆盖在线、离线、缓存测试 |
| H7 StreamingAssets smoke plan | 新 smoke staging 能构建且 Production Player 不含测试文件 |
| 阶段类名和菜单 | CI、文档和脚本均已切换模块入口 |
| 生成 DLL/metadata Git 跟踪 | 新克隆可一键生成并保持 GUID/Addressable 引用 |

不得仅因为代码“看起来未使用”就删除；必须满足对应替代能力和验证证据。

## 16. 验收标准

### 16.1 结构

- Production 构建只有一个入口。
- Editor 顶层菜单不出现 A0-A7/H0-H7。
- Validator 无资产写入副作用。
- Setup/Migration 不被生产 Pipeline 隐式调用。
- Profile、Player、Hash、RunCommand 和候选清单没有重复实现。

### 16.2 命名

以下搜索只允许命中 History、旧 Release 或归档证据：

```text
AddressablesA[0-7]
HybridClrH[0-7]
A[0-7]_
H[0-7]_
gameWithLlmAddressablesA2Smoke
```

生产日志、错误、环境变量、构建目录和新候选中不得再出现开发阶段编号。

### 16.3 功能

- C# 编译、EditMode 和所有模块验证通过。
- LocalDevelopment 内容启动、在线下载、断网和缓存命中通过。
- Tool Package metadata/DLL/Schema/ToolSet/tombstone smoke 通过。
- UI、Inventory、角色视觉和远端 Scene Packed Play smoke 通过。
- Windows IL2CPP Player 普通对话、流式回复、移动、取消、Inventory 和存档恢复通过。
- 完整发布、连续两个内容版本升级、断点重试、磁盘不足和回滚通过。
- 坏 JSON、坏 DLL、缺失 Bundle 和超预算候选不会改变生产指针。
- Event Viewer/Profiler 无未解释的 Addressables handle 泄漏。

### 16.4 架构

- Production Player 不包含本地热更新 manifest、Smoke plan 或第二条工具包加载路径。
- Go/Unity 权威边界、Runtime Bridge、A2A、MCP 和 Save Coordination 协议保持不变。
- 完成实现后，`ARCHITECTURE.md` 已同步为模块化目录、唯一发布链和最终启动流程。

## 17. 风险与控制

| 风险 | 控制措施 |
|---|---|
| 一次重命名过多导致场景脚本丢失 | 文件与 `.meta` 同步移动，检查 Missing Script |
| 删除旧入口后 CI 暗中依赖 | 删除前对 `.github`、`Scripts`、Docs 和命令行做全仓搜索 |
| 合并验证器后失败信息模糊 | 每条规则保留稳定 Module/RuleId 和独立结果 |
| Setup 与 Verify 混合产生资产变更 | 接口层强制 Validator 只读，Setup 单独菜单 |
| 本地 fallback 删除后 smoke 无法运行 | 先让 smoke 使用真实 Addressables 候选 |
| Release ID 重命名破坏回滚 | 旧 ID 永不修改，新版本才使用中性命名 |
| 取消跟踪生成二进制导致新克隆损坏 | 最后实施，并先提供一键 Prepare 和 GUID 校验 |

## 18. 完成定义

只有在以下条件全部满足时，热更新开发收束才算完成：

1. 模块化目录和统一验证框架已经落地。
2. `ContentReleasePipeline` 是唯一生产候选入口。
3. 旧阶段 Build、Smoke 和 Promote 入口已删除。
4. 生产运行时不存在本地 HybridCLR fallback。
5. 菜单、CI、日志和新 Release 已使用模块语义命名。
6. 历史标识只存在于 History、旧 Release 和归档证据中。
7. 全部 Unity、Player、升级和回滚验收通过。
8. `ARCHITECTURE.md` 已同步且不存在文档/代码冲突。
