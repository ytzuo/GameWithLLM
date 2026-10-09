using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

// Definition-driven NPC publisher. Production consumes author-owned definitions;
// it never clones named sample assets or remaps GUIDs.
public static class NpcContentRelease
{
    public static string StaticRoot => Path.GetFullPath(
        Path.Combine(Application.dataPath, "..", "..", "NpcContent"));

    public static void BuildDefinitionsFromCommandLine() => HotUpdateEditorCommand.Run(BuildDefinitions);
    public static void ValidateFromCommandLine() => HotUpdateEditorCommand.Run(Validate);
    public static void BuildLocalContentFromCommandLine() => HotUpdateEditorCommand.Run(() =>
    {
        BuildDefinitions();
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        using (new AddressablesProfileScope(settings, "LocalDevelopment"))
        {
            AddressableAssetSettings.BuildPlayerContent(
                out UnityEditor.AddressableAssets.Build.AddressablesPlayerBuildResult result);
            if (!string.IsNullOrEmpty(result.Error)) throw new InvalidDataException(result.Error);
        }
        Debug.Log("NPC_LOCAL_CONTENT_BUILD_SUCCESS");
    });

    // Compatibility entry point; it now delegates to the generic production path.
    public static void BuildSamplesFromCommandLine() => BuildDefinitionsFromCommandLine();

    [MenuItem("GameWithLLM/Content/Build NPC Definitions")]
    public static void BuildDefinitions()
    {
        NpcContentDefinition[] definitions = AssetDatabase.FindAssets("t:NpcContentDefinition")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<NpcContentDefinition>)
            .Where(value => value != null)
            .OrderBy(value => value.npcId, StringComparer.Ordinal)
            .ToArray();
        if (definitions.Length == 0)
            throw new InvalidDataException("No NpcContentDefinition assets were found.");

        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        AddressableAssetGroup group = settings.FindGroup("Remote_Characters") ??
                                      throw new InvalidDataException("Remote_Characters Addressables group is missing.");
        JObject index = Read(Path.Combine(StaticRoot, "npc", "index.json"));
        JArray entries = (JArray)index["npcs"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (NpcContentDefinition definition in definitions)
        {
            ValidateDefinition(definition);
            if (!ids.Add(definition.npcId))
                throw new InvalidDataException($"Duplicate NPC content definition '{definition.npcId}'.");
            BuildDefinition(definition, settings, group, entries);
        }

        JObject release = Read("Assets/Content/HotUpdate/release-manifest.json");
        index["catalogContentVersion"] = (string)release["contentVersion"];
        WriteJson(Path.Combine(StaticRoot, "npc", "index.json"), index, false);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Validate();
        Debug.Log("NPC_DEFINITION_BUILD_SUCCESS");
    }

    private static void BuildDefinition(
        NpcContentDefinition definition,
        AddressableAssetSettings settings,
        AddressableAssetGroup group,
        JArray entries)
    {
        string prefix = $"npc/{definition.npcId}/{definition.contentVersion}/";
        AddEntry(settings, group, AssetDatabase.GetAssetPath(definition.visualPrefab),
            prefix + "visual", definition.npcId);
        var manifest = new JObject
        {
            ["schemaVersion"] = 2,
            ["npcId"] = definition.npcId,
            ["contentVersion"] = definition.contentVersion,
            ["playerBuildId"] = "windows-x64-" + Application.version,
            ["visual"] = new JObject { ["prefabAddress"] = prefix + "visual" },
            ["animationDriver"] = BuildAnimationDriver(definition, settings, group, prefix),
            ["profile"] = new JObject
            {
                ["npcId"] = definition.npcId,
                ["displayName"] = definition.profile.displayName,
                ["personality"] = JArray.FromObject(definition.profile.personality),
                ["speakingStyle"] = definition.profile.speakingStyle,
                ["identity"] = definition.profile.identity,
                ["responsibilities"] = JArray.FromObject(definition.profile.responsibilities),
                ["worldKnowledge"] = JArray.FromObject(definition.profile.worldKnowledge),
                ["forbiddenTopics"] = JArray.FromObject(definition.profile.forbiddenTopics)
            },
            ["systemPrompt"] = new JObject
            {
                ["schemaVersion"] = 1,
                ["contentVersion"] = definition.contentVersion,
                ["locale"] = "zh-CN",
                ["template"] = definition.systemPromptTemplate
            }
        };

        string directory = Path.Combine(StaticRoot, "npc", definition.npcId, definition.contentVersion);
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "npc.json");
        WriteJson(manifestPath, manifest, true);
        WriteAvatar(definition.avatar, Path.Combine(directory, "avatar.png"));

        JObject entry = entries.Children<JObject>().SingleOrDefault(
            value => (string)value["npcId"] == definition.npcId);
        if (entry == null) { entry = new JObject(); entries.Add(entry); }
        entry["npcId"] = definition.npcId;
        entry["contentVersion"] = definition.contentVersion;
        entry["displayName"] = definition.displayName;
        entry["description"] = definition.description;
        entry["avatarPath"] = prefix + "avatar.png";
        entry["manifestPath"] = prefix + "npc.json";
        entry["manifestSha256"] = ArtifactHash.Sha256File(manifestPath);
        entry["minPlayerVersion"] = definition.minPlayerVersion;
        entry["maxPlayerVersion"] = definition.maxPlayerVersion;
    }

    private static JObject BuildAnimationDriver(
        NpcContentDefinition definition,
        AddressableAssetSettings settings,
        AddressableAssetGroup group,
        string prefix)
    {
        if (definition.animationDriverKind == NpcAnimationDriverKind.Builtin)
            return new JObject { ["kind"] = "builtin", ["driverId"] = definition.builtinDriverId };

        string path = AssetDatabase.GetAssetPath(definition.hotUpdateDriver.assemblyBytes);
        string address = prefix + "animation-script";
        AddEntry(settings, group, path, address, definition.npcId);
        byte[] bytes = definition.hotUpdateDriver.assemblyBytes.bytes;
        return new JObject
        {
            ["kind"] = "hotUpdate",
            ["address"] = address,
            ["assemblyName"] = definition.hotUpdateDriver.assemblyName,
            ["entryType"] = definition.hotUpdateDriver.entryType,
            ["length"] = bytes.LongLength,
            ["sha256"] = RemoteNpcContract.ComputeSha256(bytes)
        };
    }

    private static void ValidateDefinition(NpcContentDefinition definition)
    {
        string prefix = $"Assets/Content/Npcs/{definition.npcId}/{definition.contentVersion}/";
        string prefabPath = AssetDatabase.GetAssetPath(definition.visualPrefab);
        const string stable = "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$";
        if (!System.Text.RegularExpressions.Regex.IsMatch(definition.npcId ?? string.Empty, stable) ||
            !System.Text.RegularExpressions.Regex.IsMatch(definition.contentVersion ?? string.Empty, stable) ||
            definition.visualPrefab == null || definition.avatar == null ||
            !prefabPath.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"NPC definition '{definition.name}' has invalid identity or versioned assets.");
        CharacterVisualController.ValidateVisualInstance(definition.visualPrefab, true);
        if (definition.animationDriverKind == NpcAnimationDriverKind.Builtin)
        {
            using var driver = StandardLocomotionAnimationDriver.Create(definition.builtinDriverId);
            driver.Bind(definition.visualPrefab.GetComponentInChildren<Animator>(true));
        }
        else
        {
            if (definition.hotUpdateDriver?.assemblyBytes == null ||
                string.IsNullOrWhiteSpace(definition.hotUpdateDriver.assemblyName) ||
                string.IsNullOrWhiteSpace(definition.hotUpdateDriver.entryType))
                throw new InvalidDataException("Hot-update NPC animation driver is incomplete.");
            ValidateAnimationAssembly(definition.hotUpdateDriver.assemblyBytes.bytes,
                definition.hotUpdateDriver.assemblyName, definition.hotUpdateDriver.entryType);
        }
        string[] dependencies = AssetDatabase.GetDependencies(prefabPath, true);
        if (dependencies.Length == 0 || dependencies.Any(path => !File.Exists(path)))
            throw new InvalidDataException("NPC visual Prefab has a missing dependency.");
    }

    private static void AddEntry(AddressableAssetSettings settings, AddressableAssetGroup group,
        string path, string address, string npcId)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrWhiteSpace(guid)) throw new InvalidDataException("NPC asset is not imported: " + path);
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group) ??
                                      throw new InvalidDataException("NPC Addressable entry could not be created: " + path);
        entry.SetAddress(address, true);
        string label = "content.npc." + npcId;
        settings.AddLabel(label); entry.SetLabel(label, true);
    }

    public static void Validate()
    {
        JObject index = Read(Path.Combine(StaticRoot, "npc", "index.json"));
        JObject release = Read("Assets/Content/HotUpdate/release-manifest.json");
        if ((string)index["catalogContentVersion"] != (string)release["contentVersion"])
            throw new InvalidDataException("NPC index must describe the startup content version.");
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        foreach (JObject entry in index["npcs"].Children<JObject>())
        {
            string manifestPath = Path.Combine(StaticRoot, (string)entry["manifestPath"]);
            if (ArtifactHash.Sha256File(manifestPath) != (string)entry["manifestSha256"])
                throw new InvalidDataException("NPC manifest hash mismatch.");
            RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(File.ReadAllText(manifestPath));
            if (manifest.NpcId != (string)entry["npcId"] ||
                manifest.ContentVersion != (string)entry["contentVersion"])
                throw new InvalidDataException("NPC manifest identity does not match its catalog entry.");
            if (manifest.PlayerBuildId != (string)release["playerBuildId"])
                throw new InvalidDataException("NPC Player build mismatch.");
            foreach (string address in manifest.DownloadAddresses)
            {
                AddressableAssetEntry[] matches = Entries(settings).Where(x => x.address == address).ToArray();
                if (matches.Length != 1 || !File.Exists(matches[0].AssetPath))
                    throw new InvalidDataException("NPC Address is absent or ambiguous: " + address);
            }
            AddressableAssetEntry prefabEntry = Entries(settings).Single(x => x.address == manifest.PrefabAddress);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabEntry.AssetPath);
            CharacterVisualController.ValidateVisualInstance(prefab, manifest.SchemaVersion >= 2);
            if (!manifest.UsesBuiltinAnimationDriver)
            {
                AddressableAssetEntry scriptEntry = Entries(settings).Single(x => x.address == manifest.AnimationScriptAddress);
                byte[] bytes = File.ReadAllBytes(scriptEntry.AssetPath);
                if (bytes.LongLength != manifest.AnimationLength ||
                    RemoteNpcContract.ComputeSha256(bytes) != manifest.AnimationSha256)
                    throw new InvalidDataException("NPC DLL hash/length mismatch.");
                ValidateAnimationAssembly(bytes, manifest.AnimationAssemblyName, manifest.AnimationEntryType);
            }
        }
        Debug.Log("NPC_CONTENT_RELEASE_VALIDATED");
    }

    private static IEnumerable<AddressableAssetEntry> Entries(AddressableAssetSettings settings) =>
        settings.groups.Where(x => x != null).SelectMany(x => x.entries);

    private static void ValidateAnimationAssembly(byte[] bytes, string assemblyName, string entryType)
    {
        using var stream = new MemoryStream(bytes, false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream);
        string[] allowed = { "mscorlib", "System", "System.Core", "netstandard", "UnityEngine.CoreModule",
            "UnityEngine.AnimationModule", "GameWithLLM.Client.Gameplay" };
        if (assembly.Name.Name != assemblyName || assembly.MainModule.AssemblyReferences.Any(x => !allowed.Contains(x.Name)))
            throw new InvalidDataException("NPC DLL dependency or identity is invalid.");
        TypeDefinition type = assembly.MainModule.Types.SingleOrDefault(x => x.FullName == entryType);
        if (type == null || type.IsAbstract || !type.IsPublic ||
            !type.Interfaces.Any(x => x.InterfaceType.FullName == "INpcAnimationDriver") ||
            !type.Methods.Any(x => x.IsConstructor && x.IsPublic && !x.HasParameters))
            throw new InvalidDataException("NPC DLL entry must implement INpcAnimationDriver.");
    }

    private static void WriteAvatar(Texture2D avatar, string target)
    {
        string source = AssetDatabase.GetAssetPath(avatar);
        byte[] bytes = string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(source) : avatar.EncodeToPNG();
        WriteImmutable(target, bytes);
    }

    private static void WriteJson(string path, JObject value, bool immutable)
    {
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(value.ToString(Formatting.Indented) + "\n");
        if (immutable) WriteImmutable(path, bytes); else File.WriteAllBytes(path, bytes);
    }

    private static void WriteImmutable(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                throw new InvalidDataException("Immutable NPC version already exists with different bytes: " + path);
            return;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path),
        new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    public static void ValidateProductionPlayerContract()
    {
        if (Application.version == "0.1.0")
            throw new InvalidDataException("NPC animation changes the AOT contract: assign a new Player version before production publishing.");
        string metadata = "Assets/Content/HotUpdate/Metadata/GameWithLLM.Client.Gameplay.dll.bytes";
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(metadata);
        if (!assembly.MainModule.Types.Any(type => type.FullName == "INpcAnimationDriver" && type.IsInterface))
            throw new InvalidDataException("Target Player AOT metadata does not contain INpcAnimationDriver.");
    }
}
