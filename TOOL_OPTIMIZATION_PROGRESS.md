# NPC 工具优化结果

更新时间：2026-07-25  
工作分支：`feat/tool_enhance`  
开始基线：`2a9945d feat: 优化距离，场景工具`

## 状态

本轮工具优化的代码、Go 自动化测试、Race 检查、协议冒烟测试和正式文档已经完成。Unity Editor 不在当前执行环境中，因此 C# 编译、`SampleScene` Play Mode 和场景对象检查仍需在安装 Unity 6000.3.19f1 的机器上执行，不能以静态检查代替。

| 阶段 | 状态 | 结果 |
|---|---|---|
| 阶段 0：基线审计 | 完成 | 确认旧工具、目标标识和容器标识问题 |
| 阶段 1：统一 `targetId` | 完成 | NPC、玩家、地标使用稳定分类前缀 |
| 阶段 2：容器发现 | 完成 | 新增附近容器查询，强制稳定 `containerId` |
| 阶段 3：双向转移 | 完成 | 放入与取出共享原子转移逻辑 |
| 阶段 4：动态目标追踪 | 完成 | NPC/玩家移动时重新规划路径并返回实时状态 |
| 阶段 5：按 NPC 过滤能力 | 完成 | Unity 与 Go 全链路同步和二次校验 `npcTools` |
| 阶段 6：契约、测试与文档 | 自动部分完成 | 类型生成 Schema、Go/协议验证和文档完成；Unity Editor 验收待执行 |

## 最终实现

### 类型驱动工具契约

- `ToolArgsBase` 的具体参数类型是工具输入结构的事实源。
- `[ToolParameter]` 表达 required、数值范围、字符串长度/正则/枚举、数组长度/唯一项/元素约束和描述。
- `ToolContract<TArgs>` 在运行时首次访问时生成并缓存 JSON Schema。
- `NpcTool<TArgs>` 自动提供 Schema，具体工具不再包含 `JObject.Parse(...)`。
- Unity 执行前按照同一 Schema 严格拒绝未知字段、错误类型、缺失必填项和越界值，再反序列化为 `TArgs`。
- Go 校验器覆盖同一受控 JSON Schema 子集，不保存任何重复工具 Schema。

### 稳定世界目标

```text
NpcEntity.npcId          → npc:<npcId>
PlayerMock + PLAYER_ID   → player:<playerId>
NpcLandmark.landmarkId   → landmark:<landmarkId>
```

- `game_scene_get_targets` 支持按 `targetIds`、`categories`、`maxDistance` 和 `reachableOnly` 筛选。
- 返回 `targetId`、显示名称、类别、直线距离、NavMesh 可达性、路径距离和动态标识。
- `game_npc_move` 只接受稳定 `targetId`，动态追踪 NPC/玩家，并处理目标消失、路径失败、取消和超时。
- `game_npc_get_state` 返回当前目标、目标位置、动态追踪状态、路径状态、剩余距离和耗时。
- 旧目标标签、旧查询工具名和旧参数名均未保留兼容分支。

### 稳定容器与双向物品转移

- `game_inventory_get_nearby_containers` 返回稳定容器 ID、所属目标、距离、交互范围和容量摘要。
- 容器必须显式配置唯一 `containerId`；不再回退到 GameObject 名称。
- 玩家容器 ID 会跟随 `PLAYER_ID` 更新为 `player:<playerId>.inventory`。
- `game_inventory_put_item` 和 `game_inventory_take_item` 都先验证源数量与目标容量，不允许部分转移。
- 物品目录拒绝缺失或重复的 `itemId`。
- 距离过远等业务错误可携带结构化 `data`。

### 每 NPC 真实工具能力

- Unity 能力快照包含 `tools`、`npcs` 和 `npcTools`。
- `IsAvailable` 根据 NPC 的 `NavMeshAgent`、`InventoryComponent` 等实时组件状态判断。
- NPC 能力变化在 Unity 主线程检测；网络端串行发送 NPC 路由和完整工具快照。
- 注册请求与注册响应之间发生的 NPC 增删会在注册成功后重新对账。
- Go 校验工具目录、NPC 所有者、未知/重复工具名，并原子替换能力快照。
- 模型可见工具和实际执行权限都按实例、NPC、工具三元组检查。

### 日志边界

工具日志只记录事件、请求 ID、NPC、工具名、结果、错误码以及参数/消息/数据长度，不记录参数正文、工具结果正文、玩家消息或模型回复全文。

## 自动验证记录

以下命令已在 2026-07-25 通过：

```text
cd GameMCPServer && go test ./...
cd GameMCPServer && go vet ./...
cd GameMCPServer && go test -race ./...
AGENT_HOST_ADDR=127.0.0.1:18080 \
AGENT_HOST_BASE_URL=http://127.0.0.1:18080 \
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:18080/unity/ws \
node GameMCPServer/test_mcp.js --start-server
```

协议冒烟测试结果：5 项通过，0 项失败。

新增或扩展的自动验证包括：

- 类型、required、范围、正则、枚举、数组元素、唯一项和额外字段 Schema 校验。
- 注册快照中的重复 NPC、重复工具、未知工具和重复映射。
- 每 NPC 工具隔离、动态上线后的空能力、下线清理和执行前拒绝。
- `arguments` 保持 JSON 对象。
- 结构化业务错误数据。
- 工具超时发送 `unity.tool.cancel`。

## Unity Editor 验收清单

必须使用 `unity-NPC-agent-client/ProjectSettings/ProjectVersion.txt` 指定的 Unity 版本打开磁盘上的 `Assets/Scenes/SampleScene.unity`，然后完成：

- [ ] C# 编译 0 错误。
- [ ] Console 无 Missing Script、Tag 错误和协议解析错误。
- [ ] Unity 注册包含每个在线 NPC 的 `npcTools`。
- [ ] 普通对话能显示最终回复。
- [ ] Ryan 查询目标时返回 Alice、玩家、仓库和大门，不返回自己。
- [ ] `game_npc_move` 能到达 `landmark:warehouse` 和 `landmark:gate`。
- [ ] 玩家移动后 Ryan 重新规划路径并到达玩家当前位置附近。
- [ ] 附近容器查询返回稳定 ID、距离和所属目标。
- [ ] 放入和取出物品均无部分转移。
- [ ] 禁用 NPC 的背包组件后，该 NPC 不再暴露物品栏工具。
- [ ] Go 重启后 Unity 重连并重新注册完整能力。

上述清单属于需要真实 Unity 主线程、NavMesh 和场景资源的验收，不在 Go 或 Node 测试中伪造通过。
