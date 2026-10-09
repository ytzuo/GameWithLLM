using System;
using System.IO;
using System.Linq;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

// Explicit sample staging, then read-only validation from the existing release pipeline.
public static class NpcContentRelease
{
    public static string StaticRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "NpcContent"));
    public static void BuildSamplesFromCommandLine() => HotUpdateEditorCommand.Run(BuildSamples);
    public static void ValidateFromCommandLine() => HotUpdateEditorCommand.Run(Validate);
    public static void BuildLocalContentFromCommandLine() => HotUpdateEditorCommand.Run(() =>
    {
        BuildSamples();
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        using (new AddressablesProfileScope(settings, "LocalDevelopment"))
        {
            AddressableAssetSettings.BuildPlayerContent(out UnityEditor.AddressableAssets.Build.AddressablesPlayerBuildResult result);
            if (!string.IsNullOrEmpty(result.Error)) throw new InvalidDataException(result.Error);
        }
        Debug.Log("NPC_LOCAL_CONTENT_BUILD_SUCCESS");
    });

    [MenuItem("GameWithLLM/Content/Build NPC Samples")]
    public static void BuildSamples()
    {
        HybridClrProjectSetup.Configure();
        CompileDllCommand.CompileDll(BuildTarget.StandaloneWindows64, false);
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var group = settings.FindGroup("Remote_Characters");
        JObject index = Read(Path.Combine(StaticRoot, "npc", "index.json"));
        const string contentVersion = "2";
        string[] ids = { "merchant_001", "guide_001" };
        string[] characters = { "ryan", "alice" };
        string[] animationAssemblies =
        {
            "GameWithLLM.NpcAnimation.Merchant_001.V2",
            "GameWithLLM.NpcAnimation.Guide_001.V2"
        };
        for (int i = 0; i < ids.Length; i++)
        {
            string directory = Path.Combine(StaticRoot, "npc", ids[i], contentVersion);
            Directory.CreateDirectory(directory);
            string manifestPath = Path.Combine(directory, "npc.json");
            string sourceManifestPath = Path.Combine(StaticRoot, "npc", ids[i], "1", "npc.json");
            JObject manifest = Read(sourceManifestPath);
            manifest["contentVersion"] = contentVersion;
            manifest["playerBuildId"] = "windows-x64-" + Application.version;
            ((JObject)manifest["systemPrompt"])["contentVersion"] = contentVersion;
            JObject script = (JObject)manifest["animationScript"];
            string assembly = animationAssemblies[i];
            script["assemblyName"] = assembly;
            foreach (JValue address in manifest.SelectTokens("$..*").OfType<JValue>()
                         .Where(value => value.Type == JTokenType.String &&
                                         ((string)value).Contains("/1/", StringComparison.Ordinal)))
                address.Value = ((string)address.Value).Replace("/1/", "/" + contentVersion + "/");
            string dll = Path.Combine(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(BuildTarget.StandaloneWindows64), assembly + ".dll");
            File.Copy(dll, Path.Combine(directory, "animation.dll.bytes"), true);
            script["length"] = new FileInfo(dll).Length;
            script["sha256"] = ArtifactHash.Sha256File(dll);
            string assetRoot = "Assets/Content/Npcs/" + ids[i] + "/" + contentVersion;
            Directory.CreateDirectory(assetRoot);
            // Copy all five assets together, remapping internal GUIDs to the versioned copies.
            string[] suffixes = { "default.prefab", "default.controller", "idle.anim", "default.mat", "default-texture.asset" };
            string[] names = { "visual.prefab", "controller.controller", "idle.anim", "material.mat", "texture.asset" };
            string[] addresses = { "visual", "controller", "idle", "material", "texture" };
            var remap = new System.Collections.Generic.Dictionary<string, string>();
            for (int n = 0; n < suffixes.Length; n++)
            {
                string source = "Assets/Art/Characters/" + characters[i] + "/" + characters[i] + "-" + suffixes[n];
                string target = assetRoot + "/" + names[n];
                if (!File.Exists(target) && !AssetDatabase.CopyAsset(source, target))
                    throw new IOException("Could not copy NPC visual asset.");
                remap[AssetDatabase.AssetPathToGUID(source)] = AssetDatabase.AssetPathToGUID(target);
            }
            for (int n = 0; n < names.Length; n++)
            {
                string target = assetRoot + "/" + names[n];
                string text = File.ReadAllText(target);
                foreach (var pair in remap) text = text.Replace(pair.Key, pair.Value);
                File.WriteAllText(target, text);
            }
            string scriptPath = assetRoot + "/animation.dll.bytes";
            File.Copy(dll, scriptPath, true);
            AssetDatabase.Refresh();
            for (int n = 0; n < names.Length; n++)
                AddEntry(settings, group, assetRoot + "/" + names[n],
                    "npc/" + ids[i] + "/" + contentVersion + "/" + addresses[n]);
            AddEntry(settings, group, scriptPath, (string)script["address"]);
            // The shared Catalog retains old immutable addresses even though the
            // mutable index exposes only the latest compatible version.
            string retainedAssetRoot = "Assets/Content/Npcs/" + ids[i] + "/1";
            for (int n = 0; n < names.Length; n++)
                AddEntry(settings, group, retainedAssetRoot + "/" + names[n],
                    "npc/" + ids[i] + "/1/" + addresses[n]);
            AddEntry(settings, group, retainedAssetRoot + "/animation.dll.bytes",
                "npc/" + ids[i] + "/1/animation-script");
            // Ordinary small PNGs, independent of model downloads.
            var texture = new Texture2D(32, 32);
            Color color = i == 0 ? new Color(0.8f, 0.55f, 0.2f) : new Color(0.2f, 0.6f, 0.8f);
            texture.SetPixels(Enumerable.Repeat(color, 1024).ToArray()); texture.Apply();
            File.WriteAllBytes(Path.Combine(directory, "avatar.png"), texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented) + "\n");
            JObject entry = index["npcs"].Children<JObject>().Single(x => (string)x["npcId"] == ids[i]);
            entry["contentVersion"] = contentVersion;
            entry["avatarPath"] = "npc/" + ids[i] + "/" + contentVersion + "/avatar.png";
            entry["manifestPath"] = "npc/" + ids[i] + "/" + contentVersion + "/npc.json";
            entry["manifestSha256"] = ArtifactHash.Sha256File(manifestPath);
            entry["minPlayerVersion"] = Application.version;
            entry["maxPlayerVersion"] = Application.version;
        }
        index["catalogContentVersion"] = HotUpdateArtifactStager.ContentVersion;
        File.WriteAllText(Path.Combine(StaticRoot, "npc", "index.json"), index.ToString(Formatting.Indented) + "\n");
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Validate();
        Debug.Log("NPC_SAMPLE_BUILD_SUCCESS");
    }

    private static void AddEntry(AddressableAssetSettings settings, AddressableAssetGroup group, string path, string address)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrWhiteSpace(guid))
            throw new InvalidDataException("NPC asset is not imported: " + path);
        var entry = settings.CreateOrMoveEntry(guid, group);
        if (entry == null)
            throw new InvalidDataException("NPC Addressable entry could not be created: " + path);
        entry.SetAddress(address, true);
        string label = "content.npc." + address.Split('/')[1];
        settings.AddLabel(label); entry.SetLabel(label, true);
    }

    public static void Validate()
    {
        JObject index = Read(Path.Combine(StaticRoot, "npc", "index.json"));
        JObject release = Read("Assets/Content/HotUpdate/release-manifest.json");
        if ((string)index["catalogContentVersion"] != (string)release["contentVersion"])
            throw new InvalidDataException("NPC index must describe the startup content version.");
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        foreach (JObject entry in index["npcs"].Children<JObject>())
        {
            string id = (string)entry["npcId"];
            string version = (string)entry["contentVersion"];
            const string segment = @"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? string.Empty, segment) ||
                !System.Text.RegularExpressions.Regex.IsMatch(version ?? string.Empty, segment) ||
                (string)entry["manifestPath"] != "npc/" + id + "/" + version + "/npc.json")
                throw new InvalidDataException("NPC manifest path is invalid.");
            string manifestPath = Path.Combine(StaticRoot, (string)entry["manifestPath"]);
            if (ArtifactHash.Sha256File(manifestPath) != (string)entry["manifestSha256"])
                throw new InvalidDataException("NPC manifest hash mismatch.");
            JObject manifest = Read(manifestPath);
            if ((string)manifest["playerBuildId"] != (string)release["playerBuildId"])
                throw new InvalidDataException("NPC Player build mismatch.");
            JObject script = (JObject)manifest["animationScript"];
            string dll = Path.Combine(Path.GetDirectoryName(manifestPath), "animation.dll.bytes");
            if (new FileInfo(dll).Length != (long)script["length"] || ArtifactHash.Sha256File(dll) != (string)script["sha256"])
                throw new InvalidDataException("NPC DLL hash/length mismatch.");
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dll))
            {
                string[] allowed = { "mscorlib", "System", "System.Core", "netstandard", "UnityEngine.CoreModule", "UnityEngine.AnimationModule", "GameWithLLM.Client.Gameplay" };
                if (assembly.Name.Name != (string)script["assemblyName"] ||
                    assembly.MainModule.AssemblyReferences.Any(x => !allowed.Contains(x.Name)))
                    throw new InvalidDataException("NPC DLL dependency or identity is invalid.");
                TypeDefinition type = assembly.MainModule.Types.SingleOrDefault(x => x.FullName == (string)script["entryType"]);
                if (type == null || type.IsAbstract || !type.IsPublic ||
                    !type.Interfaces.Any(x => x.InterfaceType.FullName == "INpcAnimationDriver") ||
                    !type.Methods.Any(x => x.IsConstructor && x.IsPublic && !x.HasParameters))
                    throw new InvalidDataException("NPC DLL entry must implement INpcAnimationDriver.");
            }
            var visual = (JObject)manifest["visual"];
            var addresses = visual.Properties().SelectMany(x => x.Value is JArray a ? a.Values<string>() : new[] { (string)x.Value })
                .Concat(new[] { (string)script["address"] });
            foreach (string address in addresses)
            {
                var matches = settings.groups.Where(x => x != null).SelectMany(x => x.entries).Where(x => x.address == address).ToArray();
                if (matches.Length != 1 || !File.Exists(matches[0].AssetPath))
                    throw new InvalidDataException("NPC Address is absent or ambiguous: " + address);
                foreach (string dependency in AssetDatabase.GetDependencies(matches[0].AssetPath, true))
                    if (dependency.StartsWith("Assets/Art/Characters/", StringComparison.Ordinal))
                        throw new InvalidDataException("NPC visual references unversioned character assets.");
            }
        }
        Debug.Log("NPC_CONTENT_RELEASE_VALIDATED");
    }

    private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path),
        new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    // 0.1.0 is the existing published AOT baseline, before the animation contract.
    // Sample staging may use it in Editor; production must establish a new Player.
    public static void ValidateProductionPlayerContract()
    {
        if (Application.version == "0.1.0")
            throw new InvalidDataException("NPC animation changes the AOT contract: assign a new Player version before production publishing.");
        string metadata = "Assets/Content/HotUpdate/Metadata/GameWithLLM.Client.Gameplay.dll.bytes";
        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(metadata))
            if (!assembly.MainModule.Types.Any(type => type.FullName == "INpcAnimationDriver" && type.IsInterface))
                throw new InvalidDataException("Target Player AOT metadata does not contain INpcAnimationDriver.");
    }
}
