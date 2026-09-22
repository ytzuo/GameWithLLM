using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

public sealed class HybridClrSmokeTestRunner : MonoBehaviour
{
    private const string CommandLineFlag = "-gameWithLlmHybridClrSmoke";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartWhenRequested()
    {
        if (!Environment.GetCommandLineArgs().Contains(CommandLineFlag, StringComparer.Ordinal))
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
            LoadedToolSetRelease release = HybridClrBootstrap.LoadLocalRelease(true);
            if (release == null)
                throw new InvalidOperationException("H4 local release was not loaded.");
            ToolSetCandidate candidate = release.BuildCandidate(
                AgentToolDiscovery.DiscoverBuiltinTools());
            registry.ActivateToolSet(registry.PrepareToolSet(candidate));
            NpcEntity npc = FindFirstObjectByType<NpcEntity>();
            if (npc == null)
                throw new InvalidOperationException("Smoke test requires an active NpcEntity.");

            AgentToolDescriptor descriptor = registry.GetRuntimeTools()
                .FirstOrDefault(tool => tool.Name == "game_hotfix_smoke_query");
            AgentToolDescriptor packageDescriptor = registry.GetRuntimeTools()
                .FirstOrDefault(tool => tool.Name == "game_hotfix_smoke_package_info");
            if (descriptor == null ||
                packageDescriptor == null ||
                string.IsNullOrWhiteSpace(descriptor.InputSchemaJson) ||
                !descriptor.InputSchemaJson.Contains("echo"))
            {
                throw new InvalidOperationException("Smoke tool descriptor or schema is missing.");
            }
            ToolSetSnapshot snapshot = registry.ActiveSnapshot;
            if (snapshot == null || snapshot.ReleaseId != "h4-local-1.0.0" ||
                snapshot.ToolSetVersion != "1.0.0")
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
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Hot Update] H2_SMOKE_FAILED: {ex}");
            Application.Quit(1);
        }
    }
}
