using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using NUnit.Framework;
using UnityEngine;

public sealed class ToolSetActivationTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private GameObject _gameObject;
    private ToolsRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _gameObject = new GameObject("H4 ToolsRegistry Test");
        _registry = _gameObject.AddComponent<ToolsRegistry>();
    }

    [TearDown]
    public void TearDown() => UnityEngine.Object.DestroyImmediate(_gameObject);

    [Test]
    public async Task Upgrade_AtomicallyAddsModifiesAndRetiresTools()
    {
        ToolSetCandidate n = CandidateFromCurrent(
            "release-n",
            "1.0.0",
            new[]
            {
                HotTool("h4_modified", "1.0.0", "1.0.0", HashA, "{}"),
                HotTool("h4_deleted", "1.0.0", "1.0.0", HashA, "{}")
            });
        _registry.ActivateToolSet(_registry.PrepareToolSet(n));

        int changes = 0;
        _registry.ToolsChanged += () => changes++;
        ToolSetCandidate next = CandidateFromCurrent(
            "release-n-plus-1",
            "2.0.0",
            new[]
            {
                HotTool("h4_modified", "2.0.0", "2.0.0", HashB, "{\"value\":{\"type\":\"string\"}}"),
                HotTool("h4_added", "1.0.0", "1.0.0", HashB, "{}")
            },
            new[] { "h4_deleted" });

        ToolSetActivationResult result = _registry.ActivateToolSet(_registry.PrepareToolSet(next));

        Assert.That(result.Activated, Is.True);
        Assert.That(changes, Is.EqualTo(1));
        string[] names = _registry.GetRuntimeTools().Select(tool => tool.Name).ToArray();
        Assert.That(names, Does.Contain("h4_added"));
        Assert.That(names, Does.Contain("h4_modified"));
        Assert.That(names, Does.Not.Contain("h4_deleted"));
        AgentToolResult retired = await _registry.ExecuteAsync(
            "h4_deleted",
            new AgentToolContext(new TestEntity(), "retired"),
            "{}",
            CancellationToken.None);
        Assert.That(retired.ErrorCode, Is.EqualTo("TOOL_RETIRED"));
    }

    [Test]
    public void ChangedImplementationOrSchema_RequiresCorrespondingVersionIncrease()
    {
        _registry.ActivateToolSet(_registry.PrepareToolSet(CandidateFromCurrent(
            "release-n",
            "1.0.0",
            new[] { HotTool("h4_versioned", "1.0.0", "1.0.0", HashA, "{}") })));

        Assert.Throws<InvalidOperationException>(() => _registry.PrepareToolSet(CandidateFromCurrent(
            "bad-implementation",
            "2.0.0",
            new[] { HotTool("h4_versioned", "1.0.0", "1.0.0", HashB, "{}") })));
        Assert.Throws<InvalidOperationException>(() => _registry.PrepareToolSet(CandidateFromCurrent(
            "bad-schema",
            "2.0.0",
            new[] { HotTool("h4_versioned", "2.0.0", "1.0.0", HashB, "{\"value\":{\"type\":\"string\"}}") })));
    }

    [Test]
    public void InvalidCandidateAndStalePreparedSet_DoNotMutateSnapshot()
    {
        string before = _registry.ActiveSnapshot.Fingerprint;
        Assert.Throws<InvalidOperationException>(() => _registry.PrepareToolSet(CandidateFromCurrent(
            "invalid",
            "1.0.0",
            new[] { HotTool("H4_Invalid", "1.0.0", "1.0.0", HashA, "{}") })));
        Assert.That(_registry.ActiveSnapshot.Fingerprint, Is.EqualTo(before));

        PreparedToolSet stale = _registry.PrepareToolSet(CandidateFromCurrent(
            "stale",
            "1.0.0",
            new[] { HotTool("h4_stale", "1.0.0", "1.0.0", HashA, "{}") }));
        PreparedToolSet winner = _registry.PrepareToolSet(CandidateFromCurrent(
            "winner",
            "1.0.0",
            new[] { HotTool("h4_winner", "1.0.0", "1.0.0", HashB, "{}") }));
        _registry.ActivateToolSet(winner);
        Assert.Throws<InvalidOperationException>(() => _registry.ActivateToolSet(stale));
        Assert.That(_registry.GetRuntimeTools().Any(tool => tool.Name == "h4_stale"), Is.False);
    }

    [Test]
    public void ExactCandidateActivation_IsIdempotent()
    {
        ToolSetCandidate candidate = CandidateFromCurrent(
            "idempotent",
            "1.0.0",
            new[] { HotTool("h4_idempotent", "1.0.0", "1.0.0", HashA, "{}") });
        int changes = 0;
        _registry.ToolsChanged += () => changes++;
        _registry.ActivateToolSet(_registry.PrepareToolSet(candidate));
        ToolSetActivationResult second = _registry.ActivateToolSet(_registry.PrepareToolSet(candidate));
        Assert.That(second.Idempotent, Is.True);
        Assert.That(changes, Is.EqualTo(1));
    }

    [Test]
    public void PreparedSnapshot_IsIsolatedFromMutableManifestDtos()
    {
        ToolCandidate added = HotTool("h4_immutable", "1.0.0", "1.0.0", HashA, "{}");
        ToolSetCandidate candidate = CandidateFromCurrent(
            "immutable",
            "1.0.0",
            new[] { added });
        PreparedToolSet prepared = _registry.PrepareToolSet(candidate);
        added.Declaration.name = "h4_mutated_after_prepare";

        _registry.ActivateToolSet(prepared);
        Assert.That(_registry.ActiveSnapshot.Tools.ContainsKey("h4_immutable"), Is.True);
        Assert.That(_registry.ActiveSnapshot.Tools.ContainsKey("h4_mutated_after_prepare"), Is.False);
        ToolReleaseDeclaration exposed = _registry.ActiveSnapshot.Declarations["h4_immutable"];
        exposed.toolIdentity = "mutated-copy";
        Assert.That(
            _registry.ActiveSnapshot.Declarations["h4_immutable"].toolIdentity,
            Is.EqualTo("h4_immutable"));
    }

    [Test]
    public async Task InvocationCapturedBeforeActivation_CompletesWithOldInstance()
    {
        var blocking = new BlockingTool("h4_running");
        _registry.ActivateToolSet(_registry.PrepareToolSet(CandidateFromCurrent(
            "running",
            "1.0.0",
            new[] { HotTool(blocking, "1.0.0", "1.0.0", HashA) })));
        Task<AgentToolResult> invocation = _registry.ExecuteAsync(
            "h4_running",
            new AgentToolContext(new TestEntity(), "running"),
            "{}",
            CancellationToken.None).AsTask();
        Assert.That(blocking.Started.Task.IsCompleted, Is.True);

        _registry.ActivateToolSet(_registry.PrepareToolSet(CandidateFromCurrent(
            "running-retired",
            "2.0.0",
            Array.Empty<ToolCandidate>(),
            new[] { "h4_running" })));
        blocking.Complete();
        Assert.That((await invocation).Ok, Is.True);
    }

    [Test]
    public void RetiredName_CannotBeReusedForAnotherIdentity()
    {
        _registry.ActivateToolSet(_registry.PrepareToolSet(CandidateFromCurrent(
            "identity-n",
            "1.0.0",
            new[] { HotTool("h4_identity", "1.0.0", "1.0.0", HashA, "{}") })));
        _registry.ActivateToolSet(_registry.PrepareToolSet(CandidateFromCurrent(
            "identity-retired",
            "2.0.0",
            Array.Empty<ToolCandidate>(),
            new[] { "h4_identity" })));
        ToolCandidate reused = HotTool("h4_identity", "2.0.0", "1.0.0", HashB, "{}");
        reused.Declaration.toolIdentity = "different-semantic-identity";

        Assert.Throws<InvalidOperationException>(() => _registry.PrepareToolSet(CandidateFromCurrent(
            "identity-reused",
            "3.0.0",
            new[] { reused })));
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

    private ToolSetCandidate CandidateFromCurrent(
        string releaseId,
        string toolSetVersion,
        IEnumerable<ToolCandidate> replacements,
        IEnumerable<string> retire = null)
    {
        var retiring = new HashSet<string>(retire ?? Array.Empty<string>(), StringComparer.Ordinal);
        var replacementList = replacements.ToList();
        var replacementNames = new HashSet<string>(
            replacementList.Select(item => item.Declaration.name),
            StringComparer.Ordinal);
        var active = _registry.ActiveSnapshot.Tools
            .Where(pair => !retiring.Contains(pair.Key) && !replacementNames.Contains(pair.Key))
            .Select(pair => new ToolCandidate(_registry.ActiveSnapshot.Declarations[pair.Key], pair.Value))
            .Concat(replacementList)
            .ToList();
        var tombstones = retiring.Select(name =>
        {
            ToolReleaseDeclaration old = _registry.ActiveSnapshot.Declarations[name];
            return new RetiredToolDeclaration
            {
                name = name,
                toolIdentity = old.toolIdentity,
                implementationVersion = old.implementationVersion,
                contractVersion = old.contractVersion,
                retiredInToolSetVersion = toolSetVersion
            };
        }).Concat(_registry.ActiveSnapshot.History.Values
            .Where(record => _registry.ActiveSnapshot.IsRetired(record.name) &&
                             !replacementNames.Contains(record.name) &&
                             !retiring.Contains(record.name))
            .Select(record => new RetiredToolDeclaration
            {
                name = record.name,
                toolIdentity = record.toolIdentity,
                implementationVersion = record.implementationVersion,
                contractVersion = record.contractVersion,
                retiredInToolSetVersion = record.retiredInToolSetVersion
            })).ToArray();
        var history = active.Select(candidate =>
            {
                ToolHistoryDeclaration item = ToolSetCandidateFactory.ToHistory(candidate.Declaration, releaseId);
                if (_registry.ActiveSnapshot.History.TryGetValue(
                        candidate.Declaration.name,
                        out ToolHistoryDeclaration previous))
                    item.firstReleaseId = previous.firstReleaseId;
                return item;
            })
            .Concat(tombstones.Select(tombstone =>
            {
                if (_registry.ActiveSnapshot.Declarations.TryGetValue(
                        tombstone.name,
                        out ToolReleaseDeclaration old))
                {
                    ToolHistoryDeclaration item = ToolSetCandidateFactory.ToHistory(
                        old,
                        releaseId,
                        tombstone.retiredInToolSetVersion);
                    if (_registry.ActiveSnapshot.History.TryGetValue(
                            tombstone.name,
                            out ToolHistoryDeclaration existing))
                        item.firstReleaseId = existing.firstReleaseId;
                    return item;
                }
                ToolHistoryDeclaration previous = _registry.ActiveSnapshot.History[tombstone.name];
                return new ToolHistoryDeclaration
                {
                    name = previous.name,
                    toolIdentity = previous.toolIdentity,
                    source = previous.source,
                    implementationVersion = previous.implementationVersion,
                    contractVersion = previous.contractVersion,
                    schemaHash = previous.schemaHash,
                    assemblyHash = previous.assemblyHash,
                    firstReleaseId = previous.firstReleaseId,
                    lastReleaseId = releaseId,
                    retiredInToolSetVersion = previous.retiredInToolSetVersion
                };
            }))
            .ToArray();
        return new ToolSetCandidate(
            releaseId,
            toolSetVersion,
            "embedded-1",
            active,
            tombstones,
            history);
    }

    private static ToolCandidate HotTool(
        string name,
        string implementationVersion,
        string contractVersion,
        string hash,
        string propertiesJson) =>
        HotTool(new TestTool(name, propertiesJson), implementationVersion, contractVersion, hash);

    private static ToolCandidate HotTool(
        IAgentTool tool,
        string implementationVersion,
        string contractVersion,
        string hash)
    {
        string schema = "{\"type\":\"object\",\"properties\":" +
                        ((TestTool)tool).PropertiesJson + "}";
        return new ToolCandidate(
            new ToolReleaseDeclaration
            {
                name = tool.Descriptor.Name,
                toolIdentity = tool.Descriptor.Name,
                source = "hot-update",
                implementationVersion = implementationVersion,
                contractVersion = contractVersion,
                packageId = "h4-tests",
                packageVersion = implementationVersion,
                assemblyName = tool.GetType().Assembly.GetName().Name,
                assemblyHash = hash,
                schemaHash = ToolSetValidator.ComputeSchemaHash(schema)
            },
            tool);
    }

    private class TestTool : IAgentTool
    {
        public string PropertiesJson { get; }
        public AgentToolDescriptor Descriptor { get; }

        public TestTool(string name, string propertiesJson)
        {
            PropertiesJson = propertiesJson;
            Descriptor = new AgentToolDescriptor(
                name,
                "test",
                "{\"type\":\"object\",\"properties\":" + propertiesJson + "}");
        }

        public bool IsAvailable(AgentToolContext context) => true;
        public virtual ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            string argumentsJson,
            CancellationToken cancellationToken) =>
            new ValueTask<AgentToolResult>(AgentToolResult.Success());
    }

    private sealed class BlockingTool : TestTool
    {
        private readonly TaskCompletionSource<AgentToolResult> _completion =
            new TaskCompletionSource<AgentToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTool(string name) : base(name, "{}") { }

        public override ValueTask<AgentToolResult> ExecuteAsync(
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
        public string EntityId => "h4-test-entity";
        public bool IsOnline => true;
    }
}
