using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameWithLLM.AgentRuntime
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AgentToolAttribute : Attribute
    {
    }

    public interface IAgentEntity
    {
        string EntityId { get; }
        bool IsOnline { get; }
    }

    public interface IGameObjectAgentEntity : IAgentEntity
    {
        GameObject GameObject { get; }
    }

    public sealed class AgentToolDescriptor
    {
        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }
        public bool Interruptible { get; }
        public TimeSpan? SuggestedTimeout { get; }

        public AgentToolDescriptor(
            string name,
            string description,
            string inputSchemaJson,
            bool interruptible = true,
            TimeSpan? suggestedTimeout = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? string.Empty;
            InputSchemaJson = inputSchemaJson ?? throw new ArgumentNullException(nameof(inputSchemaJson));
            Interruptible = interruptible;
            SuggestedTimeout = suggestedTimeout;
        }
    }

    public sealed class AgentToolContext
    {
        public IAgentEntity Entity { get; }
        public string InvocationId { get; }
        public Action<double, string> ReportProgress { get; }

        public AgentToolContext(
            IAgentEntity entity,
            string invocationId,
            Action<double, string> reportProgress = null)
        {
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
            InvocationId = invocationId ?? throw new ArgumentNullException(nameof(invocationId));
            ReportProgress = reportProgress;
        }
    }

    public sealed class AgentToolResult
    {
        public bool Ok { get; }
        public string ErrorCode { get; }
        public string Message { get; }
        public string DataJson { get; }

        private AgentToolResult(bool ok, string errorCode, string message, string dataJson)
        {
            Ok = ok;
            ErrorCode = errorCode;
            Message = message;
            DataJson = dataJson;
        }

        public static AgentToolResult Success(string message = null, string dataJson = null) =>
            new AgentToolResult(true, null, message, dataJson);

        public static AgentToolResult Failure(string errorCode, string message, string dataJson = null) =>
            new AgentToolResult(false, errorCode, message, dataJson);
    }

    public interface IAgentTool
    {
        AgentToolDescriptor Descriptor { get; }

        /// <summary>按调用时刻的实体与世界状态判断工具是否可执行。</summary>
        bool IsAvailable(AgentToolContext context);

        /// <summary>执行工具并以 AgentToolResult 表达游戏业务成功或失败。</summary>
        ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken);
    }

    public interface IAgentMainThreadScheduler
    {
        bool IsMainThread { get; }
        ValueTask SwitchToMainThreadAsync(CancellationToken cancellationToken);
    }

    public sealed class NpcContentBinding
    {
        public string EntityId { get; }
        public string ContentVersion { get; }
        public string ManifestSha256 { get; }
        public NpcContentBinding(string entityId, string contentVersion, string manifestSha256)
        {
            const string segment = @"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$";
            if (!System.Text.RegularExpressions.Regex.IsMatch(entityId ?? string.Empty, segment) ||
                !System.Text.RegularExpressions.Regex.IsMatch(contentVersion ?? string.Empty, segment) ||
                !System.Text.RegularExpressions.Regex.IsMatch(manifestSha256 ?? string.Empty, @"^[0-9a-f]{64}$"))
                throw new ArgumentException("Invalid NPC content binding.");
            EntityId = entityId; ContentVersion = contentVersion; ManifestSha256 = manifestSha256;
        }
    }

    public sealed class RuntimeManifest
    {
        public string InstanceId { get; }
        public IReadOnlyList<string> EntityIds { get; }
        public IReadOnlyList<AgentToolDescriptor> Tools { get; }
        public long Revision { get; }
        public IReadOnlyList<NpcContentBinding> NpcContents { get; }

        public RuntimeManifest(
            string instanceId,
            IReadOnlyList<string> entityIds,
            IReadOnlyList<AgentToolDescriptor> tools,
            long revision,
            IReadOnlyList<NpcContentBinding> npcContents = null)
        {
            InstanceId = instanceId;
            EntityIds = entityIds;
            Tools = tools;
            Revision = revision;
            var bindings = new List<NpcContentBinding>(npcContents ?? Array.Empty<NpcContentBinding>());
            var entities = new HashSet<string>(entityIds);
            var bound = new HashSet<string>();
            foreach (NpcContentBinding binding in bindings)
                if (binding == null || !entities.Contains(binding.EntityId) || !bound.Add(binding.EntityId))
                    throw new ArgumentException("NPC binding must identify a unique manifest entity.");
            NpcContents = bindings.AsReadOnly();
        }
    }
}
