using System;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[Serializable]
[Preserve]
public sealed class SmokeTestArgs : ToolArgsBase
{
    [ToolParameter(
        Description = "随查询原样返回的可选诊断文本",
        MaxLength = 64)]
    public string echo;

    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[AgentTool]
[Preserve]
public sealed class SmokeTestTool : NpcTool<SmokeTestArgs>
{
    public override string Name => "game_hotfix_smoke_query";

    public override string Description =>
        "查询本地 HybridCLR Smoke Tool Pack 是否已加载，并返回当前 NPC 标识；不会修改游戏或存档状态。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        SmokeTestArgs args)
    {
        return Success(
            JObject.FromObject(new
            {
                packageId = "smoke-test",
                packageVersion = "1.1.0",
                npcId = npc.npcId,
                echo = args.echo ?? string.Empty
            }),
            ClientTextCatalogs.Message(
                "tool.smoke.executed",
                "HybridCLR Smoke Tool Pack 已执行。"));
    }
}

[Serializable]
[Preserve]
public sealed class SmokePackageInfoArgs : ToolArgsBase
{
    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[AgentTool]
[Preserve]
public sealed class SmokePackageInfoTool : NpcTool<SmokePackageInfoArgs>
{
    public override string Name => "game_hotfix_smoke_package_info";

    public override string Description =>
        "查询 Smoke Tool Pack 的包标识与版本，用于验证多工具包的原子注册。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        SmokePackageInfoArgs args)
    {
        return Success(
            JObject.FromObject(new
            {
                packageId = "smoke-test",
                packageVersion = "1.1.0",
                toolCount = 2
            }),
            ClientTextCatalogs.Message(
                "tool.smoke.info_returned",
                "Smoke Tool Pack 信息已返回。"));
    }
}
