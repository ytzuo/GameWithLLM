using System;
using System.Linq;
using System.Threading;
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
            await System.Threading.Tasks.Task.Yield();
            ToolsRegistry registry = ToolsRegistry.Instance;
            NpcEntity npc = FindFirstObjectByType<NpcEntity>();
            if (npc == null)
                throw new InvalidOperationException("Smoke test requires an active NpcEntity.");

            AgentToolDescriptor descriptor = registry.GetRuntimeTools()
                .FirstOrDefault(tool => tool.Name == "game_hotfix_smoke_query");
            if (descriptor == null ||
                string.IsNullOrWhiteSpace(descriptor.InputSchemaJson) ||
                !descriptor.InputSchemaJson.Contains("echo"))
            {
                throw new InvalidOperationException("Smoke tool descriptor or schema is missing.");
            }

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
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Hot Update] H2_SMOKE_FAILED: {ex}");
            Application.Quit(1);
        }
    }
}
