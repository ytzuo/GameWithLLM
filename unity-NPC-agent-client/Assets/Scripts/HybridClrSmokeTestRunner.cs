using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class HybridClrSmokeTestRunner : MonoBehaviour
{
    private const string CommandLineFlag = "-gameWithLlmHybridClrSmoke";
    private const string A2CommandLineFlag = "-gameWithLlmAddressablesA2Smoke";
    private const string H7CommandLineFlag = "-gameWithLlmH7Smoke";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartWhenRequested()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!arguments.Contains(CommandLineFlag, StringComparer.Ordinal) &&
            !arguments.Contains(A2CommandLineFlag, StringComparer.Ordinal) &&
            !arguments.Contains(H7CommandLineFlag, StringComparer.Ordinal))
            return;
        var runner = new GameObject(nameof(HybridClrSmokeTestRunner))
            .AddComponent<HybridClrSmokeTestRunner>();
        DontDestroyOnLoad(runner.gameObject);
        runner.Run();
    }

    private async void Run()
    {
        try
        {
            ToolsRegistry registry = ToolsRegistry.Instance;
            string[] commandLine = Environment.GetCommandLineArgs();
            bool h7 = commandLine.Contains(H7CommandLineFlag, StringComparer.Ordinal);
            bool a2 = h7 || commandLine.Contains(A2CommandLineFlag, StringComparer.Ordinal);
            if (a2)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while ((AgentHostClient.Instance == null || !AgentHostClient.Instance.IsContentReady) &&
                       DateTime.UtcNow < deadline)
                    await Task.Yield();
                if (AgentHostClient.Instance == null || !AgentHostClient.Instance.IsContentReady)
                    throw new TimeoutException("A2 content bootstrap did not become ready.");
            }
            else
            {
                LoadedToolSetRelease release = HybridClrBootstrap.LoadLocalRelease(true);
                if (release == null)
                    throw new InvalidOperationException("H4 local release was not loaded.");
                ToolSetCandidate candidate = release.BuildCandidate(
                    AgentToolDiscovery.DiscoverBuiltinTools());
                PreparedToolSet prepared = registry.PrepareToolSet(
                    candidate,
                    release.ToolMetadataJson);
                ClientTextCatalog agentMessages = ClientTextCatalog.Parse(
                    release.AgentMessagesJson,
                    release.CatalogVersion);
                ClientTextCatalog ui = ClientTextCatalog.Parse(
                    release.UiJson,
                    release.CatalogVersion);
                registry.ActivateToolSet(prepared);
                ClientTextCatalogs.Activate(agentMessages, ui);
            }
            NpcEntity npc = FindFirstObjectByType<NpcEntity>();
            if (npc == null)
                throw new InvalidOperationException("Smoke test requires an active NpcEntity.");

            if (h7)
            {
                await RunH7SmokeAsync(registry, npc);
                Debug.Log("[Hot Update] H7_PLAYER_SMOKE_SUCCESS: all declared calls and tombstones were verified.");
                Application.Quit(0);
                return;
            }

            AgentToolDescriptor descriptor = registry.GetRuntimeTools()
                .FirstOrDefault(tool => tool.Name == "game_hotfix_smoke_query");
            AgentToolDescriptor packageDescriptor = registry.GetRuntimeTools()
                .FirstOrDefault(tool => tool.Name == "game_hotfix_smoke_package_info");
            if (descriptor == null ||
                packageDescriptor == null ||
                !descriptor.Description.Contains("HybridCLR Smoke Tool Pack 是否已加载") ||
                string.IsNullOrWhiteSpace(descriptor.InputSchemaJson) ||
                !descriptor.InputSchemaJson.Contains("echo") ||
                !descriptor.InputSchemaJson.Contains("随查询原样返回的可选诊断文本"))
            {
                throw new InvalidOperationException("Smoke tool descriptor or schema is missing.");
            }
            ToolSetSnapshot snapshot = registry.ActiveSnapshot;
            if (snapshot == null || snapshot.ReleaseId != "h5-local-1.2.0" ||
                snapshot.ToolSetVersion != "4.0.0" ||
                registry.ActiveCatalog.ContentVersion != "2026.09.001")
                throw new InvalidOperationException(
                    $"Unexpected active ToolSet: {snapshot?.ReleaseId}/{snapshot?.ToolSetVersion}.");

            AgentToolResult result = await registry.ExecuteAsync(
                descriptor.Name,
                new AgentToolContext(npc, "hybridclr-h2-smoke"),
                "{\"echo\":\"h2\"}",
                CancellationToken.None);
            if (result == null || !result.Ok ||
                string.IsNullOrWhiteSpace(result.DataJson) ||
                !result.DataJson.Contains("smoke-test"))
            {
                throw new InvalidOperationException(
                    $"Smoke tool execution failed: {result?.ErrorCode} {result?.Message}");
            }

            Debug.Log("[Hot Update] H2_SMOKE_SUCCESS: DLL loaded, schema generated, arguments deserialized, tool executed.");
            Debug.Log("[Hot Update] H3_ATOMIC_PACK_SUCCESS: two package tools remain available in one snapshot.");
            Debug.Log("[Hot Update] H4_TOOLSET_SUCCESS: complete versioned ToolSet activated before runtime input.");
            Debug.Log("[Hot Update] H5_CATALOG_SUCCESS: JSON tool metadata and client text Catalogs activated atomically.");
            if (a2)
                Debug.Log("[Content] A2_ADDRESSABLES_SUCCESS: manifest, JSON, metadata and tool pack activated from Addressables.");
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Hot Update] H2_SMOKE_FAILED: {ex}");
            Application.Quit(1);
        }
    }

    private static async Task RunH7SmokeAsync(ToolsRegistry registry, NpcEntity npc)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "H7", "h7-smoke-plan.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("H7 smoke plan is missing.", path);
        JObject plan = JObject.Parse(File.ReadAllText(path));
        ToolSetSnapshot snapshot = registry.ActiveSnapshot;
        if ((int?)plan["schemaVersion"] != 1 ||
            !string.Equals((string)plan["releaseId"], snapshot.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals((string)plan["toolSetVersion"], snapshot.ToolSetVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("H7 smoke plan does not match the active ToolSet.");

        HashSet<string> manifestNames = new HashSet<string>(
            registry.GetRuntimeTools().Select(tool => tool.Name), StringComparer.Ordinal);
        HashSet<string> capabilityNames = new HashSet<string>(
            registry.GetAvailableToolNames(npc), StringComparer.Ordinal);
        foreach (JObject call in plan["smokeCalls"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string toolName = (string)call["toolName"];
            if (!manifestNames.Contains(toolName) || !capabilityNames.Contains(toolName))
                throw new InvalidOperationException($"H7 smoke tool '{toolName}' is absent from Manifest or capabilities.");
            string arguments = (call["arguments"] as JObject ?? new JObject()).ToString(Formatting.None);
            AgentToolResult result = await registry.ExecuteAsync(
                toolName,
                new AgentToolContext(npc, "hybridclr-h7-smoke"),
                arguments,
                CancellationToken.None);
            if (result == null || !result.Ok)
                throw new InvalidOperationException(
                    $"H7 smoke call '{toolName}' failed: {result?.ErrorCode} {result?.Message}");
        }

        foreach (string retired in plan["retiredTools"]?.Values<string>() ?? Enumerable.Empty<string>())
        {
            if (manifestNames.Contains(retired) || capabilityNames.Contains(retired))
                throw new InvalidOperationException($"Retired tool '{retired}' is still visible.");
            AgentToolResult result = await registry.ExecuteAsync(
                retired,
                new AgentToolContext(npc, "hybridclr-h7-retired-probe"),
                "{}",
                CancellationToken.None);
            if (result == null || result.Ok || !string.Equals(result.ErrorCode, "TOOL_RETIRED", StringComparison.Ordinal))
                throw new InvalidOperationException($"Retired tool '{retired}' is still routable.");
        }
    }
}
