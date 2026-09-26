# Addressables A5 角色表现基线

## 已完成范围

- `SampleScene` 中 `Alice_001`、`Ryan_001` 和 `playerMock` 的 Entity、NavMesh、
  Inventory、碰撞体、工具和存档组件继续位于随 Player 发布的根对象。
- 每个根对象新增直接子对象 `VisualRoot/FallbackVisual`。本地 fallback 不引用任何
  `Remote_Characters` 资产，因此远端内容失败不影响实体注册与工具执行。
- `CharacterContentCatalog` 从 `character/catalog/default` 读取稳定
  `characterId/appearanceId` 映射；目录仅保存 Address，不直接引用模型。
- `CharacterVisualController` 按实体实例化远端视觉、拒绝带 Entity、Inventory、
  NavMesh、Registry、Tool 或 Network 权威行为的 Prefab，并在销毁时释放实例 lease。
- Alice、Ryan、Player 的 Prefab、专属 Material/Texture、Animator Controller 与
  Idle AnimationClip 全部进入 `Remote_Characters`。该 Group 使用
  `PackSeparately`，每个角色另有独立 label，禁止跨角色资产依赖。
- 当前演示角色使用 Generic Animator，不需要 Avatar，因此目录中的
  `avatarAddress` 明确为 `null`；后续导入骨骼模型时必须分配角色私有 Avatar 地址。

## 稳定地址

| 内容 | 地址模式 |
|---|---|
| 角色目录 | `character/catalog/default` |
| 视觉 Prefab | `character/{characterId}/visual/{appearanceId}` |
| 专属材质 | `character/{characterId}/material/{materialId}` |
| 专属纹理 | `character/{characterId}/texture/{textureId}` |
| Animator Controller | `character/{characterId}/animator/{appearanceId}` |
| AnimationClip | `character/{characterId}/animation/{clipId}` |

已发布的角色为 `alice/default`、`ryan/default` 和 `player/default`。地址和业务 ID
不得删除后复用。

## 配置与验证

Unity 菜单：

- `GameWithLLM/Hot Update/Configure Addressables A5 Characters`
- `GameWithLLM/Hot Update/Verify Addressables A5 Characters`

批处理入口：

- `AddressablesA5ProjectSetup.ConfigureFromCommandLine`
- `AddressablesA5ProjectSetup.VerifyFromCommandLine`
- `AddressablesA5ProjectSetup.BuildLocalDevelopmentFromCommandLine`
- `AddressablesA5ProjectSetup.BuildWindowsPlayerFromCommandLine`

验证器会检查稳定目录映射、场景硬引用、根对象权威组件、Prefab 禁止组件、URP Lit
材质、Animator、角色间依赖和 `PackSeparately`。Windows Player 仍需作为发布门禁验证
粉色材质、Shader Variant 和 Animator/Avatar 日志。
