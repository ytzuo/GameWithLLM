using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using NUnit.Framework;
using UnityEngine;

public sealed class ToolPackRegistrationTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private GameObject _gameObject;
    private ToolsRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _gameObject = new GameObject("H3 ToolsRegistry Test");
        _registry = _gameObject.AddComponent<ToolsRegistry>();
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(_gameObject);
    }

    [Test]
    public void MultiToolPack_CommitsOnce_AndExactReloadIsIdempotent()
    {
        int changes = 0;
        _registry.ToolsChanged += () => changes++;
        var tools = new IAgentTool[]
        {
            new TestTool("h3_test_alpha"),
            new TestTool("h3_test_beta")
        };

        ToolPackRegistrationResult first =
            _registry.RegisterToolPack("h3-tests", "1.0.0", HashA, tools);
        ToolPackRegistrationResult second =
            _registry.RegisterToolPack("h3-tests", "1.0.0", HashA, tools);

        Assert.That(first.Registered, Is.True);
        Assert.That(second.Idempotent, Is.True);
        Assert.That(changes, Is.EqualTo(1));
        CollectionAssert.IsSubsetOf(
            new[] { "h3_test_alpha", "h3_test_beta" },
            _registry.GetRuntimeTools().Select(tool => tool.Name).ToArray());
    }

    [Test]
    public void InvalidTool_RejectsWholePack()
    {
        int before = _registry.GetRuntimeTools().Count;
        Assert.Throws<InvalidOperationException>(() => _registry.RegisterToolPack(
            "h3-invalid",
            "1.0.0",
            HashA,
            new IAgentTool[]
            {
                new TestTool("h3_never_committed"),
                new InvalidSchemaTool()
            }));

        Assert.That(_registry.GetRuntimeTools().Count, Is.EqualTo(before));
        Assert.That(
            _registry.GetRuntimeTools().Any(tool => tool.Name == "h3_never_committed"),
            Is.False);
    }

    [Test]
    public void ConflictingToolOrChangedPackageContent_IsRejectedWithoutMutation()
    {
        _registry.RegisterToolPack(
            "h3-original",
            "1.0.0",
            HashA,
            new[] { new TestTool("h3_unique") });
        int afterFirst = _registry.GetRuntimeTools().Count;

        Assert.Throws<InvalidOperationException>(() => _registry.RegisterToolPack(
            "h3-conflict",
            "1.0.0",
            HashB,
            new[] { new TestTool("h3_unique") }));
        Assert.Throws<InvalidOperationException>(() => _registry.RegisterToolPack(
            "h3-original",
            "1.0.0",
            HashB,
            new[] { new TestTool("h3_other") }));

        Assert.That(_registry.GetRuntimeTools().Count, Is.EqualTo(afterFirst));
    }

    [Test]
    public void SmokeAssembly_IsDiscoveredExplicitly()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(candidate =>
            candidate.GetName().Name == "GameWithLLM.Tools.Pack.SmokeTest");
        IReadOnlyList<IAgentTool> tools = AgentToolDiscovery.DiscoverFromAssembly(assembly);
        CollectionAssert.AreEquivalent(
            new[] { "game_hotfix_smoke_query", "game_hotfix_smoke_package_info" },
            tools.Select(tool => tool.Descriptor.Name).ToArray());
    }

    [Test]
    public async Task ExistingInvocation_CompletesWhileAnotherPackIsRegistered()
    {
        var runningTool = new BlockingTool();
        _registry.RegisterToolPack(
            "h3-running",
            "1.0.0",
            HashA,
            new[] { runningTool });
        Task<AgentToolResult> invocation = _registry.ExecuteAsync(
                runningTool.Descriptor.Name,
                new AgentToolContext(new TestEntity(), "running-invocation"),
                "{}",
                CancellationToken.None)
            .AsTask();

        Assert.That(runningTool.Started.Task.IsCompleted, Is.True);
        ToolPackRegistrationResult added = _registry.RegisterToolPack(
            "h3-during-call",
            "1.0.0",
            HashB,
            new[] { new TestTool("h3_added_during_call") });
        runningTool.Complete();

        Assert.That(added.Registered, Is.True);
        Assert.That((await invocation).Ok, Is.True);
    }

    private sealed class TestTool : IAgentTool
    {
        private readonly AgentToolDescriptor _descriptor;

        public TestTool(string name)
        {
            _descriptor = new AgentToolDescriptor(name, string.Empty, "{\"type\":\"object\"}");
        }

        public AgentToolDescriptor Descriptor => _descriptor;
        public bool IsAvailable(AgentToolContext context) => true;
        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken) =>
            new ValueTask<AgentToolResult>(AgentToolResult.Success());
    }

    private sealed class InvalidSchemaTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor =>
            new AgentToolDescriptor("h3_invalid", string.Empty, "[]");
        public bool IsAvailable(AgentToolContext context) => true;
        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken) =>
            new ValueTask<AgentToolResult>(AgentToolResult.Success());
    }

    private sealed class BlockingTool : IAgentTool
    {
        private readonly TaskCompletionSource<AgentToolResult> _completion =
            new TaskCompletionSource<AgentToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentToolDescriptor Descriptor { get; } =
            new AgentToolDescriptor("h3_running_tool", string.Empty, "{\"type\":\"object\"}");

        public bool IsAvailable(AgentToolContext context) => true;

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return new ValueTask<AgentToolResult>(_completion.Task);
        }

        public void Complete() => _completion.TrySetResult(AgentToolResult.Success());
    }

    private sealed class TestEntity : IAgentEntity
    {
        public string EntityId => "h3-test-entity";
        public bool IsOnline => true;
    }
}
