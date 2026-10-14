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
public static class NpcPresentationStage2SceneSmoke
{
    private const string Key = "NpcPresentationStage2Smoke.Active";
    private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Artifacts", "NpcPresentationStage2"));
    static NpcPresentationStage2SceneSmoke() { if (SessionState.GetBool(Key, false)) Attach(); }
    public static void RunFromCommandLine()
    {
        Directory.CreateDirectory(Root);
        foreach (string name in new[] { "stop", "ready", "world-saved", "world-restored", "scene-error", "world-contract.json" })
            if (File.Exists(Path.Combine(Root, name))) File.Delete(Path.Combine(Root, name));
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        SessionState.SetInt(Key + ".Builder", settings.ActivePlayerDataBuilderIndex);
        SessionState.SetString(Key + ".Profile", settings.activeProfileId);
        SessionState.SetString(Key + ".Deadline", DateTime.UtcNow.AddSeconds(480).Ticks.ToString());
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
    private static bool _preparing;
    private static bool _restoring;
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
            if (!_preparing) { _preparing = true; PrepareNpcsAsync(); }
            return;
        }
        NpcEntity ryan = UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None)
            .FirstOrDefault(npc => npc.npcId == "Merchant");
        if (ryan != null) File.WriteAllText(Path.Combine(Root, "state.json"), ryan.CreateRuntimeStateData().ToString());
        var player = UnityEngine.Object.FindFirstObjectByType<PlayerMock>();
        var saves = (SaveGameService)typeof(PlayerMock).GetField("_saveGameService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player);
        if (File.Exists(Path.Combine(Root, "save-world")) && !File.Exists(Path.Combine(Root, "world-saved")))
        {
            var file = saves.Create("NPC round one smoke");
            var merchant = file.Entities.Single(value => value.EntityId == "Merchant");
            var contract = new Newtonsoft.Json.Linq.JObject
            {
                ["version"] = file.Version,
                ["npcContents"] = Newtonsoft.Json.Linq.JArray.FromObject(file.NpcContents),
                ["merchantPosition"] = Newtonsoft.Json.Linq.JObject.FromObject(merchant.Position)
            };
            File.WriteAllText(Path.Combine(Root, "world-contract.json"), contract.ToString());
            File.WriteAllText(Path.Combine(Root, "world-saved"), file.SaveId);
        }
        if (File.Exists(Path.Combine(Root, "restore-world")) && !File.Exists(Path.Combine(Root, "world-restored")))
        {
            if (!_restoring) { _restoring = true; RestoreAsync(saves); }
        }
    }
    private static async void PrepareNpcsAsync()
    {
        try
        {
            var services = NpcLibraryServices.Current;
            var catalog = await services.CatalogClient.FetchAsync(System.Threading.CancellationToken.None);
            foreach (var summary in catalog.Npcs)
            {
                var record = await services.Installer.InstallAsync(summary, System.Threading.CancellationToken.None);
                var entity = await services.Spawner.SpawnAsync(record, System.Threading.CancellationToken.None);
                if (entity.ContentBinding.EntityId != summary.NpcId) throw new InvalidOperationException("Smoke binding invalid.");
            }
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) != 0)
                        throw new InvalidOperationException("SampleScene contains Missing Script.");
            File.WriteAllText(Path.Combine(Root,"ready"),UnityEngine.Object.FindFirstObjectByType<AgentHostClient>().unityInstanceId);
        }
        catch (Exception ex) { Debug.LogException(ex); File.WriteAllText(Path.Combine(Root,"stop"),"failure"); }
    }
    private static async void RestoreAsync(SaveGameService saves)
    {
        try
        {
            string id=File.ReadAllText(Path.Combine(Root,"world-saved"));
            var file=saves.Load(id);
            if(file.Version!=3 || file.NpcContents.Count!=2) throw new InvalidOperationException("Smoke world format invalid.");
            var services=NpcLibraryServices.Current;
            services.Spawner.Despawn("Merchant"); services.Spawner.Despawn("Guide");
            await System.Threading.Tasks.Task.Yield();
            await services.PrepareRestoreAsync(file.NpcContents,System.Threading.CancellationToken.None);
            saves.Apply(file);
            var host=UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
            await (System.Threading.Tasks.Task)typeof(AgentHostClient).GetMethod("PublishManifestAndWaitAsync",BindingFlags.NonPublic|BindingFlags.Instance)
                .Invoke(host,new object[]{System.Threading.CancellationToken.None});
            File.WriteAllText(Path.Combine(Root,"world-restored"),"ok");
        }
        catch(Exception ex) { Debug.LogException(ex); File.WriteAllText(Path.Combine(Root,"stop"),"failure"); }
    }

}
