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

public sealed class ToolPackageSmoke : MonoBehaviour
{
    private const string CommandLineFlag = "-gameWithLlmToolPackageSmoke";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartWhenRequested()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!arguments.Contains(CommandLineFlag, StringComparer.Ordinal))
            return;
        var runner = new GameObject(nameof(ToolPackageSmoke))
            .AddComponent<ToolPackageSmoke>();
        DontDestroyOnLoad(runner.gameObject);
        runner.Run();
    }

    private async void Run()
    {
        try
        {
            ToolsRegistry registry = ToolsRegistry.Instance;
            if (Environment.GetCommandLineArgs().Contains(CommandLineFlag, StringComparer.Ordinal))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while ((AgentHostClient.Instance == null || !AgentHostClient.Instance.IsContentReady) &&
                       DateTime.UtcNow < deadline)
                    await Task.Yield();
                if (AgentHostClient.Instance == null || !AgentHostClient.Instance.IsContentReady)
                    throw new TimeoutException("Content bootstrap did not become ready.");
            }
            NpcEntity npc = FindFirstObjectByType<NpcEntity>();
            if (npc == null)
                throw new InvalidOperationException("Smoke test requires an active NpcEntity.");

            await RunSmokeAsync(registry, npc);
            Debug.Log("[ToolPackages] TOOL_PACKAGE_SMOKE_SUCCESS: metadata, schema, ToolSet, calls and tombstones were verified.");
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ToolPackages] TOOL_PACKAGE_SMOKE_FAILED: {ex}");
            Application.Quit(1);
        }
    }

    private static async Task RunSmokeAsync(ToolsRegistry registry, NpcEntity npc)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "SmokeTests", "tool-package-smoke-plan.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Tool package smoke plan is missing.", path);
        JObject plan = JObject.Parse(File.ReadAllText(path));
        ToolSetSnapshot snapshot = registry.ActiveSnapshot;
        if ((int?)plan["schemaVersion"] != 1 ||
            !string.Equals((string)plan["releaseId"], snapshot.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals((string)plan["toolSetVersion"], snapshot.ToolSetVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Tool package smoke plan does not match the active ToolSet.");

        HashSet<string> manifestNames = new HashSet<string>(
            registry.GetRuntimeTools().Select(tool => tool.Name), StringComparer.Ordinal);
        HashSet<string> capabilityNames = new HashSet<string>(
            registry.GetAvailableToolNames(npc), StringComparer.Ordinal);
        foreach (JObject call in plan["smokeCalls"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string toolName = (string)call["toolName"];
            if (!manifestNames.Contains(toolName) || !capabilityNames.Contains(toolName))
                throw new InvalidOperationException($"Tool package smoke tool '{toolName}' is absent from Manifest or capabilities.");
            string arguments = (call["arguments"] as JObject ?? new JObject()).ToString(Formatting.None);
            AgentToolResult result = await registry.ExecuteAsync(
                toolName,
                new AgentToolContext(npc, "tool-package-smoke"),
                arguments,
                CancellationToken.None);
            if (result == null || !result.Ok)
                throw new InvalidOperationException(
                    $"Tool package smoke call '{toolName}' failed: {result?.ErrorCode} {result?.Message}");
        }

        foreach (string retired in plan["retiredTools"]?.Values<string>() ?? Enumerable.Empty<string>())
        {
            if (manifestNames.Contains(retired) || capabilityNames.Contains(retired))
                throw new InvalidOperationException($"Retired tool '{retired}' is still visible.");
            AgentToolResult result = await registry.ExecuteAsync(
                retired,
                new AgentToolContext(npc, "tool-package-retired-probe"),
                "{}",
                CancellationToken.None);
            if (result == null || result.Ok || !string.Equals(result.ErrorCode, "TOOL_RETIRED", StringComparison.Ordinal))
                throw new InvalidOperationException($"Retired tool '{retired}' is still routable.");
        }
    }
}
