# 热更新运维手册

> 当前事实源：`ARCHITECTURE.md`  
> 适用客户端：Unity 6000.3.19f1、Windows x86_64 IL2CPP、HybridCLR 8.14.1

## 1. 长期入口

生产候选只有一条构建链：

```text
Scripts/Build-ContentRelease.ps1
→ ContentSmokeRunner（LocalDevelopment 工具包 Player smoke）
→ ContentReleasePipeline（Production Addressables / Player）
```

发布和回滚只使用 `Scripts/Publish-ContentRelease.ps1`。编辑器的长期入口位于
`GameWithLLM/Content`；`Project Setup/Repair Configuration` 仅用于显式迁移或修复，
不会被验证器和生产 Pipeline 隐式调用。

## 2. 本地验证

在仓库根目录执行发布事务测试：

```powershell
./Scripts/Test-ContentReleaseTransaction.ps1
```

Unity 静态门禁、EditMode 和 Packed Play smoke 与
`.github/workflows/content-validation.yml` 保持一致。批处理入口为：

```text
ContentValidationRunner.ValidateFromCommandLine
ContentSmokeRunner.RunFromCommandLine
```

验证报告写入 `Artifacts/Validation`，日志写入客户端 `Logs`。报告和日志不得包含玩家
正文、模型全文、完整工具参数、Prompt、历史、密钥或 token。

## 3. 构建候选

完整候选：

```powershell
./Scripts/Build-ContentRelease.ps1 `
  -UnityExe '<Unity.exe>' `
  -Mode Full `
  -LicensingIpc '<Hub 提供的 licensing channel>'
```

内容更新候选还必须提供与目标 Player 完全匹配的已归档 content state：

```powershell
./Scripts/Build-ContentRelease.ps1 `
  -UnityExe '<Unity.exe>' `
  -Mode ContentUpdate `
  -BaselineContentState '<archived addressables_content_state.bin>' `
  -LicensingIpc '<Hub 提供的 licensing channel>'
```

输出位于 `Artifacts/Content/<releaseId>`、客户端 `Builds/ContentReleaseProduction` 和
`ServerData/production/StandaloneWindows64`。工具包 smoke plan 的源文件位于
`Assets/Editor/HotUpdate/Tests/Data`；本地 smoke 构建会临时复制到 StreamingAssets，
并在成功或失败后清除。Production 构建发现本地热更新或 smoke staging 会立即失败。

## 4. 环境 smoke 证据

候选构建不会自行生成生产环境证据。受保护环境必须针对候选执行：

- Full：fresh-install、offline、cache-hit、low-disk、interrupted-retry。
- ContentUpdate：existing-install-upgrade、offline、cache-hit、low-disk、interrupted-retry。
- Windows Player：普通对话、流式回复、移动、取消、Inventory、存档恢复。
- Event Viewer/Profiler：无未解释异常和 Addressables handle 泄漏。

通过后在候选目录写入 `content-release-smoke.passed.json`。其 `releaseId`、候选 manifest
SHA-256、`CONTENT_RELEASE_SMOKE_SUCCESS` 标记和场景结果必须与候选一致。

## 5. 发布与回滚

发布：

```powershell
./Scripts/Publish-ContentRelease.ps1 `
  -CandidateDirectory '<Artifacts/Content/releaseId>' `
  -PublishRoot '<publish-root>'
```

脚本重新校验候选、工具包证据及全部文件 hash/length，先写不可变 release 目录，最后
原子替换 `current.json`。同一 releaseId 的内容不可覆盖。

回滚只允许指向发布根目录中仍保留的 release：

```powershell
./Scripts/Publish-ContentRelease.ps1 `
  -CandidateDirectory '<publish-root>/releases/<releaseId>' `
  -PublishRoot '<publish-root>' `
  -Rollback
```

回滚后核对 `current.json.releaseId`、`previousReleaseId` 与 `rollback: true`，再执行缓存命中
和离线启动 smoke。当前版、上一版及策略要求的回滚窗口内版本不得清理。

## 6. 故障处理

- 候选验证失败：不发布、不补写证据；修复源内容并使用新的不可变版本重新构建。
- hash/length 不符：视为候选被篡改或复制不完整，废弃候选，不修改生产指针。
- Player smoke 失败：保留日志和候选用于诊断，禁止手工创建 passed 证据。
- Catalog/候选加载失败：客户端保持上一完整快照或仅运行 AOT BuiltinTools；不得恢复
  本地 DLL fallback。
- 发布中断：重新运行相同候选；事务脚本保证完整性检查和 pointer-last 语义。
- 紧急回滚：只选择已保留且证据完整的 release，执行回滚命令并重新验证。

## 7. 本地清理

确认日志、报告和候选证据已上传或归档后，可删除客户端 `Builds`、`HybridCLRData`、
`ServerData`、`Logs`、`Temp` 以及仓库 `Artifacts`。不得删除 `Assets/Content/HotUpdate`
中仍由 Git 跟踪并维持 GUID/Addressable 引用的生成文件，也不得改写已发布 release 或
`Docs/History/HotUpdate`。
