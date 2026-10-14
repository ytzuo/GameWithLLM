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
    private static string _outputRoot;
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

    [MenuItem("GameWithLLM/Content/Build NPC Definitions")]
    public static void BuildDefinitions()
    {
        NpcContentDefinition[] definitions = AssetDatabase.FindAssets("t:NpcContentDefinition")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<NpcContentDefinition>)
            .Where(value => value != null)
            .OrderBy(value => value.name, StringComparer.Ordinal)
            .ToArray();
        if (definitions.Length == 0)
            throw new InvalidDataException("No NpcContentDefinition assets were found.");

        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        AddressableAssetGroup group = settings.FindGroup("Remote_Characters") ??
                                      throw new InvalidDataException("Remote_Characters Addressables group is missing.");
        JObject index = Read(Path.Combine(StaticRoot, "npc", "index.json"));
        JArray entries = new JArray();
        index = new JObject { ["texts"] = index["texts"], ["npcs"] = entries };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NpcContentDefinition definition in definitions)
        {
            ValidateDefinition(definition);
            if (!ids.Add(definition.name)) throw new InvalidDataException("Duplicate NPC name: " + definition.name);
        }
        string destination = Path.Combine(StaticRoot, "npc");
        _outputRoot = Path.Combine(StaticRoot, "npc-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputRoot);
        string backup = destination + "-backup-" + Guid.NewGuid().ToString("N");
        try
        {
            foreach (NpcContentDefinition definition in definitions) BuildDefinition(definition, settings, group, entries);
            RemoteNpcCatalog catalog = RemoteNpcContract.ParseCatalog(index.ToString());
            foreach (RemoteNpcSummary entry in catalog.Npcs)
                RemoteNpcContract.ParseManifest(File.ReadAllText(Path.Combine(_outputRoot, entry.NpcId, "npc.json")), entry);
            WriteJson(Path.Combine(_outputRoot, "index.json"), index);
            ValidateAt(_outputRoot);
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try { Directory.Move(_outputRoot, destination); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, destination); throw; }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
        finally { if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, true); _outputRoot = null; }
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
        string prefix = $"npc/{definition.name}/";
        AddEntry(settings, group, AssetDatabase.GetAssetPath(definition.visualPrefab),
            prefix + "visual", definition.name);
        var manifest = new JObject
        {
            ["name"] = definition.name,
            ["visual"] = new JObject { ["prefabAddress"] = prefix + "visual" },
            ["animationDriver"] = BuildAnimationDriver(definition, settings, group, prefix),
            ["profile"] = new JObject
            {
                ["npcId"] = definition.name,
                ["displayName"] = definition.displayName,
                ["personality"] = JArray.FromObject(definition.profile.personality),
                ["speakingStyle"] = definition.profile.speakingStyle,
                ["identity"] = definition.profile.identity,
                ["responsibilities"] = JArray.FromObject(definition.profile.responsibilities),
                ["worldKnowledge"] = JArray.FromObject(definition.profile.worldKnowledge),
                ["forbiddenTopics"] = JArray.FromObject(definition.profile.forbiddenTopics)
            },
            ["systemPrompt"] = new JObject
            {
                ["locale"] = "zh-CN",
                ["template"] = definition.systemPromptTemplate
            }
        };

        string directory = Path.Combine(_outputRoot, definition.name);
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "npc.json");
        WriteJson(manifestPath, manifest);
        WriteAvatar(definition.avatar, Path.Combine(directory, "avatar.png"));
        if (definition.animationDriverKind == NpcAnimationDriverKind.HotUpdate)
            File.WriteAllBytes(Path.Combine(directory, "animation.dll.bytes"), definition.hotUpdateDriver.assemblyBytes.bytes);

        JObject entry = entries.Children<JObject>().SingleOrDefault(
            value => (string)value["name"] == definition.name);
        if (entry == null) { entry = new JObject(); entries.Add(entry); }
        entry["name"] = definition.name;
        entry["displayName"] = definition.displayName;
        entry["description"] = definition.description;
        entry["avatarPath"] = prefix + "avatar.png";
        entry["manifestPath"] = prefix + "npc.json";
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
        AddEntry(settings, group, path, address, definition.name);
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
        string prefix = $"Assets/Content/Npcs/{definition.name}/";
        string prefabPath = AssetDatabase.GetAssetPath(definition.visualPrefab);
        RemoteNpcContract.ValidateName(definition.name);
        if (definition.visualPrefab == null || definition.avatar == null ||
            !AssetDatabase.GetAssetPath(definition).Equals(prefix + "Definition.asset", StringComparison.Ordinal) ||
            !AssetDatabase.GetAssetPath(definition.avatar).Equals(prefix + "Avatar.png", StringComparison.Ordinal) ||
            !prefabPath.Equals(prefix + "Visual.prefab", StringComparison.Ordinal))
            throw new InvalidDataException("NPC definition must own its name directory, Visual.prefab and Avatar.png.");
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

    public static void Validate() => ValidateAt(Path.Combine(StaticRoot, "npc"));

    private static void ValidateAt(string npcDirectory)
    {
        JObject index = Read(Path.Combine(npcDirectory, "index.json"));
        RemoteNpcCatalog catalog = RemoteNpcContract.ParseCatalog(index.ToString());
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        foreach (RemoteNpcSummary entry in catalog.Npcs)
        {
            string manifestPath = Path.Combine(npcDirectory, entry.NpcId, "npc.json");
            RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(File.ReadAllText(manifestPath), entry);
            foreach (string address in manifest.DownloadAddresses)
            {
                AddressableAssetEntry[] matches = Entries(settings).Where(x => x.address == address).ToArray();
                if (matches.Length != 1 || !File.Exists(matches[0].AssetPath))
                    throw new InvalidDataException("NPC Address is absent or ambiguous: " + address);
            }
            AddressableAssetEntry prefabEntry = Entries(settings).Single(x => x.address == manifest.PrefabAddress);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabEntry.AssetPath);
            CharacterVisualController.ValidateVisualInstance(prefab, true);
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
        File.WriteAllBytes(target, bytes);
    }

    private static void WriteJson(string path, JObject value) =>
        File.WriteAllText(path, value.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));

    private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path),
        new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    public static void ValidateProductionPlayerContract()
    {
        if (Application.version == "0.1.0")
            throw new InvalidDataException("NPC animation changes the AOT contract: assign a new Player version before production publishing.");
        string metadata = "Assets/Content/HotUpdate/Metadata/GameWithLLM.Client.Gameplay.dll.bytes";
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(metadata);
        string[] requiredTypes =
        {
            "INpcAnimationDriver",
            "INpcAnimationEventSource",
            "INpcEventDrivenAnimationDriver",
            "NpcAnimationEvent",
            "NpcAnimationSnapshot"
        };
        string missing = requiredTypes.FirstOrDefault(name =>
            !assembly.MainModule.Types.Any(type => type.FullName == name));
        if (missing != null)
            throw new InvalidDataException($"Target Player AOT metadata does not contain {missing}.");
    }
}
