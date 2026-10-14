using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
public sealed class RemoteNpcRoundTwoTests
{
 private static string Fixture(params string[] parts) => File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath,"..","..","NpcContent",Path.Combine(parts))));
 [Test] public void CurrentCatalogAndBuiltinManifestsOwnCompleteDependencies()
 {
  var catalog=RemoteNpcContract.ParseCatalog(Fixture("npc","index.json")); Assert.AreEqual(2,catalog.Npcs.Count);
  foreach(var summary in catalog.Npcs)
  {
   var manifest=RemoteNpcContract.ParseManifest(Fixture("npc",summary.NpcId,"npc.json"),summary);
   Assert.True(manifest.UsesBuiltinAnimationDriver); Assert.AreEqual("npc/"+summary.NpcId+"/visual",manifest.PrefabAddress);
   CollectionAssert.AreEqual(new[]{manifest.PrefabAddress},manifest.DownloadAddresses);
   string prefix="Assets/Content/Npcs/"+summary.NpcId+"/";
   var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(prefix+"Visual.prefab");
   CharacterVisualController.ValidateVisualInstance(prefab,true);
   using(var driver=StandardLocomotionAnimationDriver.Create(manifest.BuiltinAnimationDriverId)) driver.Bind(prefab.GetComponentInChildren<Animator>(true));
   var dependencies=AssetDatabase.GetDependencies(prefix+"Visual.prefab",true);
   foreach(string path in new[]{"Animations/Controller.controller","Animations/Idle.anim","Materials/Body.mat","Models/Avatar.asset"}) CollectionAssert.Contains(dependencies,prefix+path);
  }
 }
 [Test] public void CurrentContractRejectsOldFieldsDuplicatesTraversalAndCaseCollisions()
 {
  string json=Fixture("npc","index.json"); var root=JObject.Parse(json);
  root["schemaVersion"]=1; Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ParseCatalog(root.ToString())); root.Remove("schemaVersion");
  var entries=(JArray)root["npcs"]; var duplicate=(JObject)entries[0].DeepClone(); duplicate["name"]=((string)duplicate["name"]).ToLowerInvariant(); entries.Add(duplicate);
  Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ParseCatalog(root.ToString()));
  Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ParseCatalog(json.Replace("npc/Merchant/avatar.png","npc/Merchant/../avatar.png")));
  Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ParseCatalog(json.Replace("\"texts\":", "\"texts\":{},\"texts\":")));
  foreach(string name in new[]{"CON","lpt1","../x","1Merchant","Merchant-1","Merchant\n"}) Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ValidateName(name));
  var manifest=JObject.Parse(Fixture("npc","Merchant","npc.json")); manifest["contentVersion"]="3";
  Assert.Throws<InvalidDataException>(()=>RemoteNpcContract.ParseManifest(manifest.ToString()));
 }
 [Test] public void DisplayNameDoesNotChangeIdentity()
 {
  var root=JObject.Parse(Fixture("npc","index.json")); string name=(string)root["npcs"][0]["name"]; root["npcs"][0]["displayName"]="新的展示名";
  var catalog=RemoteNpcContract.ParseCatalog(root.ToString()); Assert.AreEqual(name,catalog.Npcs[0].NpcId);
  Assert.DoesNotThrow(()=>RemoteNpcContract.ParseManifest(Fixture("npc",name,"npc.json"),catalog.Npcs[0]));
 }
 [Test] public void NpcLibraryUxmlSatisfiesRegisteredUiContract() => UiContentContractValidator.ValidateAsset(UiContentIds.NpcLibraryWindow,AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Art/UI/NpcLibraryWindow.uxml"));
 [Test] public void OldInstalledIndexRequiresReinstallWithoutDeletingIt()
 {
  string dir=Path.Combine(Path.GetTempPath(),"npc-index-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
  try
  {
   string old=Path.Combine(dir,"installed.json");File.WriteAllText(old,"[]");
   using(var provider=new ContentAssetProvider())
   using(var client=new RemoteNpcCatalogClient(new Uri("http://127.0.0.1/"),dir,TimeSpan.FromSeconds(1)))
   {
    var installer=new NpcContentInstaller(provider,client,dir);
    Assert.True(installer.RequiresReinstall);Assert.IsEmpty(installer.Installed);Assert.True(File.Exists(old));
    Assert.False(File.Exists(Path.Combine(dir,"installed-current.json")));
   }
  }
  finally{Directory.Delete(dir,true);}
 }
 [Test] public void AtomicWriteLeavesOneCommittedFile()
 {
  string dir=Path.Combine(Path.GetTempPath(),"npc-test-"+Guid.NewGuid().ToString("N"));
  try {string path=Path.Combine(dir,"installed-current.json");RemoteNpcCatalogClient.AtomicWrite(path,"[]");RemoteNpcCatalogClient.AtomicWrite(path,"[1]");Assert.AreEqual("[1]",File.ReadAllText(path));Assert.False(File.Exists(path+".tmp"));}
  finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
 }
}
