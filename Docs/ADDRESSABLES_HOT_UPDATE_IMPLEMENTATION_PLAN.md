# Addressables 客户端资源热更新实施计划

> 状态：Draft  
> 创建日期：2026-09-20  
> 适用项目：`unity-NPC-agent-client`（Unity `6000.3.19f1`）

## 1. 目标与范围

本计划用于在现有 Unity 客户端中建立基于 Addressables 的资源管线，使以下内容可在不重新发布 Player 的情况下更新：

- 物品模型、Mesh、材质和贴图。
- 角色模型、Avatar、动画和角色专属表现资源。
- UI 图标、SpriteAtlas、UXML、USS 和字体。
- `ItemDataList`、角色外观目录和 UI 资源目录等序列化内容。

Addressables 只负责内容交付，不负责更新 C#、程序集或协议。以下变更仍必须发布新 Player：

- 新增或修改 C# 类型、字段语义、工具实现或 Agent Runtime 契约。
- 升级 Unity、URP 或 Addressables 包。
- 修改远端 Prefab 依赖的组件类型，但旧 Player 中没有该组件或该组件已被裁剪。
- 变更会破坏旧存档、旧资源序列化布局或旧客户端内容契约的结构。

本计划不改变 Go Agent Service、A2A、Runtime Bridge、MCP 或 Save Coordination 协议。资源下载直接通过对象存储/CDN 完成，不经由 Agent Service 转发。

## 2. 当前基线

当前客户端采用“场景序列化引用 + `Resources.Load`”的混合方式：

- `UIManager.serializedUiConfigs` 在 `SampleScene` 中直接引用 6 个主窗口 UXML。
- HUD、聊天消息模板和 InventorySlot 通过 `Resources.Load<VisualTreeAsset>` 加载。
- `PlayerMock.itemDataList` 在场景中直接引用 `ItemDataList.asset`。
- `ItemData.Icon` 直接引用 Sprite，加载物品目录时会形成图标硬依赖。
- `ChatStyle.uss` 和 `InventoryStyle.uss` 被多个 UXML 共享。
- 项目尚未安装 `com.unity.addressables`，也没有 `AddressableAssetsData`。
- 当前还没有 Prefab、FBX、Animator Controller、AnimationClip、SpriteAtlas 或自定义 Shader 资产。

这意味着模型资源管线可以从零按新规则建立；现有 UI 与物品数据则需要先解除场景和 `Resources` 的硬引用。

## 3. 设计原则

### 3.1 逻辑与表现分离

可热更新资源应尽量只包含表现，不承担权威游戏逻辑：

```text
NpcEntity（Player/场景内置）
├─ npcId、NavMeshAgent、工具执行、取消和状态
└─ CharacterVisualRoot（Addressable 视觉 Prefab）

ItemDefinition（稳定 itemId 和业务数据）
├─ IconReference（Addressable）
└─ WorldVisualReference（Addressable）
```

- `NpcEntity`、`IAgentEntity`、`IAgentTool`、Inventory 和存档逻辑不得迁入远端资源。
- 角色视觉 Prefab 不得重复挂载 `NpcEntity`、网络客户端或工具组件。
- 物品世界模型默认作为视觉子对象；影响碰撞、可达性或工具结果的权威 Collider 留在内置逻辑对象上。
- 远端 Prefab 使用的所有 MonoBehaviour 类型必须已经编译进 Player，并通过直接引用、`[Preserve]` 或 `link.xml` 防止裁剪。

### 3.2 按生命周期划分 Bundle

Bundle 边界按“共同加载、共同更新、共同卸载”划分，不按文件扩展名机械划分：

- 只被一个根资源使用的 Mesh、材质和贴图保持为该根资源的隐式依赖。
- 被多个 Bundle 共享且体积明显的资源设为显式 Addressable，进入共享组。
- 不把所有内容打进一个大 Bundle。
- 不为每张小图标或每个小材质创建独立 Bundle。
- 频繁变化的资源不能与体积大且稳定的资源放在同一 Bundle。

### 3.3 稳定标识与地址

业务 ID、Addressables 地址和文件路径分离：

```text
业务 ID：rock
资源地址：item/rock/visual
资源路径：Assets/GameContent/Addressable/Items/Common/Rock/Rock.prefab
```

- 已发布的 `itemId`、`characterId`、`appearanceId` 不得复用。
- Addressables 地址不得直接使用可变化的磁盘路径。
- 单资源加载使用唯一地址；Label 只用于批量查询、预下载和统计。
- 内容版本由 Catalog 和 `contentRevision` 管理，不写入每个资源地址。

### 3.4 会话内版本一致

- Catalog 只在启动或回到主菜单且相关资源尚未加载时更新。
- 一次游戏会话固定使用一个 `contentRevision`。
- 不允许 ItemCatalog 已更新、角色或 UI 仍使用上一版本 Catalog 的混合状态。
- 已实例化的 GameObject、VisualElement、Material 和 Sprite 不会因 Catalog 更新自动替换；必须在安全点重建或在下一次加载时生效。

## 4. 目标目录和模块

建议逐步整理为：

```text
unity-NPC-agent-client/Assets/
├─ GameContent/
│  ├─ Bootstrap/                  # 随 Player 发布的最小可运行内容
│  └─ Addressable/
│     ├─ Catalogs/
│     ├─ UI/
│     │  ├─ Common/
│     │  ├─ Chat/
│     │  └─ Inventory/
│     ├─ Items/
│     ├─ Characters/
│     ├─ Animations/
│     └─ SharedRendering/
├─ Scripts/
│  └─ Content/
│     ├─ ClientContentBootstrap.cs
│     ├─ ContentUpdateService.cs
│     ├─ ContentAssetProvider.cs
│     ├─ UiContentCatalog.cs
│     ├─ ItemContentCatalog.cs
│     └─ CharacterContentCatalog.cs
└─ Editor/
   └─ ContentPipeline/
      ├─ ContentValidation.cs
      ├─ AddressablesBuild.cs
      └─ ContentReleaseManifestBuilder.cs
```

`Assets/Scripts/Content` 属于客户端生产实现，不加入 `Packages/com.gamewithllm.agent-runtime`。公共 Agent Runtime UPM 包继续只承载现有 Agent SDK 契约。

## 5. 推荐 Addressables 分组

| Group | 主要内容 | 打包方式 | 更新限制 | 生命周期 |
|---|---|---|---|---|
| `Local_Bootstrap` | 加载/错误 UI、缺省图标和缺省视觉 | Pack Together | Cannot Change Post Release | Player 全生命周期 |
| `Local_BaseCatalogs` | 首包可运行的基础 Item/UI/Character 目录 | 按领域 Pack Together | Cannot Change Post Release | 会话全程 |
| `Remote_UI_Common` | Theme、字体、公共 SpriteAtlas | Pack Together | 低频变更 | UI 系统生命周期 |
| `Remote_UI_Chat` | Chat UXML、USS、消息模板、专用图标 | Pack Together | Can Change Post Release | Chat 功能生命周期 |
| `Remote_UI_Inventory` | Inventory UXML、USS、Slot、专用图标 | Pack Together | Can Change Post Release | Inventory 功能生命周期 |
| `Remote_Items_<Family>` | 物品视觉 Prefab 及专属依赖 | 每个资源族 Pack Together | Can Change Post Release | 场景/功能生命周期 |
| `Remote_Characters` | 每个角色视觉根 Prefab | Pack Separately | Can Change Post Release | 角色实例/对象池生命周期 |
| `Remote_Animations_<Rig>` | 同骨骼共享动画 | Pack Together | 低频变更 | 角色集合生命周期 |
| `Remote_SharedRendering_<Set>` | Shader、SVC、稳定共享材质 | Pack Together | 尽量 Cannot Change | 场景生命周期 |

`Pack Together By Label` 只在 Label 集合受到自动化严格控制时使用。该模式按完整 Label 组合拆包，随意增加业务 Label 可能意外改变 Bundle 边界。初期优先用清晰的 Group + Pack Together/Separately。

## 6. 分阶段实施

### 阶段 1：建立 Addressables 基础设施和统一 ContentService

#### 目标

让客户端具备可控的初始化、Catalog 检查、下载、加载、释放和失败回退能力，但暂不迁移现有业务资源。

#### 实施项

1. 通过 Package Manager 安装与当前 Unity 版本兼容的 `com.unity.addressables`。
2. 创建 Addressables Settings、Group Template 和以下 Profile：
   - `LocalDevelopment`：本地 HTTP 或 Editor Hosting。
   - `QA`：测试 CDN。
   - `Production`：正式 CDN。
3. 开启 Remote Catalog，并启用手动 Catalog 更新，禁止初始化过程中不可控地自动切换内容。
4. 创建 `Local_Bootstrap`：
   - 本地加载界面。
   - 下载失败/离线提示。
   - 缺省图标、缺省物品视觉、缺省角色视觉。
5. 实现 `ClientContentBootstrap` 状态机：

   ```text
   Local Bootstrap Ready
     → Initialize Addressables
     → Fetch release manifest
     → Check client/content compatibility
     → Check/Update Catalog
     → Get download size
     → Download required labels
     → Load content catalogs
     → Enable gameplay and input
   ```

6. 实现统一的 `ContentAssetProvider`，至少提供：
   - `InitializeAsync`
   - `GetDownloadSizeAsync`
   - `DownloadLabelAsync`
   - `LoadAssetAsync<T>`
   - `InstantiateAsync`
   - `Release` / `ReleaseInstance`
7. 明确 Handle 所有权：
   - 内容目录句柄保持到会话结束。
   - UI 句柄由 UI Provider 管理。
   - 角色 Prefab 句柄保持到所有实例和对象池对象销毁。
   - 不允许业务代码直接丢弃 `AsyncOperationHandle`。
8. 增加远端 `content-release.json`，建议字段：

   ```json
   {
     "releaseId": "content-2026.09.20.1",
     "contentSchemaVersion": 1,
     "minimumClientVersion": "1.0.0",
     "mandatory": false,
     "requiredLabels": ["preload:core"]
   }
   ```

9. 所有 Unity 对象创建、赋值和 UI 更新仍在 Unity 主线程执行；下载回调不得绕过现有主线程约束。
10. 在 `ARCHITECTURE.md` 增加客户端内容交付章节，但不改变 Agent 协议图和协议边界。

#### 验收标准

- 本地、QA、Production Profile 能生成独立路径的内容。
- 首次运行可计算并展示下载大小。
- 下载中断后能重试，不启用游戏输入。
- 无网络且没有远端缓存时仍能显示本地错误 UI。
- Addressables 初始化失败不会导致黑屏或空引用。
- 所有成功加载的资源都有明确释放路径。

#### 回滚点

此阶段没有迁移业务资源。禁用 `ClientContentBootstrap` 后，现有 SampleScene 仍能按原路径运行。

---

### 阶段 2：迁移现有 UI 资源

#### 目标

消除生产代码中的 `Resources.Load`，并移除 `SampleScene` 对可更新 UXML 的直接引用，使 UI 布局、样式和图标能够独立更新。

#### 实施项

1. 新建 `UiContentCatalog`，使用稳定 UI ID 映射 `AssetReferenceT<VisualTreeAsset>`：

   ```text
   ui/hud/gameplay
   ui/window/chat
   ui/window/inventory
   ui/window/inventory-list
   ui/window/inventory-interact
   ui/window/item-dispenser
   ui/window/save-game
   ui/template/chat/system-message
   ui/template/chat/player-message
   ui/template/chat/opponent-message
   ui/template/inventory/slot
   ```

2. 将 `Assets/Resources/UI` 下的资产及其 `.meta` 一起移动到 `Assets/GameContent/Addressable/UI`。
3. 将 `UIManager.serializedUiConfigs` 的直接 `VisualTreeAsset` 引用改为内容目录或 Addressable 引用，清理 `SampleScene` 中六个 UXML 硬引用。
4. 移除以下位置的 `Resources.Load`：
   - `UIManager.CreateGameplayHud`
   - `ChatWindow.OnBindElements`
   - `InventoryWindow.OnBindElements`
   - `InventoryInteractWindow.OnBindElements`
   - `ItemDispenserWindow.OnBindElements`
5. UI 初始化策略采用“异步预加载、打开窗口时同步取缓存”：
   - Bootstrap 先加载 HUD 和必需模板。
   - `OpenNewWindow<T>()` 继续操作已缓存的 `VisualTreeAsset`，避免把全部输入回调改为异步。
   - 未预加载的可选窗口可增加 `OpenWindowAsync<T>()`，但不得在窗口内部自行访问 Addressables。
6. 将 Chat 与 Inventory 按功能打包：
   - `ChatStyle.uss` 与 Chat UXML 进入 Chat Bundle。
   - `InventoryStyle.uss` 与 Inventory UXML 进入 Inventory Bundle。
   - `GameplayHud.uss` 与 HUD 一起打包。
7. 增加 UI 内容契约校验，检查代码依赖的所有 `Q<T>("name")` 元素是否存在且类型兼容。
8. UI 热更只对新实例生效：更新后关闭并重建窗口；不尝试原地修改已有 VisualTree。

#### 验收标准

- `rg "Resources.Load" Assets/Scripts` 不再命中生产 UI 代码。
- `Assets/Resources/UI` 已清空或删除，且 `.meta` GUID 被正确保留。
- Addressables Analyze 不报告 UI 与 Build Settings Scene/Resources 的重复依赖。
- HUD、聊天、Inventory、ItemDispenser 和 SaveGame UI 均能在 Packed Play Mode 与 Player Build 中打开。
- 远端替换 USS/UXML 后，重启或重建窗口可看到新版本。
- Catalog/下载失败时仍能显示 `Local_Bootstrap` UI。

#### 回滚点

保留迁移前 Player 构建与 Catalog。若远端 UI 发布失败，回退远端 Catalog；不要把已迁移文件重新复制回 `Resources` 形成双份来源。

---

### 阶段 3：迁移物品目录、图标和物品视觉

#### 目标

使物品定义、图标、模型、材质和贴图可热更新，同时保持 Inventory、工具和存档使用稳定的 `itemId`。

#### 实施项

1. 将现有 `ItemData` 拆分为业务字段与表现引用：

   ```text
   业务字段：ItemId、ItemName/本地化 Key、Description、MaxStackSize
   表现字段：IconReference、WorldVisualReference
   ```

2. 用 `AssetReferenceSprite` 替代直接 `Sprite Icon`；用 `AssetReferenceGameObject` 表示世界视觉。
3. 将基础 `ItemDataList` 放入 `Local_BaseCatalogs`，保证首包离线可运行；内容更新后可由远端 Catalog 指向新版本。
4. `PlayerMock` 不再从 Inspector 读取 `itemDataList`：
   - `ClientContentBootstrap` 先加载 ItemCatalog。
   - 完成后注入 `InventoryViewModel`、`SaveGameService` 和工具支持层。
   - Catalog 未就绪前不开放 Inventory、发放器、存档加载或物品工具。
5. 物品视觉 Prefab 只包含表现组件。若必须影响碰撞或交互，新增经过验证的内置逻辑壳层，不允许美术 Prefab 直接改变权威规则。
6. 建立 ItemId 兼容性数据库或基线文件，CI 比较上一发布版本：
   - 禁止删除和复用已发布 ItemId。
   - 删除物品使用 tombstone 定义或显式存档迁移。
   - 降低 `MaxStackSize` 时必须提供旧存档迁移，否则拒绝发布。
7. 图标按功能或物品族创建 SpriteAtlas；避免全项目单一 Atlas。
8. 高频生成的物品模型采用“加载一次 Prefab + 本地 Instantiate/对象池”，资源句柄保持到对象池清空。

#### 验收标准

- `SampleScene` 不再直接引用 `ItemDataList.asset`。
- 打开 Inventory 时只下载所需图标/Atlas，不因加载目录而加载全部物品模型。
- 更新图标、名称、描述或视觉 Prefab 后无需发布 Player。
- 旧存档中的全部 ItemId 在新目录中可解析。
- 保存、加载、发放、取出、放入和物品查询工具全部通过现有验证。
- 删除网络或清理缓存后，首包基础物品仍能按既定降级策略运行。

#### 回滚点

ItemCatalog 以会话为单位切换。发生问题时回退 Catalog 并重启客户端，不在已创建 `SaveGameService` 的会话中替换目录对象。

---

### 阶段 4：拆分 NPC 权威实体与可更新角色视觉

#### 目标

在不替换 `NpcEntity` 根对象的前提下热更新角色模型、材质、Avatar 和动画。

#### 实施项

1. 在 NPC 根对象下增加固定 `CharacterVisualRoot` 挂点。
2. 新建 `CharacterContentCatalog`：

   ```text
   characterId / appearanceId
   → CharacterVisualPrefab
   → Portrait/Icon
   → 可选 AnimationSet
   ```

3. 新建 `CharacterVisualLoader`：
   - 根据 `appearanceId` 异步加载视觉 Prefab。
   - 校验 Prefab 不包含禁止组件。
   - 挂载到 `CharacterVisualRoot`。
   - 失败时使用本地缺省视觉。
   - 销毁/换装时释放实例和句柄。
4. 视觉 Prefab 不得拥有独立 `npcId`、`NavMeshAgent`、Inventory 或 Tool。
5. 共享动画按 Rig 拆组；角色专属 Mesh、Avatar、材质和贴图保留在角色资源边界内。
6. 若需要皮肤更新：
   - 皮肤 MaterialSet 使用独立地址。
   - 角色 Prefab 不直接引用所有皮肤。
   - Renderer 在视觉加载完成后由 `CharacterVisualLoader` 赋值。
7. 更新策略：
   - 默认下次生成或下次进场景生效。
   - 需要即时换装时，先停止视觉相关动画，创建新视觉并校验完成，再销毁旧视觉。
   - 不在 NPC 移动、工具调用或存档应用过程中替换根实体。
8. 为所有远端 Prefab 所用组件维护 `link.xml` 或 Preserve 清单。

#### 验收标准

- 替换角色视觉不改变 `NpcEntity`、npcId、NavMesh 状态和 Runtime Manifest。
- 模型下载失败时 NPC 仍存在且工具可正常执行。
- 旧视觉释放后无遗留 GameObject、Handle 或 Bundle 引用。
- Go 重启、Unity 重连和 Manifest 重发不受视觉资源加载影响。
- 存档恢复的位置、旋转和 Inventory 与角色视觉版本无关。

#### 回滚点

CharacterCatalog 回退到上一版本；本地缺省视觉始终可用。禁止通过回滚视觉 Catalog 改变实体集合或 NPC ID。

---

### 阶段 5：扩展模型、材质、动画和共享渲染资源分组

#### 目标

在真实美术资源规模下稳定 Bundle 依赖、下载粒度和内存占用。

#### 实施项

1. 建立资源导入 Preset：
   - Texture 压缩、尺寸、MipMap、平台覆盖。
   - Model Rig、Avatar、Mesh Compression、Read/Write。
   - Audio 压缩和加载方式。
2. 建立材质规则：
   - 角色/物品专属材质跟随所属资源族。
   - 高频共享且体积明显的材质进入共享组。
   - 不允许远端资源引用 Editor-only Shader。
3. 建立 Shader Variant Collection：
   - 覆盖远端材质实际使用的关键字组合。
   - 在目标平台 Player 中验证，不仅在 Editor 验证。
   - Unity/URP/Shader 结构升级视为新 Player 发布。
4. 建立质量级资源地址：

   ```text
   character/ryan/visual/standard
   character/ryan/visual/hd
   item/axe/visual/standard
   ```

   不让同一根 Prefab 同时硬引用所有质量级资源。
5. 每次构建分析：
   - Bundle 数量和大小。
   - 重复依赖字节数。
   - 单次内容更新下载量。
   - 峰值加载 Bundle 数和内存。
   - 最大依赖扇出。
6. 根据 Build Layout 调整边界，不在缺少数据时预设固定的 MB 阈值；先记录基线，再在 CI 中设置项目预算。

#### 验收标准

- 共享材质、Shader、Atlas 不在多个 Bundle 中重复出现。
- 修改一个角色的专属贴图不会要求下载其他角色 Bundle。
- 修改 Inventory 图标不会要求下载角色或物品模型 Bundle。
- 目标平台的 Shader 无粉色材质、缺失 Variant 或批处理异常。
- 资源释放后内存能回落到已定义基线。

---

### 阶段 6：接入 CI/CD、CDN、差量发布和回滚

#### 目标

将资源发布从人工 Editor 操作转为可重复、可审计、可回滚的流水线。

#### 实施项

1. 创建 Editor 构建入口，例如：

   ```text
   ContentPipeline.Validate
   ContentPipeline.BuildFull
   ContentPipeline.BuildUpdate
   ContentPipeline.GenerateReleaseManifest
   ```

2. Validation 阶段检查：
   - 地址、业务 ID 唯一。
   - Group、Label 和 Profile 合法。
   - Build Settings Scene/Resources 与 Addressables 无重复依赖。
   - 远端 Prefab 无禁止组件或 Missing Script。
   - UXML 必需元素契约完整。
   - ItemId 和旧存档兼容。
   - Shader、材质和平台导入配置合法。
3. 完整 Player 发布流水线：

   ```text
   Switch Build Target
   → Validate
   → Addressables Clean Build
   → Analyze / Build Layout
   → Player Build
   → Packed/Player Smoke Tests
   → Archive content_state.bin
   → Upload immutable bundles
   → Upload catalog/hash
   → Publish release manifest
   ```

4. 内容更新流水线：

   ```text
   Select player release + platform
   → Restore original content_state.bin
   → Verify code/Unity/package compatibility
   → Check Content Update Restrictions
   → Update a Previous Build
   → Compare patch/build-layout budgets
   → Automated load tests
   → Upload bundles
   → Upload catalog/hash
   → Publish release manifest last
   ```

5. 按 Player 版本、平台和渠道归档：

   ```text
   artifacts/addressables/
   └─ <clientVersion>/
      └─ <platform>/
         ├─ addressables_content_state.bin
         ├─ build-layout.json
         ├─ catalog/
         └─ release-manifest.json
   ```

6. CDN 使用不可变路径：

   ```text
   /content/<channel>/<platform>/<client-line>/<content-release>/
   ```

7. 发布顺序固定为：Bundle → Catalog/hash → `content-release.json`。不得先发布会引用尚未上传 Bundle 的 Catalog。
8. 保留至少一个已验证旧 Catalog 和对应 Bundle；回滚只切换发布指针，不覆盖或删除当前客户端仍可能引用的文件。
9. 增加逐步发布能力：Development → QA → Production；生产环境可按渠道或百分比切换 release manifest。
10. 日志只记录 releaseId、资源地址、错误码、下载字节数和耗时，不记录玩家正文、Token 或 Agent 对话内容。

#### 验收标准

- 同一提交和 Profile 可重复产生一致的内容构建。
- 流水线能拒绝包含代码变化的“纯内容更新”。
- 每个生产 Player 都能定位唯一的原始 `content_state.bin`。
- 可在不重新构建 Player 的情况下发布并回滚一次 UI、物品和角色视觉更新。
- 弱网、断网、缓存命中、缓存清理和磁盘不足场景均经过验证。
- 发布失败不会使现网 Catalog 指向不完整内容。

## 7. 运行时加载与释放策略

| 内容 | 加载时机 | 保持时间 | 更新生效点 |
|---|---|---|---|
| Bootstrap | Player 启动 | 全生命周期 | 新 Player |
| ItemCatalog | 进入游戏前 | 当前游戏会话 | 下次启动/会话 |
| UI Common | UI 初始化 | UI 系统生命周期 | UI 重建后 |
| UI Feature | 首次打开或预下载 | 窗口/功能生命周期 | 窗口重建后 |
| Item Visual | 首次出现或预下载 | 实例/对象池生命周期 | 下次实例化 |
| Character Visual | NPC 外观初始化 | NPC/对象池生命周期 | 下次生成或安全换装 |
| Shared Rendering | 首个依赖加载前 | 场景/依赖生命周期 | 下次场景/会话 |

必须遵守“加载一次、镜像释放”：每次 `LoadAssetAsync`/`InstantiateAsync` 都有对应的 `Release`/`ReleaseInstance`。不能仅销毁 GameObject 而不释放 Addressables Handle，也不能在实例仍存活时提前释放根 Prefab Handle。

## 8. 内容兼容性矩阵

| 变更 | 内容热更 | 新 Player | 说明 |
|---|---:|---:|---|
| 替换贴图、图标、Mesh | 是 | 否 | 保持类型和地址兼容 |
| 修改材质参数 | 是 | 否 | Shader/关键字必须已被 Player 支持 |
| 修改 UXML/USS | 是 | 否 | 必需元素名和类型保持兼容 |
| 修改 Item 名称、描述 | 是 | 否 | `itemId` 不变 |
| 删除已发布 ItemId | 否 | 可能 | 必须有 tombstone 或存档迁移 |
| 降低 MaxStackSize | 有条件 | 可能 | 不得使旧存档数量非法 |
| 替换角色视觉 Prefab | 是 | 否 | 不改变 NpcEntity 和业务组件 |
| 为远端 Prefab 增加已存在组件 | 有条件 | 否 | 类型必须已进 Player 且未被裁剪 |
| 为远端 Prefab 增加新 C# 组件 | 否 | 是 | Bundle 不包含可执行代码 |
| 修改 C# 字段/序列化类型 | 否 | 是 | 需要重新验证 TypeTree 兼容性 |
| 升级 Unity/URP/Addressables | 否 | 是 | Player 与内容一起全量构建 |

## 9. 测试清单

### 编辑器和构建检查

- Addressables Analyze 全部目标规则通过。
- Build Layout 无未解释的重复依赖。
- 所有地址在 `Use Existing Build`/Packed Play Mode 中可加载。
- 所有 Addressable Prefab 无 Missing Script。
- 目标平台 C# 编译成功。

### 热更新检查

- 首次安装全量下载。
- 缓存命中时下载大小为 0 或符合预期。
- 仅更新一个 UI Feature。
- 仅更新一个物品图标/模型。
- 仅更新一个角色模型/皮肤。
- 下载中断、超时、校验失败和重试。
- Catalog 更新失败时不进入混合版本会话。
- 回滚到上一 Catalog 后可正常加载。

### 现有业务回归

- Runtime 注册成功。
- 普通对话和流式回复正常。
- `game_npc_move` 可到达 warehouse 和 gate。
- 取消停止对应 Task 和移动。
- Go 重启后 Unity 重连并重新发布 Manifest。
- Inventory、物品工具和存档恢复正常。
- Console 无编译错误、Missing Script、线程或协议异常。

## 10. 主要风险与控制措施

| 风险 | 后果 | 控制措施 |
|---|---|---|
| 场景硬引用远端资源 | Player 与 Bundle 重复打包 | CI 执行 Scene/Addressable duplicate 分析 |
| 共享依赖未显式分组 | 多 Bundle 重复材质/贴图 | Analyze + Build Layout；只提取高价值共享依赖 |
| Bundle 过大 | 小改动导致大补丁 | 按更新频率和共同加载边界拆分 |
| Bundle 过碎 | 内存和请求开销增加 | 避免每文件一包，按功能/角色/资源族打包 |
| 更新时已有旧 Bundle 在内存 | 新旧内容冲突 | 仅在启动/主菜单更新，冻结会话版本 |
| 新资源引用被裁剪组件 | Player 加载失败 | Preserve/link.xml + 真实 IL2CPP Player 测试 |
| Shader Variant 缺失 | 粉色材质或运行时卡顿 | SVC、目标平台预热和材质校验 |
| 物品目录破坏旧存档 | 存档无法加载 | ItemId 基线、tombstone、MaxStackSize 门禁 |
| CDN 发布非原子 | Catalog 指向缺失 Bundle | Bundle 先上传，release manifest 最后发布 |
| Handle 泄漏 | Bundle 无法卸载、内存增长 | 统一 Provider、所有权表和 Event Viewer 验证 |

## 11. 里程碑完成定义

### M1：内容基础设施可用

- Addressables、Profile、Bootstrap、Catalog 更新和下载状态机完成。
- 弱网/离线失败可见且可恢复。

### M2：UI 完全迁移

- 生产 UI 不再使用 `Resources.Load`。
- UI UXML/USS/图标可独立发布和回滚。

### M3：物品内容可更新

- ItemCatalog 由 Bootstrap 注入。
- 图标和世界视觉 Addressable 化。
- 旧存档兼容门禁上线。

### M4：角色视觉可更新

- NpcEntity 与视觉 Prefab 解耦。
- 角色模型、材质、Avatar 和动画可独立更新。

### M5：生产资源管线完成

- 模型、材质、动画、Atlas 和 Shader 规则落地。
- CI/CD、CDN、差量发布、监控和回滚完成。

## 12. 官方参考

- [Addressables 迁移指南](https://docs.unity3d.com/kr/Packages/com.unity.addressables%401.21/manual/AddressableAssetsMigrationGuide.html)
- [远端内容部署](https://docs.unity3d.com/kr/Packages/com.unity.addressables%401.21/manual/remote-content-enable.html)
- [内容更新工作流](https://docs.unity3d.com/kr/Packages/com.unity.addressables%401.21/manual/content-update-builds-overview.html)
- [Bundle 打包方式](https://docs.unity3d.com/kr/Packages/com.unity.addressables%401.21/manual/PackingGroupsAsBundles.html)
- [Addressables Analyze 工具](https://docs.unity3d.com/ja/Packages/com.unity.addressables%401.20/manual/AnalyzeTool.html)
- [SpriteAtlas 与 Addressables](https://docs.unity3d.com/kr/Packages/com.unity.addressables%401.21/manual/AddressablesAndSpriteAtlases.html)
- [Addressables 内存管理](https://docs.unity3d.com/ja/Packages/com.unity.addressables%401.20/manual/MemoryManagement.html)
