using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{""type"": ""object"",
          ""properties"": {
            ""targetLandmark"": {
              ""type"": ""string"",
              ""enum"": [""warehouse"", ""gate""],
              ""description"": ""目标地标名称""
            }
          },
          ""required"": [""targetLandmark""]
        }");

    public override string Name => "game_npc_move";

    public override string Description => "使 NPC 前往指定地标 (warehouse|gate)";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override string ExecuteCore(NpcToolContext context, MoveArgs args)
    {
        return context.Npc.MoveToLandmark(args);
    }
}
