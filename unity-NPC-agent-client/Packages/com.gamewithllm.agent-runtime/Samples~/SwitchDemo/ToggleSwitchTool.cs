using System;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;

namespace GameWithLLM.AgentRuntime.Samples.SwitchDemo
{
    [AgentTool]
    public sealed class ToggleSwitchTool : IAgentTool
    {
        [Serializable]
        private sealed class Arguments { public bool enabled; }
        public AgentToolDescriptor Descriptor { get; } = new AgentToolDescriptor(
            "game_switch_set",
            "Sets an interactive switch on or off.",
            @"{""type"":""object"",""properties"":{""enabled"":{""type"":""boolean""}},""required"":[""enabled""],""additionalProperties"":false}");

        public bool IsAvailable(AgentToolContext context) =>
            context?.Entity is SwitchAgentEntity entity && entity.IsOnline;

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = (SwitchAgentEntity)context.Entity;
            Arguments arguments = UnityEngine.JsonUtility.FromJson<Arguments>(
                argumentsJson ?? "{}");
            entity.SetState(arguments.enabled);
            return new ValueTask<AgentToolResult>(
                AgentToolResult.Success("Switch state updated."));
        }
    }
}
using System;
