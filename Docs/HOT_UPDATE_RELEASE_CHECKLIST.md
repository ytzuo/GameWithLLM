# 热更新发布检查清单

## 候选前

- [ ] releaseId、contentVersion、toolSetVersion、packageVersion 为新的不可变版本。
- [ ] Unity 为 6000.3.19f1，HybridCLR 为 8.14.1，目标为 Windows x86_64 IL2CPP。
- [ ] 工作树中没有真实密钥、token 或未归档的生产证据。
- [ ] `Assets/StreamingAssets/HotUpdate` 与 `Assets/StreamingAssets/SmokeTests` 不存在。
- [ ] 内容更新使用与目标 Player 匹配的已归档 `addressables_content_state.bin`。

## 自动门禁

- [ ] `ContentValidationRunner` Candidate/Release Profile 通过且 JSON 报告已保存。
- [ ] EditMode 全量测试通过。
- [ ] UI/Inventory 与 Remote Scene Packed Play smoke 分别通过。
- [ ] Tool Package Player smoke 输出 `TOOL_PACKAGE_SMOKE_SUCCESS`。
- [ ] 工具 Schema、ToolSet 原子性、Catalog、tombstone、DLL/AOT hash 全部通过。
- [ ] Build Layout 的 Bundle、体积、重复依赖和估算峰值内存未超预算。
- [ ] `Scripts/Test-ContentReleaseTransaction.ps1` 通过。

## 候选检查

- [ ] `candidate-manifest.json` 的 releaseId、类型、Player/Unity/Addressables 身份正确。
- [ ] manifest 中每个文件的相对路径、长度和 SHA-256 已复核。
- [ ] Production Player 不含本地 manifest、DLL、metadata、PDB 或 smoke plan。
- [ ] `tool-package-smoke.passed.json` 与 release manifest hash、releaseId、ToolSet 一致。
- [ ] Full 候选包含 Player；ContentUpdate 候选绑定正确 baseline content state。

## 受保护环境

- [ ] Full：fresh-install 通过；或 ContentUpdate：existing-install-upgrade 通过。
- [ ] 连续两个内容版本升级通过。
- [ ] offline、cache-hit、low-disk、interrupted-retry 全部通过。
- [ ] 普通对话、流式回复、warehouse/gate 移动、取消、Inventory、存档恢复通过。
- [ ] 坏 JSON、坏 DLL、缺失 Bundle 和超预算候选均未改变生产指针。
- [ ] Event Viewer/Profiler 无未解释异常或 Addressables handle 泄漏。
- [ ] `content-release-smoke.passed.json` 已生成且绑定候选 manifest SHA-256。

## 发布与回滚准备

- [ ] 当前和上一 release 均完整保留，回滚窗口与保留策略满足要求。
- [ ] 发布账号仅具备目标 publish root 的必要权限。
- [ ] `Publish-ContentRelease.ps1` 的 CandidateDirectory 与 PublishRoot 已人工复核。
- [ ] 变更窗口、监控人、回滚负责人和通知渠道已确认。

## 发布后

- [ ] `current.json` 最后更新，releaseId、previousReleaseId、manifest hash 正确。
- [ ] CDN/源站 Catalog 与版本化 Bundle/DLL 可访问且 hash 正确。
- [ ] 线上 fresh/upgrade、cache-hit 和核心游戏流程抽样通过。
- [ ] 构建日志、验证报告、两个 smoke 证据、候选 manifest 和 content state 已归档。
- [ ] 本地生成目录只在证据归档后清理；已发布 release 与历史文档未改写。
