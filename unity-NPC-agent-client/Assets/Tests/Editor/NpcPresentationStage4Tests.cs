using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class NpcPresentationStage4Tests
{
    private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../../NpcContent/npc"));
    private static Type Builder => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("NpcContentRelease"))
        .Single(t => t != null);
    private static void Build() => Builder.GetMethod("BuildDefinitions").Invoke(null, null);

    [Test]
    public void RebuildRemovesDeletedGeneratedNpc()
    {
        string obsolete = Path.Combine(Root, "Stage4DeletedNpc");
        Assert.That(Directory.Exists(obsolete), Is.False);
        Directory.CreateDirectory(obsolete);
        File.WriteAllText(Path.Combine(obsolete, "npc.json"), "{}");
        try
        {
            Build();
            Assert.That(Directory.Exists(obsolete), Is.False);
            CollectionAssert.AreEquivalent(new[] { "Merchant", "Guide" }, Directory.GetDirectories(Root).Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(obsolete)) Directory.Delete(obsolete, true); }
    }

    [Test]
    public void InvalidStagedManifestLeavesCompleteCurrentTree()
    {
        var before = Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        UnityEngine.Object definition = AssetDatabase.LoadMainAssetAtPath("Assets/Content/Npcs/Guide/Definition.asset");
        FieldInfo prompt = definition.GetType().GetField("systemPromptTemplate");
        object original = prompt.GetValue(definition);
        try
        {
            // Author validation succeeds, then staged contract validation fails.
            prompt.SetValue(definition, "{{unknown}}");
            var failure = Assert.Throws<TargetInvocationException>(Build);
            Assert.That(failure.InnerException, Is.InstanceOf<InvalidDataException>());
            CollectionAssert.AreEquivalent(before.Keys, Directory.GetFiles(Root, "*", SearchOption.AllDirectories));
            foreach (KeyValuePair<string, byte[]> file in before) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key));
            Assert.That(Directory.GetDirectories(Path.GetDirectoryName(Root), "npc-staging-*"), Is.Empty);
            Assert.That(Directory.GetDirectories(Path.GetDirectoryName(Root), "npc-backup-*"), Is.Empty);
        }
        finally { prompt.SetValue(definition, original); }
    }
}
