# NPC 表现管线重构：阶段五完成记录

日期：2026-10-14。状态：第三 NPC 作者接入、表现替换和本地部署验收完成。
计划：[重构计划](../NPC_PRESENTATION_PIPELINE_REFACTOR_PLAN.md)。
事实源：[ARCHITECTURE.md](../../ARCHITECTURE.md)。
作者入口：[NPC 作者操作说明](../NPC_AUTHORING.md)。
结构化证据：[verification.json](verification.json)。

## 已交付

- 新增 `Assets/Content/Npcs/Keeper`，包含当前 Definition、Prefab、头像、Avatar、材质与动画。
  构建器自动生成 `NpcContent/npc/Keeper`，发现 `npc/Keeper/visual` 和 `content.npc.Keeper`。
  接入复用现有工具，Registry、Dispatcher、Go 工具 Schema 与公共 SDK 无修改。
- 初始候选 `npc-author-onboarding-1` 下载、生成 Keeper 并创建格式 3 世界存档和格式 2 对话快照。
  随后覆盖同一名称目录的资产，保留 GUID；源工程只有当前资产，没有旧模型/动画副本。
  当前候选为 `npc-author-current-1`，Player 0.3.0、ToolSet 7.0.0、工具包 1.6.0。
- 两轮真实 Windows IL2CPP Player 验证下表中的实际加载结果。更新后恢复初始存档，
  三个动态 NPC 的 Transform 和 Inventory 恢复，Go Context 使用当前 Profile/Prompt。
  两轮工具 Schema SHA-256 完全相同，不需要 NPC 版本迁移。
- 修正 SampleScene 地面的 y=-0.38 偏移并重新烘焙 NavMesh，保留原 GUID。
  显式场景准备入口保留 SampleScene 的宽地面及 WarehouseRemote 的独立布局，
  支持 SampleScene x=-8.09/7.68 两个地标；没有放宽严格原点生成条件。
  最终候选在修复后重新构建，并重新获取 Player evidence。
- 诊断和回归入口按当前目录验证全部 NPC，恢复时按存档身份释放实例；旧的“只有两个 NPC”
  测试断言改为核对作者 Definition 集合。诊断等待点同时观察任务完成，失败直接报告原始错误，
  避免在未进入注入等待点时只报超时。正常客户端启动不启用这些诊断。

| Keeper 表现 | 初始 | 当前 |
|---|---|---|
| 技术名称 / Address | Keeper / npc/Keeper/visual | 相同 |
| 中文展示名 | 仓库守卫 | 仓库巡查员 |
| 实际 Mesh | Cube | Sphere |
| Body 比例 | 0.7 × 1.5 × 0.7 | 0.85 × 1.6 × 0.85 |
| 实际动画长度 | 0.5 秒 | 1 秒 |
| 动画 / 材质 | 小幅移动 / 黄色 | 较大幅移动 / 橙色 |

## 验证

- 两轮 Player：全部目录 NPC 的下载生成、动画绑定、普通/流式对话、warehouse/gate 移动、
  Task 与移动取消、Inventory、世界/Context 恢复、Go 重启重连与 Manifest 重发通过。
  专用物理缓存驱逐和修复、在线缓存重启、内容服务返回 503 时离线缓存重启通过。
- 更新后的 Player 恢复初始存档通过；每个动态实体位置与原存档匹配，库存快照完整一致。
  恢复后的 Context 向确定性模型发送当前中文展示名；报告只记录检查结果，不保存 Prompt。
- 删除 Keeper 清单或将技术身份改为 KeeperRenamed，Go restore 明确失败，现有 Context 和
  Inventory 保留；恢复测试仅修改本地部署副本，随后还原。
- SampleScene 全部网络/游戏回归及故障矩阵通过，资源 lease 前后均为 20；
  原点采样误差约 `4.44e-19`。缺失 Address/依赖、取消、恢复预检、本轮生成回滚与
  无效当前 Go 配置保留世界/Inventory/Context 的检查通过。
- 最终 C# 编译和 EditMode **57/57** 通过。Go test/vet 全量通过；
  同版本 Go 1.26.5 的 WSL race 使用本机模块缓存通过。
- 两轮候选各 **778** 个文件 SHA-256 复核通过。当前候选为 26 个 Bundle，
  remoteBytes 2,683,188，重复隐式资源 0；工具包、AOT 和 Schema 门禁通过。

报告为本目录中的 initial/current-player-report、player-checks、NPC smoke evidence、
sample-scene-report、sample-fault-report、sample-visuals、sample-navmesh-report 和 author-update。
原始运行输出位于 `Artifacts/NpcPresentationStage5`；SampleScene 输出位于
`Artifacts/NpcPresentationStage3`，本目录保存本轮证据副本。
最终 Editor 日志和测试结果位于 `unity-NPC-agent-client/Logs/npc-stage5-*.log/xml`。
日志中的启动凭据字段已脱敏，报告不包含环境凭据、模型全文、历史或工具参数。

## 复现与交付边界

Editor 使用 unity-launch 技能及仓库 `Scripts/Run-UnityBatch.ps1`；启动前必须没有 Editor。
`NpcPresentationStage5Authoring.InitialFromCommandLine` / `UpdatedFromCommandLine` 是显式验收夹具，
在唯一当前资产集上生成两种表现，不参与正常构建，也不保留历史源资产。
每轮整体 releaseId 与工具 smoke plan 按既有发布配置保持一致；这不是 NPC 内容版本。

```powershell
$env:CONTENT_RELEASE_BASE_URL = 'http://127.0.0.1:8081'
./Scripts/Build-ContentRelease.ps1 -UnityExe '<Unity 6000.3.19f1 Editor/Unity.exe>'
python -X utf8 Scripts/Test-NpcPresentationStage4.py Artifacts/Content/npc-author-current-1 `
  --output Artifacts/NpcPresentationStage5/Player `
  --restore-save-id '<初始轮保存的 saveId>'
```

跨轮恢复还需保留相同 output 下的 Go conversations、初始 previous-world-contract.json 和
Unity 世界存档。SampleScene 使用阶段三协调器及 SceneSmoke；runtime-env 只传入本轮进程，
Unity 进程排除 LLM 环境字段。最终生产构建脚本与 CI 保持原有启动方式。

本次为本地 HTTP Production Profile 候选部署，使用确定性模型，未调用外部 LLM，
未生成整体受保护环境 evidence，未切换公网生产指针。公网交付仍须按实际内容根重新构建，
取得目标环境与候选绑定的 evidence，并通过原有发布事务。用户全局 Cache 和旧用户存档未删除。
