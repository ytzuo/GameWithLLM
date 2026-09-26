# Addressables A7 生产发布与回滚基线

## 已完成范围

- `AddressablesA7ReleasePipeline` 提供完整发布和内容更新两条 Production 候选路径。
  内容更新必须从归档的 Player `addressables_content_state.bin` 开始，并在构建前执行
  Content Update Restrictions。
- 生产门禁复用 A1-A6 和 H7 验证，并补充严格 JSON、Address/业务 ID 唯一性、必需
  Group/Label、AOT Player 身份、Resources/Build Settings 重复和 Addressable
  Prefab/Scene Missing Script 检查。
- `a7-release-policy.json` 固定 30 天窗口、至少两个保留版本，以及 Bundle 数、远端
  体积、补丁体积、最大 Bundle、估算峰值内存和重复隐式依赖预算。
- 候选清单归档 `addressables_content_state.bin`，并记录 Player、当前 release Bundle、
  Catalog 的相对路径、长度和 SHA-256。历史 release 不计入当前候选预算。
- `Promote-A7Release.ps1` 重新校验全部文件，并要求与候选 hash 绑定的 smoke 证据。
  Full 必须通过 fresh-install；ContentUpdate 必须通过 existing-install-upgrade；两者还
  必须通过 offline、cache-hit、low-disk 和 interrupted-retry。
- 提升先写新的不可变 release 目录，再原子替换 `current.json`。当前指针记录上一
  release；脚本不清理任何已发布版本，回滚使用同一验证与切指针过程。
- 客户端 catalog 更新不做 eager bundle cache 清理，以保留上一成功版本；各远端 Group
  继续使用 `ClearWhenSpaceIsNeededInCache`，只在 Unity Cache 空间压力下驱逐，缺失内容
  可从服务端保留窗口重新下载。

## 入口

Unity 菜单：

- `GameWithLLM/Hot Update/A7/Verify Production Gate`
- `GameWithLLM/Hot Update/A7/Build Full Release Candidate`
- `GameWithLLM/Hot Update/A7/Build Content Update Candidate`

批处理和发布：

- `AddressablesA7ReleasePipeline.VerifyProductionGateFromCommandLine`
- `AddressablesA7ReleasePipeline.BuildFullReleaseCandidateFromCommandLine`
- `AddressablesA7ReleasePipeline.BuildContentUpdateCandidateFromCommandLine`
- `Scripts/Build-A7Release.ps1`
- `Scripts/Promote-A7Release.ps1`
- `Scripts/Test-A7Promotion.ps1`
- `.github/workflows/addressables-a7.yml`
- `.github/workflows/addressables-a7-promote.yml`（受保护 `production-content` 环境）

## Smoke 证据格式

`a7-smoke.passed.json` 使用 `schemaVersion: 1`，包含 `releaseId`、候选清单 SHA-256、
`successMarker: A7_SMOKE_SUCCESS`，以及 `{name, passed}` 场景数组。证据由受保护环境的
真实 Windows Player 测试生成，仓库不伪造通过记录；没有证据的构建只能作为候选归档，
不能切换生产指针。

## 验证状态

- 新增 Editor 代码已用 Unity 生成的项目引用执行 C# 编译：0 error。
- 本机 Unity `6000.3.19f1` 批处理在编译前因没有有效 Editor entitlement 返回 198；
  完整 A7 Unity 门禁、Clean/Update Build、Player smoke 和 Event Viewer handle 检查须由
  带许可证的 `unity-6000.3.19f1` 自托管 runner 完成。
