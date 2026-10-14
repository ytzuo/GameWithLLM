# NPC 表现管线重构：阶段三完成记录

日期：2026-10-14。状态：完整性、取消与恢复闭环已完成，尚未发布新 Player。
计划：[重构计划](../NPC_PRESENTATION_PIPELINE_REFACTOR_PLAN.md)。
事实源：[ARCHITECTURE.md](../../ARCHITECTURE.md)。证据：[verification.json](verification.json)。

## 已交付

- 安装、缓存可用性检查、恢复预检和生成共用 `NpcPresentationPreparation`。先解析每个强类型 Address，再实际加载 Prefab、实例化到 inactive 父对象、创建并绑定驱动。下载大小为零不再代表资源完整。
- 检查 Missing Script、权威组件、有效 Animator/Avatar/Controller、builtin 的四个参数；hot-update 保留 DLL 长度/hash、程序集名称、入口、构造器及接口检查。失败释放实例、DLL 和驱动；成功生成后由实体生命周期持有统一 preparation lease。
- 缓存驱逐或实际加载损坏会进入修复；离线使用最后完整的 current 安装记录。安装失败和取消保留上一记录。损坏安装索引明确要求重装，不删除用户文件；配置解析错误不会被目录缓存掩盖。
- 安装维持单任务，同名生成合并到一个任务，名称唯一性大小写不敏感。异步开始前登记任务，避免同步完成和重入留下标记；提交前再次检查取消及已生成状态。切场取消并等待安装、生成，立即释放候选 lease。
- 删除存档版本冲突错误码和 UI 文案。Address 无法解析时显示“资源尚未就绪，请重启后重试”，依据当前 Catalog 的地址解析结果，不比较内容版本。
- 恢复先完整预检，再生成所需实体。本轮生成失败全部回收。暂时被移出的 NPC 保留到事务提交；Go 恢复失败时回收新增实体、重新激活原实体、还原 Transform/Inventory，并重新发布完整 Manifest。
- Unity 在 Go 恢复成功后才替换 Context 引用和聊天记录。失败保留原 Context；Go 已有的当前配置读取、既有 Context 固定配置、新建/恢复使用当前配置机制通过回归验证。

## 验证

| 检查 | 结果与证据 |
|---|---|
| Unity C# 与相关 EditMode | 28/28 通过；`unity-NPC-agent-client/Logs/npc-stage3-editmode.xml` |
| 完整性故障 | 缺失 Address/依赖、损坏 Prefab、Missing Script、错误 Animator 参数、DLL hash/字节/接口无效均拒绝；失败/取消无残留 lease |
| SampleScene 故障矩阵 | 单任务安装、取消、缓存修复、离线安装准备、同名生成、切场取消、恢复预检失败、第二实体生成失败、未提交恢复回滚通过 |
| 表现修改 | Merchant 地址映射到 Guide 的当前模型/动画并修改中文展示名后，仅身份存档仍可准备恢复，清理通过 |
| 网络与游戏回归 | 两 NPC 注册、普通/流式对话、warehouse/gate 移动、Task/移动取消、Inventory、世界/Context 恢复、Go 重启重连与完整 Manifest 通过 |
| 当前配置损坏 | Go 恢复失败后 Unity 位置/库存/Context 保持原状，既有 Context 可对话，新 Context 被拒绝 |
| Go test / vet | 全量通过；Go 生产代码未修改 |
| Go race | Go 1.26.5、Ubuntu WSL 全量通过；Windows 原生仍在启动时退出 `0xc0000139` |

故障报告为 `Artifacts/NpcPresentationStage3/fault-report.json`，真实 Addressables provider 的
lease 数量从基线 20 回到 20，无孤立远端实体。缓存驱逐、Address 缺失、依赖加载失败和切场中的
等待点通过 provider 边界故障注入验证；成功下载、加载、实例化仍走实际 Addressables。
缓存驱逐测试没有清空用户的全局 Unity Cache，不将边界模拟记为物理缓存驱逐或远端生产验收。
Editor 集成报告为 `Artifacts/NpcPresentationStage3/scene-report.json`；使用本地确定性模型，
不调用外部 LLM。日志与 transient 文件留在忽略目录，交接证据不包含正文、Prompt、历史或凭据。

## 复现

```powershell
# 仓库根启动本地协调器，再在另一 PowerShell 中启动 Unity。
python -X utf8 Scripts/Test-NpcPresentationStage3.py
$stage3Env = Get-Content Artifacts/NpcPresentationStage3/runtime-env.json -Raw | ConvertFrom-Json
foreach ($property in $stage3Env.PSObject.Properties) {
    [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
}
# 用 unity-launch 的 Run-UnityBatch.ps1 执行异步方法，不传 -quit：
# NpcPresentationStage3SceneSmoke.RunFromCommandLine
# 对应 EditMode 过滤器：
# NpcPresentationStage3Tests|RemoteNpcRoundTwoTests|RemoteNpcRoundThreeTests|NpcContentContractTests|ClientContentBootstrapTests
```

Go race 使用仓库已有的 `.cache/go-linux/go/bin/go`，WSL 设置 Windows 模块缓存为
`GOMODCACHE=/mnt/c/Users/Xiao_Chen/go/pkg/mod`，并设置 `GOTOOLCHAIN=local`。
Unity 批处理退出码、测试 XML、场景报告均通过；现有 Licensing 握手提示不被记作运行时协议错误。

## 阶段四交接

下一阶段完成生产 smoke 场景名、静态 HTTP 缓存策略、发布失败事务与历史文档收束，
建立新 Windows IL2CPP Player 和部署基线。阶段三 Editor 故障报告不替代与整体候选绑定的生产 evidence。
第三 NPC 的作者接入和实际 Player 表现替换体验仍属于阶段五。
