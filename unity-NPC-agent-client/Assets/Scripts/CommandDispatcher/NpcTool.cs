using System;
using Newtonsoft.Json.Linq;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NpcToolAttribute : Attribute
{
}

public sealed class NpcToolContext
{
    public NpcEntity Npc { get; }

    public NpcToolContext(NpcEntity npc)
    {
        Npc = npc ?? throw new ArgumentNullException(nameof(npc));
    }
}

public interface INpcTool
{
    string Name { get; }
    string Description { get; }
    JObject InputSchema { get; }
    bool IsAvailable(NpcToolContext context);

    ToolExecutionResult Execute(NpcToolContext context, string argumentsJson);
}

public abstract class NpcTool<TArgs> : INpcTool where TArgs : ToolArgsBase
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public JObject InputSchema => ToolContract<TArgs>.GetInputSchema();
    public virtual bool IsAvailable(NpcToolContext context) => context?.Npc != null;

    public ToolExecutionResult Execute(NpcToolContext context, string argumentsJson)
    {
        if (context == null)
            return ToolExecutionResult.Failure("INVALID_CONTEXT", "工具执行上下文不能为空。");

        var wrapper = new GameToolWrapper<TArgs>(args => ExecuteCore(context, args));
        return wrapper.Execute(argumentsJson);
    }

    protected abstract ToolExecutionResult ExecuteCore(NpcToolContext context, TArgs args);
}
