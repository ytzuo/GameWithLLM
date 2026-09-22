using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GameWithLLM.AgentRuntime;

public enum ToolCandidateSource
{
    Builtin,
    HotUpdate
}

[Serializable]
public sealed class ToolReleaseDeclaration
{
    public string name;
    public string toolIdentity;
    public string source;
    public string implementationVersion;
    public string contractVersion;
    public string packageId;
    public string packageVersion;
    public string assemblyName;
    public string assemblyHash;
    public string schemaHash;
}

[Serializable]
public sealed class RetiredToolDeclaration
{
    public string name;
    public string toolIdentity;
    public string implementationVersion;
    public string contractVersion;
    public string retiredInToolSetVersion;
}

[Serializable]
public sealed class ToolHistoryDeclaration
{
    public string name;
    public string toolIdentity;
    public string source;
    public string implementationVersion;
    public string contractVersion;
    public string schemaHash;
    public string assemblyHash;
    public string firstReleaseId;
    public string lastReleaseId;
    public string retiredInToolSetVersion;
}

public sealed class ToolCandidate
{
    public ToolReleaseDeclaration Declaration { get; }
    public IAgentTool Tool { get; }

    public ToolCandidate(ToolReleaseDeclaration declaration, IAgentTool tool)
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }
}

public sealed class ToolSetCandidate
{
    public string ReleaseId { get; }
    public string ToolSetVersion { get; }
    public string CatalogVersion { get; }
    public IReadOnlyList<ToolCandidate> ActiveTools { get; }
    public IReadOnlyList<RetiredToolDeclaration> RetiredTools { get; }
    public IReadOnlyList<ToolHistoryDeclaration> History { get; }

    public ToolSetCandidate(
        string releaseId,
        string toolSetVersion,
        string catalogVersion,
        IEnumerable<ToolCandidate> activeTools,
        IEnumerable<RetiredToolDeclaration> retiredTools = null,
        IEnumerable<ToolHistoryDeclaration> history = null)
    {
        ReleaseId = releaseId;
        ToolSetVersion = toolSetVersion;
        CatalogVersion = catalogVersion;
        ActiveTools = (activeTools ?? throw new ArgumentNullException(nameof(activeTools))).ToArray();
        RetiredTools = (retiredTools ?? Array.Empty<RetiredToolDeclaration>()).ToArray();
        History = (history ?? Array.Empty<ToolHistoryDeclaration>()).ToArray();
    }
}

public sealed class ToolSetSnapshot
{
    private readonly ReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ReadOnlyDictionary<string, AgentToolDescriptor> _descriptors;
    private readonly ReadOnlyDictionary<string, ToolReleaseDeclaration> _declarations;
    private readonly ReadOnlyDictionary<string, ToolHistoryDeclaration> _history;
    private readonly HashSet<string> _retiredNames;

    public string ReleaseId { get; }
    public string ToolSetVersion { get; }
    public string CatalogVersion { get; }
    public string Fingerprint { get; }
    public IReadOnlyDictionary<string, IAgentTool> Tools => _tools;
    public IReadOnlyDictionary<string, AgentToolDescriptor> Descriptors => _descriptors;
    public IReadOnlyDictionary<string, ToolReleaseDeclaration> Declarations =>
        new ReadOnlyDictionary<string, ToolReleaseDeclaration>(
            _declarations.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));
    public IReadOnlyDictionary<string, ToolHistoryDeclaration> History =>
        new ReadOnlyDictionary<string, ToolHistoryDeclaration>(
            _history.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));

    internal ToolSetSnapshot(
        string releaseId,
        string toolSetVersion,
        string catalogVersion,
        string fingerprint,
        IDictionary<string, IAgentTool> tools,
        IDictionary<string, AgentToolDescriptor> descriptors,
        IDictionary<string, ToolReleaseDeclaration> declarations,
        IDictionary<string, ToolHistoryDeclaration> history,
        IEnumerable<string> retiredNames)
    {
        ReleaseId = releaseId;
        ToolSetVersion = toolSetVersion;
        CatalogVersion = catalogVersion;
        Fingerprint = fingerprint;
        _tools = new ReadOnlyDictionary<string, IAgentTool>(
            new Dictionary<string, IAgentTool>(tools, StringComparer.Ordinal));
        _descriptors = new ReadOnlyDictionary<string, AgentToolDescriptor>(
            new Dictionary<string, AgentToolDescriptor>(descriptors, StringComparer.Ordinal));
        _declarations = new ReadOnlyDictionary<string, ToolReleaseDeclaration>(
            declarations.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));
        _history = new ReadOnlyDictionary<string, ToolHistoryDeclaration>(
            history.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));
        _retiredNames = new HashSet<string>(retiredNames, StringComparer.Ordinal);
    }

    public bool IsRetired(string toolName) =>
        !string.IsNullOrEmpty(toolName) && _retiredNames.Contains(toolName);

    private static ToolReleaseDeclaration Clone(ToolReleaseDeclaration value) =>
        new ToolReleaseDeclaration
        {
            name = value.name,
            toolIdentity = value.toolIdentity,
            source = value.source,
            implementationVersion = value.implementationVersion,
            contractVersion = value.contractVersion,
            packageId = value.packageId,
            packageVersion = value.packageVersion,
            assemblyName = value.assemblyName,
            assemblyHash = value.assemblyHash,
            schemaHash = value.schemaHash
        };

    private static ToolHistoryDeclaration Clone(ToolHistoryDeclaration value) =>
        new ToolHistoryDeclaration
        {
            name = value.name,
            toolIdentity = value.toolIdentity,
            source = value.source,
            implementationVersion = value.implementationVersion,
            contractVersion = value.contractVersion,
            schemaHash = value.schemaHash,
            assemblyHash = value.assemblyHash,
            firstReleaseId = value.firstReleaseId,
            lastReleaseId = value.lastReleaseId,
            retiredInToolSetVersion = value.retiredInToolSetVersion
        };
}

public sealed class PreparedToolSet
{
    internal ToolSetSnapshot Snapshot { get; }
    internal string BaseFingerprint { get; }

    internal PreparedToolSet(ToolSetSnapshot snapshot, string baseFingerprint)
    {
        Snapshot = snapshot;
        BaseFingerprint = baseFingerprint;
    }
}

public sealed class ToolSetActivationResult
{
    public bool Activated { get; }
    public bool Idempotent { get; }
    public int ToolCount { get; }
    public string ReleaseId { get; }
    public string ToolSetVersion { get; }

    internal ToolSetActivationResult(
        bool activated,
        bool idempotent,
        ToolSetSnapshot snapshot)
    {
        Activated = activated;
        Idempotent = idempotent;
        ToolCount = snapshot.Tools.Count;
        ReleaseId = snapshot.ReleaseId;
        ToolSetVersion = snapshot.ToolSetVersion;
    }
}
