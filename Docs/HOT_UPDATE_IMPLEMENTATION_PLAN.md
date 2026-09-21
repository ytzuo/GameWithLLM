# 客户端热更新实施计划

> 状态：H0-H3、A0 已完成；H4、A1 及后续阶段待实施
> 创建日期：2026-09-21  
> 适用项目：`unity-NPC-agent-client`（Unity `6000.3.19f1`，Windows）  
> 实施范围：第一部分 HybridCLR；第二部分 Addressables

## 1. 目标与固定边界

本计划为 Windows 客户端建立两条相互配合、但职责独立的热更新链路：

1. HybridCLR 负责加载按发布版本选择的 NPC 工具程序集。
2. Addressables 负责交付 JSON、HybridCLR 产物和游戏资产。

固定边界如下：

- Windows Player 使用 IL2CPP，并明确接入 HybridCLR。
- `Packages/com.gamewithllm.agent-runtime` 仍是公共契约 UPM 包，不参与热更新，
  也不增加热更新专用契约。
- 网络、Runtime Bridge、Registry、Dispatcher、工具基类、泛型参数基类、
  Schema 生成器和稳定游戏能力属于 AOT 客户端框架，不热更新。
- 工具目录以不可变 `ToolSetSnapshot` 表示；新发布可以新增、修改或逻辑删除
  工具，但只能在启动阶段构造并原子激活完整快照。
- 工具代码或结构 Schema 发生变化时必须生成新的实现/契约版本，并在下次启动
  生效；运行中的进程不卸载程序集，也不逐项修改 Registry。
- 删除表示从活动快照、Runtime Manifest 和执行路由中移除，不保证从当前进程
  卸载已加载程序集。已发布工具名进入历史台账，重新启用时必须延续同一逻辑
  身份并显式提升版本，禁止绑定到无关实现。
- 工具和参数的自然语言描述允许通过 JSON 独立调整；这属于元数据更新，
  不属于工具实现替换。
- 参数类型、JSON 字段名、必填项、范围、枚举和领域校验仍由 Unity 代码
  唯一生成。JSON 不得定义第二份结构 Schema。
- System Prompt 和 NPC Profile 只存在于 Go Agent Service。Unity 和
  Addressables 不下载、保存或转发 System Prompt。
- 远程内容只通过对象存储/CDN 交付，不经过 Go Agent Service 转发。
- 纯 JSON 和普通资产可在启动阶段或返回主菜单后的安全点激活；工具代码、结构
  Schema 或活动工具集合的变化可以在主菜单下载和验证，但只在下次启动激活。

在 H4 完成前，`ARCHITECTURE.md` 描述的 H3“只增不改”仍是当前实现事实；H4
实施时必须先更新架构决策，再修改 Registry 和启动编排。

## 2. 制定计划时基线

计划初稿时的仓库状态（H0 实施前）：

- Addressables `2.9.1` 已安装。
- Remote Catalog 尚未开启，`Remote.LoadPath` 仍未配置。
- HybridCLR 尚未安装。
- 除 `GameWithLLM.AgentRuntime` 外，客户端生产脚本仍主要位于
  `Assembly-CSharp`，尚未形成稳定 AOT/热更新程序集边界。
- `AgentToolDiscovery.RegisterAll` 在启动时扫描整个 AppDomain。
- `ToolsRegistry` 只提供逐个工具注册；每次注册都会触发一次
  `ToolsChanged`。
- `NpcTool<TArgs>` 缓存包含描述和 Schema 的完整 Descriptor。
- `ToolContract<TArgs>` 从参数 DTO 生成并缓存 Schema，同时把代码中的
  `ToolParameterAttribute.Description` 写入 Schema。
- `AgentHostClient` 已能在 `ToolsChanged` 后发布完整
  `runtime.manifest.changed`，不需要升级 Runtime Bridge 协议。
- Go 会在每次玩家输入开始时获取当前工具能力；正在执行的一轮 tool loop
  使用该轮开始时的能力快照。
- UI 仍混合使用场景序列化引用和 `Resources.Load`。
- 当前唯一业务场景为 `SampleScene`，应先继续作为本地启动场景。

H0-H2 的实际版本、生成命令、构建报告和 Smoke 验证结果记录在
`Docs/HYBRIDCLR_H0_H2_BASELINE.md`，H3 的原子注册实现与验证结果记录在
`Docs/HYBRIDCLR_H3_BASELINE.md`；后续阶段以这些基线和 `ARCHITECTURE.md`
的当前边界为准。

## 3. 总体目标结构

```text
Windows IL2CPP Player
├─ AOT Client
│  ├─ AgentRuntime UPM Contract
│  ├─ Client Core / Networking
│  ├─ Tool Framework / Registry / Dispatcher
│  ├─ Stable Gameplay APIs
│  ├─ HybridCLR Loader
│  └─ Addressables Bootstrap
├─ HybridCLR Tool Packs（由 ToolSetSnapshot 选择活动版本）
│  ├─ GameWithLLM.Tools.Pack.Warehouse01.dll
│  ├─ GameWithLLM.Tools.Pack.Quest01.dll
│  └─ 历史版本保留在发布存储中，但单次启动只加载候选快照所需版本
└─ Addressables Remote Content
   ├─ Client JSON Text Catalogs
   ├─ HybridCLR AOT Metadata
   ├─ HybridCLR Tool DLL/PDB
   ├─ UI / Sprite / Texture / Material
   ├─ Player / NPC Model
   └─ Scene / Scene Object
```

## 4. 统一版本与激活原则

### 4.1 发布单元

每次内容发布生成一个不可变 `releaseId`，例如：

```text
2026.09.001
```

发布清单至少包含：

```json
{
  "schemaVersion": 1,
  "releaseId": "2026.09.001",
  "minimumPlayerVersion": "1.0.0",
  "catalogVersion": "catalog-2026.09.001",
  "toolSetVersion": "15",
  "toolMetadataVersion": "12",
  "clientTextVersion": "8",
  "activeTools": [
    {
      "toolName": "game_warehouse_sort",
      "implementationVersion": 3,
      "contractVersion": 2,
      "packageId": "warehouse-01",
      "version": "1.0.0",
      "assemblyName": "GameWithLLM.Tools.Pack.Warehouse01",
      "address": "hotfix/tools/warehouse-01.dll.bytes",
      "sha256": "..."
    }
  ],
  "retiredTools": [
    {
      "toolName": "game_warehouse_sort_legacy",
      "retiredInToolSetVersion": "15"
    }
  ]
}
```

发布清单只描述客户端内容。Go 的 Prompt 版本由 Go 服务独立记录和发布。

### 4.2 激活顺序

```text
读取本地最后成功版本
→ 检查远程 Catalog/发布清单
→ 下载候选版本全部必需内容
→ 校验版本、地址、hash 和 JSON
→ 加载 HybridCLR AOT 补充元数据
→ Assembly.Load 候选工具 DLL
→ 发现工具并构造完整 ToolSetSnapshot 候选
→ 联合验证 ToolSetSnapshot 和客户端文本 Catalog
→ 原子激活工具快照并切换客户端文本 Catalog
→ 发布一次完整 Runtime Manifest
→ 记录最后成功版本
```

### 4.3 回滚边界

- JSON、Catalog 和普通资产可以在安全点切回上一兼容版本。
- DLL 在当前进程中 `Assembly.Load` 后不能依赖普通卸载完成回滚，因此代码、
  Schema 和活动工具集合只在启动时激活，失败或业务回滚均在下次启动选择上一
  个最后成功的完整发布。
- Registry 只交换完整不可变快照，不暴露逐项 `Unregister` 或原地 `Replace`。
- 新工具快照和配套描述 JSON 必须联合验证、联合激活；不得出现活动工具缺少
  描述，或旧 Catalog 描述新实现的混合状态。
- 删除工具只改变活动快照。旧 DLL、Bundle、Catalog、工具身份台账和回滚所需
  版本必须在回滚窗口内保留。

---

# 第一部分：HybridCLR 实施计划

## H0：冻结架构决策并建立验证基线

### 目标

在拆分程序集前获得可重复的 Windows IL2CPP 功能和性能基线。

### 实施项

1. 在 Windows Standalone Player Settings 中明确设置 IL2CPP 和 x86_64。
2. 记录 Unity、HybridCLR、Addressables、URP、API Compatibility Level 和
   Newtonsoft.Json 版本。
3. 将以下规则同步到 `ARCHITECTURE.md`：
   - 契约包不参与热更新。
   - 客户端稳定框架为 AOT。
   - 工具包只能添加新工具。
   - 工具描述 JSON 不得修改结构 Schema。
4. 构建一次未接入 HybridCLR 的 Windows IL2CPP Player，记录：
   - Player 是否正常启动。
   - 初始 Manifest 工具列表。
   - Build Report、包体大小和启动时间。
5. 保存当前所有工具生成的规范化 Schema 快照，作为后续拆程序集的回归基线。

### 验收标准

- Windows IL2CPP Player 构建、启动成功。
- Runtime 注册、普通和流式对话正常。
- `game_npc_move`、Inventory、取消、重连和存档恢复通过。
- 契约包没有任何热更新相关修改。
- 工具 Schema 快照可在 CI 中比较。

### 回滚点

本阶段只修改构建和文档配置，不移动生产类型。

## H1：拆分稳定 AOT 程序集

### 目标

从 `Assembly-CSharp` 中建立可被热更新 DLL 稳定引用的命名程序集。

### 推荐程序集

```text
GameWithLLM.Client.Gameplay
  NpcEntity
  NpcLandmark
  InventoryComponent
  NavMesh、库存和世界查询等稳定能力

GameWithLLM.Client.ToolFramework
  NpcTool<TArgs>
  ToolArgsBase
  ToolContract<TArgs>
  ToolParameterAttribute
  GameToolWrapper<TArgs>
  ToolsRegistry
  AgentToolDiscovery

GameWithLLM.Client.Core
  AgentHostClient
  A2A / Runtime Gateway / Save Coordination
  配置和主线程编排
  HybridCLR/Addressables 启动协调器

GameWithLLM.Client.BuiltinTools
  当前已有 Move、Query、Inventory 工具
```

依赖方向必须是无环的。若一次拆分会形成 Core、UI 和 Gameplay 循环，先拆出
`ToolFramework` 与 `Gameplay`，其余代码暂留 `Assembly-CSharp`；不得为了目录
整齐引入反向依赖。

### 实施项

1. 为上述边界创建 asmdef，并显式声明依赖。
2. 保留所有场景脚本和 `.meta` GUID；移动资源时同步移动 `.meta`。
3. 保持现有命名空间、序列化字段名和 MonoBehaviour 完整类型身份稳定。
4. 处理内部可见性；只暴露新工具确实需要的稳定客户端 API。
5. 为反射入口、Json.NET DTO 和远程 Prefab 依赖的稳定组件增加必要的
   `link.xml`/`[Preserve]`。
6. 每拆一个程序集就运行一次 C# 编译和 Windows IL2CPP 构建。

### 验收标准

- 场景没有 Missing Script。
- 工具名称、Schema 和可用性与 H0 快照一致。
- BuiltinTools 仍由当前启动发现流程注册。
- Windows IL2CPP Player 完成全部现有业务回归。
- Hot-update 工具能够只引用命名程序集，不需要引用 `Assembly-CSharp.dll`。

### 回滚点

按 asmdef 拆分批次逐一回退；不得使用破坏场景 GUID 的重新创建方式回滚。

## H2：接入 HybridCLR 和本地 Smoke Tool Pack

### 目标

先不依赖远程 CDN，证明 Windows IL2CPP Player 能加载和执行一个本地热更新
工具程序集。

### 实施项

1. 安装与 Unity `6000.3.19f1` 匹配的官方 HybridCLR Unity 包。
2. 完成 HybridCLR Installer，并固定包版本。
3. 创建 `GameWithLLM.Tools.Pack.SmokeTest.asmdef`。
4. 将 SmokeTest asmdef 加入 HybridCLR hot-update assembly 列表。
5. Smoke Tool Pack 仅包含：
   - 一个全新工具名。
   - 一个新的 `ToolArgsBase` DTO。
   - 一个不会修改存档的最小查询行为。
6. 执行 HybridCLR `Generate/All`，生成：
   - AOT generic references。
   - method bridge。
   - reverse P/Invoke（如需要）。
   - `link.xml`。
7. 为下列泛型/反射路径验证补充元数据：
   - `NpcTool<HotArgs>`。
   - `ToolContract<HotArgs>`。
   - `GameToolWrapper<HotArgs>`。
   - `ValueTask<AgentToolResult>`。
   - Json.NET 对 HotArgs 的构造、字段访问和反序列化。
8. 本地启动顺序固定为：加载 AOT 元数据，再 `Assembly.Load` Smoke DLL。

### 验收标准

- Windows IL2CPP Player 成功加载 Smoke DLL。
- 新工具生成正确 Schema，能够反序列化、校验和执行。
- 无 `MissingMethodException`、`ExecutionEngineException` 或缺失 AOT 泛型错误。
- BuiltinTools 的名称、Schema 和行为没有变化。
- PDB 仅进入 Development/QA 包，不进入正式包。

### 回滚点

关闭 HybridCLR Bootstrap 后，AOT BuiltinTools 和现有客户端仍可独立运行。

## H3：按程序集发现和工具包原子追加

### 目标

把“启动扫描全部程序集”扩展为“显式扫描一个新工具包”，并严格实现只增不改。

### 实施项

1. 保留 AOT BuiltinTools 的启动注册能力。
2. 在 `AgentToolDiscovery` 增加：

   ```csharp
   IReadOnlyList<IAgentTool> DiscoverFromAssembly(Assembly assembly);
   ```

3. 对传入程序集只接受：
   - 具体、非抽象、非开放泛型类型。
   - 实现 `IAgentTool`。
   - 标记 `[AgentTool]`。
   - 能由无参构造函数实例化。
4. 在 `ToolsRegistry` 增加：

   ```csharp
   RegisterToolPack(packageId, packageVersion, assemblyHash, tools);
   ```

5. `RegisterToolPack` 在锁外构造候选集合，在锁内完成一次性提交：
   - 包 ID、版本和 hash 合法。
   - 工具名在包内唯一。
   - 工具名与已注册工具不重复。
   - Descriptor 非空。
   - Schema 是 JSON object。
   - 整个包全部成功后才写入 Registry。
6. 一次工具包注册只触发一次 `ToolsChanged`。
7. 保存本进程已处理的 `packageId + version + hash + toolNames`，重复加载直接
   返回幂等结果。
8. 不实现 Unregister、Replace、Assembly unload 或覆盖同名工具的分支。
9. 加载发生在 Unity 主线程可控的启动状态；工具执行仍沿用 Dispatcher 的
   主线程和每实体 FIFO 规则。

### 验收标准

- 一个包含多个工具的包只产生一次完整 Manifest 更新。
- 包内任一工具无效时，整个包没有任何工具进入 Registry。
- 同名工具或重复包被拒绝，已有工具不受影响。
- 旧工具正在执行时可以加载候选包；旧调用完成语义不变。
- 新工具从下一次玩家消息开始对模型可见，不要求进入已开始的 tool loop。

### 回滚点

提交 Registry 前可完全拒绝候选包；提交后当前进程不提供删除路径。

## H4：版本化工具集切换、修改与删除

### 目标

把 H3 的“工具包原子追加”升级为“完整活动工具集原子切换”，允许一个新发布
新增工具、修改既有工具实现或 Schema，以及从模型可见目录和执行路由中删除
工具，同时不依赖程序集卸载，也不让调用方观察到半套新旧工具。

### 语义边界

- **新增**：活动快照中出现新的工具逻辑身份。
- **修改**：同一工具名选择新的实现版本；描述文字单独由 H5 Catalog 管理，
  实现或结构 Schema 变化必须显式提升版本。
- **删除**：工具名从活动快照、Runtime Manifest 和 Registry 路由中消失，并写入
  tombstone；代码、DLL 和缓存的物理清理由发布保留策略负责。
- **重新启用**：只能恢复同一逻辑工具身份并提升版本，禁止把已发布名称分配给
  无关语义的新工具。
- AOT BuiltinTool 的代码修改仍要求发布新 Player；远端发布只能选择其启用状态，
  不得用热更新 DLL 覆盖 AOT BuiltinTool。热更新工具可以在不同发布间选择新的
  包版本。

### 实施项

1. 在 release manifest 中把工具声明改为完整期望状态，而不是增量操作：
   - `toolSetVersion`。
   - `activeTools`，包含工具名、来源、`implementationVersion`、
     `contractVersion`、包版本、程序集和 hash。
   - `retiredTools`，记录 tombstone 和停用版本。
2. 新增不可变 `ToolSetSnapshot`、`ToolCandidate` 和严格校验器。候选快照必须满足：
   - 工具名大小写规范且唯一。
   - 每个活动工具只解析到一个允许的 AOT 类型或候选工具包类型。
   - 包版本、程序集名、SHA-256 和 Player 兼容范围一致。
   - `implementationVersion` 和 `contractVersion` 不回退；实现变化必须提升
     `implementationVersion`，结构 Schema 变化还必须提升 `contractVersion`。
   - 删除和重新启用均与历史工具身份台账一致。
3. 重构 `AgentToolDiscovery`：发现只产生候选，不直接修改 Registry。内置工具、
   每个选中的热更新程序集和 release manifest 分别输入候选构建器。
4. 将 `ToolsRegistry` 的活动状态改为一个不可变快照引用，并提供两阶段接口：

   ```csharp
   PreparedToolSet PrepareToolSet(ToolSetCandidate candidate);
   ToolSetActivationResult ActivateToolSet(PreparedToolSet prepared);
   ```

   `PrepareToolSet` 完成全部发现、Descriptor、Schema、冲突和历史版本校验；
   `ActivateToolSet` 在锁内一次交换活动字典和包索引，只触发一次 `ToolsChanged`。
   不提供对生产调用方开放的逐项 `Unregister`、原地 `Replace` 或程序集卸载接口。
5. 工具实现、结构 Schema 或活动集合发生变化时：
   - 主菜单只允许下载、验证并记录“待下次启动激活”的 release。
   - 启动时在 Runtime Gateway 连接和 A2A 输入开放前激活完整快照。
   - 本次启动只加载候选快照引用的热更新程序集，不预加载被删除或被替换版本。
6. `GetRuntimeTools`、`GetAvailableToolNames` 和 `ExecuteAsync` 必须从同一个快照
   读取。执行开始时捕获工具实例；快照切换不会改变已捕获实例，但正常流程中
   启动激活前不存在在途调用。
7. 对来自旧 Manifest 或异常竞态的已停用工具调用返回稳定业务错误
   `TOOL_RETIRED` 或 `TOOL_CATALOG_CHANGED`，不得路由到同名旧实例。
8. 候选 ToolSet 与 H5 的工具描述 Catalog 联合验证：所有活动工具和参数都有
   描述，Catalog 不得包含未声明的活动工具；验证完成前两者都不生效。
9. 成功激活后只生成一次完整 Runtime Manifest。现有
   `runtime.manifest.changed` 已是完整替换语义，不新增网络删除协议。
10. 保存最后成功的 `releaseId + toolSetVersion + catalogVersion`。候选失败时使用
    上一个最后成功快照；已激活版本的业务回滚在下次启动完成。
11. 维护 CI 历史台账，记录工具名、来源、历次 contract/implementation 版本、
    Schema hash、首次发布、停用和重新启用版本，防止删除后误复用名称。
12. H4 实现开始时先更新 `ARCHITECTURE.md` 的 HybridCLR 边界和启动流程；完成后
    H3 文档继续作为旧的本地追加基线，不改写其历史验证结论。

### 验收标准

- 从版本 N 升级到 N+1 时，可以同时新增一个工具、修改一个工具并删除一个工具；
  启动后的 Runtime Manifest 只包含 N+1 活动集合。
- 同名实现或 Schema 改变但未提升相应版本时，候选发布被拒绝。
- 删除后的工具不能被模型发现、实体能力枚举或 Dispatcher 执行；陈旧调用返回
  稳定错误，不会落到旧实现。
- 候选包、Schema、Catalog 或历史台账任一校验失败时，Registry 保持上一个完整
  快照，且不会发布中间 Manifest。
- 从最后成功版本启动可以恢复被错误发布删除或修改的工具；回滚不依赖卸载当前
  进程中的程序集。
- 同一候选重复激活为幂等操作，只产生零次或一次 `ToolsChanged`。

### 回滚点

候选快照激活前可以无副作用拒绝。代码或工具集合激活后不在当前进程内反向切换
程序集；记录上一成功发布，并通过重启选择其完整 ToolSet、Catalog 和 Addressables
内容。纯描述文本仍可按 H5 的规则在安全点原子回滚。

## H5：JSON 工具描述与客户端文本 Catalog

### 目标

允许不更新 DLL 即可调整已有和新增工具的自然语言描述，同时保持 Unity
结构 Schema 的唯一事实源。

### JSON 分类

```text
tool_metadata.zh-CN.json
  工具描述、参数描述、模型使用提示

agent_messages.zh-CN.json
  工具成功/失败时返回给模型的自然语言文本

ui.zh-CN.json
  UI 标题、按钮、状态和错误提示
```

System Prompt 使用 Go 侧独立文件，例如：

```text
GameMCPServer/config/system_prompt.zh-CN.json
```

它不进入 Unity Addressables。

### 工具元数据格式

```json
{
  "schemaVersion": 1,
  "contentVersion": "2026.09.001",
  "locale": "zh-CN",
  "tools": {
    "game_npc_move": {
      "description": "使 NPC 前往目标查询结果所指向的位置附近。",
      "parameters": {
        "targetId": {
          "description": "目标查询工具返回的稳定目标标识。"
        },
        "approachDistance": {
          "description": "与目标保持的距离；0 表示使用默认停止距离。"
        }
      }
    }
  }
}
```

### 实施项

1. 新增不可变 `ToolMetadataCatalog` 和严格 JSON DTO/校验器。
2. `ToolContract<TArgs>.CachedSchema` 只保留结构字段：
   - type。
   - properties。
   - required。
   - min/max。
   - pattern/enum。
   - additionalProperties。
3. `ToolsRegistry.GetRuntimeTools()` 克隆结构 Schema 后，从当前 Catalog 按
   `toolName + JSON parameter name` 注入 description。
4. 不再把 `NpcTool<TArgs>` 缓存的 Description 作为 Manifest 最终描述来源。
5. JSON 不允许覆盖类型、必填项、范围、枚举或路由字段 `entityId`。
6. Catalog 校验规则：
   - 每个活动工具都有描述。
   - 每个 Schema 参数都有描述或显式标记为无需描述。
   - JSON 不含未知、已停用工具和未知参数。
   - ToolSet 声明的全部活动工具存在于同一候选 Catalog。
   - 文本长度、locale 和版本满足限制。
7. JSON 验证成功后原子交换 Catalog，触发一次 `ToolsChanged`。
8. `agent_messages` 和 `ui` 使用稳定文本键；错误码、协议错误和日志事件名
   继续保留在代码中。
9. Go Prompt JSON 在 Go 启动或显式重载时验证；现有 Context 固定使用创建时
   Prompt，新 Context 使用新版本。

### 验收标准

- 只更新 JSON，不更新 Player 或工具 DLL，即可改变已有工具描述。
- 下一次 Manifest 和下一轮玩家消息使用新描述。
- JSON 无效时继续使用上一个已激活 Catalog。
- 单独更新 JSON 时，结构 Schema 与当前活动 ToolSet 的规范化 Schema 一致，
  只有 description 发生变化。
- Unity 不读取 System Prompt；Go 日志不输出 Prompt 正文。

### 回滚点

文本 Catalog 可以原子切回上一版本，但不得切换到无法描述当前已加载新增工具
的旧 Catalog。

## H6：接入 Addressables 工具包交付

### 目标

用第二部分建立的 Addressables 管线交付 AOT metadata、工具 DLL 和 JSON。

### 实施项

1. 新增 `HybridClrToolPackageLoader`：
   - 按地址加载 `TextAsset`/bytes。
   - 校验 packageId、assemblyName、版本和 SHA-256。
   - 先加载 AOT metadata，再加载工具 DLL。
   - 调用 `DiscoverFromAssembly`，并把结果交给 H4 的 ToolSet 候选构建器。
2. DLL 和补充元数据不得作为 MonoScript 或场景组件使用。
3. Development/QA 可加载 PDB；Production 不下载 PDB。
4. 工具包加载失败必须返回稳定错误码，并保持上一个最后成功 ToolSet 可用；
   没有远端成功版本时回到本地默认 BuiltinTools。
5. 记录 releaseId、packageId、版本、hash、阶段和耗时；不得记录完整工具参数。

### 验收标准

- 全新 Windows Player 可从远端下载并激活包含新增、修改和删除的目标 ToolSet。
- 缓存命中时不重复下载或注册。
- AOT metadata 缺失、DLL hash 错误、工具身份/版本冲突或 JSON 缺失都会使整个
  候选 ToolSet 拒绝。
- 拒绝候选包不会改变当前 Runtime Manifest。
- 成功激活后只发送一次完整 `runtime.manifest.changed`。

## H7：HybridCLR CI/CD 与生产门禁

### 目标

将生成、构建、校验和发布变成可重复流水线。

### 流水线

```text
锁定 Unity/HybridCLR/稳定程序集版本
→ Windows IL2CPP AOT Build
→ HybridCLR Generate/All
→ 生成并归档 AOT metadata
→ 构建版本化 tool packs 和完整 ToolSetSnapshot
→ 校验程序集引用和工具名基线
→ 校验 JSON metadata
→ 交给 Addressables 构建
→ Windows Player smoke test
→ 发布候选内容
```

### 发布门禁

- 工具包不得引用未允许的程序集。
- 新工具名不得与历史工具身份台账冲突。
- 同名替代实现必须延续同一逻辑身份、提升实现版本，并遵守
  `contractVersion` 与 Schema hash 规则。
- 删除和重新启用必须有 tombstone/历史台账记录。
- 所有新工具必须具有 JSON 描述。
- Schema 规范化快照必须可生成。
- HybridCLR metadata 必须与目标 Player 构建相匹配。
- 真实 Windows IL2CPP Player 必须执行每个新增或修改工具的 smoke call，并验证
  每个删除工具不再出现在 Manifest、能力枚举和执行路由中。

### 验收标准

- 同一提交能重复生成对应 Player、metadata、工具 DLL 和 hash 清单。
- 旧 Player 不会加载声明不兼容的新工具包。
- 连续发布两个包含新增、修改和删除的 ToolSet 后，活动目录与目标快照一致，
  且可在重启后回滚到上一成功快照。
- 失败构建不能更新生产 release 指针。

---

# 第二部分：Addressables 实施计划

## A0：资产盘点、地址规则与本地/远端边界

> 完成记录（2026-09-22）：逐项台账、稳定地址规则、Group 所有权、加载/释放
> 生命周期和已解释场景硬引用见 `Docs/ADDRESSABLES_A0_INVENTORY.md`；机器可读基线
> 位于 `Docs/Baselines/addressables-a0-inventory.json`，可由 Unity Editor 校验。

### 目标

在迁移前确定每类内容的所有者、生命周期和稳定地址。

### 实施项

1. 盘点：
   - `Assets/Resources/UI`。
   - `Assets/Art/UI`。
   - ItemData、Sprite 和 Texture。
   - Material、Shader 和 Shader Variant。
   - 玩家/NPC 模型、Animator、Avatar 和 AnimationClip。
   - 场景、场景物件和 NavMeshData。
   - HybridCLR AOT metadata、DLL、PDB。
   - 客户端 JSON 文本。
2. 为每项记录：本地/远端、Group、地址、Label、依赖、加载点、释放点和更新
   生效点。
3. 地址使用稳定逻辑 ID，不直接使用可变磁盘路径：

   ```text
   ui/window/chat
   ui/template/chat/player-message
   item/rock/icon
   character/ryan/visual/default
   scene/warehouse/main
   config/tool-metadata/zh-CN
   hotfix/tools/warehouse-01
   ```

4. `SampleScene` 和最小错误/下载 UI 第一阶段保持本地。
5. 更新内容前禁止删除或复用已经发布的业务 ID 和 Addressables 地址。

### 验收标准

- 每个计划热更新的资产只有一个所有者 Group。
- 每个运行时加载都有明确的释放责任人。
- 远端资产不存在未解释的场景硬引用。
- 地址、Label 和业务 ID 分离。

## A1：建立 Addressables Bootstrap 和 Windows Remote Catalog

### 目标

先建立可靠的初始化、Catalog 检查、下载和错误恢复能力，不迁移大规模业务资产。

### 推荐 Profile

```text
LocalDevelopment
QA
Production
```

路径示例：

```text
Remote.BuildPath = ServerData/[BuildTarget]/[ReleaseId]
Remote.LoadPath  = https://<content-host>/<channel>/[BuildTarget]/[ReleaseId]
```

Catalog 或 release index 使用稳定入口；Bundle 使用不可变、可长期保留的版本化
路径。不得覆盖或提前删除旧客户端仍可能引用的 Bundle。

### 推荐 Group

| Group | 内容 | 位置 |
|---|---|---|
| `Local_Bootstrap` | 加载/错误 UI、默认 JSON、兜底图标/材质 | Local |
| `Local_UnityPackage` | `unifiedraytracing` 等引擎包内容 | Local |
| `Remote_ClientConfig` | 工具、Agent message、UI JSON | Remote |
| `Remote_HotfixMetadata` | HybridCLR AOT metadata | Remote |
| `Remote_ToolPacks` | 版本化工具 DLL/PDB | Remote |
| `Remote_UI` | UXML、USS、PanelSettings、模板 | Remote |
| `Remote_SpritesTextures` | Sprite、PNG、Texture、Atlas | Remote |
| `Remote_Materials` | Material 和受控渲染依赖 | Remote |
| `Remote_Characters` | 玩家/NPC Prefab、Avatar、Animation | Remote |
| `Remote_Scenes` | 远端场景及场景专属资源 | Remote |

### 实施项

1. 开启 Remote Catalog。
2. 为所有 Profile 配置有效的 Remote Build/Load Path。
3. 明确禁止在启动过程中隐式进入游戏；Content Bootstrap 完成前冻结游戏输入、
   A2A 对话和 Runtime Manifest 初始化。
4. 实现 `ClientContentBootstrap` 状态机：

   ```text
   Local Ready
   → Addressables Initialize
   → Catalog Check/Update
   → Release Manifest Load
   → Compatibility Validation
   → Required Download
   → Candidate Validation
   → Activation
   → Enable Runtime/Game/UI
   ```

5. 实现统一 `ContentAssetProvider`，封装：
   - 初始化。
   - Catalog 检查/更新。
   - 下载大小和下载进度。
   - `LoadAssetAsync<T>`。
   - `InstantiateAsync`。
   - `LoadSceneAsync`。
   - `Release`/`ReleaseInstance`/`UnloadSceneAsync`。
6. 为每个 Handle 定义所有权；业务代码不得加载后丢弃 Handle。
7. 初期 Catalog 更新不自动清理旧缓存，清理策略在 A7 再启用。

### 验收标准

- LocalDevelopment、QA 和 Production 能生成独立 Windows 内容路径。
- 首次运行可显示下载大小、进度、失败和重试。
- 无网且无缓存时仍能显示 Local Bootstrap 错误 UI。
- 有最后成功缓存时，远端检查失败可以继续进入该版本。
- Addressables 初始化失败不会出现黑屏或空引用。

### 回滚点

未迁移业务资源前禁用 Bootstrap，`SampleScene` 仍可按原路径运行。

## A2：迁移 JSON 文本和 HybridCLR 交付物

### 目标

优先打通体积最小、最容易验证、同时又是 HybridCLR 所需的远端内容链路。

### 实施项

1. 将以下客户端文件作为独立 TextAsset/bytes 管理：
   - `tool_metadata.zh-CN.json`。
   - `agent_messages.zh-CN.json`。
   - `ui.zh-CN.json`。
2. 将 HybridCLR AOT metadata 放入 `Remote_HotfixMetadata`。
3. 将版本化工具 DLL 放入 `Remote_ToolPacks`；扩展名使用不会被 Unity 当作
   普通托管插件编译的 `.bytes` 形式。
4. AOT metadata 和工具 DLL 使用独立 Label，保证加载顺序可控。
5. Release Manifest 把完整 ToolSetSnapshot、retired tombstone、JSON、metadata 和
   DLL 绑定为同一候选发布。
6. 所有文件在激活前校验地址、长度、SHA-256、版本和 Player 兼容性。
7. JSON 解析进入不可变候选对象，验证通过后再替换 active catalog。
8. Go `system_prompt.zh-CN.json` 由 Go 部署流水线独立发布，不进入本 Group。

### 验收标准

- 只修改 JSON 即可改变 UI 文本、模型可见消息和工具描述。
- 新 DLL 可经 Addressables 下载并由 HybridCLR 加载，替换/删除后的活动工具集合
  与 ToolSetSnapshot 一致。
- 缓存命中时不会重复下载。
- 损坏 JSON、错误 hash 或缺失 metadata 会拒绝整个候选版本。
- 失败时上一个最后成功 ToolSet 和文本 Catalog 继续可用；首次启动失败时使用
  本地默认 BuiltinTools 和默认文本。

## A3：迁移 UI 布局、样式和图标

### 目标

移除生产 UI 对 `Resources.Load` 和场景直接 UXML 引用的依赖，使 UI 布局、
USS、图标和模板可独立更新。

### 实施项

1. 新增 `UiContentCatalog`，使用稳定 UI ID 或 `AssetReferenceT` 映射：
   - Gameplay HUD。
   - Chat、Inventory、ItemDispenser、SaveGame 窗口。
   - Chat message 和 Inventory slot 模板。
2. 把 `Assets/Resources/UI` 和 `Assets/Art/UI` 中计划更新的资源迁入
   Addressables；移动时保留 `.meta` GUID。
3. 替换 `UIManager`、`ChatWindow`、`InventoryWindow`、
   `InventoryInteractWindow` 和 `ItemDispenserWindow` 中的
   `Resources.Load`。
4. 采用“异步预加载、打开窗口时从缓存同步取模板”的策略，避免在输入回调中
   分散 Addressables 加载。
5. UXML、USS、字体、SpriteAtlas 按共同加载和共同更新关系分包。
6. 增加 UI 内容契约验证：代码依赖的 `Q<T>(name)` 元素必须存在且类型兼容。
7. 已打开窗口不原地切换布局；更新在窗口关闭重建或下次启动时生效。

### 验收标准

- 迁移范围内不再出现 `Resources.Load`。
- HUD、聊天、Inventory、ItemDispenser 和 SaveGame 在 Packed Play Mode 与
  Windows Player 中正常打开。
- 更新 UXML、USS、图标或 JSON 后无需重建 Player。
- Addressables Analyze 不报告 UI 与 Resources/场景的重复打包。
- UI 关闭后相关 Handle 按所有权正确释放。

### 回滚点

保留上一版本 Catalog 和 Bundle；回滚后重启或重建窗口生效，不复制一份资源
回 `Resources` 形成双来源。

## A4：迁移 Sprite、Texture、物品目录和物品视觉

### 目标

使图标、PNG、SpriteAtlas、物品模型和表现数据可更新，同时保持 Inventory、
工具和存档继续使用稳定 `itemId`。

### 实施项

1. 把 Item 业务字段与表现引用分离：

   ```text
   业务：itemId、数量规则、存档语义
   表现：名称文本键、描述文本键、Icon、WorldVisual
   ```

2. 用 `AssetReferenceSprite`/稳定地址替代直接 Sprite 硬引用。
3. 多个小图标按功能或物品族组织 SpriteAtlas，避免每张小图一个 Bundle。
4. 高频生成的世界模型采用“加载一次 Prefab + Instantiate/对象池”。
5. ItemCatalog 就绪前不开放 Inventory、发放器、存档加载和物品工具。
6. 建立发布 ID 基线：
   - 已发布 `itemId` 不得复用。
   - 删除使用 tombstone 或显式存档迁移。
   - 改变 MaxStackSize 等规则时必须验证旧存档。
7. 原始 PNG 只有在确实需要运行时解析时才直接加载；普通 UI/模型纹理优先使用
   Unity 导入后的 Sprite/Texture Addressable。

### 验收标准

- 打开 Inventory 不会隐式下载所有世界模型。
- 更新一个图标不会要求下载角色或场景 Bundle。
- 更新物品视觉后无需重建 Player。
- 旧存档中的全部 itemId 可以解析。
- Inventory、发放、取出、放入和物品查询工具全部回归通过。

## A5：迁移材质、玩家/NPC 模型和动画

### 目标

在不改变权威实体与工具逻辑的前提下热更新角色和场景物件的表现资源。

### 实施项

1. NPC/Player 根对象继续包含 AOT 权威组件：
   - Entity ID。
   - NavMeshAgent。
   - Inventory。
   - 工具和存档行为。
2. 在根对象下增加稳定 VisualRoot，由 Addressables 加载视觉 Prefab。
3. 视觉 Prefab 不得重复挂载 NpcEntity、网络、Registry、Inventory 或 Tool。
4. Character Catalog 用稳定 `characterId/appearanceId` 映射：
   - 模型 Prefab。
   - Avatar。
   - Animator Controller/AnimationSet。
   - Material/Texture。
5. Material 与 Shader 管理规则：
   - 专属 Material 跟随所属角色/模型。
   - 经过数据证明的共享资源才进入 Shared Rendering。
   - Shader、SVC 和 URP 兼容性必须在 Windows Player 验证。
6. 下载或加载失败时显示本地兜底视觉，但权威实体和工具仍可运行。
7. 已实例化模型默认不原地切换；下次生成、换装安全点或下次启动生效。

### 验收标准

- 替换 NPC 模型不改变 Entity ID、NavMesh、Inventory 和 Runtime Manifest。
- 模型加载失败时 NPC 工具仍能执行。
- Windows Player 无粉色材质、缺失 Shader Variant 或 Animator/Avatar 错误。
- 销毁视觉后 GameObject、Prefab Handle 和 Bundle 引用正确释放。
- 更新一个角色专属贴图不会下载其他角色 Bundle。

## A6：迁移场景及场景物件

### 目标

在内容基础设施稳定后，使业务场景和场景专属模型可远程更新。

### 实施项

1. 保留一个随 Player 发布的本地 Bootstrap Scene。
2. `SampleScene` 初期继续本地；先验证远端测试场景，再决定是否迁移。
3. 远端场景统一使用 `Addressables.LoadSceneAsync` 和
   `Addressables.UnloadSceneAsync`。
4. 全局单例、AgentHostClient、Content Bootstrap 和下载 UI 留在本地持久层。
5. 场景专属模型、材质、光照、NavMeshData 和探针按场景生命周期分组。
6. 禁止远端场景重复创建持久单例或第二条 Runtime Gateway 连接。
7. 场景版本切换前完成当前工具调用、存档协调和实体注销。
8. 远端 Prefab/Scene 引用的 MonoBehaviour 类型必须存在于 AOT 或已在场景
   加载前完成 HybridCLR 加载，并有裁剪保护。

### 验收标准

- 远端场景可以下载、加载、切换和卸载。
- 不重复创建 AgentHostClient、ToolsRegistry 或 Runtime 连接。
- NavMesh、NPC 移动、取消、Inventory 和存档恢复通过。
- 场景卸载后场景 Handle 和专属依赖被释放。
- 场景更新失败时仍可返回本地 Bootstrap Scene。

## A7：Addressables CI/CD、差量更新和回滚

### 目标

建立可重复、可审计、可回滚的 Windows 内容发布流水线。

### 完整 Player 发布

```text
切换 StandaloneWindows64
→ Validation
→ HybridCLR Generate/Build
→ Addressables Clean Build
→ Analyze / Build Layout
→ Windows IL2CPP Player Build
→ Fresh Install Smoke Test
→ 归档 content_state.bin 和 AOT 产物
→ 上传不可变 Bundle/Catalog
→ 发布 release manifest/current 指针
```

### 内容更新发布

```text
选择 Player 基线和原始 content_state.bin
→ 验证 Unity/Package/AOT 兼容性
→ Check Content Update Restrictions
→ Update a Previous Build
→ JSON/工具包/依赖/预算校验
→ Existing Install Upgrade Test
→ 上传新 Bundle 和版本化 Catalog
→ 最后切换 release manifest/current 指针
```

### CI 校验

- Remote Catalog 已开启，Remote LoadPath 非空。
- 地址、业务 ID、工具名唯一。
- 必需 Label 和 Group 存在。
- JSON 严格校验通过。
- 工具包引用白名单通过。
- AOT metadata 与 Player 构建匹配。
- Resources/Build Settings Scene 与 Addressables 无意外重复依赖。
- Prefab 和 Scene 无 Missing Script。
- Build Layout 的重复依赖、Bundle 数、补丁大小和峰值内存不超过项目预算。
- Release Manifest hash 与实际上传文件一致。

### 发布顺序

```text
Bundle/DLL/JSON
→ Catalog 和 hash
→ 版本化 release manifest
→ current 指针（最后）
```

### 验收标准

- 全新安装、已有安装升级、断点重试、断网、缓存命中和磁盘不足经过验证。
- 连续两个内容版本能够安装和升级。
- 坏 JSON、坏 DLL 或缺失 Bundle 不会切换 current 指针。
- 上一版本 Catalog、Bundle 和 release manifest 在回滚窗口内始终保留。
- 重启后可选择最后成功版本。
- Addressables Event Viewer/Profiler 无未解释的 Handle 泄漏。

## 5. 两部分的推荐实施顺序

```text
H0  Windows IL2CPP 与架构基线
→ H1  AOT asmdef 拆分
→ A0  资产、地址和生命周期盘点
→ A1  Addressables Bootstrap / Remote Catalog
→ H2  HybridCLR 本地 Smoke Tool Pack
→ H3  按程序集发现和原子追加注册
→ H4  版本化 ToolSet 原子切换、修改与删除
→ H5  JSON Catalog 与 Schema 描述覆盖
→ A2  远端 JSON / AOT metadata / Tool Pack 交付
→ H6  HybridCLR 与 Addressables 集成
→ H7  HybridCLR CI/CD 门禁
→ A3  UI
→ A4  Sprite / Texture / Item
→ A5  Material / Character
→ A6  Scene
→ A7  Addressables 生产发布与回滚
```

当前仓库已经完成 H0-H3，因此实际继续执行顺序为：

```text
A0 → A1 → H4 → H5 → A2 + H6 最小纵向集成 → H7
→ A3 → A4 → A5 → A6 → A7
```

A2 与 H6 使用同一个 Smoke ToolSet 迭代完成，不把“内容打包”和“运行时加载”
拆成两个长期分支。在完成 A2 + H6 前，不开始大规模美术资产迁移。先用小型
JSON 和 Smoke Tool Pack 验证下载、校验、激活、缓存和失败恢复，再扩大内容范围。

## 6. 关键修改路径

### HybridCLR 与工具链

- `unity-NPC-agent-client/Packages/manifest.json`
- `unity-NPC-agent-client/ProjectSettings/ProjectSettings.asset`
- `unity-NPC-agent-client/Assets/Scripts/CommandDispatcher/NpcToolDiscovery.cs`
- `unity-NPC-agent-client/Assets/Scripts/CommandDispatcher/ToolsRegistry.cs`
- `unity-NPC-agent-client/Assets/Scripts/CommandDispatcher/NpcTool.cs`
- `unity-NPC-agent-client/Assets/Scripts/CommandDispatcher/ToolContract.cs`
- `unity-NPC-agent-client/Assets/Scripts/AgentHostClient.cs`
- 新增客户端 Core/ToolFramework/Gameplay/BuiltinTools asmdef
- 新增 HybridCLR Loader、ToolPackageManifest、ToolMetadataCatalog 和验证器

### Addressables 与资产

- `unity-NPC-agent-client/Assets/AddressableAssetsData/AddressableAssetSettings.asset`
- `unity-NPC-agent-client/Assets/AddressableAssetsData/AssetGroups/`
- `unity-NPC-agent-client/Assets/Scripts/UIManager/UIManager.cs`
- `unity-NPC-agent-client/Assets/Scripts/UIManager/View/ChatWindow.cs`
- `unity-NPC-agent-client/Assets/Scripts/UIManager/View/InventoryWindow.cs`
- `unity-NPC-agent-client/Assets/Scripts/UIManager/View/InventoryInteractWindow.cs`
- `unity-NPC-agent-client/Assets/Scripts/UIManager/View/ItemDispenserWindow.cs`
- `unity-NPC-agent-client/Assets/Scripts/Gameplay/Inventory/ItemData.cs`
- `unity-NPC-agent-client/Assets/Scripts/Gameplay/Inventory/ItemDataList.cs`
- 新增 Content Bootstrap、ContentAssetProvider 和各领域 Content Catalog

### Go Prompt JSON

- `GameMCPServer/internal/agent/prompt.go`
- `GameMCPServer/internal/agent/profile.go`
- `GameMCPServer/internal/config/config.go`
- `GameMCPServer/config/`

## 7. 最终完成定义

### HybridCLR 完成

- Windows IL2CPP Player 可远程加载版本化 Tool Pack 和完整 ToolSetSnapshot。
- 新发布可以新增、修改和逻辑删除工具；代码或 Schema 变更在下次启动生效。
- 工具集原子激活、幂等，未声明的同名冲突和历史名称误复用会被拒绝。
- Json.NET、泛型参数 DTO 和 Schema 反射在 IL2CPP 下稳定运行。
- JSON 可以独立调整已有/新增工具描述。
- ToolSet 与描述 Catalog 联合激活后只发布一次完整 Runtime Manifest。

### Addressables 完成

- Remote Catalog、Profile、CDN、缓存和发布指针投入使用。
- JSON、HybridCLR metadata/DLL、UI、图标、材质、角色模型和场景均有明确
  加载与释放路径。
- 支持全新安装、差量更新、失败恢复和下次启动回滚。
- 更新一个小资源不会导致无关大 Bundle 下载。
- Windows Player 无 Missing Script、Shader、NavMesh、线程或协议异常。

### 现有业务总回归

1. Runtime 注册成功。
2. 普通对话和流式回复正常。
3. 未在 ToolSet 中显式停用的 BuiltinTools 名称、Schema 和行为保持兼容。
4. 新增或修改的 Tool Pack 在下次启动激活后可见并可执行，删除的工具不可见且
   不可执行。
5. `game_npc_move` 能到达 warehouse 和 gate。
6. 取消能停止对应 Task 和移动。
7. Go 重启后 Unity 能重连并重新发布完整 Manifest。
8. Inventory 和存档恢复正常。
9. JSON 更新不会产生第二份结构 Schema。
10. Console 无编译错误、Missing Script、AOT metadata、线程或协议异常。

## 8. 主要风险与控制措施

| 风险 | 后果 | 控制措施 |
|---|---|---|
| asmdef 拆分形成循环依赖 | 无法编译或边界失效 | 小步拆分；必要时保留 Core/UI 在 Assembly-CSharp |
| AOT 泛型或反射元数据缺失 | 新工具运行时崩溃 | Generate/All、补充元数据、真实 IL2CPP smoke call |
| 新工具与历史工具身份冲突 | Manifest 和路由歧义 | ToolSet 历史台账；候选快照提交前全量校验 |
| 删除或替换时仍有旧调用 | 旧实现继续改变世界状态 | 代码/Schema/活动集合仅在下次启动激活；开放 Runtime 和 A2A 前完成快照切换 |
| 已删除名称被无关工具复用 | 历史 Context 和模型语义错配 | tombstone 永久记录逻辑身份；重新启用必须延续身份并提升版本 |
| DLL 已加载后发现错误 | 当前进程无法卸载 | Registry 提交前验证；激活后问题下次启动回退 |
| JSON 与工具 DLL 不一致 | 模型获得错误说明 | 同一 releaseId；新工具缺少 metadata 时拒绝整包 |
| JSON 复制结构 Schema | Unity 不再是唯一事实源 | JSON 只允许 description/message，不接受类型和约束字段 |
| Catalog 更新时旧 Bundle 已加载 | 新旧内容混用 | 仅启动/主菜单更新；一次会话固定 releaseId |
| 场景硬引用远端资产 | 重复打包或无法更新 | Analyze、Build Layout、显式 Content Catalog |
| Shader Variant 缺失 | 粉色材质或卡顿 | SVC、锁定 URP、Windows Player 实测 |
| Handle 泄漏 | Bundle 不能卸载、内存增长 | 统一 Provider、所有权表、Profiler/Event Viewer |
| 发布顺序不原子 | Catalog 指向缺失文件 | 先文件、后 Catalog、最后 current 指针 |
| System Prompt 下发 Unity | 破坏安全与架构边界 | Prompt JSON 只由 Go 加载并按 Context 固定版本 |
