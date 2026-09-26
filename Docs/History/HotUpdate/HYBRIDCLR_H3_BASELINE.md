# HybridCLR H3 基线

## 完成范围

- AOT BuiltinTools 只从 `GameWithLLM.Client.BuiltinTools` 稳定程序集启动注册。
- `AgentToolDiscovery.DiscoverFromAssembly` 对指定程序集进行完整发现；标记为
  `[AgentTool]` 的类型必须是具体、闭合、实现 `IAgentTool` 且具有公共无参构造函数。
- 本地工具包清单记录 `packageId`、`packageVersion`、程序集名、DLL SHA-256、
  DLL 文件和可选 PDB 文件。Player 在 `Assembly.Load` 前验证 SHA-256。
- `ToolsRegistry.RegisterToolPack` 在锁外验证整包候选，在锁内完成冲突检查和一次性
  追加。失败时 Registry 不发生变化，成功时只触发一次 `ToolsChanged`。
- 相同 `packageId + version + hash + toolNames` 的重复加载返回幂等结果；同版本内容
  漂移、包内重名和与现有工具重名均被拒绝。
- 不支持工具包卸载、替换、程序集卸载或同名覆盖。

## Smoke Tool Pack

`GameWithLLM.Tools.Pack.SmokeTest` 当前包含两个只读工具：

- `game_hotfix_smoke_query`
- `game_hotfix_smoke_package_info`

Windows IL2CPP Player 的 `-gameWithLlmHybridClrSmoke` 验证仍保留
`H2_SMOKE_SUCCESS` 兼容标记，并新增 `H3_ATOMIC_PACK_SUCCESS` 标记。

## 自动验证

`Assets/Tests/Editor/ToolPackRegistrationTests.cs` 覆盖：

- 多工具包只触发一次变更事件。
- 完全相同的包重复注册为幂等操作。
- 任一 Descriptor/Schema 无效时整包不提交。
- 工具名冲突和相同包版本的内容漂移不会改变既有 Registry。
- Smoke 程序集按程序集显式发现两个工具。
- 旧工具调用等待期间可追加新包，旧调用仍按原工具实例完成。

可重复执行入口：

- `HybridClrProjectSetup.GenerateAndStageFromCommandLine`
- `HybridClrProjectSetup.BuildH3SmokePlayerFromCommandLine`
- Player 参数 `-gameWithLlmHybridClrSmoke`

## 2026-09-21 验证结果

- Unity EditMode：H3 专项测试 5/5 通过（全项目 EditMode 6/6 通过）。
- BuiltinTools Schema 基线一致，`SampleScene` 无 Missing Script。
- HybridCLR `Generate/All` 与 staging 成功；清单 SHA-256 和 staged DLL 实际哈希一致。
- Windows x64 IL2CPP Development Player 构建成功。
- Player 退出码为 0，输出 `H2_SMOKE_SUCCESS` 和 `H3_ATOMIC_PACK_SUCCESS`；日志中
  无 `MissingMethodException` 或 `ExecutionEngineException`。
