# NPC 表现管线重构：阶段一交接

日期：2026-10-14
状态：契约、依赖盘点、迁移清单和夹具已完成；运行时尚未切换。
计划：[重构计划](../NPC_PRESENTATION_PIPELINE_REFACTOR_PLAN.md)。
待实施契约事实源：[ARCHITECTURE.md §2.8](../../ARCHITECTURE.md)。

## 1. 本阶段产物与使用边界

本目录只用于评审和阶段二交接，不被 Unity AssetDatabase、Go 内容解析器或生产发布链读取。
当前 SDK、生产索引、NPC 资产、安装与存档行为保持原样；本阶段没有迁移、删除或部署资源。

| 文件 | 用途 |
|---|---|
| `dependency-inventory.json` | 当前仓库 44 个 NPC 关联文件的检索命中与行号；不是只有直接比较才算依赖 |
| `asset-migration.json` | 16 个当前 v3 资产的源/目标路径、meta、GUID、引用文件和直接依赖 |
| `address-migration.json` | 26 个旧 NPC Address 的逐项操作：2 项替换，24 项移除 entry |
| `cleanup-candidates.json` | 88 个已跟踪旧源资产、动画源码和静态交付文件的清理候选，不是删除脚本 |
| `Fixtures/` | 固定新契约的索引、两份清单、绑定、安装索引、世界存档和对话快照 |
| `acceptance-cases.json` | 后续解析测试、迁移验收与生产 smoke 的目标场景 |
| `verify_stage1.py` | 仅校验本阶段夹具、路径/GUID 与盘点一致性，不证明未来运行时实现已通过 |

盘点针对 git 跟踪的 Go、Unity Assets/Packages、Scripts 和 NpcContent 文本文件，
检索 `NpcContent/NPCContent/npcContent/RemoteNpc/npc/merchant_001/guide_001`。
命中只存位置与标记，不复制日志、密钥、正文或 Prompt。历史文档与构建缓存不纳入生产依赖。
以下人工分类补充泛型版本字段、纯生命周期代码等不一定命中上述词的关联点。

## 2. 依赖与切换责任

路径相对仓库根；Unity 路径的公共前缀为 `unity-NPC-agent-client/`。

| 子系统 | 当前入口 | 阶段二或后续必要处理 |
|---|---|---|
| SDK | `Packages/com.gamewithllm.agent-runtime/Runtime/Core/AgentRuntimeContracts.cs` | NpcContentBinding 构造器/属性改为仅身份；Manifest 仍检查实体成员关系和唯一性 |
| Manifest 装配 | `Assets/Scripts/AgentHostClient.cs`、`Networking/RuntimeGatewayClient.cs` | 初始注册、重新序列化、revision 快照和重连全部只传 entityId；移除给资料库传启动内容版本 |
| Entity | `Assets/Scripts/Gameplay/NpcEntity.cs` | inactive 绑定约束保留；移除版本字段，不改变主线程与移动事件 |
| 目录与安装 | `Assets/Scripts/Networking/RemoteNpcContent.cs` | 统一新结构、固定路径、名称校验；删除 raw manifest hash 匹配、Player 判断和旧解析；换新缓存/索引文件 |
| 生成与恢复 | `Assets/Scripts/NpcLibraryRuntime.cs` | 删除 ActiveCatalogContentVersion、绑定版本/hash 校验、数字版本要求；保留唯一实例、NavMesh、准备/释放和回滚 |
| 表现 | `Networking/CharacterVisualController.cs`、Gameplay 动画驱动及事件接口 | 保留 visual-only 和 Animator 校验；完整性不允许 fallback 成功；实际资源复用统一准备路径 |
| UI | `UIManager/View/NpcLibraryWindow.cs`、`GameLogic/PlayerMock.cs` | 删除版本/兼容判断；按 Address 可解析性提示未就绪；M 键入口及玩家 NPC 列表保持 |
| 世界存档 | `Assets/Scripts/GameLogic/SaveGameService.cs` | 新 format 3；npcContents 仅身份；旧格式拒绝；容器引用跟随技术名称 |
| Go 身份/解析 | `internal/agent/npc_content.go`、`npc_index.go` | 仅身份绑定、名称路径、固定结构；删除版本 hash 缓存与跨字段版本检查 |
| Go Context | `internal/agent/conversation.go`、`session.go` | 新 Context 当前配置，旧 Context 固定 Prompt；身份检查保留，不进行历史内容比较 |
| Go 归档 | `internal/agent/conversation_archive.go`、`internal/savecoord` 调用链 | 新快照 format 2，仅身份；读取当前 Prompt 后整体替换；失败保留原 Context |
| Go 装配 | `internal/gateway/server.go`、`internal/handler/router.go`、`internal/config/config.go` | Gateway 接受/保存/克隆新绑定；现有 NPC_CONTENT_BASE_URL 的可信根来源保留 |
| Go Prompt | `internal/agent/prompt.go` | 通用 Prompt Catalog 不改格式；将 NPC locale/template 转为现有运行时 Prompt，不建立 NPC 版本 |
| 作者与构建 | `Assets/Editor/HotUpdate/Release/NpcContentDefinition.cs`、`NpcContentRelease.cs` | ASCII name/单展示名、固定 Address、覆盖当前文件、提交前验证；清理专用输出中不存在的名称 |
| 统一验证 | `Release/ContentReleasePipeline.cs`、`Validation/ModuleValidators.cs`、`ContentValidationRunner.cs` | 新 NPC 契约进入同一个验证器；保留实际 AOT 能力检查，不做 NPC Player 版本相等判断 |
| 命令行与部署 | `GameMCPServer/cmd/npc-content-validate/main.go`、`Scripts/Build-ContentRelease.ps1`、`Publish-ContentRelease.ps1` | 静态验证新字段；当前 NPC 文件允许覆盖；整体 release 不可变/证据匹配逻辑保留 |
| 现有测试 | Go npc_content/gateway/Context/archive 测试、Unity NpcContentContract/RemoteNpcRoundTwo/Three、Scripts/Test-ContentReleaseTransaction.ps1 | 替换旧 JSON/旧身份；删除版本冲突断言，增加完整性与当前配置恢复断言 |

版本检索要限定 NPC 语义：工具 release、ToolSet、通用 Prompt Catalog、generation、
存档文件格式标记和 DLL 下载字节 hash 不能因名称相似被一并删除。

## 3. 资产迁移与清理操作清单

| 源目录 | 目标目录 | 源目录 meta GUID（保留） | 展示名称 |
|---|---|---|---|
| `Assets/Content/Npcs/merchant_001/3` | `Assets/Content/Npcs/Merchant` | `44c07dacf138e9f4ab81e86a9737b4da` | 旅行商人 |
| `Assets/Content/Npcs/guide_001/3` | `Assets/Content/Npcs/Guide` | `cdf750e1f981b5546a1022ff41a0bb9c` | 仓库向导 |

阶段二通过 Unity Editor 的资产移动接口或同时移动文件/meta 保留 GUID。
Definition 的 m_Name 更新为技术名称，字段切为 name，单一 displayName 生成 Profile 展示名；
原始人格和 Prompt 保留，{{npcId}} 占位符仍使用协议身份。

两目录采用相同映射：

| 当前文件 | 目标文件 |
|---|---|
| `merchant_001_v3.asset` / `guide_001_v3.asset` | `Definition.asset` |
| `visual.prefab` | `Visual.prefab` |
| `avatar.png` | `Avatar.png` |
| `controller.controller` | `Animations/Controller.controller` |
| `idle.anim` | `Animations/Idle.anim` |
| `material.mat` | `Materials/Body.mat` |
| `texture.asset` | `Materials/Texture.asset` |
| `visual-avatar.asset` | `Models/Avatar.asset` |

逐文件 GUID 与引用见 `asset-migration.json`。旧根 merchant_001/guide_001 及其 meta 在子目录
迁移并清理后移除；新增分类子目录由 Editor 创建 meta，不能用新 GUID 替换已迁移资源。
当前 Prefab 在 Remote_Characters 中分别改为 `npc/Merchant/visual`、`npc/Guide/visual`，
旧 content.npc 标签同步替换。保留当前 Prefab 的 GUID 与 entry 对应关系。

清理范围：

- 两 NPC 的旧 1/2 源目录及 meta；旧 3 目录的剩余壳和根目录 meta。
- `NpcContent/npc/merchant_001`、`guide_001` 下所有版本交付物；重新生成当前名称目录。
- `Assets/HotUpdate/NpcAnimation/Merchant`、`MerchantV2`、`Guide`、`GuideV2` 的旧专属
  动画源码、asmdef 和 meta：当前两个样例是 builtin，无需保留历史 DLL 编译源。
- 两种旧 NPC 的全部 26 个 Address 按清单替换/移除，不把其他 character/* entries 删除。
- AddressableAssetSettings 的旧 NPC Labels、测试中的旧 DLL 程序集名/入口/路径与旧 JSON。

`cleanup-candidates.json` 不包含完整的目录 meta 与 label 操作，执行时必须结合以上步骤。
删除前核对被删 GUID 的反向引用；保留资源依赖旧资源时先迁移依赖，不能先删后补。
Prefab 依赖的共享 URP Shader/Gameplay MonoScript 属于保留项。
检索中没有发现 SampleScene/其他 scene 或 prefab 写入 merchant_001/guide_001 身份；
已有 Alice/Ryan 本地角色和其 character/* 内容不进行身份替换。
动态库存的 `npc:{name}.inventory` 跟随生成入口统一修改，旧存档不做身份映射。

## 4. 冻结的保存、错误和离线约定

- 世界存档使用 `version: 3`；对话归档使用 `snapshotVersion: 2`，拒绝旧 1/2 世界存档与旧 1 对话快照。
  不以字段缺失猜测格式，不静默丢弃 npcContents；旧用户文件保持原样。
- 安装索引：persistentDataPath/NpcContent/installed-current.json；顶层为记录数组，字段
  `name/displayName/description/avatarPath/manifestJson`。manifestJson 是本地安装记录内的原始 JSON 文本，
  不是工具网络参数，也不作为 Runtime 网络载荷。它不计算或持久化 manifest hash。
- 目录缓存：同目录的 catalog-cache-current.json，只接受新索引结构；旧 installed.json 和
  catalog-cache.json 不读取。如果发现旧安装记录且没有新记录，UI 提示重新安装，不自动删文件。
- 目录刷新失败允许回退到最后一次完整的新格式缓存；请求取消不触发回退。离线生成只使用完整
  新安装记录和可加载缓存。缓存缺失显示需修复，离线修复不能成功。
- 无待下载字节不代表资源有效：必须实际加载 Prefab/必要资源并检查参数，再提交安装或激活实例。
- 旧/未知字段、重复字段、非法 name、身份不一致是内容格式无效；仍保留超时、大小、同源、
  路径穿越检查。资源名不接受大小写别名，大小写碰撞在作者/目录阶段被拒绝。
- 后续稳定错误按原因区分：`NPC_CONTENT_INVALID`、`NPC_RESOURCE_NOT_READY`、
  `NPC_RESOURCE_INCOMPLETE`、`NPC_CONTENT_NOT_INSTALLED`、`NPC_CONTENT_CACHE_MISSING`、
  `NPC_SAVE_FORMAT_UNSUPPORTED`；保持当前取消/忙碌/生成/NavMesh 错误语义。
  删除 NPC_CONTENT_VERSION_MISMATCH/CONFLICT 及 Player 兼容性提示。
- DLL hash/长度只用于确认字节完整，程序集/入口/接口确认能否加载；不建立 NPC 版本比较。
  验证证据仍绑定整体 candidate hash，与 NPC 表现版本无关。

夹具 world-save 包含玩家、Merchant、Guide 及空库存，conversation-snapshot 为两个空历史 Context，
用于结构和身份验收；不会调用 LLM、保存 Prompt 或构造真实玩家正文。不是当前解析器的有效输入。

## 5. 生产 smoke 替换设计

仍由阶段四同步切换生成器、发布脚本、事务测试及发布检查清单；本阶段不改生产门禁。

| 原场景名 | 目标场景名 | 通过条件 |
|---|---|---|
| dynamic-download | dynamic-download | 当前名称下载、完整准备、激活、注册成功 |
| animation-driver | animation-driver | builtin 正常；测试专用 hot-update 的字节和入口有效，坏 DLL 拒绝 |
| save-restore | save-restore | 当前资源恢复 format 3/2 存档；更换视觉后仍可恢复 |
| version-conflict | resource-integrity | 缺依赖、损坏 Prefab、错误 Animator 参数不提交成功记录/半成品实体 |
| cache-repair | cache-repair | 缓存驱逐后明确缺失；修复后实际加载成功 |
| catalog-restart-required | catalog-address-missing | 不比较版本；Address 缺失提示未就绪，重启加载当前 Catalog 后成功 |
| scene-cancel | scene-cancel | 切场等待取消收束，无迟到安装提交或实体激活 |
| manifest-reregister | manifest-reregister | Go 重启后仅身份绑定完整重发，工具能力保持 |

额外验收：展示名修改不改变身份；新 Context 使用当前 Profile，已有 Context 固定原 Prompt；
旧格式明确拒绝；离线完整缓存成功与离线不完整缓存失败；删除定义清理专用输出但不删共享资源。

## 6. 阶段一验证与阶段二交接

执行 `python Docs/NpcPresentationStage1/verify_stage1.py` 校验 JSON、跨夹具名称/路径、
无版本字段、源 GUID、地址迁移和当前依赖盘点；执行 `git diff --check` 检查补丁。

基线 C# 验证最初因现有生成 csproj 漏列 Gameplay 动画事件类型失败；类型源文件存在，
这不是本阶段引入的变更。随后 Unity 6000.3.19f1 批处理重新导入/编译，退出码 0、
未超时，无 error CS；日志
`unity-NPC-agent-client/Logs/npc-presentation-stage1-compile.log` 记录编译及成功退出。
日志存在授权 access-token 更新失败及退出时 Curl 42 信息，不将其计作 C# 错误。

2026-10-14 已修复本地生成工程：使用已安装 Rider 集成的命令行入口
`Packages.Rider.Editor.RiderScriptEditor.SyncSolution`，通过 Unity `-executeMethod` 重新生成
整套 csproj/solution。无需安装或打开 Rider IDE，也无需手工维护 Compile Include；
生成文件被 Git 忽略。生成日志为 `unity-NPC-agent-client/Logs/npc-stage1-regenerate-csproj.log`，
Unity 退出码 0、未超时。以后源码移动或新增后若生成工程再次过期，可使用相同入口刷新。

在 `unity-NPC-agent-client` 中执行原命令
`dotnet build Assembly-CSharp-Editor.csproj --no-restore --verbosity quiet` 已通过，退出码 0，
0 错误、3 警告：System.Net.Http 引用冲突，以及 HybridClrProjectSetup/ToolPackageReleaseGate
使用的 SetArchitecture/GetArchitecture 过时提示。未修改业务源码或抑制警告。

本阶段没有修改 Go/C# 生产实现、场景或 Addressables 配置，不运行业务 smoke，
不宣称新契约已能在 SampleScene 工作。阶段二必须运行 Go 全量三项检查、C# 编译和
新契约端到端 SampleScene 验证，最终阶段执行 Windows IL2CPP 与环境发布 smoke。
