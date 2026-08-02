using System;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public abstract class NpcTool<TArgs> : IAgentTool where TArgs : ToolArgsBase
{
    private AgentToolDescriptor _descriptor;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public AgentToolDescriptor Descriptor =>
        _descriptor ??= new AgentToolDescriptor(
            Name,
            Description,
            ToolContract<TArgs>.GetInputSchema().ToString(Formatting.None));

    public virtual bool IsAvailable(AgentToolContext context) =>
        context?.Entity is NpcEntity;

    public ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!(context?.Entity is NpcEntity npc))
        {
            return new ValueTask<AgentToolResult>(
                AgentToolResult.Failure(
                    "INVALID_CONTEXT",
                    "该工具要求实现 IGameObjectAgentEntity 的 NPC 实体。"));
        }

        var wrapper = new GameToolWrapper<TArgs>(
            (args, token) => ExecuteCoreAsync(context, npc, args, token));
        return wrapper.ExecuteAsync(argumentsJson, cancellationToken);
    }

    protected virtual ValueTask<AgentToolResult> ExecuteCoreAsync(
        AgentToolContext context,
        NpcEntity npc,
        TArgs args,
        CancellationToken cancellationToken) =>
        new ValueTask<AgentToolResult>(ExecuteCore(context, npc, args));

    protected virtual AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        TArgs args) =>
        throw new NotSupportedException(
            $"工具 '{Name}' 必须实现同步或异步执行逻辑。");

    protected static AgentToolResult Success(JToken data, string message = null) =>
        AgentToolResult.Success(message, data?.ToString(Formatting.None));
}
