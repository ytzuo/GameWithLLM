using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameWithLLM.AgentRuntime
{
    public sealed class RuntimeCommand
    {
        public string InvocationId { get; }
        public string EntityId { get; }
        public string ToolName { get; }
        public string ArgumentsJson { get; }
        public RuntimeCommand(
            string invocationId,
            string entityId,
            string toolName,
            string argumentsJson)
        {
            InvocationId = invocationId;
            EntityId = entityId;
            ToolName = toolName;
            ArgumentsJson = argumentsJson;
        }
    }

    public interface IRuntimeTransport
    {
        Task StartAsync(RuntimeManifest manifest, CancellationToken cancellationToken);
        IAsyncEnumerable<RuntimeCommand> ReadCommandsAsync(CancellationToken cancellationToken);
        Task SendResultAsync(string invocationId, AgentToolResult result, CancellationToken cancellationToken);
        Task SendProgressAsync(string invocationId, double progress, string message, CancellationToken cancellationToken);
    }
}
