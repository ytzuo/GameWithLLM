using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

// External smoke coordinator drives the real A2A/MCP/Save HTTP endpoints while this
// Editor session runs the authored Warehouse with the local Bootstrap host. No test endpoint is added to production code.
[InitializeOnLoad]
public static class NpcPresentationStage3SceneSmoke
{
    private const string Key = "NpcPresentationStage3Smoke.Active";
    private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Artifacts", "NpcPresentationStage3"));
    static NpcPresentationStage3SceneSmoke() { if (SessionState.GetBool(Key, false)) Attach(); }
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
        settings.ActivePlayerDataBuilderIndex = 0;
        settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
        EditorSceneManager.OpenScene("Assets/Scenes/BootstrapScene.unity", OpenSceneMode.Single);
        var host = UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
        var coordinator = new SerializedObject(host.GetComponent<RemoteSceneCoordinator>());
        coordinator.FindProperty("loadInitialScene").boolValue = false;
        coordinator.ApplyModifiedPropertiesWithoutUndo();
        var warehouse = EditorSceneManager.OpenScene("Assets/Content/Scenes/WarehouseRemote.unity", OpenSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(warehouse);
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
    private static bool _failingRestore;
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
        if (File.Exists(Path.Combine(Root, "failed-context-restore")) && !File.Exists(Path.Combine(Root, "failed-world-restored")))
        {
            if (!_failingRestore) { _failingRestore = true; FailContextRestoreAsync(saves, host); }
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
            var surface = UnityEngine.Object.FindFirstObjectByType<Unity.AI.Navigation.NavMeshSurface>();
            bool found = UnityEngine.AI.NavMesh.SamplePosition(Vector3.zero, out var hit, 100, UnityEngine.AI.NavMesh.AllAreas);
            File.WriteAllText(Path.Combine(Root, "navmesh-report.json"), new Newtonsoft.Json.Linq.JObject {
                ["found"] = found, ["distance"] = found ? Vector3.Distance(hit.position, Vector3.zero) : -1,
                ["height"] = found ? hit.position.y : -1,
                ["surfaceActive"] = surface != null && surface.isActiveAndEnabled,
                ["dataPresent"] = surface != null && surface.navMeshData != null }.ToString());
            await NpcPresentationStage3FaultMatrix.RunAsync();
            var services = NpcLibraryServices.Current;
            var catalog = await services.CatalogClient.FetchAsync(System.Threading.CancellationToken.None);
            if (!UnityEngine.AI.NavMesh.SamplePosition(UnityEngine.Object.FindFirstObjectByType<PlayerMock>().transform.position, out var testHit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                throw new InvalidOperationException("Smoke player is not on NavMesh.");
            Vector3 testPosition = testHit.position;
            await VerifyCameraPlacementAsync(catalog);
            var visuals = new Newtonsoft.Json.Linq.JObject();
            foreach (var summary in catalog.Npcs)
            {
                var record = await services.Installer.InstallAsync(summary, System.Threading.CancellationToken.None);
                var entity = await services.Spawner.SpawnAsync(record, testPosition, System.Threading.CancellationToken.None);
                if (entity.ContentBinding.EntityId != summary.NpcId) throw new InvalidOperationException("Smoke binding invalid.");
                var mesh = entity.GetComponentInChildren<MeshFilter>();
                var animator = entity.GetComponentInChildren<Animator>();
                visuals[summary.NpcId] = new Newtonsoft.Json.Linq.JObject {
                    ["displayName"] = summary.DisplayName, ["mesh"] = mesh.sharedMesh.name,
                    ["clipLength"] = animator.runtimeAnimatorController.animationClips[0].length };
            }
            File.WriteAllText(Path.Combine(Root, "visuals.json"), visuals.ToString());
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) != 0)
                        throw new InvalidOperationException("Warehouse contains Missing Script.");
            File.WriteAllText(Path.Combine(Root,"ready"),UnityEngine.Object.FindFirstObjectByType<AgentHostClient>().unityInstanceId);
        }
        catch (Exception ex) { Debug.LogException(ex); File.WriteAllText(Path.Combine(Root,"stop"),"failure"); }
    }
    private static async System.Threading.Tasks.Task VerifyCameraPlacementAsync(RemoteNpcCatalog catalog)
    {
        var services = NpcLibraryServices.Current;
        var summary = catalog.Npcs.Single(value => value.NpcId == "Merchant");
        await services.Installer.InstallAsync(summary, System.Threading.CancellationToken.None);
        Camera camera = Camera.main;
        if (camera == null) throw new InvalidOperationException("Placement smoke requires the active camera.");
        Vector3 originalPosition = camera.transform.position;
        Quaternion originalRotation = camera.transform.rotation;
        NpcLibraryWindow window = null;
        var checks = new Newtonsoft.Json.Linq.JArray();
        try
        {
            window = UIManager.Instance.OpenNewWindow<NpcLibraryWindow>();
            var catalogField = typeof(NpcLibraryWindow).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic);
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (catalogField.GetValue(window) == null)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("NPC window did not load its catalog.");
                await System.Threading.Tasks.Task.Yield();
            }
            typeof(NpcLibraryWindow).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, summary);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.up);
            await (System.Threading.Tasks.Task)typeof(NpcLibraryWindow).GetMethod("RunActionAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            var popup = window.RootElement.Q<UnityEngine.UIElements.VisualElement>("npc-library-invalid-position-popup");
            if (services.Spawner.IsSpawned(summary.NpcId) || popup == null ||
                popup.Q<UnityEngine.UIElements.Label>().text != "当前位置不是有效位置")
                throw new InvalidOperationException("Invalid camera hit did not reject spawn and show the popup.");
            checks.Add("invalid-ray-no-spawn-and-ui-popup");
            popup.RemoveFromHierarchy();

            Vector3 requested = UnityEngine.Object.FindFirstObjectByType<PlayerMock>().transform.position + new Vector3(2, 0, 2);
            if (!UnityEngine.AI.NavMesh.SamplePosition(requested, out var target, 2f, UnityEngine.AI.NavMesh.AllAreas))
                throw new InvalidOperationException("Placement smoke requires a non-origin ground point.");
            camera.transform.position = target.position + Vector3.up * 5f;
            camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Physics.SyncTransforms();
            if (!NpcSpawnPlacement.TryFromCamera(camera, out Vector3 expected) || expected.magnitude < 0.5f)
                throw new InvalidOperationException("Non-origin camera ground hit was rejected.");
            await (System.Threading.Tasks.Task)typeof(NpcLibraryWindow).GetMethod("RunActionAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            var npc = UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None).SingleOrDefault(value => value.npcId == summary.NpcId);
            if (npc == null || Vector3.Distance(npc.transform.position, expected) > 0.15f)
                throw new InvalidOperationException("NPC window did not spawn at its camera center hit.");
            checks.Add("ui-spawns-at-non-origin-camera-hit");
            services.Spawner.Despawn(summary.NpcId);
            await System.Threading.Tasks.Task.Yield();
            File.WriteAllText(Path.Combine(Root, "placement-report.json"), new Newtonsoft.Json.Linq.JObject
            {
                ["passed"] = true, ["checks"] = checks,
                ["position"] = Newtonsoft.Json.Linq.JObject.FromObject(new SaveGameVector3(expected))
            }.ToString());
        }
        finally
        {
            window?.Close();
            if (window != null) UIManager.Instance.RemoveWindow(window);
            camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
        }
    }

    private static async void FailContextRestoreAsync(SaveGameService saves, AgentHostClient host)
    {
        try
        {
            string context = File.ReadAllText(Path.Combine(Root, "failed-context-restore"));
            var contexts = (System.Collections.Generic.IDictionary<string, string>)typeof(AgentHostClient)
                .GetField("_contexts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            contexts["Merchant"] = context;
            var original = saves.CaptureCurrentState();
            var saved = saves.Load(File.ReadAllText(Path.Combine(Root, "world-saved")));
            using var preparation = await NpcLibraryServices.Current.BeginRestoreAsync(saved, System.Threading.CancellationToken.None);
            var npcIds = saved.Entities.Where(value => value.EntityId != ((IGameplayWorldTarget)UnityEngine.Object.FindFirstObjectByType<PlayerMock>()).WorldTargetId)
                .Select(value => value.EntityId).ToList();
            var result = await host.LoadConversationsForSaveGameAsync(saved.SaveId, npcIds, () => saves.Apply(saved), () =>
            {
                preparation.Dispose(); saves.Apply(original);
            });
            if (result.Ok || contexts["Merchant"] != context) throw new InvalidOperationException("Failed restore replaced Context.");
            var after = saves.CaptureCurrentState();
            if (!Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JArray.FromObject(original.Inventories),
                Newtonsoft.Json.Linq.JArray.FromObject(after.Inventories))) throw new InvalidOperationException("Failed restore changed inventory.");
            File.WriteAllText(Path.Combine(Root, "failed-world-restored"), "rollback-ok");
        }
        catch (Exception ex) { Debug.LogException(ex); File.WriteAllText(Path.Combine(Root, "stop"), "failure"); }
    }

    private static async void RestoreAsync(SaveGameService saves)
    {
        try
        {
            string id=File.ReadAllText(Path.Combine(Root,"world-saved"));
            var file=saves.Load(id);
            if(file.Version!=3 || file.NpcContents.Count<2) throw new InvalidOperationException("Smoke world format invalid.");
            var services=NpcLibraryServices.Current;
            foreach (var npc in file.NpcContents) services.Spawner.Despawn(npc.EntityId);
            await System.Threading.Tasks.Task.Yield();
            await services.PrepareRestoreAsync(file,System.Threading.CancellationToken.None);
            saves.Apply(file);
            var host=UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
            await (System.Threading.Tasks.Task)typeof(AgentHostClient).GetMethod("PublishManifestAndWaitAsync",BindingFlags.NonPublic|BindingFlags.Instance)
                .Invoke(host,new object[]{System.Threading.CancellationToken.None});
            File.WriteAllText(Path.Combine(Root,"world-restored"),"ok");
        }
        catch(Exception ex) { Debug.LogException(ex); File.WriteAllText(Path.Combine(Root,"stop"),"failure"); }
    }

}
