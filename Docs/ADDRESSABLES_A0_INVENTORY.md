# Addressables A0 资产、地址与生命周期基线

> 状态：A0 完成（2026-09-22）  
> 机器可校验台账：`Docs/Baselines/addressables-a0-inventory.json`

本文是 `Docs/HOT_UPDATE_IMPLEMENTATION_PLAN.md` A0 的交付物。它只冻结迁移目标和
所有权，不提前执行 A1-A6 的资源迁移。当前 `SampleScene`、UI、ItemData 和 H3
HybridCLR 文件仍沿用现有本地加载方式；表中的 `Remote_*` 表示迁移完成后的唯一
目标 Group。

## 1. 地址、Label 与业务 ID 规则

- Address 使用小写逻辑 ID（规范 BCP 47 locale 段除外），以 `/` 分段；不得使用
  `Assets/...`、文件扩展名、GUID 或发布机器路径。
- 单个业务对象的地址格式为 `<domain>/<business-id>/<role>`，例如
  `item/rock/icon`。版本化二进制在地址中包含不可复用的包版本，例如
  `hotfix/tools/smoke-test/1.0.0/assembly`。
- Label 只表达批量下载集合，使用 `content.<set>`；Label 不能作为业务 ID。
- `itemId`、`characterId`、`sceneId`、`packageId` 等业务 ID 独立记录。已经发布的
  业务 ID 和 Address 只能保留或 tombstone，不能删除后绑定到别的内容。
- Bundle 路径由 Addressables Profile 和内容 hash 决定，不构成业务地址。
- 一个源资产只能有一个 owner Group。跨组共享依赖必须在迁移阶段提取成明确的
  shared group，不能把同一资产复制进多个 Group。

## 2. 本地/远端边界

| 所有者 Group | 位置 | 内容与边界 |
|---|---|---|
| `Local_Bootstrap` | Local | 最小错误/下载 UI、默认文本和兜底图标；A1 创建，不能依赖远端内容 |
| `Local_SampleScene` | Local | `SampleScene`、当前 NavMeshData 和第一阶段场景固有材质 |
| `Local_UnityPackage` | Local | `unifiedraytracing` 等 Unity 包生成内容；不纳入业务发布 |
| `Remote_ClientConfig` | Remote | 客户端三个 zh-CN JSON；Go system prompt 明确不在此处 |
| `Remote_HotfixMetadata` | Remote | 与 Player build identity 匹配的 AOT metadata |
| `Remote_ToolPacks` | Remote | 不可变版本的工具 DLL；PDB 仅 Development/QA |
| `Remote_UI` | Remote | 窗口、HUD、模板、USS 和 PanelSettings |
| `Remote_SpritesTextures` | Remote | ItemCatalog 的表现部分及已使用物品图标 |
| `Remote_Materials` | Remote | A5 后从远端视觉 Prefab 可达的材质；当前为空 |
| `Remote_Characters` | Remote | A5 后的视觉 Prefab、Avatar 和 Animation；当前为空 |
| `Remote_Scenes` | Remote | A6 后迁移的附加场景；`SampleScene` 不在其中 |

`Assets/AddressableAssetsData/AssetGroups/unifiedraytracing.asset` 是 Unity 包维护的
本地 Group，不改名、不混入业务资产。`Default Local Group` 当前为空；A1 建组时
再替换为上述显式业务 Group。

## 3. 当前盘点结论

完整逐项地址、依赖、加载点、释放责任人和生效点记录在机器可读台账。摘要如下：

| 类别 | 当前事实 | 目标与生命周期 |
|---|---|---|
| 启动场景/NavMesh | `SampleScene` 在 Build Settings 启动；NavMeshData 被场景硬引用 | 保持 `Local_SampleScene`，由场景加载/卸载拥有 |
| UI | 6 个窗口/HUD 入口由场景序列化引用，消息和 slot 模板由 `Resources.Load` 获取 | A3 迁至 `Remote_UI`；`UiContentCatalog` 持有预加载 handle，窗口关闭或应用退出释放 |
| ItemData/图标 | `ItemDataList` 被 `PlayerMock` 硬引用；4 个有效 itemId 各引用一个 Sprite | A4 迁移表现数据和 4 个图标；ItemCatalog 生命周期持有 handle，业务 `itemId` 不变 |
| 其余 Item PNG | 53 个 PNG 没有进入 `ItemDataList` 或场景 | 未纳入热更新发布，继续作为本地未使用源资产；加入业务目录前必须先分配 itemId/address |
| Material/Shader | 6 个项目材质被 `SampleScene` 硬引用；无项目自定义 Shader/SVC | 第一阶段随本地场景；A5 只迁移视觉 Prefab 自有材质，URP Shader 仍随 Player |
| 角色/动画 | 没有独立模型、Prefab、Animator、Avatar 或 AnimationClip；角色是场景内对象 | A5 前保持本地；将来只迁移 `VisualRoot` 下的表现 Prefab，权威组件仍是 AOT |
| HybridCLR | 7 个 AOT metadata、1 个 smoke DLL、1 个 Development PDB 和本地 manifest 位于 StreamingAssets | A2 分别迁到 metadata/tool groups；bootstrap/release candidate 持有 bytes handle，装载后释放下载 handle，代码只在下次启动激活 |
| 客户端 JSON | 三个计划中的客户端文本 JSON 尚不存在 | 地址已预留；A2 创建本地默认和远端版本。Go `system_prompt.zh-CN.json` 不进入客户端 |
| A1 delivery probe | 一个不含业务数据的极小 JSON | `Remote_ClientConfig` / `config/bootstrap/a1-probe`，仅用于验证真实下载与缓存链路 |
| 远端场景 | 当前不存在 | `scene/warehouse/main` 地址保留给未来远端业务场景，不能指向 `SampleScene` |

## 4. 已解释的临时场景硬引用

以下计划远端内容现在仍被本地 `SampleScene` 间接或直接引用，这在 A0 是已知迁移
债务，不代表它们已经可以放入远端 Bundle：

- `PanelSettings`、窗口 UXML：A3 由 `UiContentCatalog` 和预加载缓存替代场景引用。
- `ItemDataList` 及其 4 个 Sprite：A4 由 ItemCatalog 加载流程替代场景引用。

在对应迁移完成前，这些资产必须继续随 Player 本地交付。A0 校验器会拒绝新增的、
未在台账写明处置阶段的远端候选场景硬引用。

## 5. 释放责任与更新生效点

- `ClientContentBootstrap`（A1）拥有初始化、Catalog、release manifest 和 required
  download handles；激活结束或应用退出释放。
- `UiContentCatalog`（A3）拥有 UI 预加载 handles；实例由窗口拥有，窗口关闭调用
  `ReleaseInstance`，Catalog 在退出时释放模板。
- `ItemCatalog`（A4）拥有 ItemData 与 Sprite handles；运行期复用，退出时释放。
- 角色视觉（A5）由创建它的实体视觉控制器拥有；销毁视觉时同时释放 instance。
- 远端场景（A6）由场景协调器拥有；离开场景时 `UnloadSceneAsync`。
- 工具 bytes 由启动候选构建器拥有；hash/兼容性校验及 `Assembly.Load` 完成后释放
  Addressables handle。程序集本身不尝试卸载；工具集变化下次启动生效。

## 6. 校验

Unity 菜单 `GameWithLLM/Hot Update/Verify Addressables A0 Inventory` 或批处理方法
`AddressablesA0InventoryValidator.VerifyFromCommandLine` 会检查：台账字段完整、源
资产存在、源资产只有一个 owner、地址/Label 格式与唯一性、Local/Remote 与 Group
一致，以及 `SampleScene` 对 Remote 候选的硬引用都带有明确迁移处置说明。
