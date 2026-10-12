# Addressables A1 Bootstrap 与 Remote Catalog 基线

> 状态：完成  
> 日期：2026-09-22  
> Unity：6000.3.19f1  
> Addressables：2.9.1  
> 目标：Windows x86_64

## 1. Profile 与发布路径

当前活动 Profile 为 `LocalDevelopment`。三套部署 Profile 均使用 `ReleaseId`
变量，Bundle 路径不可变，Catalog 路径稳定：

| Profile | Remote Build | Remote Load | Catalog Build | Catalog Load |
|---|---|---|---|---|
| LocalDevelopment | `ServerData/local/[BuildTarget]/[ReleaseId]` | `http://127.0.0.1:8081/ServerData/local/[BuildTarget]/[ReleaseId]` | `ServerData/local/[BuildTarget]/catalog` | `http://127.0.0.1:8081/ServerData/local/[BuildTarget]/catalog` |
| QA | `ServerData/qa/[BuildTarget]/[ReleaseId]` | `https://content-qa.gamewithllm.dev/[BuildTarget]/[ReleaseId]` | `ServerData/qa/[BuildTarget]/catalog` | `https://content-qa.gamewithllm.dev/[BuildTarget]/catalog` |
| Production | `ServerData/production/[BuildTarget]/[ReleaseId]` | `https://content.gamewithllm.dev/[BuildTarget]/[ReleaseId]` | `ServerData/production/[BuildTarget]/catalog` | `https://content.gamewithllm.dev/[BuildTarget]/catalog` |

默认 `ReleaseId` 是 `a1-bootstrap`，发布流水线必须为正式内容版本传入不可复用的
release ID。远端 Catalog 已开启；启动时的隐式 Catalog 更新已关闭，检查与更新只
能由 `ClientContentBootstrap` 执行。Catalog 请求超时 15 秒，Bundle 超时 30 秒、
重试 2 次。

## 2. Group 基线

本地 Group：

- `Local_Bootstrap`（默认 Group）
- `Local_SampleScene`
- `Local_UnityPackage`

远端 Group：

- `Remote_ClientConfig`
- `Remote_HotfixMetadata`
- `Remote_ToolPacks`
- `Remote_UI`
- `Remote_SpritesTextures`
- `Remote_Materials`
- `Remote_Characters`
- `Remote_Scenes`

远端 Group 使用 hash-only Bundle 名称和 Remote path pair。本地 Group 使用 Local
path pair。所有 Group 的旧缓存策略均为“仅空间不足时清理”；A1 不主动清除旧缓存。
`unifiedraytracing` 仍由 Unity 包维护，没有并入业务 Group。

A1 不迁移业务资源；除下述 delivery probe 外，目标 Group 当前为空。A2-A6 按 A0
台账逐项移动资源，避免 Bootstrap 与资源迁移同时改变故障面。

例外是 `Remote_ClientConfig` 中 73 字节的 `config/bootstrap/a1-probe`，Label 为
`content.a1-required`。它不是业务配置，只用于让 A1 在没有迁移 A2 资源时仍能真实
验证下载大小、进度、版本化 Bundle、缓存命中和失败恢复。

## 3. 启动状态机与门控

`AgentHostClient` 在 `Awake` 阶段立即关闭 `PlayerMock` 输入并隐藏业务
`UIDocument`，随后执行：

```text
Local Ready
→ Addressables Initialize
→ Catalog Check/Update
→ Release Manifest Load（A1 内置空候选；A2 接远端清单）
→ Compatibility Validation
→ Required Download
→ Candidate Validation
→ Activation
→ Enable Runtime/Game/UI
```

激活前不会创建 A2A Client、Save Client 或 Runtime Gateway，不会生成/发布
Runtime Manifest，也不会加载 H3 工具包。激活完成后才恢复 UI 和玩家输入。

`ContentBootstrapOverlay` 只使用 Player 内置 IMGUI，不依赖 Addressables、业务
UIDocument、远端字体或 Bundle。首次离线且无成功缓存时，它显示错误并提供重试，
Runtime 与游戏保持冻结；存在最后成功标记时，远端 Catalog 检查失败可继续使用
Addressables 已恢复的缓存版本。

场景上的 `enableContentBootstrap` 是显式回滚开关。关闭后 `SampleScene` 仍沿用
A0/H3 本地路径启动。

## 4. Handle 所有权

`ContentAssetProvider` 是唯一 Addressables 运行时入口：

- 初始化、Catalog 检查/更新、下载大小和下载进度的临时 handle 在操作结束释放。
- `LoadAssetAsync<T>` 返回 `ContentAssetLease<T>`。
- `InstantiateAsync` 返回 `ContentInstanceLease`，由 `ReleaseInstance`/`Dispose` 释放。
- `LoadSceneAsync` 返回 `ContentSceneLease`，由 `UnloadSceneAsync` 释放。
- Provider 在应用退出时兜底释放尚未释放的 lease。

业务调用方不得保存裸 `AsyncOperationHandle`。Catalog 更新固定传入
`autoCleanBundleCache: false`。

## 5. 验证记录

- A1 配置校验：3 个 Profile、稳定 Catalog、3 个本地 Group、8 个远端 Group通过。
- Windows Addressables 实际构建：LocalDevelopment、QA、Production 全部成功，
  Catalog 和 probe Bundle 分别输出到三个独立 channel/release 目录。
- 在线 Play Mode：远端 probe 下载并验证后，Runtime、游戏输入和 UI 才解锁。
- 离线 Play Mode：已有 Catalog/probe 缓存时能够继续激活；无缓存失败分支由
  状态机测试验证会保持 Runtime/游戏冻结并进入本地重试 UI。
- EditMode：10/10 通过，其中 A1 状态机覆盖成功、首次离线失败、缓存降级和取消；
  原有 H3 工具注册测试继续通过。
- A0 台账校验继续通过。

生成的 `ServerData` 和 `Assets/AddressableAssetsData/Windows` content-state 是发布
产物，不提交到源码仓库。
