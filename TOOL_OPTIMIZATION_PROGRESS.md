# NPC 工具优化进度

更新时间：2026-07-25  
工作分支：`feat/tool_enhance`  
开始基线：`2a9945d feat: 优化距离，场景工具`

## 目标

本轮工作计划完成以下六个阶段：

1. 建立稳定基线。
2. 使用稳定 `targetId` 重构世界目标查询与移动。
3. 增加容器发现并稳定容器标识。
4. 补齐双向物品转移和动态目标追踪。
5. 按 NPC 暴露真实可用工具。
6. 补齐自动化测试、协议验证和正式文档。

## 总体状态

| 阶段 | 状态 | 当前说明 |
|---|---|---|
| 阶段 0：基线审计 | 已完成 | 开始时工作区干净，确认基线提交、现有 7 个工具和标签/容器问题 |
| 阶段 1：统一 `targetId` | 代码已落地，未验收 | 目标发现、查询 Schema、移动参数和场景地标已经修改 |
| 阶段 2：容器发现 | 代码已落地，未验收 | 已新增附近容器查询，容器 ID 已改为语义化值 |
| 阶段 3：双向转移 | 代码已落地，未验收 | 已新增取物工具并抽取共享转移逻辑 |
| 阶段 4：动态目标追踪 | 代码已落地，未验收 | 移动过程中会重新追踪 NPC/玩家位置，状态查询已扩展 |
| 阶段 5：按 NPC 过滤能力 | 部分开始 | 工具接口已增加 `IsAvailable`，但 Unity 注册快照和 Go Registry 尚未接通 |
| 阶段 6：验证与文档 | 未开始 | 尚未完成 Unity 编译、Play Mode、Go、Race 和协议验证 |

> 当前是开发中间状态，不应视为可交付版本，也不应提交或合并。必须完成下方“剩余工作”和“验收清单”。

## 已落地结果

### 1. 世界目标模型

旧模型：

```text
npcTarget 标签
→ GameObject.name
→ targetLandmark
```

当前工作区模型：

```text
NpcEntity.npcId             → npc:<npcId>
PlayerMock.worldTargetId    → player:<playerId>
NpcLandmark.landmarkId      → landmark:<landmarkId>
```

已完成：

- 新增 `NpcLandmark` 组件。
- `gate` 配置为 `landmark:gate`，显示名称为“大门”。
- `warehouse` 配置为 `landmark:warehouse`，显示名称为“仓库”。
- NPC 和玩家改为按组件发现。
- `npcTarget` 已从场景对象和 `TagManager.asset` 移除。
- 目标唯一性改为校验稳定 `targetId`。
- 玩家 ID 会从 `PLAYER_ID` 配置同步到 `PlayerMock`。

当前预期目标：

```text
npc:Alice_001
npc:Ryan_001
player:local-player-1
landmark:gate
landmark:warehouse
```

### 2. 目标查询工具

旧工具：

```text
game_scene_get_npc_targets
```

当前工作区工具：

```text
game_scene_get_targets
```

新增可选参数：

```json
{
  "targetIds": ["player:local-player-1"],
  "categories": ["npc", "player", "landmark"],
  "maxDistance": 20,
  "reachableOnly": true
}
```

返回字段包括：

- `targetId`
- `displayName`
- `category`
- `distance`
- `isReachable`
- `pathDistance`
- `isDynamic`

旧工具名暂未保留兼容分支；Go 测试和文档中仍存在 `game_scene_get_npc_targets`、`targetLandmark` 引用，必须在阶段 6 统一迁移。

### 3. 移动工具

`game_npc_move` 输入已从：

```json
{ "targetLandmark": "warehouse" }
```

改为：

```json
{
  "targetId": "landmark:warehouse",
  "approachDistance": 1.5
}
```

已加入：

- 动态目标引用。
- NPC/玩家移动后的定时重新寻路。
- 目标位移阈值。
- 最大移动持续时间。
- 目标销毁或离线错误。
- 到达结果中的 `targetId`、距离和耗时。
- `game_npc_get_state` 中的目标位置、动态追踪状态和耗时。

### 4. 容器发现

新增工具：

```text
game_inventory_get_nearby_containers
```

计划返回：

- `containerId`
- `displayName`
- `ownerTargetId`
- `ownerCategory`
- `distance`
- `interactionRange`
- `inRange`
- 容量摘要

场景容器 ID 已从数字改为：

```text
npc:Alice_001.inventory
npc:Ryan_001.inventory
player:local-player-1.inventory
```

容器解析已改为只接受稳定 `containerId`，不再使用 GameObject 名称和显示名称作为隐式别名。

`CONTAINER_TOO_FAR` 已准备携带结构化数据，包括距离、交互范围和所属 `targetId`。

### 5. 双向物品转移

现有工具：

```text
game_inventory_put_item
```

新增工具：

```text
game_inventory_take_item
```

已抽取共享原子转移逻辑，计划统一处理：

- 源容器数量检查。
- 目标容量检查。
- 禁止部分转移。
- 源、目标双方最新库存摘要。
- 放入和取出方向对应的稳定错误码。

物品 ID 已从数字改为：

```text
rock
wood
axe
helmet
```

### 6. 工具可用性接口

`INpcTool` 和 `NpcTool<TArgs>` 已增加：

```csharp
bool IsAvailable(NpcToolContext context)
```

移动工具已经按 `NavMeshAgent` 判断可用性；物品栏工具已经准备按 `InventoryComponent` 判断。

尚未完成：

- `ToolsRegistry` 根据 NPC 过滤定义。
- Unity 注册消息携带 NPC 到工具名的映射。
- Go `UnityRegistry` 保存和校验每个 NPC 的工具集合。
- Go 执行前按 NPC 二次校验工具权限。
- NPC 上下线或组件变化后的能力快照更新。

## 当前改动范围

当前工作区包含约 18 个已修改文件和 6 个新增文件，主要涉及：

- 场景与物品数据。
- 世界目标查询与移动。
- 容器发现与双向转移。
- 工具错误结构。
- 工具可用性接口。

当前新增文件：

```text
NpcLandmark.cs
NpcLandmark.cs.meta
GetNearbyContainersTool.cs
GetNearbyContainersTool.cs.meta
TakeItemFromContainerTool.cs
TakeItemFromContainerTool.cs.meta
```

## 已知未完成项与风险

1. 尚未完成一次包含全部新增脚本的 Unity C# 编译。
2. Unity 生成的 `Assembly-CSharp.csproj` 尚未刷新到新增脚本。
3. 最近一次 `dotnet build --no-restore` 在进入源码编译前失败：缺少 `Temp/obj/Assembly-CSharp/project.assets.json`，错误为 `NETSDK1004`。
4. 因此当前不能宣称 C# 代码无编译错误。
5. 当前 Unity Editor 曾加载 `Temp/__Backupscenes/0.backup`，Play Mode 验证前必须重新打开磁盘上的 `SampleScene`。
6. Go 端协议和 Registry 尚未实现每 NPC 工具过滤。
7. Go 和 Node 测试仍使用旧的 `targetLandmark` 参数，需要统一迁移。
8. `ARCHITECTURE.md` 和 README 仍描述旧的 `npcTarget`/`targetLandmark` 契约，尚未同步。
9. 尚未验证语义化物品 ID 是否影响已有运行时库存或编辑器数据引用。
10. 当前改动尚未提交，不能用于回滚点或发布。

## 下一步执行顺序

### A. 先恢复编译闭环

- [ ] 让 Unity 刷新所有新增脚本及 `.meta`。
- [ ] 修复全部 C# 编译错误。
- [ ] 检查场景无 Missing Script。
- [ ] 重新打开 `Assets/Scenes/SampleScene.unity`。

### B. 完成阶段 5

- [ ] `ToolsRegistry.GetToolNamesForNpc`。
- [ ] `CommandDispatcher` 暴露已注册 NPC 实体快照。
- [ ] Unity 注册 DTO 增加每 NPC 工具名映射。
- [ ] `unity.tools.changed` 同步每 NPC 工具名映射。
- [ ] Go Protocol 校验 NPC 工具映射。
- [ ] Go Registry 按 NPC 保存能力。
- [ ] ToolExecutor 按实例、NPC、工具三元组校验。

### C. 完成阶段 6

- [ ] 更新所有 Go 测试中的 `targetLandmark`。
- [ ] 增加每 NPC 能力隔离测试。
- [ ] 增加结构化错误数据测试。
- [ ] 增加目标 ID 和容器 ID 重复测试。
- [ ] 增加动态目标销毁、路径失败、取消和超时验证。
- [ ] 更新 `ARCHITECTURE.md`。
- [ ] 更新 README 示例。
- [ ] 运行完整验证命令。

## 最终验收清单

### Unity

- [ ] Unity 编译 0 错误。
- [ ] Console 无 Missing Script、Tag 错误和协议解析错误。
- [ ] Ryan 查询目标时返回 Alice、玩家、仓库和大门，不返回自己。
- [ ] `game_npc_move` 可到达仓库和大门。
- [ ] 玩家移动后 Ryan 会重新规划路径并到达玩家当前位置附近。
- [ ] 附近容器查询返回稳定 ID、距离和所属目标。
- [ ] 放入和取出物品均为原子操作。
- [ ] 没有背包的 NPC 不暴露物品栏工具。
- [ ] Go 重启后 Unity 能重连并重新注册完整能力。

### Go 与协议

- [ ] `go test ./...`
- [ ] `go vet ./...`
- [ ] `go test -race ./...`
- [ ] `node GameMCPServer/test_mcp.js --start-server`
- [ ] JSON-RPC `arguments` 始终为对象。
- [ ] Go 不包含重复的 Unity 工具 Schema。
- [ ] 日志不记录玩家正文、模型全文或密钥。

## 当前结论

当前已经完成主要设计和大部分 Unity 端代码落地，但仍处于未编译、未联调、未验收的中间状态。下一次继续工作时，应从“恢复 Unity 编译闭环”开始，而不是继续增加新功能。