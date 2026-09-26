# Addressables A3 UI 内容基线

> 状态：A3 完成（2026-09-25）

本基线记录 UI 布局、样式和模板从场景/`Resources` 迁移到 Addressables 后的稳定
契约。架构边界仍以 `ARCHITECTURE.md` 为准。

## 1. 运行时加载与所有权

- `AgentHostClient` 在内容 Bootstrap 成功后创建 `UiContentCatalog`，在启用输入、
  A2A、Runtime 和业务 UI 前完成全部 UI 资产预加载与契约验证。
- `UiContentCatalog` 独占预加载产生的 Addressables lease，并在应用退出时统一释放。
- `UIManager` 从已激活 Catalog 同步克隆 HUD 和窗口；窗口不在输入回调中发起加载。
- `BaseWindow.Close` 移除 VisualTree 实例；Catalog 中的共享模板保持到应用退出。
- `PanelSettings` 在 Catalog 就绪后赋给 `UIDocument`。`SampleScene` 不再直接引用
  PanelSettings 或窗口 UXML。
- `UIManager` 等待 `UIDocument.rootVisualElement` 稳定后再挂载 Gameplay HUD；若
  PanelSettings 导致根节点延迟重建，HUD 会自动迁移到新根节点，避免只显示一帧。
- 启动后不原地替换已打开的布局。Catalog 更新在下次启动生效；将来的运行期更新
  若要支持，只能在窗口关闭并重建后切换候选。

## 2. 稳定地址

| 内容 | Address |
|---|---|
| PanelSettings | `ui/panel/default` |
| Gameplay HUD | `ui/hud/gameplay` |
| Chat | `ui/window/chat` |
| Inventory list | `ui/window/inventory-list` |
| Inventory interaction | `ui/window/inventory-interact` |
| Inventory | `ui/window/inventory` |
| Item dispenser | `ui/window/item-dispenser` |
| Save game | `ui/window/save-game` |
| 三种 Chat message 模板 | `ui/template/chat/{system-message,player-message,opponent-message}` |
| Inventory slot 模板 | `ui/template/inventory/slot` |
| USS | `ui/style/{chat,inventory,item-dispenser,save-game,gameplay-hud}` |
| Runtime theme | `ui/theme/default-runtime` |

以上 18 个入口全部属于 `Remote_UI`，并带 `content.ui-required` Label。Group 使用
Pack Together，使共同加载和共同更新的 UXML、USS、PanelSettings 与 theme 保持同一
交付单元。当前 UI 没有独立图标或 SpriteAtlas；A4 的物品图标仍由
`Remote_SpritesTextures` 负责。

## 3. 内容契约

`UiContentContractValidator` 在激活前克隆候选 VisualTree，并验证生产代码使用的
`Q<T>(name)` 元素名称和类型。Chat、Inventory、ItemDispenser、SaveGame 与复用模板
任一契约不兼容时，内容启动失败，业务 UI 和 Runtime 不会开放。

Editor 菜单 `GameWithLLM/Hot Update/Verify Addressables A3 UI`（批处理入口
`AddressablesA3ProjectSetup.VerifyFromCommandLine`）还会校验：

- 所有稳定地址位于唯一的 `Remote_UI` Group 并带必需 Label；
- `Assets/Resources/UI` 已移除，生产 UI 脚本不含 `Resources.Load`；
- `SampleScene` 不再依赖远端 UI 资产；
- Addressables 的 Scene/Resources 重复依赖 Analyze 规则不报告 UI 重复打包；
- 全部 UXML 契约可实例化且元素类型匹配。

EditMode 回归测试位于 `Assets/Tests/Editor/UiContentContractTests.cs`。

2026-09-25 验证结果：A3 配置校验通过；23/23 EditMode 测试通过；
LocalDevelopment 内容构建、Packed Play Mode 启动烟测和 Windows x64 Development
Player 构建/启动烟测均通过。Player 通过本地内容端点下载 Catalog/Bundle，并输出
`Gameplay HUD attached to the stable UIDocument root`；未发现 UI、Addressables、Missing
Script 或内容契约错误。
