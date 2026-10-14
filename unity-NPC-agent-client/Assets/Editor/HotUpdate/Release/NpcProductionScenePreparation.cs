using System;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Explicit author repair, never invoked implicitly by the production gate.
public static class NpcProductionScenePreparation
{
    public static void RunFromCommandLine() => HotUpdateEditorCommand.Run(() =>
    {
        foreach (string path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Content/Scenes/WarehouseRemote.unity" })
        {
            var scene = EditorSceneManager.OpenScene(path);
            var surface = UnityEngine.Object.FindFirstObjectByType<NavMeshSurface>();
            if (surface == null || surface.navMeshData == null) throw new InvalidDataException("Scene NavMesh source is missing.");
            surface.transform.parent.position = new Vector3(0, 0, -4.66f);
            surface.transform.parent.localScale = new Vector3(1, 1, 2);
            surface.buildHeightMesh = true; surface.overrideVoxelSize = true; surface.voxelSize = 0.02f;
            var original = surface.navMeshData;
            surface.BuildNavMesh();
            var generated = surface.navMeshData;
            surface.RemoveData();
            // Keep the existing native asset and GUID so scene/Addressable references survive.
            EditorUtility.CopySerialized(generated, original);
            surface.navMeshData = original;
            surface.AddData();
            UnityEngine.Object.DestroyImmediate(generated);
            if (!NavMesh.SamplePosition(Vector3.zero, out var hit, 0.05f, NavMesh.AllAreas) || Vector3.Distance(hit.position, Vector3.zero) > 0.01f)
                throw new InvalidDataException("NPC scene origin is not on NavMesh.");
            foreach (var npc in UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None))
                npc.GetComponent<NavMeshAgent>().enabled = false;
            EditorUtility.SetDirty(original);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }
        Debug.Log("NPC_CURRENT_SCENES_PREPARED");
    });
}
