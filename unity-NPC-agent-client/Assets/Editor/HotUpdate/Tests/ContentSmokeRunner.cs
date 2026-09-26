using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEngine;

public static class ContentSmokeRunner
{
    private const string LocalProfile = "LocalDevelopment";

    [MenuItem("GameWithLLM/Content/Run Packed Play Smoke")]
    public static void RunPackedPlaySmoke() => UiInventoryPackedPlaySmoke.Run();

    [MenuItem("GameWithLLM/Content/Build Local Smoke Player")]
    public static void BuildLocalSmokePlayer()
    {
        HybridClrProjectSetup.GenerateAndStageForPipeline(false);
        AddressablesA2ProjectSetup.StageAndConfigure();
        ToolPackageGateResult gate = ToolPackageReleaseGate.ValidateCandidate();
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        using (new AddressablesProfileScope(settings, LocalProfile))
        {
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new BuildFailedException("Local smoke Addressables build failed: " + result.Error);
        }
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds",
            "ContentLocalSmoke", "GameWithLLM.exe"));
        WindowsPlayerBuilder.Build(settings, LocalProfile, output,
            new[] { "Assets/Scenes/SampleScene.unity" },
            BuildOptions.Development | BuildOptions.CleanBuildCache);
        WriteToolSchemaSnapshot(gate);
        Debug.Log("[Content] LOCAL_SMOKE_PLAYER_READY: tool package smoke Player was built.");
    }

    public static void RunFromCommandLine()
    {
        string layer = HotUpdateEditorCommand.GetArgument("-contentSmokeLayer") ?? "tool-package-player";
        switch (layer.ToLowerInvariant())
        {
            case "bootstrap-online": ContentBootstrapSmoke.RunFromCommandLine(); break;
            case "bootstrap-offline": ContentBootstrapSmoke.RunOfflineFromCommandLine(); break;
            case "ui-inventory": UiInventoryPackedPlaySmoke.Run(); break;
            case "remote-scene": RemoteScenePackedPlaySmoke.RunFromCommandLine(); break;
            case "tool-package-player": HotUpdateEditorCommand.Run(BuildLocalSmokePlayer); break;
            default: throw new ArgumentException($"Unknown content smoke layer '{layer}'.");
        }
    }

    private static void WriteToolSchemaSnapshot(ToolPackageGateResult gate)
    {
        string outputDirectory = new ContentBuildContext().ResolveArtifactPath(
            "Content", gate.ReleaseId);
        Directory.CreateDirectory(outputDirectory);
        var snapshot = new JObject
        {
            ["schemaVersion"] = 1,
            ["releaseId"] = gate.Manifest["releaseId"],
            ["toolSetVersion"] = gate.Manifest["toolSetVersion"],
            ["tools"] = new JArray(gate.Manifest["activeTools"]?.Children<JObject>() ??
                                   Enumerable.Empty<JObject>())
        };
        File.WriteAllText(Path.Combine(outputDirectory, "tool-schema-snapshot.json"),
            snapshot.ToString(Formatting.Indented) + Environment.NewLine);
    }
}
