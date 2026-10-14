# NPC 作者操作说明

NPC 技术名称是内容与存档身份，展示名称可独立修改。当前样例为 Merchant、Guide、Keeper。
新增 NPC 的流程只有：**名称目录 → 资源配置 → Definition → 构建发布 → 下载生成**。

## 创建资源

1. 在 `unity-NPC-agent-client/Assets/Content/Npcs/{name}` 建立唯一目录。
   技术名称匹配 `^[A-Za-z][A-Za-z0-9_]{0,63}$`，大小写不敏感唯一，不能使用 Windows 设备保留名。
2. 配置 `Visual.prefab`、`Avatar.png`，以及所需 Models、Materials、Animations。
   Prefab 只包含表现组件，不能包含 Entity、NavMeshAgent、Inventory 或工具等权威组件。
   所有依赖必须存在；共享 Shader 等引用现有共享目录。
3. 视觉根配置有效 Animator、Avatar 和 Controller。使用 builtin `standard-locomotion` 时，
   Controller 必须包含 `Speed(float)`、`Moving(bool)`、`Thinking(bool)`、`Speaking(bool)`。
   不依赖 root motion 移动实体。自定义动画 DLL 使用 hot-update 驱动字段并验证接口与字节完整性。
4. 用 Unity 菜单 `Create > GameWithLLM > NPC Content Definition` 创建 `Definition.asset`，
   设置 name、中文 displayName、介绍、Prefab、头像、驱动、静态 Profile 和 Prompt。
   Prompt 保留所有必需占位符，可从现有 Definition 的模板开始修改。
   Profile 不填写实时位置、库存或任务状态。移动已有资源时同时保留 `.meta` 和 GUID。
   从样例复制资源时使用 Unity 的 Duplicate，使新 NPC 获得新 GUID；不要原样复制样例 `.meta`。

## 构建与交付

- 运行 `GameWithLLM > Content > Build NPC Definitions`，或批处理
  `NpcContentRelease.BuildDefinitionsFromCommandLine`。构建器自动发现 Definition，
  生成 `NpcContent/npc/{name}/npc.json`、`avatar.png` 和索引，配置
  `npc/{name}/visual` Address 与 `content.npc.{name}` Label。不手改生成 JSON 或注册器。
- 按 [生产运维](HOT_UPDATE_OPERATIONS.md) 构建完整候选或匹配基线的内容更新候选，
  执行工具包、NPC Player 和目标环境 smoke，再由发布事务交付。
  NPC 没有版本号；整体 release、工具 ToolSet、DLL 校验和发布门禁继续按原流程维护。
- 发布先交付 Bundle 与当前清单，最后切换 NPC 索引。当前清单使用 HTTP 重新验证，
  hash Bundle 保持缓存策略。不要直接覆盖公网指针绕过发布事务。
- 新启动客户端后，在 NPC 资料库刷新、下载/安装，再生成。已有工具自动绑定；
  不修改 Registry、Dispatcher 或 Go 工具 Schema。

## 修改与恢复

直接修改同一名称目录中的当前 Prefab、动画、头像或 displayName，再构建、发布并重启客户端。
不创建旧模型/旧动画副本，不填写或升级 NPC 内容版本，不转换格式 3 的世界存档或格式 2 的对话快照。
这些存档只引用身份，恢复时使用当前资源和当前 Profile/Prompt。
已有活动 Context 保持创建时配置；新建和恢复 Context 读取当前配置。

Catalog 在启动阶段更新；运行中不替换已生成 NPC。新增 Address 尚不可解析时，
客户端提示“资源尚未就绪，请重启后重试”。失败不能用占位视觉冒充安装成功。
离线只能使用最后完整安装的清单及完整缓存；缓存被驱逐后需联网修复。

改变技术名称或删除 NPC 是身份变化，原名称的存档恢复会失败，不存在别名或自动迁移。
删除 Definition 后重新构建会清理专用生成目录中的对应清单、Address 和 Label；
用户存档不会自动删除。旧 NPC 清单、旧安装索引和旧格式存档不支持自动转换。

## 接入验证

检查作者构建、实际加载和驱动绑定；在 SampleScene 与 Windows IL2CPP Player 验证
下载生成、普通/流式对话、warehouse/gate 移动、取消、Inventory、存档恢复和 Go 重启重连。
修改模型/动画/displayName 后用修改前创建的新格式存档再恢复一次，并确认实际加载的新表现。
Console 不得出现编译错误、Missing Script、线程或协议异常。
