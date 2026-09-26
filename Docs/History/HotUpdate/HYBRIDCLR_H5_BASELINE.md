# HybridCLR H5 基线

> 完成日期：2026-09-22  
> 描述去硬编码修订：2026-09-24
> Unity：`6000.3.19f1`  
> HybridCLR：`8.14.1`

## 已实现

- `ToolContract<TArgs>` 的缓存 Schema 不再生成 description；结构 Schema 仍是 Unity
  唯一事实源，Schema hash 不受文案变更影响。
- 新增不可变 `ToolMetadataCatalog`。严格验证 `schemaVersion`、`contentVersion`、
  locale、文本限制、活动工具全集和参数全集；拒绝未知/停用工具、未知参数及
  `entityId` 覆盖。
- release 启动时先准备完整 ToolSet，再验证 `tool_metadata`、`agent_messages` 和
  `ui` 三份 JSON；全部成功后才激活 Registry 与客户端文本快照。
- `ToolsRegistry.GetRuntimeTools` 从活动 Catalog 注入工具和参数描述。纯 JSON 文案
  更新可通过 `PrepareCatalog` / `ActivateCatalog` 原子切换，只触发一次
  `ToolsChanged`，不改变执行路由或结构 Schema。
- 工具类、`ToolParameterAttribute`、路由参数和结构 Schema 基线不再保存任何描述。
  无法加载 JSON 时只以工具名占位并省略全部参数描述，同时输出显式 Warning。
- `agent_messages.zh-CN.json` 和 `ui.zh-CN.json` 使用稳定文本键；工具结果的主要
  自然语言消息已通过 `ClientTextCatalogs` 解析，调用点不再保留自然语言 fallback；
  Catalog 未激活时仅返回稳定文本键。错误码和日志事件名仍在代码中。
- H5 源 JSON 位于 `Assets/Content/Catalogs`，本地 staging 将其复制到
  `StreamingAssets/HotUpdate`。三份 JSON 与 release manifest 使用同一
  `contentVersion`；A2 将负责迁入 Addressables。
- 当前 H5 release 为 `h5-local-1.2.0`，ToolSet 为 `4.0.0`。Smoke 包因移除程序集内
  描述和消息 fallback 而将包版本提升到 `1.3.0`、实现版本提升到 `4.0.0`；
  结构 Schema 未变，因此 contractVersion 保持 `1.0.0`。历史台账保留 H4 的
  首发 release，并记录 H5 的最后发布及新程序集 hash。
- Go 侧 `system_prompt.zh-CN.json` 独立加载、严格验证且不进入 Unity。现有 Context
  保存创建时的 Prompt 字符串，新建及存档恢复产生的新 Context 使用当前活动版本；
  日志不输出 Prompt 正文。

## 自动验证

- Unity EditMode 覆盖只更新 Catalog 时描述改变、结构 Schema hash 不变、一次
  `ToolsChanged`、重复激活幂等，以及非法 Catalog 不改变活动快照。
- Unity 全项目 EditMode：16/16 通过，包含无 Catalog 时的标识符降级与描述省略验证，结果位于本地
  `unity-NPC-agent-client/Logs/h5-editmode-results.xml`。
- HybridCLR `Generate/All` 与 H5 staging 成功。三份 JSON 均落入本地 release，
  manifest 的 `catalogVersion` 为 `2026.09.001`，声明 DLL SHA-256
  `c7587745...20061e9` 与 staged 文件一致。
- Windows x64 IL2CPP Development Player 构建成功，构建记录见
  `Docs/Baselines/h5-windows-il2cpp-build.json`。Player smoke 退出码为 0，并输出
  `H2_SMOKE_SUCCESS`、`H3_ATOMIC_PACK_SUCCESS`、`H4_TOOLSET_SUCCESS` 和
  `H5_CATALOG_SUCCESS`；未出现 `MissingMethodException` 或
  `ExecutionEngineException`。
- 分别缺失 `tool_metadata.zh-CN.json`、`agent_messages.zh-CN.json` 的 Player 负向
  smoke 均输出 `Required H5 Catalog ... was not found` Warning，并按预期拒绝该
  release；测试文件随后均已恢复。
- Go `go test ./...` 与 `go vet ./...` 通过。
- 当前机器的 `go test -race ./...` 在测试进程启动时统一以 Windows
  `0xc0000139` 退出；单包复现且没有测试断言输出，属于本机 Go 1.26.5 与
  MinGW/race runtime 加载问题，未计作代码测试通过。

## 回滚

无效 JSON 在准备阶段拒绝，保留上一成功 Catalog。纯文案可以原子切回仍完整描述
当前 ToolSet 的旧版本；工具实现或结构集合的回滚继续遵循 H4 的重启激活语义。
