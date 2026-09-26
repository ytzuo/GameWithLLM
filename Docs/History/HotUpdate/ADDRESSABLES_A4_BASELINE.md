# Addressables A4 物品内容基线

> 状态：A4 完成（2026-09-26）

本基线记录物品业务定义、文案和图标从场景硬引用迁移到 Addressables
后的稳定契约。架构边界仍以 `ARCHITECTURE.md` 为准。

## 1. 运行时边界

- `AgentHostClient` 在内容 Bootstrap 成功后创建 `ItemContentCatalog`，在开放
  Player 输入、存档、Inventory UI 和 Runtime 工具前完成目录、文案与图标预加载。
- `ItemContentCatalog` 独占 ItemData、文案和 SpriteAtlas lease，应用退出时释放。
- `PlayerMock` 不再序列化 `ItemDataList`；Catalog 就绪后注入
  `InventoryViewModel`、`SaveGameService` 和发放器。
- Inventory 与发放器从内存缓存同步取图标，打开窗口不发起下载。
- 当前项目没有物品世界 Prefab。`WorldVisualAddress` 仅定义稳定地址契约，
  空值不会触发预加载；日后有真实生成点时再与该生命周期一起实现按需加载和对象池。

## 2. 稳定地址与分包

| 内容 | Address | Label |
|---|---|---|
| ItemDataList | `item/catalog/default` | `content.items` |
| zh-CN 物品文案 | `item/catalog/text/zh-CN` | `content.items` |
| 核心物品图标 Atlas | `item/icons/core-atlas` | `content.item-icons` |

三个入口均由 `Remote_SpritesTextures` 唯一拥有。四张小图标只作为
`ItemCore.spriteatlas` 的 packable，不再单独注册 Addressable；项目固定使用
`SpritePackerMode.BuildTimeOnlyAtlas`。因此更新物品图标不会带下角色或场景 Bundle。

## 3. 已发布 itemId

| itemId | MaxStackSize | 文本键前缀 | Atlas Sprite |
|---|---:|---|---|
| `rock` | 64 | `item.rock` | `11` |
| `wood` | 64 | `item.wood` | `10979` |
| `axe` | 1 | `item.axe` | `weapon_0` |
| `helmet` | 1 | `item.helmet` | `1086` |

上述 ID 不得删除后复用。删除物品必须保留 tombstone 或提供显式存档迁移；
修改 `MaxStackSize` 必须先验证旧存档。A4 保持了 A0 盘点时的全部 ID 和堆叠规则。

## 4. 校验与结果

Editor 菜单 `GameWithLLM/Hot Update/Verify Addressables A4 Items`（批处理入口
`AddressablesA4ProjectSetup.VerifyFromCommandLine`）会校验：

- 已发布 itemId、堆叠规则、文本键、Atlas/Sprite 映射和世界表现地址；
- 三个稳定地址的 Group/Label 唯一性；
- `SampleScene` 不依赖远端 ItemData、文案、Atlas 或源 Sprite；
- 源 Sprite 不是重复 Addressable entry，Scene/Resources Analyze 无重复打包。

2026-09-26 验证结果：A4 配置校验通过；27/27 EditMode 测试通过；
LocalDevelopment 内容构建、Packed Play Mode 烟测、Windows x64 IL2CPP
Development Player 构建与真实启动烟测通过。Player 日志确认 UI 和物品
Catalog/Atlas 均在内容就绪门禁前激活，未出现 Fatal bootstrap、Missing Script 或编译错误。
