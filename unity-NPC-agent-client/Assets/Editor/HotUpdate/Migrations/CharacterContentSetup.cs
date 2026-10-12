using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWithLLM.AgentRuntime;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class CharacterContentSetup
{
    private const string GroupName = "Remote_Characters";
    private const string CharacterLabel = "content.characters";
    private const string CatalogLabel = "content.character-catalog";
    private const string CatalogPath = "Assets/Content/Characters/character-catalog.json";
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string CharacterRoot = "Assets/Art/Characters";

    private sealed class CharacterSpec
    {
        public string RootName;
        public string CharacterId;
        public string SourceMaterial;
        public Color TextureColor;

        public string Folder => $"{CharacterRoot}/{CharacterId}";
        public string PrefabPath => $"{Folder}/{CharacterId}-default.prefab";
        public string MaterialPath => $"{Folder}/{CharacterId}-default.mat";
        public string TexturePath => $"{Folder}/{CharacterId}-default-texture.asset";
        public string ControllerPath => $"{Folder}/{CharacterId}-default.controller";
        public string ClipPath => $"{Folder}/{CharacterId}-idle.anim";
        public string Prefix => $"character/{CharacterId}";
    }

    private static readonly CharacterSpec[] Characters =
    {
        new CharacterSpec
        {
            RootName = "Alice_001", CharacterId = "alice",
            SourceMaterial = "Assets/Art/Material/Red.mat", TextureColor = new Color(0.85f, 0.12f, 0.12f)
        },
        new CharacterSpec
        {
            RootName = "Ryan_001", CharacterId = "ryan",
            SourceMaterial = "Assets/Art/Material/Purple.mat", TextureColor = new Color(0.38f, 0.12f, 0.65f)
        },
        new CharacterSpec
        {
            RootName = "playerMock", CharacterId = "player",
            SourceMaterial = "Assets/Art/Material/White.mat", TextureColor = new Color(0.9f, 0.9f, 0.9f)
        }
    };

    public static void Configure()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>() ??
                                         throw new InvalidOperationException("Remote_Characters bundle schema is missing.");
        schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
        EditorUtility.SetDirty(schema);
        settings.AddLabel(CharacterLabel, false);
        settings.AddLabel(CatalogLabel, false);
        foreach (CharacterSpec character in Characters)
            settings.AddLabel(CharacterLabel + "." + character.CharacterId, false);

        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Art", "Characters"));
        foreach (CharacterSpec character in Characters)
        {
            Directory.CreateDirectory(Path.Combine(
                Application.dataPath,
                "Art",
                "Characters",
                character.CharacterId));
            CreateCharacterAssets(character);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        ConfigureEntry(settings, group, CatalogPath, CharacterContentIds.Catalog, CatalogLabel);
        foreach (CharacterSpec character in Characters)
        {
            string label = CharacterLabel + "." + character.CharacterId;
            ConfigureEntry(settings, group, character.PrefabPath,
                $"{character.Prefix}/visual/default", CharacterLabel, label);
            ConfigureEntry(settings, group, character.MaterialPath,
                $"{character.Prefix}/material/default-body", CharacterLabel, label);
            ConfigureEntry(settings, group, character.TexturePath,
                $"{character.Prefix}/texture/default-body", CharacterLabel, label);
            ConfigureEntry(settings, group, character.ControllerPath,
                $"{character.Prefix}/animator/default", CharacterLabel, label);
            ConfigureEntry(settings, group, character.ClipPath,
                $"{character.Prefix}/animation/idle", CharacterLabel, label);
        }

        MigrateSceneRoots();
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
        Debug.Log("[Content] Addressables character visuals and stable VisualRoot owners configured.");
    }

    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema == null || schema.BundleMode != BundledAssetGroupSchema.BundlePackingMode.PackSeparately)
            throw new InvalidDataException("Character entries must use PackSeparately.");

        VerifyEntry(settings, group, CatalogPath, CharacterContentIds.Catalog, CatalogLabel);
        TextAsset catalogAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(CatalogPath) ??
                                 throw new FileNotFoundException("Character Catalog is missing.");
        IReadOnlyDictionary<string, CharacterAppearanceDefinition> catalog =
            CharacterContentCatalog.Parse(catalogAsset.text);
        if (catalog.Count != Characters.Length)
            throw new InvalidDataException("Published character appearance baseline changed.");

        foreach (CharacterSpec character in Characters)
        {
            string label = CharacterLabel + "." + character.CharacterId;
            VerifyEntry(settings, group, character.PrefabPath,
                $"{character.Prefix}/visual/default", CharacterLabel, label);
            VerifyEntry(settings, group, character.MaterialPath,
                $"{character.Prefix}/material/default-body", CharacterLabel, label);
            VerifyEntry(settings, group, character.TexturePath,
                $"{character.Prefix}/texture/default-body", CharacterLabel, label);
            VerifyEntry(settings, group, character.ControllerPath,
                $"{character.Prefix}/animator/default", CharacterLabel, label);
            VerifyEntry(settings, group, character.ClipPath,
                $"{character.Prefix}/animation/idle", CharacterLabel, label);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(character.PrefabPath) ??
                                throw new FileNotFoundException($"Visual Prefab is missing: {character.PrefabPath}");
            CharacterVisualController.ValidateVisualInstance(prefab);
            if (prefab.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidDataException($"Visual Prefab '{character.CharacterId}' owns a Collider.");
            Animator animator = prefab.GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
                throw new InvalidDataException($"Visual Prefab '{character.CharacterId}' has no Animator Controller.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(character.MaterialPath);
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(character.TexturePath);
            if (material == null || texture == null || material.mainTexture != texture ||
                material.shader == null || material.shader.name != "Universal Render Pipeline/Lit")
                throw new InvalidDataException(
                    $"Character '{character.CharacterId}' material, texture, or URP shader contract is invalid.");

            string otherCharacterDependency = AssetDatabase.GetDependencies(character.PrefabPath, true)
                .FirstOrDefault(path => Characters.Any(other =>
                    other != character && path.StartsWith(other.Folder + "/", StringComparison.Ordinal)));
            if (otherCharacterDependency != null)
                throw new InvalidDataException(
                    $"Character '{character.CharacterId}' depends on another character asset '{otherCharacterDependency}'.");
        }

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (CharacterSpec character in Characters)
            VerifySceneRoot(scene, character);
        var remotePaths = new HashSet<string>(
            Characters.SelectMany(character => new[]
            {
                character.PrefabPath, character.MaterialPath, character.TexturePath,
                character.ControllerPath, character.ClipPath
            }).Append(CatalogPath),
            StringComparer.Ordinal);
        string hardReference = AssetDatabase.GetDependencies(ScenePath, true).FirstOrDefault(remotePaths.Contains);
        if (hardReference != null)
            throw new InvalidDataException($"SampleScene still hard-references remote character asset '{hardReference}'.");

        if (typeof(IAgentEntity).IsAssignableFrom(typeof(CharacterVisualController)) ||
            typeof(IAgentTool).IsAssignableFrom(typeof(CharacterVisualController)))
            throw new InvalidDataException("CharacterVisualController crossed the Entity/Tool authority boundary.");
        Debug.Log(
            "[Content] Addressables characters verified: stable IDs, VisualRoot ownership, fallback, " +
            "per-character bundles, URP materials, animation and authority boundary.");
    }

    private static void CreateCharacterAssets(CharacterSpec character)
    {
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(character.TexturePath);
        if (texture == null)
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = character.CharacterId + "-default-texture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            Color accent = Color.Lerp(character.TextureColor, Color.white, 0.2f);
            texture.SetPixels(new[] { character.TextureColor, accent, accent, character.TextureColor });
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, character.TexturePath);
        }

        Material material = AssetDatabase.LoadAssetAtPath<Material>(character.MaterialPath);
        if (material == null)
        {
            Material source = AssetDatabase.LoadAssetAtPath<Material>(character.SourceMaterial);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                            throw new InvalidOperationException("URP Lit shader is unavailable.");
            material = source == null ? new Material(shader) : new Material(source);
            material.shader = shader;
            material.name = character.CharacterId + "-default";
            AssetDatabase.CreateAsset(material, character.MaterialPath);
        }
        material.mainTexture = texture;
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        EditorUtility.SetDirty(material);

        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(character.ClipPath);
        if (clip == null)
        {
            clip = new AnimationClip { name = character.CharacterId + "-idle", frameRate = 30 };
            AssetDatabase.CreateAsset(clip, character.ClipPath);
        }
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(character.ControllerPath);
        if (controller == null)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(character.ControllerPath);
            AnimatorState state = controller.layers[0].stateMachine.AddState("Idle");
            state.motion = clip;
            controller.layers[0].stateMachine.defaultState = state;
        }

        var root = new GameObject("RemoteVisual");
        try
        {
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());
            body.GetComponent<MeshRenderer>().sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(root, character.PrefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void MigrateSceneRoots()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (CharacterSpec character in Characters)
        {
            GameObject root = scene.GetRootGameObjects().Single(item => item.name == character.RootName);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            Transform visualRoot = root.transform.Find("VisualRoot");
            if (visualRoot == null)
            {
                var visualRootObject = new GameObject("VisualRoot");
                visualRoot = visualRootObject.transform;
                visualRoot.SetParent(root.transform, false);
            }
            Transform fallback = visualRoot.Find("FallbackVisual");
            if (fallback == null)
            {
                var fallbackObject = new GameObject("FallbackVisual");
                fallback = fallbackObject.transform;
                fallback.SetParent(visualRoot, false);
            }

            MeshFilter rootFilter = root.GetComponent<MeshFilter>();
            MeshRenderer rootRenderer = root.GetComponent<MeshRenderer>();
            MeshFilter fallbackFilter = fallback.GetComponent<MeshFilter>();
            if (fallbackFilter == null)
                fallbackFilter = fallback.gameObject.AddComponent<MeshFilter>();
            MeshRenderer fallbackRenderer = fallback.GetComponent<MeshRenderer>();
            if (fallbackRenderer == null)
                fallbackRenderer = fallback.gameObject.AddComponent<MeshRenderer>();
            if (rootFilter != null)
                fallbackFilter.sharedMesh = rootFilter.sharedMesh;
            if (rootRenderer != null)
                fallbackRenderer.sharedMaterials = rootRenderer.sharedMaterials;
            if (fallbackFilter.sharedMesh == null)
                fallbackFilter.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (fallbackRenderer.sharedMaterial == null)
                fallbackRenderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(character.SourceMaterial);
            if (rootRenderer != null) UnityEngine.Object.DestroyImmediate(rootRenderer);
            if (rootFilter != null) UnityEngine.Object.DestroyImmediate(rootFilter);

            CharacterVisualController controller = root.GetComponent<CharacterVisualController>();
            if (controller == null)
                controller = root.AddComponent<CharacterVisualController>();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("characterId").stringValue = character.CharacterId;
            serialized.FindProperty("appearanceId").stringValue = "default";
            serialized.FindProperty("visualRoot").objectReferenceValue = visualRoot;
            serialized.FindProperty("fallbackVisual").objectReferenceValue = fallback.gameObject;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(root);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void VerifySceneRoot(Scene scene, CharacterSpec character)
    {
        GameObject root = scene.GetRootGameObjects().Single(item => item.name == character.RootName);
        CharacterVisualController controller = root.GetComponent<CharacterVisualController>() ??
                                               throw new InvalidDataException(
                                                   $"'{character.RootName}' has no CharacterVisualController.");
        if (controller.CharacterId != character.CharacterId || controller.AppearanceId != "default" ||
            controller.VisualRoot == null || controller.VisualRoot.parent != root.transform ||
            controller.VisualRoot.name != "VisualRoot" || controller.FallbackVisual == null ||
            controller.FallbackVisual.transform.parent != controller.VisualRoot ||
            !controller.FallbackVisual.activeSelf || root.GetComponent<MeshRenderer>() != null ||
            root.GetComponent<MeshFilter>() != null)
            throw new InvalidDataException($"'{character.RootName}' VisualRoot/fallback contract is invalid.");
        if (controller.FallbackVisual.GetComponent<MeshRenderer>() == null ||
            controller.FallbackVisual.GetComponent<MeshFilter>() == null)
            throw new InvalidDataException($"'{character.RootName}' local fallback is incomplete.");
        if (character.CharacterId == "player")
        {
            if (root.GetComponent("PlayerMock") == null || root.GetComponent<InventoryComponent>() == null)
                throw new InvalidDataException("Player AOT authority components changed during character migration.");
        }
        else if (root.GetComponent<NpcEntity>() == null || root.GetComponent<InventoryComponent>() == null ||
                 root.GetComponent<UnityEngine.AI.NavMeshAgent>() == null)
            throw new InvalidDataException(
                $"NPC '{character.RootName}' AOT authority components changed during character migration.");
    }

    private static void ConfigureEntry(
        AddressableAssetSettings settings,
        AddressableAssetGroup group,
        string path,
        string address,
        params string[] labels)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrWhiteSpace(guid))
            throw new FileNotFoundException($"Character asset is not imported: '{path}'.");
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = address;
        foreach (string label in labels)
            entry.SetLabel(label, true, false, false);
    }

    private static void VerifyEntry(
        AddressableAssetSettings settings,
        AddressableAssetGroup group,
        string path,
        string address,
        params string[] labels)
    {
        AddressableAssetEntry entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
        if (entry == null || entry.parentGroup != group || entry.address != address ||
            labels.Any(label => !entry.labels.Contains(label)))
            throw new InvalidDataException($"Character Addressable entry '{address}' is invalid.");
    }

}
