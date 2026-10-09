# 远端 NPC 内容契约 v1

冻结日期：2026-10-09。第一轮的可执行夹具位于 `NpcContent/npc/`。
不新增 WebSocket 方法，不改变已有 release、工具或存档业务字段的含义。

## 文件与字段

可信内容根下只有一个可变入口 `npc/index.json`；每版目录为
`npc/{npcId}/{contentVersion}/`，包含 `npc.json`、`avatar.png` 和用于构建校验的
`animation.dll.bytes`。角色资源和同字节的动画 DLL 由共享 Addressables Catalog
中的版本化 Address 交付。原始清单通过普通 HTTP 请求下载，不注册为模型工具。

ID/version 为 `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`，不允许点号、斜杠、反斜杠、百分号。
所有 SHA-256 均为原始文件字节的 64 位小写十六进制；JSON 不先规范化再求 hash。
JSON 必须为单一对象，不含重复或未知属性。清单/索引最大 256 KiB，头像最大
256 KiB，脚本 DLL 最大 16 MiB。发布过的目录不可覆盖；修订须增加 contentVersion
并分配新的程序集名。

`index.json` 的根字段：

| 字段 | 契约 |
|---|---|
| schemaVersion | 固定 1 |
| catalogContentVersion | 本轮沿用 release 的 contentVersion，即启动时加载内容版本 |
| texts | 下述全部文本键，普通文本渲染，未知键拒绝 |
| npcs | 数组，一个 npcId 仅一行，允许空数组 |

每行字段固定为 `npcId`、`contentVersion`、`displayName`、`description`、
`avatarPath`、`manifestPath`、`manifestSha256`、`minPlayerVersion`、`maxPlayerVersion`。
头像和清单路径必须分别为 `npc/{id}/{version}/avatar.png` 与
`npc/{id}/{version}/npc.json`。列表只提供展示信息和清单引用，不承担下载字节预算；
下载大小仍由 Addressables 实算。

texts 完整键集合为：

```text
remoteTab localTab download spawn cancel retry close refresh alreadySpawned
restartRequired progressFormat downloading installed remoteEmpty localEmpty
errorFetch errorContent errorCompatibility errorDownload errorCancelled errorSpawn errorBusy
```

每个值为非空文本，最多 2048 UTF-8 字节。`progressFormat` 必须包含
`{downloaded}` 与 `{total}`，由 UI 替换为字节数。错误类别映射：网络列表/清单失败
使用 errorFetch；hash/JSON/定义/DLL 校验失败使用 errorContent；Player/AOT 不兼容
使用 errorCompatibility；Catalog 不匹配使用 restartRequired；Bundle 下载失败使用
errorDownload；取消使用 errorCancelled；视觉或生成失败使用 errorSpawn；并发下载
使用 errorBusy。错误码仍由代码固定，文案来自列表。

`npc.json` 的根字段为：

| 字段 | 契约 |
|---|---|
| schemaVersion | 固定 1 |
| npcId / contentVersion | 与索引、绑定和路径完全一致 |
| playerBuildId | 基础 Player 精确身份；生产必须匹配包含动画 AOT 接口的新 Player |
| visual | prefabAddress、animatorControllerAddress、animationAddresses、materialAddresses、textureAddresses |
| animationScript | address、assemblyName、entryType、length、sha256 |
| profile | 现有 NPCProfile 全字段，npcId 与根相同，只含静态信息 |
| systemPrompt | schemaVersion=1、contentVersion 与 NPC 相同、locale=zh-CN、template |

所有 Address 位于 `npc/{id}/{version}/`；Prefab 正常引用 Controller、动画、材质和贴图。
视觉依赖由构建校验，不要求运行时逐张重复加载。DLL 显式入口是公共、非抽象、具有
公共无参构造函数并实现 `INpcAnimationDriver` 的类型。样例程序集分别为
`GameWithLLM.NpcAnimation.Merchant_001.V1` 与 `GameWithLLM.NpcAnimation.Guide_001.V1`。
动画包不注册工具，不要求每 NPC 补充 metadata；新增 AOT 能力必须发布新基础 Player。

## Runtime 与对话归档

SDK RuntimeManifest 的 NpcContents 可选，线上字段为：

```json
{"npcContents":[{"entityId":"merchant_001","contentVersion":"1","manifestSha256":"64位小写hash"}]}
```

原有 `entities/tools/instanceId/revision` 保留。绑定仅指向本 Manifest 的唯一实体。
省略或空数组表示沿用旧 Profile 配置。Unity 不能发送 Profile、Prompt 或任意 URL。
清单原文只在安装时保存到本地；LLM 仍由 Go 独占。

Go 创建 Context 独立读取可信根下同一文件。失败返回稳定 NPC_CONTENT 错误，失败
不缓存；已有 Context 保持创建时 Prompt。恢复前先核对存档的 npcContent 三字段，
冲突明确失败，保持当前 Context。snapshotVersion=1 不变，旧未绑定存档继续可读。
Prompt 与 system 消息不进入归档。

## 构建、验证与交接

显式调用 `NpcContentRelease.BuildSamplesFromCommandLine` 生成两份 DLL、PNG 与版本化
Unity 视觉资产；`BuildLocalContentFromCommandLine` 另外生成 LocalDevelopment Catalog
与 Bundle。源 JSON 与实际 DLL/hash 已随本轮交付；DLL 不由本地固定脚本替代。

```powershell
cd GameMCPServer
go run ./cmd/npc-content-validate ../NpcContent
```

此命令复用服务端严格解析器。Unity 的 `NpcContent` 验证模块核对 Address、依赖、
Player、DLL 引用、入口和 hash。生产候选包含静态文件台账；提升复用原脚本，拒绝
不可变版本被改写，最后切换列表。第一轮未向任何真实远端发布。

第二轮从这些字段接入 catalog client、installer、spawn controller 和 UI。
必须先构建包含新接口且具有新身份的基础 IL2CPP Player，再验证之后的 NPC 安装不需
重建 Player。本轮测试源沿用现有开发 release 的 0.1.0 标识，不能据此宣称已发布
0.1.0 Player 支持新接口；发布门禁仍必须验证新 AOT 底座与裁剪。
