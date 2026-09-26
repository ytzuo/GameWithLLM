using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWithLLM.AgentRuntime;
using HybridCLR.Editor.Settings;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public sealed class ToolPackageGateResult
{
    public ToolPackageGateResult(JObject manifest)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ReleaseId = (string)manifest["releaseId"];
        ToolSetVersion = (string)manifest["toolSetVersion"];
        ToolPackages = (manifest["toolPackages"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()).ToArray();
    }

    public JObject Manifest { get; }
    public string ReleaseId { get; }
    public string ToolSetVersion { get; }
    public IReadOnlyList<JObject> ToolPackages { get; }
}

public static class ToolPackageReleaseGate
{
    private const string PolicyPath = "Assets/Content/HotUpdate/tool-package-release-policy.json";
    private const string ManifestPath = "Assets/Content/HotUpdate/release-manifest.json";
    private const string SmokePlanPath = "Assets/StreamingAssets/SmokeTests/tool-package-smoke-plan.json";
    private const string ToolMetadataPath = "Assets/Content/Catalogs/tool_metadata.zh-CN.json";
    private const string ExpectedUnityVersion = "6000.3.19f1";
    private const string ExpectedHybridClrVersion = "8.14.1";

    public static ToolPackageGateResult ValidateCandidate()
    {
        VerifyPinnedToolchain();
        AddressablesA2ProjectSetup.Verify();
        JObject policy = ReadObject(PolicyPath);
        JObject manifest = ReadObject(ManifestPath);
        JObject smokePlan = ReadObject(SmokePlanPath);
        VerifyEnvelope(policy, manifest, smokePlan);
        VerifyAssemblyReferences(policy, manifest);
        VerifyToolSetAndCatalog(manifest);
        VerifySmokeCoverage(manifest, smokePlan, policy);
        Debug.Log($"[ToolPackages] RELEASE_GATE_SUCCESS: release '{(string)manifest["releaseId"]}'.");
        return new ToolPackageGateResult(manifest);
    }

    private static void VerifyPinnedToolchain()
    {
        if (!string.Equals(Application.unityVersion, ExpectedUnityVersion, StringComparison.Ordinal))
            throw new BuildFailedException(
                $"Tool package release requires Unity {ExpectedUnityVersion}; current Editor is {Application.unityVersion}.");
        string manifest = File.ReadAllText("Packages/manifest.json");
        if (!manifest.Contains("hybridclr_unity.git#v" + ExpectedHybridClrVersion))
            throw new BuildFailedException($"Tool package release requires HybridCLR {ExpectedHybridClrVersion}.");
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) != ScriptingImplementation.IL2CPP ||
            PlayerSettings.GetArchitecture(BuildTargetGroup.Standalone) != 1)
            throw new BuildFailedException("Tool package release requires Windows x86_64 IL2CPP Player settings.");
    }

    private static void VerifyEnvelope(JObject policy, JObject manifest, JObject smokePlan)
    {
        if ((int?)policy["schemaVersion"] != 1 || (int?)manifest["schemaVersion"] != 1 ||
            (int?)smokePlan["schemaVersion"] != 1)
            throw new InvalidDataException("Tool package policy, manifest and smoke plan must use schemaVersion 1.");
        string releaseId = RequiredString(manifest, "releaseId");
        string toolSetVersion = RequiredString(manifest, "toolSetVersion");
        if (!string.Equals(releaseId, (string)smokePlan["releaseId"], StringComparison.Ordinal) ||
            !string.Equals(toolSetVersion, (string)smokePlan["toolSetVersion"], StringComparison.Ordinal))
            throw new InvalidDataException("Tool package smoke plan does not target the staged release and ToolSet.");
        string playerBuildId = RequiredString(manifest, "playerBuildId");
        foreach (JObject metadata in manifest["aotMetadata"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string address = RequiredString(metadata, "address");
            if (!address.StartsWith("hotfix/aot/" + playerBuildId + "/", StringComparison.Ordinal))
                throw new InvalidDataException($"AOT metadata '{address}' does not match Player build '{playerBuildId}'.");
        }
        string expectedRelease = Environment.GetEnvironmentVariable("CONTENT_RELEASE_ID");
        if (!string.IsNullOrWhiteSpace(expectedRelease) &&
            !string.Equals(expectedRelease, releaseId, StringComparison.Ordinal))
            throw new InvalidDataException($"CONTENT_RELEASE_ID '{expectedRelease}' does not match '{releaseId}'.");
    }

    private static void VerifyAssemblyReferences(JObject policy, JObject manifest)
    {
        var allowed = new HashSet<string>(
            policy["allowedAssemblyReferences"]?.Values<string>() ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        string[] allowedPrefixes = policy["allowedFrameworkReferencePrefixes"]?.Values<string>().ToArray()
                                   ?? Array.Empty<string>();
        foreach (JObject package in manifest["toolPackages"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string assemblyName = RequiredString(package, "assemblyName");
            string fileName = RequiredString((JObject)package["assembly"], "fileName");
            string path = "Assets/Content/HotUpdate/ToolPacks/" + fileName;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Tool package '{assemblyName}' is missing.", path);
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path);
            if (!string.Equals(assembly.Name.Name, assemblyName, StringComparison.Ordinal))
                throw new InvalidDataException($"Tool package file declares '{assembly.Name.Name}', not '{assemblyName}'.");
            string[] denied = assembly.MainModule.AssemblyReferences
                .Select(reference => reference.Name)
                .Where(reference => !allowed.Contains(reference) &&
                                    !allowedPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
            if (denied.Length > 0)
                throw new InvalidDataException(
                    $"Tool package '{assemblyName}' has forbidden references: {string.Join(", ", denied)}.");
        }
    }

    private static void VerifyToolSetAndCatalog(JObject manifest)
    {
        HotUpdateReleaseManifest release = manifest.ToObject<HotUpdateReleaseManifest>() ??
                                           throw new InvalidDataException("Tool package release manifest is invalid.");
        var tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (IAgentTool tool in AgentToolDiscovery.DiscoverBuiltinTools())
            tools.Add(tool.Descriptor.Name, tool);
        foreach (HotUpdateToolPackageDeclaration package in release.toolPackages)
        {
            System.Reflection.Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, package.assemblyName, StringComparison.Ordinal)) ??
                throw new InvalidOperationException($"Editor assembly '{package.assemblyName}' is not loaded.");
            foreach (IAgentTool tool in AgentToolDiscovery.DiscoverFromAssembly(assembly))
                tools.Add(tool.Descriptor.Name, tool);
        }
        ToolCandidate[] candidates = release.activeTools.Select(declaration =>
            new ToolCandidate(
                declaration,
                tools.TryGetValue(declaration.name, out IAgentTool tool)
                    ? tool
                    : throw new InvalidDataException($"Release tool '{declaration.name}' has no discovered implementation.")))
            .ToArray();
        var candidate = new ToolSetCandidate(
            release.releaseId,
            release.toolSetVersion,
            release.catalogVersion,
            candidates,
            release.retiredTools,
            release.toolHistory);
        ToolSetSnapshot snapshot = ToolSetValidator.Prepare(candidate, null);
        ToolMetadataCatalog.ParseAndValidate(File.ReadAllText(ToolMetadataPath), snapshot, release.catalogVersion);
    }

    private static void VerifySmokeCoverage(JObject manifest, JObject smokePlan, JObject policy)
    {
        var calls = (smokePlan["smokeCalls"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            .ToDictionary(item => RequiredString(item, "toolName"), StringComparer.Ordinal);
        var active = manifest["activeTools"]?.Children<JObject>().ToArray() ?? Array.Empty<JObject>();
        foreach (JObject tool in active.Where(item => string.Equals((string)item["source"], "hot-update", StringComparison.Ordinal)))
        {
            string name = RequiredString(tool, "name");
            if (!calls.ContainsKey(name))
                throw new InvalidDataException($"Hot-update tool '{name}' has no tool package smoke call.");
        }
        JObject approved = policy["approvedTools"] as JObject ?? new JObject();
        foreach (JObject tool in active)
        {
            string name = RequiredString(tool, "name");
            string fingerprint = ToolFingerprint(tool);
            if ((!approved.TryGetValue(name, out JToken prior) ||
                 !string.Equals((string)prior, fingerprint, StringComparison.OrdinalIgnoreCase)) &&
                !calls.ContainsKey(name))
                throw new InvalidDataException($"New or modified tool '{name}' has no tool package smoke call.");
        }
        var retired = new HashSet<string>(
            manifest["retiredTools"]?.Children<JObject>().Select(item => RequiredString(item, "name"))
            ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var smokeRetired = new HashSet<string>(
            smokePlan["retiredTools"]?.Values<string>() ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (!retired.SetEquals(smokeRetired))
            throw new InvalidDataException("Tool package smoke plan must verify every and only release tombstone.");
        foreach (JObject call in calls.Values)
        {
            string name = RequiredString(call, "toolName");
            if (!active.Any(item => string.Equals((string)item["name"], name, StringComparison.Ordinal)))
                throw new InvalidDataException($"Tool package smoke call targets inactive tool '{name}'.");
            if (!(call["arguments"] is JObject))
                throw new InvalidDataException($"Tool package smoke call '{name}' arguments must be a JSON object.");
        }
    }

    private static string ToolFingerprint(JObject tool) => Sha256(
        string.Join("|", new[]
        {
            (string)tool["toolIdentity"], (string)tool["source"],
            (string)tool["implementationVersion"], (string)tool["contractVersion"],
            (string)tool["packageId"], (string)tool["packageVersion"],
            (string)tool["assemblyName"], (string)tool["assemblyHash"], (string)tool["schemaHash"]
        }));

    private static JObject ReadObject(string path) => File.Exists(path)
        ? JObject.Parse(File.ReadAllText(path))
        : throw new FileNotFoundException($"Required tool package release file is missing: '{path}'.", path);

    private static string RequiredString(JObject value, string property)
    {
        string result = (string)value?[property];
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException($"Required tool package field '{property}' is missing.");
        return result;
    }

    private static string Sha256(string value) => ArtifactHash.Sha256(value);
}
