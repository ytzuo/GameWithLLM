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
    }

    [Test]
    public void CatalogRejectsUnknownDuplicateAndTraversalFields()
    {
        string valid = Fixture("npc", "index.json");
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"unknown\": true")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("npc/merchant_001/1/avatar.png", "npc/merchant_001/1/../../avatar.png")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("\"contentVersion\": \"1\"", "\"contentVersion\": \"1.1\"")));
        Assert.Throws<InvalidDataException>(() => RemoteNpcContract.ParseCatalog(
            valid.Replace("npc/merchant_001/1/avatar.png", "npc/merchant_001/1/nested/avatar.png")));
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
