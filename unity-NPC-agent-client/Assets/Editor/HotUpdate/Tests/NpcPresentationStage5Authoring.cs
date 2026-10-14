using System;
using UnityEditor;
using UnityEngine;

// Explicit acceptance fixture: edits the one current author asset set, never
// participates in normal builds or stores historical NPC assets.
public static class NpcPresentationStage5Authoring
{
    public static void InitialFromCommandLine() => HotUpdateEditorCommand.Run(() => Configure(false));
    public static void UpdatedFromCommandLine() => HotUpdateEditorCommand.Run(() => Configure(true));

    private static void Configure(bool updated)
    {
        const string root = "Assets/Content/Npcs/Keeper/";
        var definition = AssetDatabase.LoadAssetAtPath<NpcContentDefinition>(root + "Definition.asset");
        if (definition == null || definition.name != "Keeper") throw new InvalidOperationException("Keeper author definition is required.");
        definition.displayName = updated ? "仓库巡查员" : "仓库守卫";
        definition.profile.identity = "负责仓库巡查和物品保管的守卫";
        definition.systemPromptTemplate = "你是一位负责仓库巡查和物品保管的守卫。\n" +
            "你是 {{displayName}}，实体标识 {{npcId}}。\n身份：{{identity}}\n性格：{{personality}}\n" +
            "说话方式：{{speakingStyle}}\n职责：{{responsibilities}}\n背景：{{worldKnowledge}}\n" +
            "禁止事项：{{forbiddenTopics}}\n实时状态和行为结果必须通过工具确认，默认使用简体中文简洁回复。";
        EditorUtility.SetDirty(definition);
        var prefab = PrefabUtility.LoadPrefabContents(root + "Visual.prefab");
        try
        {
            var primitive = GameObject.CreatePrimitive(updated ? PrimitiveType.Sphere : PrimitiveType.Cube);
            try { prefab.GetComponentInChildren<MeshFilter>().sharedMesh = primitive.GetComponent<MeshFilter>().sharedMesh; }
            finally { UnityEngine.Object.DestroyImmediate(primitive); }
            prefab.transform.Find("Body").localScale = updated ? new Vector3(0.85f, 1.6f, 0.85f) : new Vector3(0.7f, 1.5f, 0.7f);
            PrefabUtility.SaveAsPrefabAsset(prefab, root + "Visual.prefab");
        }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(root + "Animations/Idle.anim");
        clip.ClearCurves();
        float length = updated ? 1f : 0.5f;
        clip.SetCurve("Body", typeof(Transform), "localPosition.y", AnimationCurve.EaseInOut(0, 0, length, updated ? 0.16f : 0.04f));
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        EditorUtility.SetDirty(clip);
        var material = AssetDatabase.LoadAssetAtPath<Material>(root + "Materials/Body.mat");
        material.SetColor("_BaseColor", updated ? new Color(1f, 0.5f, 0.15f) : new Color(0.9f, 0.75f, 0.2f));
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        Debug.Log("NPC_STAGE5_AUTHOR_ASSETS_READY");
    }
}
