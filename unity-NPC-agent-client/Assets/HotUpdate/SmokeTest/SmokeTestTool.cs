using System;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[Serializable]
[Preserve]
public sealed class SmokeTestArgs : ToolArgsBase
{
    [ToolParameter(MaxLength = 64)]
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

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        SmokeTestArgs args)
    {
        return Success(
            JObject.FromObject(new
            {
                packageId = "smoke-test",
                packageVersion = "1.4.0",
                npcId = npc.npcId,
                echo = args.echo ?? string.Empty
            }),
            ClientTextCatalogs.Message("tool.smoke.executed"));
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

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        SmokePackageInfoArgs args)
    {
        return Success(
            JObject.FromObject(new
            {
                packageId = "smoke-test",
                packageVersion = "1.4.0",
                toolCount = 2
            }),
            ClientTextCatalogs.Message("tool.smoke.info_returned"));
    }
}
