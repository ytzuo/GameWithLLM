# NPC 表现资产管线破坏性重构计划

日期：2026-10-14  
状态：阶段一已完成，阶段二至五待实施；运行时尚未切换。
当前架构事实源：`../ARCHITECTURE.md`。

## 1. 目标与范围

将 NPC 内容从“版本化资源包”重构为“按 ASCII 技术名称组织的当前资源集”。
每个 NPC 只有一个名称目录、一份当前定义和一个运行时实例；工程不保留历史版本资产，
历史由版本控制管理。系统不执行 NPC 内容版本、Player 版本范围、历史清单 hash 或
Catalog 内容版本匹配，只检查配置、依赖和实际加载结果是否完整有效。

本次替代 `NPC_CONTENT_AND_ANIMATION_SIMPLIFICATION_PLAN.md` 中有关稳定 ID/版本、
旧格式兼容、存档版本绑定和不可变 NPC 版本目录的设计；保留其中 Prefab 依赖图、
builtin/hot-update 动画驱动和表现事件的已实现能力。实施完成后将该旧计划归档并标明替代关系。

范围包含作者资源、NPC 静态目录、Addressables entries、安装与生成、公共内容绑定、
Go Profile/Prompt 解析、世界存档、对话归档及 NPC 发布门禁。不修改 A2A/Runtime/MCP
方法，不移除工具 ToolSet、整体 release 或 AOT metadata 的校验；不引入第二套资源加载器。

## 2. 固定设计决定

### 2.1 名称、展示名称与目录

- `name` 为唯一技术名称，规则为 `^[A-Za-z][A-Za-z0-9_]{0,63}$`；以大小写不敏感方式
  检查唯一性，保留配置中的原始大小写。构建时检查名称可作为 Windows 文件夹名称，拒绝设备保留名。
- `displayName` 为单独配置的展示名称，允许中文；资料库、对话和 UI 使用它。
- 名称示例固定为 `Merchant`、`Guide`，不拼接版本号或实体编号。
- `name` 是内容键；现有协议中的 `entityId`、Profile 的 `npcId` 等身份字段继续承担原职责，
  动态 NPC 的这些字段值统一使用 `name`，不为本次重构全局重命名协议字段。
- 修改展示名称不影响存档与地址；修改技术名称视为身份变化，本次不建立别名或自动重命名机制。

```text
unity-NPC-agent-client/Assets/Content/Npcs/
  Merchant/
    Definition.asset
    Visual.prefab
    Avatar.png
    Models/
    Materials/
    Animations/
  Guide/
    ...

NpcContent/npc/
  index.json
  Merchant/
    npc.json
    avatar.png
  Guide/
    ...
```

每个 NPC 只维护上述一套源资产；静态目录是构建器生成的交付物，不是第二套手工作者输入。
共享 Shader、材质等继续放在现有共享资产目录，不复制进每个 NPC。
视觉入口固定为 `npc/{name}/visual`，可选动画 DLL 入口为 `npc/{name}/animation-script`。
每 NPC 的 Label 为 `content.npc.{name}`，不含版本。

### 2.2 作者配置和文件契约

- `NpcContentDefinition` 保留 `name`、`displayName`、介绍、视觉 Prefab、头像、
  Profile、Prompt 和动画驱动选择；展示名称只有一个作者输入，生成 Profile 时复用该值。
- NPC 索引顶层保留 `texts`、`npcs`；每项只包含 `name`、`displayName`、`description`、
  `avatarPath`、`manifestPath`。路径由名称确定，不接受任意远端地址。
- `npc.json` 顶层只包含 `name`、`visual`、`animationDriver`、`profile`、`systemPrompt`。
  `visual` 只包含 `prefabAddress`；Profile 保留现有静态人格字段，`npcId` 值为名称。
  NPC 专用 `systemPrompt` 只包含 `locale`、`template`，仍检查必需占位符。
- 删除 NPC 文件的 `schemaVersion`、`contentVersion`、`playerBuildId`、Player 版本范围、
  `catalogContentVersion` 和 `manifestSha256`；采用一种固定结构，删除 v1/v2 解析与 fallback。
- 通用 Go Prompt Catalog 的现有格式不因本次 NPC 重构而全面改变；NPC 解析结果直接构造
  可用 Prompt 定义，不再借用其内容版本作为 NPC 绑定或缓存键。
- builtin 继续使用 `kind`、`driverId`；hot-update 继续使用 `kind`、`address`、
  `assemblyName`、`entryType`、`length`、`sha256`。后两者只验证下载字节完整性，
  不参与历史一致性、存档匹配或 Player 版本判断。

### 2.3 完整性检查与资源生命周期

完整性同时包含“依赖完整”和“实际可加载”，不能只凭下载大小为零宣布安装成功。

| 阶段 | 必须检查 |
|---|---|
| 作者构建 | 名称唯一、必需字段、Prefab/头像存在、依赖无缺失、Address 唯一 |
| 视觉准备 | 无 Missing Script；有效 Animator、Avatar、Controller；无权威组件 |
| 内置动画 | `Speed(float)`、`Moving(bool)`、`Thinking(bool)`、`Speaking(bool)` 齐全 |
| 远端读取 | 当前结构有效、身份一致、路径合法且同源、请求大小和超时受限 |
| 下载与安装 | 所有 Address 可解析、依赖下载成功、资源及可选 DLL 实际可读取 |
| 生成 | Prefab 实例可用、驱动可创建并绑定、NavMesh 前置条件满足 |
| 存档恢复 | 每个所需名称存在、资源可用，完整准备后再应用游戏状态 |

保留 Addressables Bundle hash、CRC、缓存和依赖机制，以及 DLL 字节完整性和接口检查。
这些不属于 NPC 版本兼容检查。继续复用 `IContentAssetProvider`，通过统一准备/释放流程
验证资源，避免安装和生成各实现一套检查；完整性验证失败不得以占位视觉或 fallback 冒充成功。

Catalog 仍只在现有内容启动阶段更新，本次不增加运行中刷新或替换已经生成的 NPC。
当前 Catalog 无法解析远端新增名称时，显示“资源尚未就绪，请重启后重试”，依据 Address
是否存在判断，不比较内容版本。自定义动画 DLL 的更新也在下次进程启动使用，不实现程序集卸载。

安装索引按名称存储当前清单与必要展示资料；不保存版本或历史 hash。目录刷新只更新发现结果，
安装/修复时取得当前清单，离线使用最后一次完整安装的清单及可用缓存。
安装记录、生成实例均在全部准备成功后提交；取消、失败、切场必须释放资源及生成中标记。

### 2.4 Runtime、Go 和存档

- 公共 `NpcContentBinding` 只保留 `EntityId`；线上 `npcContents` 每项只含 `entityId`。
  绑定仍必须对应同一 Manifest 中的唯一实体，完整 Manifest 与重连保留该引用。
- Go 从可信内容根读取 `npc/{name}/npc.json`，独立解析 Profile/Prompt；Unity 不提供模型
  历史或组装 Prompt。删除内容版本/hash 冲突及按该组合永久缓存的逻辑。
- 新 Context 独立读取当前配置；成功后该 Context 固定使用创建时配置。既有 Context 不因
  表现资产或人格文件变化被替换。解析失败不创建 Context，重试仍走当前读取路径。
- 世界存档中的远端 NPC 引用只保存 `entityId`，Transform/Inventory 等状态保留原机制。
  对话归档中 NPC 引用同样只保存身份，不保存版本/hash；restore 使用当前 Profile/Prompt。
- 无完整资源时恢复失败，不跳过实体，不部分提交；原 Context 和世界状态不得被失败结果覆盖。
  已完成准备后沿用“生成实体 → 恢复状态 → 完整 Manifest → Go Context restore”的顺序。
- 本计划默认不兼容旧安装索引、旧 NPC 清单、旧身份存档及旧对话归档，不实现自动转换和长期
  双格式分支。使用新的存档格式标记及新的 NPC 安装索引文件，旧文件明确提示不可用，
  不自动删除用户存档。这里的格式标记用于识别文件结构，不用于校验 NPC 资产版本。

### 2.5 构建与发布

`BuildDefinitions` 生成当前文件并可覆盖旧生成内容；删除 `WriteImmutable` 的 NPC 路径。
使用临时输出校验成功后提交静态目录，清理专用生成目录里不再存在的 NPC；校验失败不留下半套索引。
保留 `Remote_Characters` 的 Pack Separately，固定 Address 指向当前资源。

发布流程改为校验 NPC 当前资源和加载证据，不再要求历史版本目录或版本冲突证据。
保留整体 release/Catalog 发布事务、工具校验和环境 smoke。发布时先交付资源及当前清单，
最后切换 NPC 索引；静态当前路径使用缓存重新验证策略，避免长期缓存旧清单。
不增加逐 NPC 发布历史台账。整体 release 归档和已生成 Bundle 的运维保留策略不在本次清理范围。

## 3. 敏捷阶段与验收

阶段按依赖顺序实施，不预设日历工期。每阶段以一个可评审增量结束，记录改动、验证证据、
已知问题和下一阶段交接。阶段一是准备增量；阶段二是原子契约切换，不能将只修改一端的状态发布。
最终交付前不得把新旧协议兼容分支留在生产代码中。

### 阶段一：冻结契约与资产迁移清单

目标：将本计划中的决定落实成可执行的接口与迁移清单，消除实施歧义。

- 在 `ARCHITECTURE.md` 增加明确标为“待实施”的重构契约，描述协议字段变化、删除条件及切换边界，
  保留当前实现事实，切换完成后再替换现状描述。
- 盘点 SDK、客户端、Go、世界存档、对话归档、发布脚本中全部 NPC 版本/hash 依赖。
- 固定样例迁移：`merchant_001` 当前内容 → `Merchant`，`guide_001` 当前内容 → `Guide`；
  只保留当前 v3 源资产，列出需保留 GUID、移除旧 Address 和引用修复项。
- 定义新索引、清单、绑定及存档夹具，明确旧格式拒绝和离线安装行为。
- 更新生产 NPC smoke 场景设计：版本冲突改为缺失/损坏资源，Catalog 版本提示改为 Address 缺失提示。

验收：依赖清单无遗漏；两种样例的目录、Address、身份及清理项明确；现有项目仍可编译，
仅有契约文档和准备项，不宣称新行为已上线。

2026-10-14 完成记录：`ARCHITECTURE.md` §2.8 已标注待实施契约；
[阶段一交接](NpcPresentationStage1/README.md) 提供 44 个关联文件的盘点、16 个保留 GUID
的资产迁移项、26 个 Address 操作项、旧资产清理候选、新 JSON 夹具和 smoke 替换设计。
世界存档格式固定为 3、对话快照格式为 2，安装/目录缓存使用新的 current 文件名。
Unity Editor 批处理基线编译成功；生成 csproj 漏列动画事件源的问题已通过 Rider 集成
重新生成工程修复，原 dotnet build 命令通过（0 错误、3 个现有警告）。未修改生产实现或迁移资产。

### 阶段二：完成无版本的最小端到端切换

目标：两个 builtin NPC 在新目录下可构建、下载、生成、对话、存档和恢复。

- 修改作者入口、生成器和 Unity/Go 解析器，同步修改公共绑定及所有序列化/快照路径。
- 在 Unity Editor 中迁移当前资源与 `.meta`，更新 Address、场景实体身份、库存身份及相关配置引用；
  移除旧版本资源、旧 entries、旧版本静态清单和旧格式测试夹具，检查其他源目录是否残留历史副本。
- 同步切换 Runtime 注册、Go 配置读取、安装记录、世界存档及对话归档，删除版本/hash 比较。
- 新格式只走当前资源；旧索引与存档不进入新流程，不为旧身份保留别名。
- 构建和验证入口同步接受新契约，避免作者输出与生产门禁要求冲突。

验收：Merchant、Guide 使用固定 Address 成功生成并注册；普通与流式对话正常；新存档
恢复成功；旧格式得到明确提示；大小写重复名称被拒绝；改变 displayName 不改变身份。
Go 全量检查和 Unity C# 编译通过。本阶段是 Unity、Go、内容一起切换的最小交付单元。

### 阶段三：补齐完整性、取消和恢复闭环

目标：删除版本机制后，失败仍可预测且不会留下半成品。

- 统一安装和生成的实际加载验证与资源释放，保留 builtin 参数检查和 hot-update DLL 完整性/接口检查。
- 删除兼容性、版本冲突 UI 和错误码；改为配置无效、资源缺失/损坏、下载失败、取消和加载失败。
- 校验离线安装记录、缓存驱逐后的修复、Address 不存在的重启提示、单任务下载及同名唯一生成。
- 验证取消、切场、重连 Manifest、恢复前置失败与本轮生成失败的清理和回滚。
- 验证更新模型/动作/展示名称不阻止新格式存档恢复；已有 Context 保持原配置，新 Context
  及恢复 Context 使用当前配置。

验收：损坏 Prefab、缺失依赖、错误 Animator 参数、无效 DLL 均无法被标记成功；失败和取消
无孤立实例/资源 lease；库存和 Context 不因恢复失败被覆盖；缓存修复与离线完整缓存可用。

### 阶段四：收束生产发布与工程历史

目标：生产流水线只交付当前 NPC 资源，旧校验不再成为隐藏门禁。

- 修改构建/发布脚本、Go 静态验证命令、候选文件清单和发布检查文档，删除 NPC 不可变版本规则。
- NPC runtime smoke 必测：动态下载、动画驱动、存档恢复、资源缺失/损坏、缓存修复、
  Catalog Address 缺失提示、切场取消、重连 Manifest；证据仍绑定整体候选，防止复用旧结果。
- 检查生成目录对删除 NPC 的清理行为、失败构建的原子性和静态 HTTP 缓存策略。
- 全仓搜索残留 NPC 历史地址、实体编号目录和版本比较；整体 ToolSet/release 检查保留。
- 同步 `ARCHITECTURE.md` 为最终事实，归档已被替代的阶段计划；历史文档只作为记录，
  不保留其配套生产资产或参与构建。

验收：Windows IL2CPP 新 Player 与完整内容候选构建通过；部署后的两种 NPC 下载/恢复
smoke 通过；工程无历史 NPC 资产；现有生产事务及工具包检查通过。

### 阶段五：接入体验验收与交付

目标：证明新增和修改 NPC 都不需要版本维护。

- 新建第三个 ASCII 名称 NPC，只通过目录、Prefab、头像和 Definition 接入，不修改 Registry、
  Dispatcher 或 Go 工具 Schema；使用已有工具能力。
- 替换该 NPC 的模型/动画并修改中文 displayName，重建发布后新启动使用当前资源，
  新格式存档可恢复，不要求内容版本迁移。
- 完成 SampleScene 与实际 Player 验收，更新 NPC 作者操作说明及阶段完成记录。

验收：接入步骤仅为“名称目录 → 资源配置 → Definition → 构建发布 → 下载生成”；
没有填写或升级 NPC 版本号的步骤，没有历史资源副本，没有遗留旧格式 fallback。

## 4. 测试与最终完成定义

Go 代码变更阶段执行：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

Unity 代码变更阶段必须完成 C# 编译，并运行与变更相关的 EditMode/内容测试。
最终完成 Windows IL2CPP 与 `SampleScene` 验证：

1. Runtime 注册与 Go 重启后的重连、完整 Manifest 发布成功。
2. 普通对话、流式回复正常；取消停止对应 Task 与移动。
3. `game_npc_move` 到达 warehouse、gate；Inventory 正常。
4. 下载、生成、离线缓存、缓存修复、切场取消和新格式存档/Context 恢复正常。
5. 更新表现或 displayName 后恢复不受历史内容影响；改技术名称或删除 NPC 得到明确失败。
6. Console 无编译错误、Missing Script、线程或协议异常。

最终完成必须同时满足：代码、测试、作者流程、发布门禁和 `ARCHITECTURE.md` 一致；
旧 NPC 格式和生产版本判断全部删除；每名称只有当前资产；LLM/历史仅在 Go，Unity API
仅在主线程，工具契约仍由公共 UPM 包定义，日志不泄露正文、Prompt、参数或密钥。

旧存档不兼容是本计划的默认迁移决定，不是对用户文件的删除授权。若实施前要求保留旧存档，
应先调整本计划，增加离线的一次性转换阶段，不在运行时恢复版本兼容分支。
