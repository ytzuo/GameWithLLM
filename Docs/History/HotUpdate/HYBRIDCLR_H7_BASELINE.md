# HybridCLR H7 基线

> 完成日期：2026-09-25  
> Unity：`6000.3.19f1`  
> HybridCLR：`8.14.1`  
> 目标：Windows x86_64 IL2CPP

## 已实现

- `HybridClrH7ReleasePipeline` 提供候选准备、生产门禁和最终 Production 候选三个可重复
  Editor 入口；H0-H6 的 Generate/All、A2 staging 和 Addressables 构建仍是唯一实现。
- 门禁同时检查锁定工具链、Player build id、Addressables 产物 hash、实际工具 DLL
  AssemblyRef 白名单、完整 ToolSet/历史台账、JSON metadata 覆盖和规范化 Schema 快照。
- `h7-release-policy.json` 保存最后批准的工具指纹。候选中新增或实现/契约/Schema/包
  身份发生变化的工具必须在 smoke plan 中声明；所有热更新工具始终需要 smoke call。
- H7 Player 读取数据驱动的 `h7-smoke-plan.json`，在真实 IL2CPP/HybridCLR 路径执行调用。
  tombstone 必须同时从 Runtime Manifest、实体能力和执行路由消失，路由探测固定返回
  `TOOL_RETIRED`。
- `Scripts/Build-H7Release.ps1` 严格按“候选 → 本地内容服务 → Player smoke → Production”
  顺序执行。smoke 证据绑定 release、ToolSet 和 release manifest hash；缺失或陈旧证据
  会阻止最终候选。
- 最终候选记录 Player、Catalog、Bundle 的路径、长度和 SHA-256。
  `Scripts/Promote-H7Release.ps1` 在复验全部 hash 后才以同目录临时文件原子替换
  `current.json`，并保留 `previousReleaseId` 供回滚选择。
- `.github/workflows/hybridclr-h7.yml` 在锁定标签的 Windows self-hosted runner 上运行完整
  流水线并上传不可变候选；生产 pointer 提升保持为受保护环境中的独立步骤。

## 发布入口

```powershell
./Scripts/Build-H7Release.ps1 `
  -UnityExe 'D:\Application\Unity\6000.3.19f1\Editor\Unity.exe' `
  -LicensingIpc '<由 Unity Hub/CI runner 提供的 channel>'

./Scripts/Promote-H7Release.ps1 `
  -CandidateDirectory './Artifacts/H7/<releaseId>' `
  -PointerDirectory '<已上传不可变内容的 production channel>'
```

提升脚本不负责上传。调用方必须先上传候选清单列出的不可变 Player/Bundle/Catalog，再在
受保护环境中切换 pointer。完整 Addressables 差量更新、CDN 发布及保留窗口属于 A7。

## 变更新工具或 ToolSet

1. 先按 H4 规则更新 release manifest 与永久历史台账。
2. 为每个新增或修改工具在 smoke plan 中给出 JSON 对象参数；为每个删除项保留
   tombstone。工具 JSON 描述必须同步更新。
3. 流水线成功后，才将 `approvedTools` 更新为已通过候选的指纹并提交下一发布基线。
4. 不得手工制造 smoke evidence 或直接调用 finalize/promotion 绕过 Player。

## 验收映射

- 可重复生成：固定工具链和统一入口；候选清单记录所有交付文件 hash。
- 旧 Player 兼容：H4/H6 的 min/max Player 校验继续生效，H7 额外绑定 metadata address
  与 `playerBuildId`。
- 新增/修改/删除：指纹差异强制 smoke；Player 验证调用、可见性和退休路由。
- 回滚：pointer 记录上一 release；运行时仍只在下次启动选择完整上一成功快照。
- 失败不提升：Production finalize 要求外部 Player smoke 证据；pointer 又要求完整候选和
  全部文件 hash，两处均失败关闭。

## 2026-09-25 验证结果

- Unity 全项目 EditMode：21/21 通过。
- 实际 DLL 引用门禁曾拒绝未声明的 `Newtonsoft.Json`，补充为精确允许项后通过；没有
  使用宽泛的第三方依赖前缀。
- Production Generate/All 连续生成的 Smoke 包 SHA-256 均为
  `ac0623bc...32e8cbd9`，release 提升为 `h7-production-1.3.0`，ToolSet 为 `5.0.0`，
  包版本为 `1.4.0`。
- Windows IL2CPP smoke Player 从 LocalDevelopment Addressables 内容启动并真实执行两个
  hot-update 工具，退出码为 0，日志包含 `H7_PLAYER_SMOKE_SUCCESS`。
- Production Addressables 与非 Development Windows IL2CPP Player 构建成功；候选清单
  记录 742 个文件的路径、长度与 SHA-256。
- 使用隔离测试目录执行 pointer 提升成功，`current.json` 包含当前 release、Player build、
  候选 manifest hash 与 `previousReleaseId` 字段；仓库/真实生产 pointer 未被修改。
