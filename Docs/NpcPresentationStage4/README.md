# NPC 表现管线重构：阶段四完成记录

日期：2026-10-14。状态：生产管线与工程历史收束、新 Windows IL2CPP 候选及本地部署验收完成。
计划：[重构计划](../NPC_PRESENTATION_PIPELINE_REFACTOR_PLAN.md)。
事实源：[ARCHITECTURE.md](../../ARCHITECTURE.md)。证据：[verification.json](verification.json)。

## 已交付

- 生产 NPC evidence 的八项场景固定为 dynamic-download、animation-driver、save-restore、resource-integrity、cache-repair、catalog-address-missing、scene-cancel、manifest-reregister。整体候选 SHA-256 绑定、ToolSet、工具包、AOT metadata 与通用环境门禁保持。
- 构建链先原子生成当前 NPC 定义，成功后清理已删除 NPC 的目录、Address 与 Label。候选只收集 `NpcContent/npc`；Go 静态验证拒绝多余目录/文件、损坏 PNG 与 DLL 字节异常。CI 同时上传当前静态目录，监听作者资产、配置、台账及脚本变化。
- 发布的 WhatIf 不创建归档或覆盖文件。整体归档复制后再次核对 hash/length；当前 payload、指针和最后索引使用可回滚事务，清理已删除 NPC。发布锁与原子写入的持久化日志支持进程中断恢复；回滚失败时保留备份以便重试。事务测试拒绝旧版本场景证据。
- 当前 NPC、Catalog 和指针路径使用 `no-cache, must-revalidate` 与内容 ETag；Bundle hash URL 保持 immutable。HTTP 测试覆盖 304、同秒修改后的新正文和新 ETag。仓库包含独立的 Unity 批处理启动器及五项模拟测试，构建失败不会继续运行旧 Player。
- 新基线为 Player **0.3.0**、release **npc-current-baseline-1**、ToolSet **7.0.0**、Smoke Tool Pack **1.6.0**。当前工具台账位于 `Docs/Release/tool-history-current.json`，旧台账与 release 归档保持。
- 修复 IL2CPP 下 dormant Animator 的参数检查：通过短生命周期 Controller Playable 检查实际 Controller，图始终释放；初始化后同步事件快照。此行为在真实 Player 中验证，未激活实体仍保持 inactive。API 依据：[Unity Controller Playable](https://docs.unity3d.com/ja/6000.0/ScriptReference/Animations.AnimatorControllerPlayable.html)。
- WarehouseRemote 同步 SampleScene 的地面、严格原点 NavMesh 与高度网格；场景 Agent 在 Surface 注册后启用。两份现有 NavMesh 资产保留 GUID，不在启动时动态重建或放宽生成条件。
- 旧简化计划与阶段二一次性迁移脚本（含原 `.meta`）移入 `Docs/History/RemoteNpc`，不再参与 Unity 编译或生产构建。生产源目录、静态目录、SDK、Go 和脚本没有历史 NPC 地址/目录或版本比较；拒绝测试与历史记录保留旧文本。

## 验证

| 检查 | 结果 |
|---|---|
| 完整候选与 Windows x64 IL2CPP | 通过；28 个 Bundle，remoteBytes 4,807,310，重复隐式资源 0 |
| Tool Package Player smoke | `TOOL_PACKAGE_SMOKE_SUCCESS`，工具/Schema/ToolSet/metadata/hash 门禁通过 |
| 候选绑定 NPC runtime smoke | 八项全部通过；[证据副本](npc-runtime-smoke.passed.json) |
| 实际物理缓存 | 专用 NPC Cache 驱逐后检测下载量大于零，真实重新下载/准备后为零；用户全局 Cache 未清空 |
| 动画与网络/游戏 | 两 NPC 下载/生成/注册，对话与流式回复、warehouse/gate 移动、Moving 参数、Task/移动取消、Inventory、世界/Context 恢复、Go 重启与完整 Manifest 重发通过 |
| 在线/离线缓存重启 | 同一 Production Player 重启通过；离线测试内容服务返回 503，NPC 使用最后完整记录与缓存 |
| EditMode/C# | 全量 57/57；含删除生成目录和失败 staged manifest 保留完整当前目录 |
| SampleScene | 最终重跑阶段三集成与故障矩阵通过；当前配置损坏时原世界、库存、Context 保留 |
| Go | test/vet 全量通过；同版本 Go 1.26.5 的 WSL race 全量通过 |
| Windows 原生 race | 仍在启动时退出 `0xc0000139`，记录为环境失败，未标记通过 |
| 发布、HTTP 与启动器 | 发布失败/中断/回滚/WhatIf/清理/旧证据拒绝通过；HTTP 重新验证与五项模拟启动测试通过 |
| 候选完整性 | smoke 后重新核对全部 778 个候选文件的 SHA-256；通过 |
| 整体环境门禁 | 缺少 `content-release-smoke.passed.json` 时拒绝真实候选，未创建 publish root |

Player 报告为 `Artifacts/NpcPresentationStage4/scene-report.json`、`player-checks.json`、
`player.log`、`cache-hit-player.log`、`offline-player.log`。SampleScene 重跑沿用
`Artifacts/NpcPresentationStage3/scene-report.json` 与 fault-report，日志为
`unity-NPC-agent-client/Logs/npc-stage4-sample-scene.log`。EditMode XML 为
`unity-NPC-agent-client/Logs/npc-stage4-editmode.xml`。结构化记录只收集通过项与 hash，
不包含正文、Prompt、历史、环境凭据或 runtime-env。

资源完整性和取消故障使用实际候选资产及 provider 边界注入，包括真实 inactive 实例的
Animator 损坏、缺失 Address/依赖与生成等待点；不把这些注入描述为线上 CDN 故障。
在线主流程没有 Console 编译、Missing Script、线程或协议错误。离线重启的 503 请求会
产生预期的 Addressables 网络错误日志，与在线主流程报告分开。

## 复现

```powershell
./Scripts/Test-UnityBatch.ps1
./Scripts/Test-ContentReleaseTransaction.ps1
python Scripts/Test-ContentHttp.py

# 已烘焙并提交的场景无需重复执行 NpcProductionScenePreparation。
$env:CONTENT_RELEASE_BASE_URL = 'http://127.0.0.1:8081'
./Scripts/Build-ContentRelease.ps1 -UnityExe '<Unity 6000.3.19f1 Editor/Unity.exe>'
python -X utf8 Scripts/Test-NpcPresentationStage4.py Artifacts/Content/npc-current-baseline-1
```

EditMode 使用 unity-launch / 仓库 Run-UnityBatch，参数为 `-runTests -testPlatform EditMode`
与 `-testResults <XML>`，不传 `-quit`。SampleScene 重跑使用既有阶段三协调器和
`NpcPresentationStage3SceneSmoke.RunFromCommandLine`，对应 runtime-env 仅传入本轮进程。
启动前必须没有 Unity Editor；诊断 Player 均由脚本拥有并结束。

## 阶段五交接与发布边界

第三 NPC 的作者接入、模型/动画/中文展示名替换与实际 Player 体验仍属于阶段五。
本阶段构建的 Player/Catalog 绑定本地 HTTP 部署地址，使用确定性模型，未调用外部 LLM。
没有生成整体受保护环境证据，没有切换公网生产指针；低磁盘、下载中断等整体环境门禁
仍要求在目标部署环境真实执行。公网候选必须按实际内容根重新构建并重新取得候选绑定证据。
