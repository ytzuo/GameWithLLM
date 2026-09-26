# HybridCLR H6 基线

> 完成日期：2026-09-25  
> Unity：`6000.3.19f1`  
> HybridCLR：`8.14.1`  
> 集成基线：Addressables A2

## 已实现

- `AddressableHotUpdateReleaseLoader` 保留 release manifest、兼容性和三份客户端 JSON
  的整体验证；`HybridClrToolPackageLoader` 独立负责 AOT metadata、工具 DLL 与可选 PDB。
- 所有选中二进制先按稳定地址读取并校验长度和 SHA-256，之后才依次加载 AOT metadata、
  工具程序集并调用 `AgentToolDiscovery.DiscoverFromAssembly`。发现结果直接进入 H4 候选构建。
- 工具包按 `packageId + packageVersion + assembly hash` 线程安全缓存；同一进程重复候选不会
  重复 `Assembly.Load` 或工具发现。失败缓存项会移除，允许后续重试。
- Development/QA 可在清单提供 PDB 时加载；Production 路径不把 PDB 纳入必需地址。
  当前 A2 公共候选仍统一不发布 PDB，因此 Production 内容不携带调试符号。
- 装载错误使用 `HOT_UPDATE_*` 稳定错误码，并记录 releaseId、packageId、版本、hash、阶段
  和耗时。日志不包含工具参数、对话或 Prompt。
- A2 内容成功标记延后到 H4 `PrepareToolSet` / `ActivateToolSet` 完成之后。候选加载或工具
  身份、版本、Schema、Catalog 校验失败时，Registry 与 Runtime Manifest 保持旧快照；首次
  安装则继续使用 BuiltinTools 和稳定文本键。
- Runtime Gateway 仍在内容激活后创建；启动时成功激活只形成初始完整 Manifest，不会发布
  半套候选或逐工具增量。

## 稳定错误码

| 错误码 | 阶段 |
|---|---|
| `HOT_UPDATE_ARTIFACT_LOAD_FAILED` | Addressables artifact load |
| `HOT_UPDATE_ARTIFACT_LENGTH_MISMATCH` | bytes 长度校验 |
| `HOT_UPDATE_ARTIFACT_HASH_MISMATCH` | SHA-256 校验 |
| `HOT_UPDATE_METADATA_LOAD_FAILED` | HybridCLR AOT metadata 加载 |
| `HOT_UPDATE_PACKAGE_IDENTITY_MISMATCH` | 包、版本或程序集身份校验 |
| `HOT_UPDATE_ASSEMBLY_LOAD_FAILED` | 工具程序集加载 |
| `HOT_UPDATE_TOOL_DISCOVERY_FAILED` | 显式工具发现 |
| `HOT_UPDATE_TOOLSET_REJECTED` | H4/H5 候选联合校验或原子激活 |

## 验证

- Unity EditMode：21/21 通过；覆盖 Production 不请求 PDB、工具显式发现、包缓存复用、
  DLL hash 稳定错误码、A2 成功标记延后到 Registry 激活，以及既有 H4/H5/A1/A2 回归。
- A2 staging：11 个不可变候选交付物校验通过。
- Windows x64 IL2CPP Development Player 使用 MSVC 14.42 清缓存构建成功；Player 内没有
  `StreamingAssets/HotUpdate` 双来源。
- 本地 HTTP 内容服务 smoke 退出码为 0，依次输出 `H2_SMOKE_SUCCESS`、
  `H3_ATOMIC_PACK_SUCCESS`、`H4_TOOLSET_SUCCESS`、`H5_CATALOG_SUCCESS` 与
  `A2_ADDRESSABLES_SUCCESS`。H6 遥测确认 metadata、package-ready、complete 顺序；本次
  命中已验证的 Addressables bundle cache，仅重新请求远端 Catalog hash，未重复下载 bundle。
