# NPC 资产接入与动画事件简化计划

> 状态：待实施  
> 日期：2026-10-10  
> 当前事实源：`ARCHITECTURE.md`  
> 已实现基线：`History/RemoteNpc/REMOTE_NPC_CONTENT_CONTRACT_V1.md`  
> 历史实施记录：`History/RemoteNpc/REMOTE_NPC_IMPLEMENTATION_PLAN.md`

## 1. 目标

本次修改解决两个直接相关的问题：

1. 普通 Asset Store 人物必须拆分并重复声明 Controller、动画、材质和贴图 Address，
   每个 NPC 还必须提供动画 DLL，导致接入成本高于常规 Unity Prefab 工作流。
2. 动画 Driver 只能每帧读取移动速度，不能可靠感知移动、思考和说话的生命周期。

完成后，普通 NPC 的最小输入为一个已配置完成的视觉 Prefab、头像、静态 Profile、
Prompt 和稳定 ID/版本。Prefab 自带模型/FBX、骨骼网格、材质、贴图、Animator、Avatar、
Controller 与 AnimationClip 引用；Addressables 自动收集传递依赖。普通 NPC 复用内置动画
Driver，只有确实需要特殊动画逻辑时才交付热更新 DLL。

本计划不改变 Go/Unity 权威边界、A2A/Runtime/MCP 方法、工具 Schema 来源、存档绑定、
不可变版本发布和主线程规则。计划完成并验证前，现有 v1 行为仍是有效实现。

## 2. 设计决定

### 2.1 Prefab 是唯一视觉入口

新增 NPC 内容清单 schema v2。`visual` 只要求：

```json
{
  "visual": {
    "prefabAddress": "npc/blacksmith_001/1/visual"
  }
}
```

运行时实例化后严格检查 Prefab：

- 至少有一个 `Animator`，并已设置 Avatar 和 RuntimeAnimatorController；
- 模型、骨骼、材质、贴图、Controller 和 AnimationClip 通过 Prefab/Controller 的正常
  Unity 引用形成依赖；
- 不得包含 `NpcEntity`、`NavMeshAgent`、`InventoryComponent`、工具、Registry、网络或
  第二套运行时组件；
- 视觉实例仍只能挂在本地 AOT 权威根的 `VisualRoot` 下。

v2 删除 `animatorControllerAddress`、`animationAddresses`、`materialAddresses` 和
`textureAddresses`。Installer 只对 `prefabAddress` 调用 Addressables 依赖下载；不再维护
一份可能与 Unity 实际依赖图不一致的手写列表。共享 Shader 继续由既有共享 Group 持有。

已经发布的 v1 文件、Address 和存档不可修改或删除。解析器明确按 `schemaVersion` 处理：
v1 继续读取冻结字段，v2 使用简化字段；两者规范化为同一个运行时模型，不通过字段缺失
猜测版本，也不把 v2 失败静默回退成 v1。

### 2.2 普通动画不要求 NPC 专属 DLL

v2 使用显式动画模式：

```json
{
  "animationDriver": {
    "kind": "builtin",
    "driverId": "standard-locomotion"
  }
}
```

`standard-locomotion` 位于稳定 Gameplay AOT 程序集，约定 Animator 参数：

| 参数 | 类型 | 含义 |
|---|---|---|
| `Speed` | float | NavMeshAgent 实际速度，用于 Idle/Walk/Run 混合 |
| `Moving` | bool | 一次移动操作已开始且尚未结束 |
| `Thinking` | bool | 请求已发出且尚未收到有效回复 |
| `Speaking` | bool | 正在播放回复对应的说话表现 |

特殊 NPC 可选择 `kind: hotUpdate`，继续声明 DLL Address、程序集名、入口类型、长度和
SHA-256。热更新 Driver 仍不得注册工具或持有网络/Entity 权威。Builtin 与 hot-update
二选一，未知 kind 或 driverId 直接拒绝。

### 2.3 用 Editor 作者配置生成发布契约

新增 Editor-only `NpcContentDefinition`（可采用 ScriptableObject）和统一构建入口。作者只填：

- `npcId`、`contentVersion`、显示名和兼容 Player；
- 完成配置的视觉 Prefab 与头像；
- builtin Driver ID，或可选 hot-update Driver 定义；
- Profile 和 System Prompt 内容源。

构建器负责注册版本化 Prefab Address/Label、校验依赖、生成严格 `npc.json`、计算原始字节
hash/长度、更新 `npc/index.json` 并调用现有内容验证。现有只处理 Merchant/Guide 的
`BuildSamples` 不再作为新增 NPC 的生产入口；样例可继续作为测试夹具，但生产构建不得
硬编码 NPC ID、资源文件名或 GUID 重映射表。

## 3. NPC 表现事件

### 3.1 稳定 AOT 契约

在 Gameplay AOT 程序集中增加：

- `INpcAnimationEventSource`：事件和当前快照；
- `NpcAnimationEvent`：事件类型、operationId、结束原因；
- `NpcAnimationSnapshot`：`IsMoving/IsThinking/IsSpeaking`；
- `INpcEventDrivenAnimationDriver`：可选的事件源绑定接口。

保持现有 `INpcAnimationDriver.Bind/Tick/Dispose` 不变，使已发布 v1 Driver 继续可加载。
Driver 若实现 `INpcEventDrivenAnimationDriver`，Host 在普通 `Bind` 后再绑定事件源；Dispose
必须取消订阅。快照用于订阅时同步当前状态，避免丢失先于订阅发生的事件。

所有事件在 Unity 主线程发布。Driver 只能控制 Animator 或其他纯表现对象；事件不会进入
Runtime Manifest、Go 对话历史、NPC Profile 或世界存档。

### 3.2 状态模型与事件语义

移动和会话是两个可并行维度，不能继续用一个互斥枚举表示全部表现状态：

```text
Movement:     Idle | Moving
Conversation: Idle | Thinking | Speaking
```

现有工具可查询的真实移动状态保持不变；新的 Thinking/Speaking 只属于客户端表现。

| 事件 | 精确定义 |
|---|---|
| MovementStarted | `SetDestination` 成功后发布；参数或寻路前置校验失败不发布 |
| MovementEnded | 到达、失败、取消、存档恢复中断或销毁时恰好发布一次，并携带原因 |
| ThinkingStarted | `AgentHostClient` 取得发送锁、即将发送该 NPC 的 A2A 请求时发布 |
| ThinkingEnded | 首个非空 `TextDelta` 到达；无 delta 时在 completed/failed/cancelled/异常结束 |
| SpeakingStarted | 首个非空 `TextDelta` 到达；仅有最终正文时在 completed 发布 |
| SpeakingEnded | completed 后满足估算播放时间；failed/cancelled/销毁时立即结束 |

每次移动和每次响应使用独立 `operationId`。结束方法只接受当前活动 ID，重复或迟到事件
保持幂等。`ResponseFailed`、取消、网络异常、场景切换和对象销毁必须收束所有仍活动状态。

### 3.3 流式文本的说话时长

首版不引入 TTS。以最终可见字符数和统一读取速度估算说话时长，并设置最短/最长值：

```text
expected = clamp(visibleCharacterCount / charactersPerSecond, min, max)
remaining = max(0, expected - elapsedSinceSpeakingStarted)
```

流式传输已经消耗的时间计入说话时间，避免 completed 后重复播放整段时长。计时由本地
主线程表现协调器持有，不放入热更新 Driver。将来接入 TTS 时，以真实音频播放开始/结束
替换估算器，不改变 Driver 事件契约。

### 3.4 事件路由

```text
NpcEntity 移动生命周期 ─────────────┐
                                     ├→ NpcEntity 表现事件源 → Driver → Animator
AgentHostClient/A2A 响应生命周期 ───┘
```

- `NpcEntity` 在现有集中移动退出路径发布移动事件。
- `AgentHostClient` 已持有请求对应的 `npcId`，负责生成 response operationId。
- A2A 回调仍只产生 SDK `AgentResponseEvent`；网络线程把状态变更投递到现有主线程队列，
  不直接访问 `NpcEntity` 或 Animator。
- Host 按 `npcId` 查找当前已注册实体；实体已注销时丢弃迟到表现事件。
- `NpcAnimationDriverHost` 绑定 Driver、事件源和 Animator，并在销毁时统一释放。

## 4. 两个敏捷迭代

### 迭代一：一个普通 Prefab 可端到端接入

交付一个可玩的纵向切片，而不是先铺设通用框架：

1. 增加 schema v2、Prefab 传递依赖下载和 prefab 内 Animator/Avatar/Controller 验证；
   保留明确的 v1 解析路径。
2. 实现 `standard-locomotion` builtin Driver，并使 hot-update Driver 在 v2 中变为可选。
3. 实现作者配置和统一构建入口，移除生产接入对硬编码样例复制器的依赖。
4. 选择一名现有样例迁移为 v2；从一个常规完整 Prefab 构建、下载、安装、生成和移动，
   同时用另一名 v1 样例验证兼容性。

迭代验收：

- 新 NPC 接入时不手填 Controller/动画/材质/贴图 Address，不创建专属 DLL；
- Prefab 的 FBX、Mesh、Avatar、材质、贴图、动画和 Controller 都随依赖下载；
- 缺 Animator、Avatar、Controller、越界组件或依赖时在构建或安装阶段给出明确错误；
- v1 已安装内容、旧存档和旧 Driver 仍能生成、移动与恢复；
- 构建产物不修改任何已发布 v1 字节或 Address。

### 迭代二：事件驱动动画并完成迁移

1. 增加事件源、快照和可选 Driver 事件接口；实现 builtin Driver 的事件订阅。
2. 在 `NpcEntity` 接入移动开始/结束，在 `AgentHostClient` 接入 thinking/speaking，完成
   主线程路由、operationId、取消、失败、迟到事件和销毁清理。
3. 为流式和仅最终消息实现统一说话计时；用 Animator 参数验证移动与会话可以叠加。
4. 将第二名样例迁移为 v2，删除不再被生产路径使用的硬编码 staging 逻辑；同步更新
   `ARCHITECTURE.md`、活动内容契约、运维和发布检查清单。

迭代验收：

- 移动成功、失败、取消和销毁均产生一对且仅一对开始/结束事件；
- 请求、首个 delta、completed、failed、cancelled 和无 delta 回复符合上表语义；
- NPC 可以同时 `Moving + Thinking` 或 `Moving + Speaking`，Animator 不被错误重置；
- 取消或切场后没有残留 Thinking/Speaking，迟到 SSE 不改变新请求状态；
- v1 Driver 继续使用旧 Bind/Tick，v2 builtin/custom Driver 均正确工作；
- 生产候选仍通过不可变版本、hash、存档、重连和 NPC runtime smoke 门禁。

## 5. 预计代码落点

| 位置 | 修改 |
|---|---|
| `Assets/Scripts/Networking/RemoteNpcContent.cs` | v1/v2 严格解析、规范化模型、Prefab 依赖下载 |
| `CharacterVisualController.cs` | 使用 Prefab 内 Animator/Avatar/Controller，取消 v2 强制覆盖 Controller |
| `NpcLibraryRuntime.cs` | builtin/hot-update Driver 选择、事件源绑定与释放 |
| `Gameplay/INpcAnimationDriver.cs` | 保持旧接口；旁路增加事件源、事件 Driver 和快照契约 |
| `Gameplay/NpcEntity.cs` | 独立移动表现状态及恰好一次的开始/结束事件 |
| `AgentHostClient.cs` | 按 npcId/operationId 路由 thinking/speaking 主线程事件 |
| `Editor/HotUpdate/Release/NpcContentRelease.cs` | 通用定义驱动构建与 v1/v2 发布验证 |
| `GameMCPServer/internal/agent` | 严格接受 v1/v2 Profile/Prompt 外壳，继续由 Go 独立读取可信正文 |
| `NpcContent/npc` | 新 v2 夹具；保留所有已发布 v1/v2 历史目录 |

不得把作者 ScriptableObject、Unity Object 引用或 Animator 参数上传给 Go；Go 只关心可信
Profile、Prompt 和现有 `entityId/contentVersion/manifestSha256` 绑定。

## 6. 验证与完成定义

每个迭代都必须保持主分支可运行，并至少执行：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

Unity 侧补充 EditMode 契约测试、Addressables 依赖测试和 Windows IL2CPP smoke，并完成：

1. v1/v2 正常与未知/重复字段、错误版本、错误 hash 测试；
2. 完整 Asset Store 风格 Prefab 的构建、下载、离线缓存、生成和释放；
3. builtin 与 hot-update Driver、旧 v1 Driver 兼容测试；
4. 移动/思考/说话事件顺序、重叠、失败、取消、迟到和销毁测试；
5. SampleScene 原七项回归，以及动态 NPC 存档恢复、Catalog 重启提示和 Manifest 重注册；
6. Production candidate 的 NPC 八项 runtime smoke 和现有发布事务测试。

完成定义：两个迭代验收全部通过；没有新增第二套下载器、Entity、Transport 或 Schema；
活动文档只描述最终实现，阶段计划与 v1 契约保留在 `Docs/History/RemoteNpc`；
`ARCHITECTURE.md` 与代码、测试和发布检查一致。
