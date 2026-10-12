# Addressables A2 基线

> 完成日期：2026-09-25  
> Unity：`6000.3.19f1`  
> Addressables：`2.9.1`  
> HybridCLR：`8.14.1`

## 已实现

- 三份 `zh-CN` JSON 由 `Remote_ClientConfig` 以稳定逻辑地址发布。
- 七份 AOT metadata 由 `Remote_HotfixMetadata` 发布；Smoke Tool Pack 的版本化
  DLL 和 Development PDB 由 `Remote_ToolPacks` 发布。
- `hotfix/release/manifest` 绑定完整 ToolSetSnapshot、retired tombstone、历史台账、
  JSON、metadata 和工具包。每个交付物声明稳定地址、文件名、长度和 SHA-256，清单
  同时声明内容版本、Player build identity 和兼容版本范围。
- `ClientContentBootstrap` 在 Runtime、A2A 和业务 UI 开放前完成清单加载、兼容性
  校验、按地址下载和候选验证。`AddressableHotUpdateReleaseLoader` 在任何 HybridCLR
  副作用前复制并验证全部 bytes 和 JSON；加载完成即释放 Addressables lease。
- ToolSet 与三份文本 Catalog 继续在 `ToolsRegistry`/`ClientTextCatalogs` 边界原子
  激活。候选失败不记录成功版本，并以 BuiltinTools 和稳定文本键启动。
- A2 staging 完成后删除可重建的旧 `StreamingAssets/HotUpdate` 中转目录；Windows
  Player 不再包含本地/远端双来源。
- LocalDevelopment、QA、Production 使用相同不可变 ReleaseId、独立 channel 路径和
  稳定 Catalog 入口。Addressables 缓存命中由地址与 bundle hash 保证不重复下载。

## 验证结果

- A2 staging 校验：11 个候选交付物的地址、长度、SHA-256 和 Addressables owner
  Group 全部一致。
- Unity EditMode：17/17 通过，包含损坏 A2 清单时拒绝候选、保留内置工具且不记录
  成功版本的覆盖。
- LocalDevelopment、QA、Production 三套 Windows Addressables 内容均构建成功，
  版本目录为 `h5-local-1.2.0`。
- Windows x64 IL2CPP Development Player 构建成功；Player 内不存在
  `StreamingAssets/HotUpdate`。
- 本地 HTTP 内容服务端到端 smoke 退出码为 0，输出 `H2_SMOKE_SUCCESS` 至
  `H5_CATALOG_SUCCESS` 以及 `A2_ADDRESSABLES_SUCCESS`；服务日志确认 Catalog 和
  三个远端 bundle 由版本目录返回。

## 发布命令

```text
GameWithLLM/Hot Update/Stage And Configure Addressables A2
AddressablesA2ProjectSetup.BuildAllWindowsProfilesFromCommandLine
AddressablesA2ProjectSetup.BuildSmokePlayerFromCommandLine
```

发布 staging 依赖已生成的 HybridCLR Windows x64 产物。当前三个 channel 共用同一
候选，因此统一不发布 PDB，确保 Production 不携带调试符号。
