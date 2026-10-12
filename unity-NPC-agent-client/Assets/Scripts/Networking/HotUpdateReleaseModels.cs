using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameWithLLM.AgentRuntime;

public sealed class LoadedToolPack
{
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string AssemblyHash { get; }
    public Assembly Assembly { get; }
    public IReadOnlyList<IAgentTool> DiscoveredTools { get; }

    public LoadedToolPack(
        string packageId,
        string packageVersion,
        string assemblyHash,
        Assembly assembly,
        IReadOnlyList<IAgentTool> discoveredTools = null)
    {
        PackageId = packageId;
        PackageVersion = packageVersion;
        AssemblyHash = assemblyHash;
        Assembly = assembly;
        DiscoveredTools = discoveredTools ?? AgentToolDiscovery.DiscoverFromAssembly(assembly);
    }
}

public sealed class LoadedToolSetRelease
{
    public string ReleaseId { get; }
    public string ToolSetVersion { get; }
    public string CatalogVersion { get; }
    public IReadOnlyList<ToolReleaseDeclaration> ActiveTools { get; }
    public IReadOnlyList<RetiredToolDeclaration> RetiredTools { get; }
    public IReadOnlyList<ToolHistoryDeclaration> History { get; }
    public IReadOnlyList<LoadedToolPack> ToolPacks { get; }
    public string ToolMetadataJson { get; }
    public string AgentMessagesJson { get; }
    public string UiJson { get; }

    public LoadedToolSetRelease(
        string releaseId,
        string toolSetVersion,
        string catalogVersion,
        IReadOnlyList<ToolReleaseDeclaration> activeTools,
        IReadOnlyList<RetiredToolDeclaration> retiredTools,
        IReadOnlyList<ToolHistoryDeclaration> history,
        IReadOnlyList<LoadedToolPack> toolPacks,
        string toolMetadataJson,
        string agentMessagesJson,
        string uiJson)
    {
        ReleaseId = releaseId;
        ToolSetVersion = toolSetVersion;
        CatalogVersion = catalogVersion;
        ActiveTools = activeTools;
        RetiredTools = retiredTools;
        History = history;
        ToolPacks = toolPacks;
        ToolMetadataJson = toolMetadataJson;
        AgentMessagesJson = agentMessagesJson;
        UiJson = uiJson;
    }

    public ToolSetCandidate BuildCandidate(IReadOnlyList<IAgentTool> builtinTools)
    {
        var byAssemblyAndName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (IAgentTool tool in builtinTools ?? Array.Empty<IAgentTool>())
            AddResolvedTool(byAssemblyAndName, tool);
        foreach (LoadedToolPack package in ToolPacks)
        {
            foreach (IAgentTool tool in package.DiscoveredTools)
                AddResolvedTool(byAssemblyAndName, tool);
        }

        var candidates = new List<ToolCandidate>(ActiveTools.Count);
        foreach (ToolReleaseDeclaration declaration in ActiveTools)
        {
            if (string.Equals(declaration.source, "hot-update", StringComparison.Ordinal))
            {
                LoadedToolPack package = ToolPacks.SingleOrDefault(candidate =>
                    string.Equals(candidate.PackageId, declaration.packageId, StringComparison.Ordinal) &&
                    string.Equals(candidate.PackageVersion, declaration.packageVersion, StringComparison.Ordinal));
                if (package == null ||
                    !string.Equals(package.Assembly.GetName().Name, declaration.assemblyName, StringComparison.Ordinal) ||
                    !string.Equals(package.AssemblyHash, declaration.assemblyHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Release package identity does not match tool '{declaration.name}'.");
            }
            string key = declaration.assemblyName + "\n" + declaration.name;
            if (!byAssemblyAndName.TryGetValue(key, out IAgentTool tool))
                throw new InvalidDataException(
                    $"Release tool '{declaration.name}' could not be resolved from '{declaration.assemblyName}'.");
            candidates.Add(new ToolCandidate(declaration, tool));
        }
        return new ToolSetCandidate(
            ReleaseId,
            ToolSetVersion,
            CatalogVersion,
            candidates,
            RetiredTools,
            History);
    }

    private static void AddResolvedTool(IDictionary<string, IAgentTool> tools, IAgentTool tool)
    {
        string key = tool.GetType().Assembly.GetName().Name + "\n" + tool.Descriptor.Name;
        if (tools.ContainsKey(key))
            throw new InvalidOperationException($"Assembly contains duplicate resolved tool '{tool.Descriptor.Name}'.");
        tools.Add(key, tool);
    }
}
