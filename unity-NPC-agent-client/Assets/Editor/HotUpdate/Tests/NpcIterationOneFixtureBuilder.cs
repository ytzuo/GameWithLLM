using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// One-time fixture migration for the vertical slice. Production publishing is
// exclusively definition-driven in NpcContentRelease.
public static class NpcIterationOneFixtureBuilder
{
    public static void CreateMerchantV2Fixture()
    {
        const string sourceRoot = "Assets/Content/Npcs/merchant_001/2";
        const string targetRoot = "Assets/Content/Npcs/merchant_001/3";
        Directory.CreateDirectory(targetRoot);
        Copy(sourceRoot + "/visual.prefab", targetRoot + "/visual.prefab");
        Copy(sourceRoot + "/controller.controller", targetRoot + "/controller.controller");
        Copy(sourceRoot + "/idle.anim", targetRoot + "/idle.anim");
        Copy(sourceRoot + "/material.mat", targetRoot + "/material.mat");
        Copy(sourceRoot + "/texture.asset", targetRoot + "/texture.asset");
        File.Copy(
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "NpcContent", "npc", "merchant_001", "2", "avatar.png")),
            targetRoot + "/avatar.png", true);
        AssetDatabase.Refresh();

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
            targetRoot + "/controller.controller");
        EnsureParameter(controller, "Speed", AnimatorControllerParameterType.Float);
        EnsureParameter(controller, "Moving", AnimatorControllerParameterType.Bool);
        EnsureParameter(controller, "Thinking", AnimatorControllerParameterType.Bool);
        EnsureParameter(controller, "Speaking", AnimatorControllerParameterType.Bool);
        controller.layers[0].stateMachine.defaultState.motion =
            AssetDatabase.LoadAssetAtPath<AnimationClip>(targetRoot + "/idle.anim");
        Material material = AssetDatabase.LoadAssetAtPath<Material>(targetRoot + "/material.mat");
        Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(targetRoot + "/texture.asset");
        foreach (string property in material.GetTexturePropertyNames())
            if (material.GetTexture(property) != null) material.SetTexture(property, texture);

        string avatarPath = targetRoot + "/visual-avatar.asset";
        Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
        if (avatar == null)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(targetRoot + "/visual.prefab");
            avatar = AvatarBuilder.BuildGenericAvatar(source, string.Empty);
            avatar.name = "merchant_001_visual_avatar";
            AssetDatabase.CreateAsset(avatar, avatarPath);
        }

        string prefabPath = targetRoot + "/visual.prefab";
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            Animator animator = contents.GetComponentInChildren<Animator>(true) ??
                                throw new InvalidDataException("Merchant fixture has no Animator.");
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }

        string sourceManifest = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "..", "NpcContent", "npc", "merchant_001", "2", "npc.json"));
        JObject manifest = JObject.Parse(File.ReadAllText(sourceManifest));
        JObject profile = (JObject)manifest["profile"];
        JObject prompt = (JObject)manifest["systemPrompt"];
        const string definitionPath = targetRoot + "/merchant_001_v3.asset";
        NpcContentDefinition definition = AssetDatabase.LoadAssetAtPath<NpcContentDefinition>(definitionPath);
        if (definition == null)
        {
            definition = ScriptableObject.CreateInstance<NpcContentDefinition>();
            AssetDatabase.CreateAsset(definition, definitionPath);
        }
        definition.npcId = "merchant_001";
        definition.contentVersion = "3";
        definition.displayName = "旅行商人";
        definition.description = "旅行商人，可以交流并使用现有游戏工具。";
        definition.minPlayerVersion = Application.version;
        definition.maxPlayerVersion = Application.version;
        definition.visualPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        definition.avatar = AssetDatabase.LoadAssetAtPath<Texture2D>(targetRoot + "/avatar.png");
        definition.animationDriverKind = NpcAnimationDriverKind.Builtin;
        definition.builtinDriverId = StandardLocomotionAnimationDriver.DriverId;
        definition.profile.displayName = (string)profile["displayName"];
        definition.profile.personality = profile["personality"].Values<string>().ToArray();
        definition.profile.speakingStyle = (string)profile["speakingStyle"];
        definition.profile.identity = (string)profile["identity"];
        definition.profile.responsibilities = profile["responsibilities"].Values<string>().ToArray();
        definition.profile.worldKnowledge = profile["worldKnowledge"].Values<string>().ToArray();
        definition.profile.forbiddenTopics = profile["forbiddenTopics"].Values<string>().ToArray();
        definition.systemPromptTemplate = (string)prompt["template"];
        EditorUtility.SetDirty(definition);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        NpcContentRelease.BuildDefinitions();
    }

    private static void Copy(string source, string target)
    {
        if (!File.Exists(target) && !AssetDatabase.CopyAsset(source, target))
            throw new IOException("Could not copy NPC fixture asset: " + source);
    }

    private static void EnsureParameter(
        AnimatorController controller,
        string name,
        AnimatorControllerParameterType type)
    {
        AnimatorControllerParameter existing = controller.parameters.FirstOrDefault(
            value => value.name == name);
        if (existing == null) controller.AddParameter(name, type);
        else if (existing.type != type)
            throw new InvalidDataException($"Animator parameter '{name}' has the wrong type.");
    }
}
