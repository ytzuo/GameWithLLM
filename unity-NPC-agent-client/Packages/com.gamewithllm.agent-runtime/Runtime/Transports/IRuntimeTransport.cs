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
        public CancellationToken CancellationToken { get; }

        public RuntimeCommand(
            string invocationId,
            string entityId,
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            InvocationId = invocationId ?? throw new System.ArgumentNullException(nameof(invocationId));
            EntityId = entityId ?? throw new System.ArgumentNullException(nameof(entityId));
            ToolName = toolName ?? throw new System.ArgumentNullException(nameof(toolName));
            ArgumentsJson = argumentsJson ?? "{}";
            CancellationToken = cancellationToken;
        }
    }

    public interface IRuntimeTransport
    {
        /// <summary>启动长期连接，并用完整 Manifest 完成首次注册。</summary>
        Task StartAsync(RuntimeManifest manifest, CancellationToken cancellationToken);

        /// <summary>发布替换旧能力快照的完整 Manifest。</summary>
        Task UpdateManifestAsync(RuntimeManifest manifest, CancellationToken cancellationToken);

        /// <summary>持续读取需要由 Unity 主线程调度的 Runtime 命令。</summary>
        IAsyncEnumerable<RuntimeCommand> ReadCommandsAsync(CancellationToken cancellationToken);

        /// <summary>为一次调用发送唯一的最终业务结果。</summary>
        Task SendResultAsync(string invocationId, AgentToolResult result, CancellationToken cancellationToken);

        /// <summary>发送不改变调用完成状态的在途进度。</summary>
        Task SendProgressAsync(string invocationId, double progress, string message, CancellationToken cancellationToken);
    }
}
