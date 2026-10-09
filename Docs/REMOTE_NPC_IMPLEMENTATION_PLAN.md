# 远端 NPC 下载、安装与生成实现方案

日期：2026-10-08  
状态：第一轮已实施并完成本地验证；第二、三轮待实施。目标方案和已实现内容由文末记录区分，当前实现事实仍以 `ARCHITECTURE.md` 为准。

## 1. 目标与范围

玩家打开新 UI，查看远端可用 NPC 和本地已下载 NPC，选择下载，下载完成后在当前场景生成该 NPC。每个 `npcId` 在一个运行中的 Unity 实例内至多存在一个游戏实体；多个玩家客户端可以各自拥有同一 NPC。

一个 NPC 内容版本包含：模型、动画、材质及贴图、System Prompt 与静态 Profile、动画控制脚本。列表头像、NPC 名称、说明和操作提示文案均由远端返回。

沿用版本发布：每个 NPC 用 `npcId + contentVersion` 唯一标识内容，不引入 NPC 类型、多个实例、实例 UUID 或额外 Agent 定义版本。模型资源和脚本的地址均包含内容版本。原有游戏 release 负责 Player、AOT 契约、工具和基础内容；NPC 内容在其兼容范围内独立下载。

按三轮实施，每轮最多使用一个 1M 上下文窗口。该约束是工作拆分和范围上限，不是凭上下文长度对实际工期的保证。每轮必须留下可独立运行的结果、验证证据和下一轮交接记录；发现超出范围的需求，不把它隐式加入首版。

### 首版必须完成

- 远端列表、头像和提示文案；本地安装记录及列表。
- 单任务下载、字节进度、取消、失败重试；完整校验后才写安装成功。
- 下载后自动生成；再次启动后可从本地列表手动生成。
- NPC 唯一实例、现有对话与工具能力、Go 重启重连、存档恢复。
- 动画控制脚本确实由远端 DLL 交付，不以本地固定脚本冒充代码热更新。
- 新 NPC 内容安装不要求玩家重建 Player；超出 AOT 契约或平台兼容范围时明确拒绝。

### 首版不做

- 游戏运行中替换已生成 NPC 的版本；卸载 DLL；跨端两阶段提交。
- 每 NPC 独立 Addressables Catalog、插件管理平台、通用依赖求解器。
- 动态下载新的 Agent 工具包或改变现有 ToolSet；NPC 使用现有工具。
- 并行下载、队列调度、应用层分片续传、数据库、安装历史台账、后台自动更新。
- 自定义生成坐标、复杂编辑器、商店、权限商城、任意第三方脚本安装。
- 卸载与缓存精确回收。首版不提供卸载按钮，避免共享 Bundle 和程序集生命周期问题。

## 2. 现有实现的复用与必要变化

| 现有位置 | 复用方式与必要修改 |
|---|---|
| `Assets/Scripts/Networking/ContentAssetProvider.cs` | 唯一 Addressables 入口；复用下载大小、依赖下载、加载、实例 lease |
| `ClientContentBootstrap.cs` | 启动时更新共享 Catalog；游戏运行中不更新 Catalog |
| `CharacterContentCatalog.cs` | 保留旧角色路径；复用外观字段，新增按 NPC 清单直接加载的入口，不要求所有新 NPC 预写进旧角色目录 |
| `CharacterVisualController.cs` | 复用 VisualRoot、表现校验和 lease；增加配置入口与严格加载结果，远端 NPC 失败不能被 fallback 掩盖为生成成功 |
| `HybridClrToolPackageLoader.cs` | 复用/小范围提取字节哈希、AOT metadata、程序集加载逻辑；不让动画包参加工具发现 |
| `AgentHostClient.cs`、`NpcEntity.cs` | 接入动态生成、完整 Manifest 更新与实体可用事件，防止 OnEnable 提前注册半成品 |
| 公共 UPM 包的 `RuntimeManifest` | 添加远端 NPC 内容绑定，禁止在 Assets 定义第二份 RuntimeManifest |
| Go `internal/agent` | 按实体绑定加载版本化 Profile/Prompt；已有场景 NPC 保持现有配置来源 |
| 现有内容构建、发布脚本 | 增加 NPC 产物和静态索引，仍使用唯一发布链 |

实施第一轮先核对上述接口与保存格式，不能仅按本文的建议类名盲目创建文件。建议类可以根据现有代码合并，但不要形成第二套网络、资源或 Entity 系统。

## 3. 远端交付：静态文件，不增加业务服务器

复用现有热更新 HTTP 文件托管与发布脚本。新增可变列表 `npc/index.json` 和不可变版本目录 `npc/{npcId}/{contentVersion}/`。同一版本发布后禁止修改文件，修订必须发布新版本。

### 3.1 列表

示意结构如下，最终字段在第一轮一次确定并落入测试夹具：

```json
{
  "schemaVersion": 1,
  "catalogContentVersion": "release-2026-10-08",
  "texts": {
    "remoteTab": "可下载 NPC",
    "localTab": "本地 NPC",
    "download": "下载并生成",
    "spawn": "生成",
    "alreadySpawned": "已在场景中",
    "restartRequired": "重启游戏后可下载此版本"
  },
  "npcs": [
    {
      "npcId": "merchant_001",
      "contentVersion": "1",
      "displayName": "旅行商人",
      "description": "可以交流并执行已有游戏工具。",
      "avatarPath": "npc/merchant_001/1/avatar.png",
      "manifestPath": "npc/merchant_001/1/npc.json",
      "manifestSha256": "<sha256>",
      "minPlayerVersion": "<version>",
      "maxPlayerVersion": "<version>"
    }
  ]
}
```

列表包含 UI 所需完整文本键，包含进度格式、空列表、错误码对应提示和所有按钮。展示文本用普通文本渲染，不执行 HTML 或远端富文本。缓存完整列表和已安装版本对应的展示信息。断网时使用已有缓存；首次无缓存时显示现有本地基础错误 UI，不以空白界面阻断退出。

头像通过同一受信任内容根下的普通图片请求按可见行加载，限制大小；关闭 UI 释放 Texture/Sprite。头像失败使用本地占位图，不下载角色模型。下载字节数由 Addressables 实际计算，远端仅决定展示文案。

### 3.2 NPC 清单

`npc.json` 包含以下字段，Profile/Prompt 正文直接放在此文件中，减少跨文件一致性与请求数量：

| 字段 | 含义 |
|---|---|
| `schemaVersion / npcId / contentVersion` | 格式、稳定 ID、版本 |
| `playerBuildId` | 兼容的基础 Player 身份；复用现有精确匹配规则 |
| `visual` | 版本化 Prefab、Animator Controller、动画、材质和贴图 Address |
| `animationScript` | DLL Address、程序集名、入口类型、长度、SHA-256；每版独立程序集名 |
| `profile` | 现有静态 NPCProfile 字段；`npcId` 必须一致 |
| `systemPrompt` | 复用现有模板校验要求的完整模板、locale；内容版本沿用 NPC contentVersion |

所有视觉依赖由构建时校验，优先使用 Prefab 的正常依赖关系；不要在运行时重复加载已经被 Prefab 引用的每张贴图。脚本 DLL 单独加载并验长度/hash。AssetBundle 完整性复用 Addressables 的 Bundle 校验与发布文件清单，不额外实现第二套 Bundle 下载器。

新增脚本只能使用基础 Player 已支持的 AOT 泛型与契约。复用启动时的 AOT metadata，不为每个 NPC 建 metadata 依赖树；构建门禁检查缺失依赖，需扩大 AOT 能力时发布新 Player。

### 3.3 Catalog 与发布约束

首版继续使用共享 Catalog。构建时纳入所有已发布且仍被安装记录/存档引用的版本化 Address；不能因索引只展示最新版就删除旧内容。

运行时刷新 UI 列表不会切换 Catalog。列表 `catalogContentVersion` 必须等于本次启动所用 Catalog 的内容版本才能开启远端下载；若列表已发布到更新的 Catalog，保留本地 NPC 操作，远端下载显示远端的重启提示。这使发布新 NPC 后可以刷新看见列表，但玩家可能需要重启一次才能下载；它是省去运行中 Catalog 切换与资源冲突协调的明确取舍。

发布顺序：不可变 NPC 文件/Bundle → Catalog 等现有 release 产物 → 校验 Unity 与 Go 都能读取 → 最后切换 `npc/index.json`。复用现有发布事务，不设计新的远端管理 API。

## 4. System Prompt 的交付与 Go 使用

满足“下载内容包含 System Prompt”：Unity 下载并校验 `npc.json`，将原始文件保存到本地安装目录，因此正文确实在本地。Unity 不将其写入 A2A、模型历史或日志，也不在客户端组装模型请求。

Go 从服务端配置的可信内容根读取同一个不可变 `npc.json`。Unity 只发送版本标识和清单 hash，不能发送任意 URL 或用本地正文覆盖 Go 配置。服务端独立下载并校验 hash、大小上限、严格 JSON、Profile 和模板占位符。

新增配置仅为 `NPC_CONTENT_BASE_URL`：Go 通过现有配置优先级读取；Unity 使用现有内容 channel 的根地址推导 NPC 列表，不另开一套环境变量系统。开发允许本地 HTTP，正式发布沿用 HTTPS 与现有部署信任边界，不新增签名服务或客户端密钥。

Go 请求只允许固定根下由合法 ID/version 构造的路径，拒绝路径穿越；限制响应大小与请求时间，不跟随到任意地址的重定向。不要持有 Registry/Session 的全局锁等待 HTTP。

新增小型 `NPCContentResolver`：以 `npcId + contentVersion + hash` 缓存成功解析的不可变定义；失败不入缓存。无数据库、定时刷新、TTL 或启动扫描，Go 重启后按需重新读取。

公共 Manifest 增加可选 `npcContents` 数组，每项只有 `entityId / contentVersion / manifestSha256`，其中 entityId 就是 npcId。保持现有 `entityIds` 和工具字段；没有绑定的现有场景 NPC 使用原配置。校验绑定只能指向本 Manifest 的实体且不可重复。它是现有协议的字段扩展，不新增 WS 方法、独立确认协议或 Unity 本地 LLM DTO。

Go 创建 Context 时先校验实体与绑定，从 Resolver 获取定义，再构建 Prompt。下载超时/失败返回可识别错误，允许下次对话重试；Unity 不将“本地生成成功”冒充“Go 对话已经可用”。已有 Context 继续使用创建时 Prompt，首版禁止其 NPC 在运行中换版本。

Save restore 使用当前实体绑定解析 Prompt，保存 NPC 内容版本及 hash 用于核对。版本不一致时明确失败，首版不做自动迁移。现有无远端绑定的存档仍按原规则恢复。

必须在第一轮更新 `ARCHITECTURE.md`：将“不进入 Unity/Addressables”改为“可随 NPC 内容保存到 Unity 本地，但 Go 独立加载可信正文并执行 LLM”；Prompt 正文仍不进入日志和对话存档。

## 5. Unity 安装与 UI

建议最小职责划分：`RemoteNpcCatalogClient` 获取列表/清单；`NpcContentInstaller` 串行下载和写安装记录；`NpcSpawnController` 管唯一实例；`NpcLibraryPanel` 展示并调用上述入口。安装记录可做 installer 内部模块，不必为每个职责再建接口与仓库。

安装目录：`Application.persistentDataPath/NpcContent/`。一个 JSON 索引维护每个 npcId 当前唯一已安装版本，同时保存该版本原始清单与远端展示信息。使用安全文件名与临时文件写入后替换；启动校验索引和清单 hash，忽略未提交的临时文件。

仅支持一项正在执行的下载，不排队。状态为：`未安装 → 下载中 → 已安装`，下载失败/取消回到先前状态；进度和错误是临时 UI 状态，不建立持久化状态机。

安装步骤：

1. 校验列表与启动 Catalog/Player 兼容性，获取并严格校验清单和 hash。
2. 用清单必需 Address 计算缺失大小，下载 Addressables 依赖并显示字节进度。
3. 验证 DLL 字节、入口契约和视觉资源可以解析；完整成功后才提交本地记录。DLL 真正加载安排在首次生成，不将程序集副作用放入安装事务。
4. 刷新本地列表，然后调用生成入口。安装成功但生成失败时保留安装记录，显示可重试生成。

取消仅保证不继续提交记录/生成。必须按现有 Addressables Await/lease 行为释放句柄，不能宣称底层网络请求必然瞬时停止；已完整缓存的 Bundle 可用于再次下载。

Addressables 缓存可能被磁盘压力或用户操作清除。进入本地列表和生成前，用 GetDownloadSize 检查资源：缺失时标记“需要补下载”，点击修复后重新校验；不能仅凭安装索引声称资源完整。不另建永久 Bundle 仓库，也不把缓存失效当成安装版本丢失。

UI 两个页签/区域足够：远端项显示头像、简介、兼容状态和“下载并生成”；本地项显示安装版本、资源状态和“生成/已在场景中/补下载”。若已有旧版且未生成，允许一次更新替换安装记录；已生成时禁用更新并提示重启。生成失败、对话暂不可用分别显示，不混为下载失败。

## 6. 生成与动画脚本

基础 Player 随包提供一个最小 NPC 根 Prefab，含现有 NpcEntity、NavMeshAgent、Inventory、交互与存档组件以及 VisualRoot。根对象先保持 inactive，填入 npcId、绑定版本并完成外观准备后才激活和注册。不能克隆已经存在的 NPC 或让远端 Prefab 自带权威根。

生成位置使用场景内一个配置好的 SpawnPoint，按当前 NavMesh 校验；失败显示提示，不随机寻找整张地图。使用固定少量偏移避免多个不同 NPC 重叠，无自定义摆放系统。

`NpcSpawnController` 维护按 npcId 的唯一实例及生成中集合：场景中已有同 ID 实体时直接拒绝重复生成；首次请求占位，所有成功/异常路径都释放生成中标记。恢复存档、下载自动生成和手动生成共用此入口。

首版动画脚本采用纯 C# 类，避免远端 Prefab 序列化热更新 MonoBehaviour 和复杂 Unity 生命周期：

- 在现有稳定 Gameplay AOT 程序集中定义窄接口 `INpcAnimationDriver`，具有 Bind、Tick、Dispose 三个生命周期。
- AOT 组件把 Animator 和必要的只读运动状态传给 driver，在主线程 Tick/Dispose。
- 热更新 DLL 的一个显式入口类实现该接口，由程序集名/类型名创建，不扫描所有类型，不做服务容器。
- driver 控制 Animator 参数、表现 Transform 等视觉行为；不能改 Entity ID、Inventory、网络或工具注册。
- 不使用反射式万能上下文；新增所需只读字段直接扩展稳定参数结构并更新兼容 Player。
- 入口验证包含接口实现、可实例化性、依赖程序集；发布只接收本项目可信构建产物。

加载顺序：确认基础 metadata → 校验 DLL → 幂等加载程序集 → 创建 driver → 实例化视觉 Prefab → 绑定 Animator → 激活根对象。每版本程序集名包含 NPC ID/版本，避免同名 DLL 替换；已加载失败不能承诺卸载/内存回滚，重试复用成功程序集，实例失败释放所有 lease 和 driver。

现有 VisualRoot 校验应明确允许 AOT 动画代理，拒绝权威组件，不把新增动画包交给 IAgentTool 扫描。新 NPC 使用当前 ToolSet 和执行时可用性检查，不增加工具协议。

实体激活后复用 Host 的实体注册事件及完整 Manifest 发布。UI 开放对话后，Go 首次创建 Context 才确认 Prompt 可用；已有运行时注册尚未到达 Go 时按现有暂不可用错误处理并重试，不增加逐 NPC ack 消息。

## 7. 存档、重启与故障行为

- 本地安装列表不是游戏存档；安装成功不意味着每次启动自动生成所有 NPC。
- 游戏存档在现有 Unity 保存结构中添加远端 NPC 的 npcId、版本和清单 hash；位置/Inventory 继续由 Unity 保存。
- 恢复顺序：检查所需安装版本/资源 → 共用生成入口创建缺失实体 → 恢复位置/Inventory → 发布 Manifest → 调用现有 Go Context restore。
- 缺内容时明确提示补下载，网络不可用则不恢复半套世界/对话；沿用现有恢复事务与错误返回，不新建事务框架。
- 本地只保留一个已安装版本，旧存档所需版本不同则显示版本冲突，允许玩家确认安装该不可变版本后重新恢复；首版不自动迁移、不多版本切换 UI。
- 切场销毁旧实体时清理唯一实例引用，继续复用已有冻结请求、等待在途工具、注销和 Manifest 更新流程。安装中的取消/场景变更不能自动在已卸载场景生成。
- Go 重启后 Unity 重连并重发带绑定的 Manifest；Resolver 按需重载，不恢复内存对话历史。
- 远端断网：已完整安装资源可以生成；LLM 对话是否可用取决于 Go 服务及定义解析，UI 不承诺离线对话。

## 8. 三轮实施及验收

### 第一轮：冻结契约，打通远端内容与 Go

工作内容：

1. 核对发布脚本、Catalog 版本来源、Host 注册和现有存档格式，冻结 index/npc.json/Manifest 扩展格式。
2. 更新 ARCHITECTURE 的目标边界；新增两名测试 NPC 的静态文件与发布校验，不改变旧 release 结构含义。
3. 实现公共 Manifest 扩展、Unity 序列化与 Go 校验；新增 Go 内容配置、Resolver 和按绑定创建/恢复 Context。
4. 保留未绑定场景 NPC 原路径；加入窄动画接口与示例 DLL 构建，不在此轮做完整 UI。

验收：两名 NPC 的不同 Prompt 进入正确 Go Context；Go 独立验证 hash；未知版本、非法路径、损坏 JSON、错实体绑定被拒绝；已有 Context 保持 Prompt；旧场景对话不回归。发布产物包括真实动画 DLL 与全部依赖。

Go 运行 `go test ./...`、`go vet ./...`、`go test -race ./...`；完成 Unity C# 编译和 SampleScene 基线验收。网络定义解析通过本地 HTTP 测试服务器验证，不依赖外部 LLM 的内容正确性。

### 第二轮：完成下载安装、生成和 UI

工作内容：

1. 实现列表获取、头像/文案、安装记录、单任务下载、取消与缓存失效检测。
2. 实现 inactive 根 Prefab、唯一生成入口、严格视觉加载与动画 DLL driver，接入 Host 注册。
3. 实现远端/本地 UI、自动生成、重复点击处理、最新 Catalog 重启提示及已生成版本的更新限制。
4. 用真实两名 NPC 在 Windows IL2CPP Player 验证远端下载、脚本执行与 Go 对话，不能只依赖 Editor 反射加载。

验收：首次启动只有头像/列表请求，不下载模型；选择一名 NPC 只下载其依赖；完成后生成唯一实例并显示动画；Prompt 对应正确；重复点击不多实例；取消/损坏 DLL/视觉失败不产生半成品 Entity；关闭 UI 无头像 lease 泄漏；重启后本地列表可生成。

### 第三轮：存档、发布与回归收尾

工作内容：

1. 接入远端 NPC 存档生成与 restore 顺序，处理缺资源、版本冲突和切场取消。
2. 完成生产构建门禁、不可变版本保留、发布顺序、部署说明和失败提示。
3. 补充关键失败路径测试；修复真实 Player 的裁剪/AOT、资源生命周期和注册竞态。
4. 更新 ARCHITECTURE 到最终实现，更新热更新运维/发布检查清单，移除临时测试入口。

最终验收必须覆盖仓库既有 SampleScene 七项：Runtime 注册、普通/流式对话、移动到 warehouse/gate、取消 Task 与移动、Go 重启重连及 Manifest、Inventory 和存档恢复、Console 无编译/Missing Script/线程/协议异常。另验证缓存被清除后的补下载、NPC 更新后旧 Context 不被混用、旧存档版本冲突、当前运行中发布新 Catalog 的重启提示。

每轮结束将实际改动、命令/日志位置、已通过和未通过项、下一轮入口追加到本文。验证受环境或凭据阻塞时明确记录，不能将未执行项目标记为通过。

## 9. 实施预算与防止范围膨胀

以新增约 4 个 Unity 业务职责、1 个 Go Resolver、1 个稳定动画接口为起点；小型 DTO 与测试夹具不计作独立系统。优先修改已有 provider/Host/存档入口，不为可替换性创建多层接口。

每轮优先使用约 70% 上下文完成实现、20% 验证修复、10% 文档与交接；比例是执行建议。首轮若发现必须重做发布平台或动态工具体系，先退回本方案边界，不能带着未解决的框架建设进入第二轮。

不以“以后可能需要”为由增加数据库、NPC 类型层、通用安装图、跨端激活事务、DLL 卸载、独立 Catalog 或完整插件 SDK。唯一允许扩大范围的条件是已证明某项首版验收无法通过，并在本文记录具体原因与最小修正。

## 10. 第一轮交接记录（2026-10-09）

已完成：

- 核对 Host 对 instanceId 添加运行期后缀、注册/完整 Manifest 快照与重连、release
  contentVersion 来源、共享 Catalog 以及 snapshotVersion=1 格式。契约冻结在
  `Docs/REMOTE_NPC_CONTENT_CONTRACT.md`，真实夹具在 `NpcContent/npc/`。
- SDK 增加可选 NpcContents，Unity Host/Transport 全链路保留，inactive Entity
  绑定入口拒绝错 ID；Go 拒绝非法/重复/不存在的绑定、未知或重复绑定字段。
- 新增 NPC_CONTENT_BASE_URL、Go Resolver、按绑定创建 Context 和 restore。
  Go 独立验证原始清单 hash、严格 JSON/Profile/模板/固定版本地址；成功缓存、失败
  可重试、HTTP 不持有生命周期/Registry 锁。原场景 NPC 配置路径和旧归档保留。
- 归档可选记录版本/hash，冲突拒绝且不替换现有 Context；已有 Context 保留 Prompt，
  跨重连内容身份变化拒绝复用。完整 Manifest 更新禁止仍在线实体换内容。
- 两名 NPC 使用不同 Prompt 和不同真实动画 DLL。新增窄动画 AOT 接口，样例 DLL
  构建、程序集/入口/视觉依赖校验、版本化角色资产和 LocalDevelopment Bundle。
  DLL 仅用基础 AOT/Unity 动画能力，不加入工具包或 ToolSet。
- 统一验证器增加 NpcContent 模块，Go CLI 复用服务端严格解析器。原生产候选台账
  扩展静态 NPC 文件；同一提升脚本先交付不可变内容、最后切换索引并支持索引回滚。
  同版本改写会拒绝且保留原 release pointer/index。
- ARCHITECTURE 同步 Unity 可保存原始清单、Go 独立加载与执行模型的边界。

验证结果：

| 检查 | 结果与证据 |
|---|---|
| go test ./... | 全部通过；Artifacts/NpcRound1/go-test.log |
| go vet ./... | 通过，无输出；Artifacts/NpcRound1/go-vet.log |
| go test -race ./... | Windows 测试进程启动返回 0xc0000139，未进入测试；相同 Go 1.26.5 在已有 Ubuntu WSL 下全部通过，Artifacts/NpcRound1/go-race-wsl.log |
| 网络定义解析 | httptest HTTP 验证两名 Prompt、hash、非法路径、未知/损坏定义、重定向、取消、失败重试、缓存独立副本、存档冲突和旧场景路径 |
| Go 静态产物校验 | go run ./cmd/npc-content-validate ../NpcContent 通过，两名 NPC |
| C# 与样例 DLL/Bundle | Unity 6000.3.19f1 编译及构建通过；Logs/npc-round1-build.log、Logs/npc-round1-content-build.log，NPC_LOCAL_CONTENT_BUILD_SUCCESS |
| Unity EditMode | 35/35 通过；Artifacts/NpcRound1/editmode-results.xml |
| NPC 内容模块 | 通过；Artifacts/NpcRound1/content-validation.json 与 Logs/npc-round1-validation.log |
| SampleScene 七项基线 | 注册、普通/流式对话、warehouse/gate 移动、取消 Task/移动、Go 重启重连/Manifest、Inventory/世界及对话恢复、无 Console 错误/Missing Script 全部通过；Artifacts/NpcRound1/scene-report.json、go-scene.log、Logs/npc-round1-scene.log |
| 发布事务 | Scripts/Test-ContentReleaseTransaction.ps1 通过：原链路、篡改拒绝、NPC 版本不可变、指针保留与索引回滚 |

场景回归由 `Scripts/Test-NpcRoundOne.py` 配合 Editor 入口
`NpcRoundOneSceneSmoke.RunFromCommandLine` 完成。测试先启动本地 HTTP 内容托管、
固定响应的 OpenAI 兼容测试服务和真实 Go 进程，写 runtime-env.json；以该测试环境
启动 Unity 入口后，协调脚本驱动真实 A2A/MCP/Save API，并在主线程观测移动和保存/
恢复世界。完成后停止自建服务/Editor。模型正确性由本地 fixture 验证，不依赖外部
LLM；真实游戏行为由 Unity SampleScene 执行。测试生成的运行期证据位于忽略目录
Artifacts 和 Unity Logs，源测试入口只在 Editor 编译。

尚未执行：真实远端生产提升、基础 IL2CPP Player 裁剪与动画 driver 下载执行。
这些属于第二/三轮，本轮没有将它们标记为通过。开发样例沿用旧 0.1.0 release 身份，
不代表旧已发布 Player 包含新 AOT 接口；生产输入门禁拒绝用旧 0.1.0 身份发布，且
要求目标 Gameplay AOT metadata 包含 INpcAnimationDriver。

下一轮入口：

1. 先建立具有新 Player 身份的动画 AOT 基线，运行现有 HybridCLR 生成链，显式重新
   staging 样例以更新 playerBuildId、min/maxPlayerVersion、hash，再验证新 Player
   裁剪和之后的新 NPC 不需重建 Player。
2. 按冻结契约实现 catalog client、installer、spawn controller 和 UI；复用现有
   ContentAssetProvider、CharacterVisualController 和完整 Manifest 发布。
3. 使用 inactive 根设置 npcId 与 BindContent，在全部视觉/脚本成功后激活；Animator
   driver 在主线程 Bind/Tick/Dispose，不参与工具发现。当前只有绑定入口，尚无安装
   UI 或动态生成流程。
4. 正式发布前保留所有已发布旧版本的 Address/Bundle/静态目录，并补 Unity 与 Go
   从真实受信任端点读取的发布证据。不要用生产发布来替代本地 staging。
