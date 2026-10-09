using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.SceneManagement;
using UnityEngine;

// External smoke coordinator drives the real A2A/MCP/Save HTTP endpoints while this
// Editor session runs SampleScene. No test endpoint is added to production code.
[InitializeOnLoad]
public static class NpcRoundOneSceneSmoke
{
    private const string Key = "NpcRoundOneSmoke.Active";
    private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Artifacts", "NpcRound1"));
    static NpcRoundOneSceneSmoke() { if (SessionState.GetBool(Key, false)) Attach(); }
    public static void RunFromCommandLine()
    {
        Directory.CreateDirectory(Root);
        foreach (string name in new[] { "stop", "ready", "world-saved", "world-restored", "scene-error" })
            if (File.Exists(Path.Combine(Root, name))) File.Delete(Path.Combine(Root, name));
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        SessionState.SetInt(Key + ".Builder", settings.ActivePlayerDataBuilderIndex);
        SessionState.SetString(Key + ".Profile", settings.activeProfileId);
        SessionState.SetString(Key + ".Deadline", DateTime.UtcNow.AddSeconds(240).Ticks.ToString());
        SessionState.SetBool(Key, true);
        settings.ActivePlayerDataBuilderIndex = 1;
        settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        Attach(); EditorApplication.EnterPlaymode();
    }
    private static void Attach()
    {
        EditorApplication.update -= Poll; EditorApplication.update += Poll;
        Application.logMessageReceived -= OnLog; Application.logMessageReceived += OnLog;
    }
    private static void OnLog(string condition, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            File.WriteAllText(Path.Combine(Root, "scene-error"), type.ToString());
    }
    private static void Poll()
    {
        if (SessionState.GetBool(Key + ".Finishing", false))
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            settings.ActivePlayerDataBuilderIndex = SessionState.GetInt(Key + ".Builder", 1);
            settings.activeProfileId = SessionState.GetString(Key + ".Profile", settings.activeProfileId);
            SessionState.SetBool(Key, false); SessionState.SetBool(Key + ".Finishing", false);
            Application.logMessageReceived -= OnLog; EditorApplication.update -= Poll;
            EditorApplication.Exit(File.Exists(Path.Combine(Root, "scene-error")) ? 1 : 0); return;
        }
        if (File.Exists(Path.Combine(Root, "stop")) || DateTime.UtcNow.Ticks > long.Parse(SessionState.GetString(Key + ".Deadline", "0")))
        {
            SessionState.SetBool(Key + ".Finishing", true); EditorApplication.ExitPlaymode(); return;
        }
        if (!EditorApplication.isPlaying) return;
        AgentHostClient host = UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
        if (host == null || !host.IsContentReady) return;
        if (!File.Exists(Path.Combine(Root, "ready")))
        {
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) != 0)
                        throw new InvalidOperationException("SampleScene contains Missing Script.");
            File.WriteAllText(Path.Combine(Root, "ready"), host.unityInstanceId);
        }
        NpcEntity ryan = UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None)
            .FirstOrDefault(npc => npc.npcId == "Ryan_001");
        if (ryan != null) File.WriteAllText(Path.Combine(Root, "state.json"), ryan.CreateRuntimeStateData().ToString());
        var player = UnityEngine.Object.FindFirstObjectByType<PlayerMock>();
        var saves = (SaveGameService)typeof(PlayerMock).GetField("_saveGameService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player);
        if (File.Exists(Path.Combine(Root, "save-world")) && !File.Exists(Path.Combine(Root, "world-saved")))
        {
            var file = saves.Create("NPC round one smoke");
            File.WriteAllText(Path.Combine(Root, "world-saved"), file.SaveId);
        }
        if (File.Exists(Path.Combine(Root, "restore-world")) && !File.Exists(Path.Combine(Root, "world-restored")))
        {
            string id = File.ReadAllText(Path.Combine(Root, "world-saved"));
            saves.Apply(saves.Load(id)); File.WriteAllText(Path.Combine(Root, "world-restored"), "ok");
        }
    }
}
