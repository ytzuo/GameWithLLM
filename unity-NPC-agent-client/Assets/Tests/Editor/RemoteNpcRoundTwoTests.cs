using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

public sealed class RemoteNpcRoundTwoTests
{
    private static string Fixture(params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "..", "NpcContent", Path.Combine(parts)));
        return File.ReadAllText(path);
    }

    [Test]
    public void RealCatalogAndManifestsPassStrictContract()
    {
        RemoteNpcCatalog catalog = RemoteNpcContract.ParseCatalog(Fixture("npc", "index.json"));
        Assert.AreEqual(2, catalog.Npcs.Count);
        foreach (RemoteNpcSummary summary in catalog.Npcs)
        {
            string json = Fixture("npc", summary.NpcId, summary.ContentVersion, "npc.json");
            RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(json, summary);
            Assert.AreEqual(summary.NpcId, manifest.NpcId);
            CollectionAssert.Contains(manifest.DownloadAddresses, manifest.PrefabAddress);
        }
        RemoteNpcManifest merchant = RemoteNpcContract.ParseManifest(
            Fixture("npc", "merchant_001", "3", "npc.json"));
        Assert.AreEqual(2, merchant.SchemaVersion);
        Assert.True(merchant.UsesBuiltinAnimationDriver);
        Assert.AreEqual("standard-locomotion", merchant.BuiltinAnimationDriverId);
        CollectionAssert.AreEqual(new[] { merchant.PrefabAddress }, merchant.DownloadAddresses);
        RemoteNpcManifest guide = RemoteNpcContract.ParseManifest(
            Fixture("npc", "guide_001", "2", "npc.json"));
        Assert.AreEqual(1, guide.SchemaVersion);
        Assert.False(guide.UsesBuiltinAnimationDriver);
        CollectionAssert.Contains(guide.DownloadAddresses, guide.AnimationScriptAddress);
    }

    [Test]
    public void VersionTwoRejectsLegacyVisualFieldsAndUnknownDrivers()
    {
        string valid = Fixture("npc", "merchant_001", "3", "npc.json");
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseManifest(
            valid.Replace(
                "\"prefabAddress\": \"npc/merchant_001/3/visual\"",
                "\"prefabAddress\": \"npc/merchant_001/3/visual\", \"materialAddresses\": []")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseManifest(
            valid.Replace("\"standard-locomotion\"", "\"unknown\"")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseManifest(
            valid.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3")));
    }

    [Test]
    public void VersionTwoPrefabOwnsItsCompleteTransitiveVisualGraph()
    {
        const string prefabPath = "Assets/Content/Npcs/merchant_001/3/visual.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(prefabPath);
        Assert.NotNull(prefab);
        Assert.DoesNotThrow(() => CharacterVisualController.ValidateVisualInstance(prefab, true));
        string[] dependencies = AssetDatabase.GetDependencies(prefabPath, true);
        CollectionAssert.Contains(dependencies, "Assets/Content/Npcs/merchant_001/3/controller.controller");
        CollectionAssert.Contains(dependencies, "Assets/Content/Npcs/merchant_001/3/idle.anim");
        CollectionAssert.Contains(dependencies, "Assets/Content/Npcs/merchant_001/3/material.mat");
        CollectionAssert.Contains(dependencies, "Assets/Content/Npcs/merchant_001/3/texture.asset");
        CollectionAssert.Contains(dependencies, "Assets/Content/Npcs/merchant_001/3/visual-avatar.asset");
    }

    [Test]
    public void CatalogRejectsUnknownDuplicateAndTraversalFields()
    {
        string valid = Fixture("npc", "index.json");
        RemoteNpcSummary merchant = RemoteNpcContract.ParseCatalog(valid).Npcs[0];
        string avatar = merchant.AvatarPath;
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"unknown\": true")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace(avatar, $"npc/{merchant.NpcId}/{merchant.ContentVersion}/../../avatar.png")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace($"\"contentVersion\": \"{merchant.ContentVersion}\"", "\"contentVersion\": \"1.1\"")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace(avatar, $"npc/{merchant.NpcId}/{merchant.ContentVersion}/nested/avatar.png")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace(
                "已下载 {downloaded} / {total} 字节",
                "已下载 {downloaded} 字节")));
    }

    [Test]
    public void ManifestRejectsIdentityAndHashShapeViolations()
    {
        RemoteNpcCatalog catalog = RemoteNpcContract.ParseCatalog(Fixture("npc", "index.json"));
        RemoteNpcSummary expected = catalog.Npcs[0];
        string manifest = Fixture("npc", expected.NpcId, expected.ContentVersion, "npc.json");
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseManifest(
            manifest.Replace($"\"npcId\": \"{expected.NpcId}\"", "\"npcId\": \"other_001\""), expected));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            Fixture("npc", "index.json").Replace(expected.ManifestSha256, "abc")));
    }

    [Test]
    public void NpcLibraryUxmlSatisfiesRegisteredUiContract()
    {
        VisualTreeAsset asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            "Assets/Art/UI/NpcLibraryWindow.uxml");
        Assert.NotNull(asset);
        UiContentContractValidator.ValidateAsset(UiContentIds.NpcLibraryWindow, asset);
    }

    [Test]
    public void AtomicWriteLeavesOneCommittedFileAndNoTempRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gamewithllm-npc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "installed.json");
            RemoteNpcCatalogClient.AtomicWrite(path, "[]");
            RemoteNpcCatalogClient.AtomicWrite(path, "[1]");
            Assert.AreEqual("[1]", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
