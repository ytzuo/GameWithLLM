# NPC 表现管线重构：阶段二完成记录

日期：2026-10-14。状态：无版本最小端到端切换已完成；尚未发布新 Player。
计划：[重构计划](../NPC_PRESENTATION_PIPELINE_REFACTOR_PLAN.md)。
事实源：[ARCHITECTURE.md](../../ARCHITECTURE.md)。结构化证据：[verification.json](verification.json)。

## 已交付

- Merchant、Guide 当前 builtin 源资产迁入各自名称目录，Definition/Visual/Avatar 与分类依赖采用固定名称；16 个文件及两个目录的 GUID 保持不变。
- 删除两个样例的历史源资产、旧动画程序集源码与 HybridCLR 注册项、旧 NPC Address/Label，以及旧版本静态目录。保留工具包、整体 release、AOT metadata 与共享资源。
- 作者只填写 name 和一份 displayName；Profile 复用展示名。构建先校验并生成临时当前目录，再提交静态目录，清理移除的 NPC 输出。当前 Prefab Address 为 npc/{name}/visual。
- Unity、Go、SDK 和所有 Manifest 快照统一新结构；绑定与存档仅含身份。NPC schema/content/Player/Catalog 版本与清单 hash 不再参与解析或匹配。可选动画 DLL 仍检查字节完整性及接口。
- Go 每次新建 Context 读取当前 Profile/Prompt，已有 Context 保持创建时 Prompt；恢复使用当前配置，读取失败不替换原 Context。测试覆盖配置改变与失败恢复。
- 世界存档格式 3、对话快照格式 2；安装索引和目录缓存使用 current 文件名。旧格式明确拒绝，发现旧安装索引提示重装，保留旧用户文件。
- 统一静态验证入口接受新契约；发布脚本允许覆盖 NPC 当前载荷、最后切换索引，整体 release 的不可变校验与 smoke 证据门禁保持。

## 端到端发现并修复的问题

动态注册原先在 RegisterEntity 同步发布 Manifest 后才加入绑定，导致后续补绑定被 Gateway 拒绝。Host 现先准备能力与绑定，再注册实体，使首份完整 Manifest 同时包含实体与身份引用。

SampleScene 原地面位于 y=-0.65，原点又靠近 NavMesh 边缘，不能满足已有严格 (0,0,0) 生成约束。已扩大地面、调整高度并以 0.02 voxel 和高度网格重烘焙，保留 NavMesh 资产 GUID。运行时检查没有放宽；本地 Alice/Ryan 身份保持现状。

## 验证

| 检查 | 结果与证据 |
|---|---|
| Go test / vet | 全量通过 |
| Go race | 同版本 Go 1.26.5 在 Ubuntu WSL 全量通过；Artifacts/NpcPresentationStage2/go-race-wsl.log |
| Unity C# 与相关 EditMode | 15/15 通过；Logs/npc-stage2-editmode.xml |
| Addressables 内容构建 | NPC_LOCAL_CONTENT_BUILD_SUCCESS；Logs/npc-stage2-content-build.log |
| Go 静态内容验证 | NPC_CONTENT_VALIDATED npc_count=2 |
| 发布事务脚本 | 当前载荷覆盖、篡改拒绝、索引切换与回滚通过 |
| GUID/依赖审计 | 16 个文件、2 个目录 GUID 保留，无指向已删除 GUID 的资产引用 |
| SampleScene | 下载/生成/注册、两个 NPC 普通与流式对话、移动 warehouse/gate、取消 Task/移动、Inventory、重新生成后的世界与 Context 恢复、Go 重启重连、Console/Missing Script 检查全部通过 |

Unity 日志路径相对 unity-NPC-agent-client；运行报告为 Artifacts/NpcPresentationStage2/scene-report.json。集成使用本地确定性模型服务，不调用外部 LLM，不将正文、Prompt 或凭据复制到交接文档。世界契约摘要确认格式 3 与仅身份引用，对话归档检查格式 2。大小写重复名称、Windows 保留名、展示名不改变身份、旧字段和旧保存格式拒绝由相关测试覆盖。

Windows 原生 race 仍在测试进程启动时退出 0xc0000139，未进入用例；未将它记为原生通过。WSL 使用临时解压的相同 Go 工具链与已有模块缓存。Unity 日志中的已有授权握手/access-token 提示不作为 C# 或运行时协议错误；实际批处理退出码与测试、业务标记通过。

可复现入口：

```powershell
# 仓库根；启动协调器后，在另一个 PowerShell 为本次 Unity 子进程应用生成的 runtime-env.json。
python -X utf8 Scripts/Test-NpcPresentationStage2.py
# 通过 unity-launch 的 Run-UnityBatch.ps1 执行以下异步方法，不加 -quit：
# NpcPresentationStage2SceneSmoke.RunFromCommandLine
python -X utf8 Docs/NpcPresentationStage2/verify_assets.py
```

一次性资源迁移入口为 NpcPresentationStage2Migration.Run，场景准备入口为 PrepareSampleScene；二者不被生产构建隐式调用。常规作者构建继续使用 NpcContentRelease.BuildDefinitions。

## 下一阶段边界

阶段三补齐统一准备/释放、离线缓存修复、Address 缺失专用提示、损坏资源、切场取消及更全面恢复失败回滚。
阶段四收束生产 smoke 场景名、缓存重新验证、失败发布事务与历史文档，并建立新的 Windows IL2CPP/部署验收基线。当前 production smoke 的 version-conflict/catalog-restart-required 标签仍待该阶段替换，不据此宣称新的生产环境 smoke 已通过。
阶段五验证第三个 NPC 的接入与表现替换体验。
