# Addressables A6 场景热更新基线

## 已完成范围

- `BootstrapScene` 是 Windows Player 的首场景，只承载 `AgentHostClient`、
  `RemoteSceneCoordinator`、内容启动/下载错误 UI 和 `UIManager`。Bootstrap 在整个
  应用生命周期保持加载，不包含 Player、NPC、Inventory、NavMesh 或业务场景物件。
- `SampleScene` 继续作为本地开发与回归场景，不占用远端地址，也不进入
  `Remote_Scenes`。
- `WarehouseRemote` 是第一座远端验证场景，稳定业务 ID 为 `warehouse`，地址为
  `scene/warehouse/main`。场景、场景物件和独立 `WarehouseRemote-NavMesh.asset`
  以及从 Sample 本地所有权分离出的场景材质，由 `Remote_Scenes` 同一 bundle 生命周期
  拥有。
- `RemoteSceneCoordinator` 只通过 `IContentSceneProvider.LoadSceneAsync` /
  `UnloadSceneAsync` 加载和释放场景，并持有唯一活动 `ContentSceneLease`。
- 切场前 `AgentHostClient` 禁止新对话和 Runtime 调用，取得对话/存档协调锁，等待
  已开始的工具调用完成，注销旧场景 NPC 并发布 Manifest。新场景激活后重新绑定
  Item Catalog、角色视觉、Player ID 和 NPC；Runtime Gateway 连接始终只有一条。
- 候选场景加载、激活或边界校验失败时释放候选 handle，并把 Active Scene 恢复为
  本地 Bootstrap。远端场景若包含 Host、UIManager、Registry、Dispatcher、内容覆盖层
  或第二个协调器，会在激活提交前被拒绝。
- 远端 Prefab/Scene 所用运行时代码均来自 AOT 程序集；HybridCLR 候选仍在场景加载前
  由内容启动流程完成加载与验证。

## 配置与验证

Unity 菜单：

- `GameWithLLM/Hot Update/Configure Addressables A6 Scenes`
- `GameWithLLM/Hot Update/Verify Addressables A6 Scenes`

批处理入口：

- `AddressablesA6ProjectSetup.ConfigureFromCommandLine`
- `AddressablesA6ProjectSetup.VerifyFromCommandLine`
- `AddressablesA6ProjectSetup.BuildLocalDevelopmentFromCommandLine`
- `AddressablesA6ProjectSetup.BuildWindowsPlayerFromCommandLine`
- `AddressablesA6PackedPlaySmoke.RunFromCommandLine`

验证器检查本地/远端 Scene 边界、Build Settings、稳定 Address/Label、AOT 协调器、
远端权威世界、独立 NavMeshData、Missing Script 和 Scene lease API。Packed Play
Smoke 覆盖远端加载/绑定、卸载释放和坏候选回退 Bootstrap。Windows Player 仍需联合
Go 服务验证 NPC 移动/取消、Inventory、存档恢复和实际 CDN 断网场景。
