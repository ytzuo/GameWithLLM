using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

public sealed class HybridClrSmokeTestRunner : MonoBehaviour
{
    private const string CommandLineFlag = "-gameWithLlmHybridClrSmoke";
    private const string A2CommandLineFlag = "-gameWithLlmAddressablesA2Smoke";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartWhenRequested()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!arguments.Contains(CommandLineFlag, StringComparer.Ordinal) &&
            !arguments.Contains(A2CommandLineFlag, StringComparer.Ordinal))
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
            bool a2 = Environment.GetCommandLineArgs()
                .Contains(A2CommandLineFlag, StringComparer.Ordinal);
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
}
