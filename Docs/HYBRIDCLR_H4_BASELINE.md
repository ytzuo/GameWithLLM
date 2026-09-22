# HybridCLR H4 基线

> 完成日期：2026-09-22  
> Unity：`6000.3.19f1`  
> HybridCLR：`8.14.1`

## 已实现

- 本地 release manifest 表示完整期望状态，包含 `releaseId`、`toolSetVersion`、
  `catalogVersion`、Player 兼容范围、`activeTools`、`retiredTools` 和
  `toolHistory`。
- 活动工具声明固定逻辑身份、来源、实现/契约版本、包版本、程序集、程序集
  SHA-256 和规范化结构 Schema SHA-256。
- `AgentToolDiscovery` 只产生内置或指定程序集的候选，不再直接修改 Registry。
- `ToolsRegistry.PrepareToolSet` 在活动锁外验证完整候选；
  `ActivateToolSet` 只在锁内校验准备基线并原子交换不可变快照。
- Runtime 工具目录、实体能力枚举和执行路由读取同一个快照。执行开始时捕获工具
  实例；后续快照变化不影响在途调用。
- 删除工具必须提供并持续保留 tombstone。陈旧调用返回 `TOOL_RETIRED`，不会落到
  旧实例；重新启用必须保持逻辑身份并提升实现版本。
- 实现包 hash 或包版本改变必须提升 `implementationVersion`；去除 description 后
  的规范化结构 Schema hash 改变必须提升 `contractVersion`。
- 历史台账禁止改写工具身份、来源和首次 release。候选失败和过期的
  `PreparedToolSet` 都不会改变当前快照。
- `Docs/Baselines/h4-tool-history.json` 是已提交的 H4 历史台账；staging 会把实际
  工具/Schema/程序集 hash 与该台账逐项比较，缺失、漂移或删除后未留 tombstone
  都会阻断构建。
- 启动先完成内容 bootstrap 和 H4 ToolSet 激活，再创建 A2A、Save 和 Runtime
  Gateway 客户端。成功激活记录最后成功的 release/toolSet/catalog 三元组。
- staging 只写入 release 选择的工具包；Production 仍排除 PDB。

H5 的 Addressables JSON 文案 Catalog 尚未实施。H4 当前使用工具 Descriptor 的
内嵌描述并要求 `catalogVersion`，同时保持结构 Schema hash 不受 description 文本
影响，为 H5 的 ToolSet/Catalog 联合激活保留边界。

## 自动验证

EditMode 覆盖：

- N 到 N+1 同时新增、修改和删除，且只触发一次 `ToolsChanged`。
- 实现或结构 Schema 改变未提升相应版本时拒绝候选。
- 删除后 Manifest/路由不可见，陈旧调用返回 `TOOL_RETIRED`。
- 非法候选、历史身份复用和过期 prepared snapshot 无副作用。
- 相同候选重复激活幂等。
- 快照切换前捕获的在途调用仍以旧实例完成。
- Smoke Tool Pack 继续只通过显式程序集发现。

验证命令由项目固定 Unity batch 启动器运行 EditMode tests。结果文件：
`unity-NPC-agent-client/Logs/h4-editmode-results.xml`（本地生成，不提交）。

## 2026-09-22 验证结果

- Unity 全项目 EditMode：13/13 通过。
- HybridCLR `Generate/All` 与 H4 staging 成功；完整 manifest 含 11 个活动工具、
  11 条历史记录和 1 个候选包，声明 DLL SHA-256 与文件实际 hash 一致。
- Windows x64 IL2CPP Development Player 构建成功；构建记录见
  `Docs/Baselines/h4-windows-il2cpp-build.json`。
- Player smoke 退出码为 0，并输出 `H2_SMOKE_SUCCESS`、
  `H3_ATOMIC_PACK_SUCCESS` 和 `H4_TOOLSET_SUCCESS`；未出现
  `MissingMethodException` 或 `ExecutionEngineException`。
