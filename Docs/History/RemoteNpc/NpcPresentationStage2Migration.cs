using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
public static class NpcPresentationStage2Migration
{
 public static void PrepareSampleScene()
 {
  var scene=UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
  var surface=UnityEngine.Object.FindFirstObjectByType<Unity.AI.Navigation.NavMeshSurface>();
  var plane=surface.transform.parent;
  plane.position=new Vector3(0,0,-4.66f); plane.localScale=new Vector3(1,1,2);
  surface.buildHeightMesh=true; surface.overrideVoxelSize=true; surface.voxelSize=0.02f;
  var original=surface.navMeshData;
  surface.BuildNavMesh();
  var candidate=surface.navMeshData;
  surface.RemoveData();
  string path=AssetDatabase.GetAssetPath(original);
  AssetDatabase.CreateAsset(candidate,path); surface.navMeshData=candidate; surface.AddData();
  if(!UnityEngine.AI.NavMesh.SamplePosition(Vector3.zero,out var hit,0.05f,UnityEngine.AI.NavMesh.AllAreas) || Vector3.Distance(hit.position,Vector3.zero)>0.01f)
   throw new InvalidDataException("NPC scene origin is not on NavMesh: " + hit.position);
  UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
  UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
  Debug.Log("NPC_STAGE2_SAMPLE_SCENE_READY");
 }
 public static void Run()
 {
  var settings = AddressableAssetSettingsDefaultObject.Settings;
  foreach (var entry in settings.groups.Where(g=>g!=null).SelectMany(g=>g.entries).Where(e=>e.address.StartsWith("npc/merchant_001/", StringComparison.Ordinal) || e.address.StartsWith("npc/guide_001/", StringComparison.Ordinal)).ToArray()) settings.RemoveAssetEntry(entry.guid);
  foreach (string label in settings.GetLabels().Where(l=>l=="content.npc.merchant_001" || l=="content.npc.guide_001").ToArray()) settings.RemoveLabel(label);
  Move("Assets/Content/Npcs/merchant_001/3", "Assets/Content/Npcs/Merchant");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Merchant/Animations")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Merchant", "Animations");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Merchant/Materials")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Merchant", "Materials");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Merchant/Models")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Merchant", "Models");
  Move("Assets/Content/Npcs/Merchant/avatar.png", "Assets/Content/Npcs/Merchant/Avatar.png");
  Move("Assets/Content/Npcs/Merchant/controller.controller", "Assets/Content/Npcs/Merchant/Animations/Controller.controller");
  Move("Assets/Content/Npcs/Merchant/idle.anim", "Assets/Content/Npcs/Merchant/Animations/Idle.anim");
  Move("Assets/Content/Npcs/Merchant/material.mat", "Assets/Content/Npcs/Merchant/Materials/Body.mat");
  Move("Assets/Content/Npcs/Merchant/merchant_001_v3.asset", "Assets/Content/Npcs/Merchant/Definition.asset");
  Move("Assets/Content/Npcs/Merchant/texture.asset", "Assets/Content/Npcs/Merchant/Materials/Texture.asset");
  Move("Assets/Content/Npcs/Merchant/visual-avatar.asset", "Assets/Content/Npcs/Merchant/Models/Avatar.asset");
  Move("Assets/Content/Npcs/Merchant/visual.prefab", "Assets/Content/Npcs/Merchant/Visual.prefab");
  AssetDatabase.DeleteAsset("Assets/Content/Npcs/merchant_001");
  Move("Assets/Content/Npcs/guide_001/3", "Assets/Content/Npcs/Guide");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Guide/Animations")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Guide", "Animations");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Guide/Materials")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Guide", "Materials");
  if (!AssetDatabase.IsValidFolder("Assets/Content/Npcs/Guide/Models")) AssetDatabase.CreateFolder("Assets/Content/Npcs/Guide", "Models");
  Move("Assets/Content/Npcs/Guide/avatar.png", "Assets/Content/Npcs/Guide/Avatar.png");
  Move("Assets/Content/Npcs/Guide/controller.controller", "Assets/Content/Npcs/Guide/Animations/Controller.controller");
  Move("Assets/Content/Npcs/Guide/guide_001_v3.asset", "Assets/Content/Npcs/Guide/Definition.asset");
  Move("Assets/Content/Npcs/Guide/idle.anim", "Assets/Content/Npcs/Guide/Animations/Idle.anim");
  Move("Assets/Content/Npcs/Guide/material.mat", "Assets/Content/Npcs/Guide/Materials/Body.mat");
  Move("Assets/Content/Npcs/Guide/texture.asset", "Assets/Content/Npcs/Guide/Materials/Texture.asset");
  Move("Assets/Content/Npcs/Guide/visual-avatar.asset", "Assets/Content/Npcs/Guide/Models/Avatar.asset");
  Move("Assets/Content/Npcs/Guide/visual.prefab", "Assets/Content/Npcs/Guide/Visual.prefab");
  AssetDatabase.DeleteAsset("Assets/Content/Npcs/guide_001");
  AssetDatabase.DeleteAsset("Assets/HotUpdate/NpcAnimation/Merchant");
  AssetDatabase.DeleteAsset("Assets/HotUpdate/NpcAnimation/MerchantV2");
  AssetDatabase.DeleteAsset("Assets/HotUpdate/NpcAnimation/Guide");
  AssetDatabase.DeleteAsset("Assets/HotUpdate/NpcAnimation/GuideV2");
  foreach (string name in new[]{"Merchant","Guide"})
  {
   var d = AssetDatabase.LoadAssetAtPath<NpcContentDefinition>($"Assets/Content/Npcs/{name}/Definition.asset");
   d.name = name; ((UnityEngine.Object)d).name = name;
   EditorUtility.SetDirty(d);
  }
  AssetDatabase.SaveAssets();
  string indexPath = Path.Combine(NpcContentRelease.StaticRoot,"npc","index.json");
  var index = JObject.Parse(File.ReadAllText(indexPath));
  index["texts"]["errorResource"] = "NPC 资源缺失或无法加载";
  ((JObject)index["texts"]).Remove("errorCompatibility");
  index["texts"]["restartRequired"] = "资源尚未就绪，请重启后重试";
  File.WriteAllText(indexPath,index.ToString());
  NpcContentRelease.BuildDefinitions();
  Debug.Log("NPC_STAGE2_MIGRATION_SUCCESS");
 }
 private static void Move(string source, string target)
 {
  string existing = AssetDatabase.GUIDToAssetPath(AssetDatabase.AssetPathToGUID(target));
  if (existing == target) return;
  if (source.Equals(target,StringComparison.OrdinalIgnoreCase))
  {
   string temp = Path.GetDirectoryName(source).Replace("\\", "/") + "/Moving" + Path.GetExtension(source);
   string first = AssetDatabase.MoveAsset(source,temp);
   if (!string.IsNullOrEmpty(first)) throw new InvalidDataException(first);
   source = temp;
  }
  string error=AssetDatabase.MoveAsset(source,target);
  if (!string.IsNullOrEmpty(error)) throw new InvalidDataException(error);
 }
}
