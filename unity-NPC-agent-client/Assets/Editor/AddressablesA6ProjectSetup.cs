using System;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public static class AddressablesA6ProjectSetup
{
    private const string GroupName = "Remote_Scenes";
    private const string SceneLabel = "content.scenes";
    private const string WarehouseLabel = "content.scenes.warehouse";
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string BootstrapScenePath = "Assets/Scenes/BootstrapScene.unity";
    private const string RemoteScenePath = "Assets/Content/Scenes/WarehouseRemote.unity";
    private const string RemoteNavMeshPath = "Assets/Content/Scenes/WarehouseRemote-NavMesh.asset";
    private const string RemoteMaterialRoot = "Assets/Content/Scenes/WarehouseRemoteAssets/Materials";

    [MenuItem("GameWithLLM/Hot Update/Configure Addressables A6 Scenes")]
    public static void Configure()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>() ??
                                         throw new InvalidOperationException("Remote_Scenes bundle schema is missing.");
        schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
        EditorUtility.SetDirty(schema);
        settings.AddLabel(SceneLabel, false);
        settings.AddLabel(WarehouseLabel, false);

        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Content", "Scenes"));
        CreateRemoteWarehouseScene();
        CreateBootstrapScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string guid = AssetDatabase.AssetPathToGUID(RemoteScenePath);
        if (string.IsNullOrWhiteSpace(guid))
            throw new FileNotFoundException("A6 remote warehouse scene was not imported.");
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = RemoteSceneCoordinator.WarehouseAddress;
        entry.SetLabel(SceneLabel, true, false, false);
        entry.SetLabel(WarehouseLabel, true, false, false);

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(BootstrapScenePath, true),
            new EditorBuildSettingsScene(SampleScenePath, true)
        };
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
        Debug.Log("[Content] Addressables A6 Bootstrap and remote warehouse scenes configured.");
    }

    [MenuItem("GameWithLLM/Hot Update/Verify Addressables A6 Scenes")]
    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException("Remote_Scenes group is missing.");
        BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema == null || schema.BundleMode != BundledAssetGroupSchema.BundlePackingMode.PackTogether)
            throw new InvalidDataException("A6 scene-owned dependencies must share the scene bundle lifecycle.");

        AddressableAssetEntry entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(RemoteScenePath));
        if (entry == null || entry.parentGroup != group ||
            entry.address != RemoteSceneCoordinator.WarehouseAddress ||
            !entry.labels.Contains(SceneLabel) || !entry.labels.Contains(WarehouseLabel))
            throw new InvalidDataException("A6 remote warehouse Addressable entry is invalid.");
        AddressableAssetEntry sampleEntry = settings.FindAssetEntry(
            AssetDatabase.AssetPathToGUID(SampleScenePath));
        if (sampleEntry != null && sampleEntry.parentGroup == group)
            throw new InvalidDataException("SampleScene must not be assigned to Remote_Scenes.");

        EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
        if (buildScenes.Length < 2 || !buildScenes[0].enabled ||
            buildScenes[0].path != BootstrapScenePath ||
            !buildScenes.Any(item => item.enabled && item.path == SampleScenePath) ||
            buildScenes.Any(item => item.path == RemoteScenePath))
            throw new InvalidDataException(
                "Build Settings must start with local Bootstrap, retain local SampleScene, " +
                "and exclude the remote scene.");

        Scene bootstrap = EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
        AgentHostClient host = FindInScene<AgentHostClient>(bootstrap).SingleOrDefault() ??
                               throw new InvalidDataException("Bootstrap Scene requires one AgentHostClient.");
        if (host.GetComponent<RemoteSceneCoordinator>() == null ||
            FindInScene<UIManager>(bootstrap).Count() != 1 ||
            FindInScene<PlayerMock>(bootstrap).Any() || FindInScene<NpcEntity>(bootstrap).Any())
            throw new InvalidDataException("Bootstrap persistent/world ownership boundary is invalid.");

        Scene remote = EditorSceneManager.OpenScene(RemoteScenePath, OpenSceneMode.Single);
        RemoteSceneCoordinator.ValidateRemoteSceneBoundary(remote);
        if (FindInScene<PlayerMock>(remote).Count() != 1 || FindInScene<NpcEntity>(remote).Count() < 1)
            throw new InvalidDataException("Remote warehouse must contain the authoritative demo world.");
        NavMeshSurface surface = FindInScene<NavMeshSurface>(remote).SingleOrDefault() ??
                                 throw new InvalidDataException("Remote warehouse has no NavMeshSurface.");
        if (surface.navMeshData == null || AssetDatabase.GetAssetPath(surface.navMeshData) != RemoteNavMeshPath)
            throw new InvalidDataException("Remote warehouse NavMeshData is not scene-owned.");
        string foreignSceneMaterial = AssetDatabase.GetDependencies(RemoteScenePath, true)
            .FirstOrDefault(path => path.StartsWith("Assets/Art/Material/", StringComparison.Ordinal));
        if (foreignSceneMaterial != null)
            throw new InvalidDataException(
                $"Remote warehouse still references local SampleScene material '{foreignSceneMaterial}'.");
        foreach (GameObject root in remote.GetRootGameObjects())
            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(root) != 0)
                throw new InvalidDataException($"Remote warehouse root '{root.name}' has a missing script.");

        string coreAssembly = typeof(RemoteSceneCoordinator).Assembly.GetName().Name;
        if (coreAssembly != "GameWithLLM.Client.Core")
            throw new InvalidDataException("Remote scene coordinator must be present in the AOT Core assembly.");
        Debug.Log(
            "[Content] Addressables A6 verified: Bootstrap ownership, stable scene address, " +
            "AOT types, NavMesh lifecycle, no persistent duplicates and paired scene lease API.");
    }

    public static void ConfigureFromCommandLine() => RunCommand(Configure);
    public static void VerifyFromCommandLine() => RunCommand(Verify);
    public static void BuildLocalDevelopmentFromCommandLine() => RunCommand(() =>
    {
        Verify();
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        string originalProfile = settings.activeProfileId;
        try
        {
            settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new InvalidOperationException("A6 Addressables build failed: " + result.Error);
        }
        finally
        {
            settings.activeProfileId = originalProfile;
            AssetDatabase.SaveAssets();
        }
    });

    public static void BuildWindowsPlayerFromCommandLine() => RunCommand(() =>
    {
        Verify();
        string output = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "Builds", "AddressablesA6", "GameWithLLM.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { BootstrapScenePath, SampleScenePath },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("A6 Windows Player build failed: " + report.summary.result);
    });

    private static void CreateRemoteWarehouseScene()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(RemoteScenePath) == null &&
            !AssetDatabase.CopyAsset(SampleScenePath, RemoteScenePath))
            throw new IOException("Unable to clone SampleScene for the A6 remote warehouse test scene.");
        AssetDatabase.ImportAsset(RemoteScenePath, ImportAssetOptions.ForceUpdate);
        Scene scene = EditorSceneManager.OpenScene(RemoteScenePath, OpenSceneMode.Single);
        foreach (AgentHostClient host in FindInScene<AgentHostClient>(scene).ToArray())
            UnityEngine.Object.DestroyImmediate(host.gameObject);
        foreach (UIManager ui in FindInScene<UIManager>(scene).ToArray())
            UnityEngine.Object.DestroyImmediate(ui.gameObject);

        NavMeshSurface surface = FindInScene<NavMeshSurface>(scene).Single();
        NavMeshData source = surface.navMeshData;
        if (AssetDatabase.LoadAssetAtPath<NavMeshData>(RemoteNavMeshPath) == null)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath) || !AssetDatabase.CopyAsset(sourcePath, RemoteNavMeshPath))
                throw new IOException("Unable to create scene-owned A6 NavMeshData.");
            AssetDatabase.ImportAsset(RemoteNavMeshPath, ImportAssetOptions.ForceUpdate);
        }
        surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(RemoteNavMeshPath) ??
                              throw new FileNotFoundException("A6 scene-owned NavMeshData is missing.");
        EditorUtility.SetDirty(surface);
        MoveSceneMaterialsToRemoteOwnership(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void MoveSceneMaterialsToRemoteOwnership(Scene scene)
    {
        Directory.CreateDirectory(Path.Combine(
            Application.dataPath,
            "Content", "Scenes", "WarehouseRemoteAssets", "Materials"));
        AssetDatabase.Refresh();
        foreach (Renderer renderer in FindInScene<Renderer>(scene))
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int index = 0; index < materials.Length; index++)
            {
                Material source = materials[index];
                string sourcePath = AssetDatabase.GetAssetPath(source);
                if (source == null || !sourcePath.StartsWith("Assets/Art/Material/", StringComparison.Ordinal))
                    continue;
                string guid = AssetDatabase.AssetPathToGUID(sourcePath);
                string targetPath = $"{RemoteMaterialRoot}/" +
                                    $"{Path.GetFileNameWithoutExtension(sourcePath)}-{guid.Substring(0, 8)}.mat";
                Material owned = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (owned == null)
                {
                    if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
                        throw new IOException($"Unable to copy scene material '{sourcePath}'.");
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    owned = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                }
                materials[index] = owned ??
                                   throw new FileNotFoundException($"A6 scene material is missing: '{targetPath}'.");
                changed = true;
            }
            if (changed)
            {
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
        }
    }

    private static void CreateBootstrapScene()
    {
        Scene source = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        GameObject hostSource = FindInScene<AgentHostClient>(source).Single().gameObject;
        GameObject uiSource = FindInScene<UIManager>(source).Single().gameObject;
        Scene bootstrap = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        bootstrap.name = "BootstrapScene";
        GameObject host = UnityEngine.Object.Instantiate(hostSource);
        host.name = "AgentRuntimeHost";
        SceneManager.MoveGameObjectToScene(host, bootstrap);
        if (host.GetComponent<RemoteSceneCoordinator>() == null)
            host.AddComponent<RemoteSceneCoordinator>();
        GameObject ui = UnityEngine.Object.Instantiate(uiSource);
        ui.name = "PersistentUI";
        SceneManager.MoveGameObjectToScene(ui, bootstrap);
        EditorSceneManager.SaveScene(bootstrap, BootstrapScenePath);
        EditorSceneManager.CloseScene(source, true);
    }

    private static T[] FindInScene<T>(Scene scene) where T : Component =>
        scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true))
            .ToArray();

    private static void RunCommand(Action action)
    {
        try { action(); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
}
