using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class ContentOwnershipValidation
{
    private static readonly HashSet<string> RetiredInventoryEntries =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "a1-bootstrap-delivery-probe"
        };
    private const string InventoryRelativePath =
        "Docs/History/HotUpdate/Baselines/addressables-a0-inventory.json";

    private static readonly string[] RequiredInventoryRoots =
    {
        "Assets/Art/Items",
        "Assets/Art/Material",
        "Assets/Art/UI",
        "Assets/Content/Bootstrap",
        "Assets/Data/Items",
        "Assets/Resources/UI",
        "Assets/Scenes",
        "Assets/StreamingAssets/HotUpdate"
    };

    private static readonly string[] RequiredFields =
    {
        "id", "sourcePaths", "sourceState", "location", "ownerGroup", "labels",
        "businessIds", "dependencies", "loadPoint", "releaseOwner", "releasePoint",
        "activationPoint", "sceneReferenceDisposition"
    };

    public static void Verify()
    {
        string inventoryPath = GetInventoryPath();
        if (!File.Exists(inventoryPath))
            throw new FileNotFoundException("Content ownership inventory is missing.", inventoryPath);

        JObject document = JObject.Parse(File.ReadAllText(inventoryPath));
        if (document.Value<int?>("schemaVersion") != 1)
            throw new InvalidOperationException("Content ownership inventory schemaVersion must be 1.");

        string sampleScene = RequireString(document, "sampleScene", "inventory root");
        if (!File.Exists(ToAbsoluteProjectPath(sampleScene)))
            throw new FileNotFoundException("The inventoried sample scene is missing.", sampleScene);

        JArray entries = document["entries"] as JArray ??
                         throw new InvalidOperationException("Content ownership inventory has no entries array.");
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var ownedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        var remoteAssets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JObject entry in entries.OfType<JObject>())
            ValidateEntry(entry, ids, addresses, ownedAssets, remoteAssets, errors);

        ValidateCoverage(ownedAssets, errors);
        ValidateSceneReferences(sampleScene, remoteAssets, entries, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Content ownership inventory validation failed:\n- " + string.Join("\n- ", errors));
        }

        Debug.Log($"[Content] Ownership inventory verified: " +
                  $"{entries.Count} entries, {ownedAssets.Count} source assets, " +
                  $"{addresses.Count} stable address rules.");
    }

    private static void ValidateEntry(
        JObject entry,
        ISet<string> ids,
        ISet<string> addresses,
        IDictionary<string, string> ownedAssets,
        IDictionary<string, string> remoteAssets,
        ICollection<string> errors)
    {
        string id = entry.Value<string>("id") ?? "<missing-id>";
        // Historical ownership baselines are immutable. Explicitly retired entries are
        // excluded here after their assets, labels and runtime consumers are removed.
        if (RetiredInventoryEntries.Contains(id))
            return;
        foreach (string field in RequiredFields)
        {
            if (entry[field] == null)
                errors.Add($"Entry '{id}' is missing '{field}'.");
        }

        if (!ids.Add(id))
            errors.Add($"Duplicate entry id '{id}'.");

        string state = entry.Value<string>("sourceState") ?? string.Empty;
        string location = entry.Value<string>("location") ?? string.Empty;
        string ownerGroup = entry.Value<string>("ownerGroup") ?? string.Empty;
        if (state != "existing" && state != "reserved" && state != "excluded")
            errors.Add($"Entry '{id}' has invalid sourceState '{state}'.");
        if (location != "Local" && location != "Remote")
            errors.Add($"Entry '{id}' has invalid location '{location}'.");
        if (state != "excluded" && !ownerGroup.StartsWith(location + "_", StringComparison.Ordinal))
            errors.Add($"Entry '{id}' location '{location}' does not match owner Group '{ownerGroup}'.");

        var addressValues = new List<string>();
        if (entry.Value<string>("address") is { } address)
            addressValues.Add(address);
        if (entry["addresses"] is JArray addressArray)
            addressValues.AddRange(addressArray.Values<string>());
        if (entry.Value<string>("addressPattern") is { } pattern && state != "excluded")
            addressValues.Add(pattern);
        if (state != "excluded" && addressValues.Count == 0)
            errors.Add($"Entry '{id}' has no address or addressPattern.");

        foreach (string value in addressValues)
        {
            if (!IsLogicalAddress(value))
                errors.Add($"Entry '{id}' has invalid logical address '{value}'.");
            if (!addresses.Add(value))
                errors.Add($"Address or address pattern '{value}' is assigned more than once.");
        }

        foreach (string label in entry["labels"]?.Values<string>() ?? Enumerable.Empty<string>())
        {
            if (!label.StartsWith("content.", StringComparison.Ordinal) || label.Contains('/'))
                errors.Add($"Entry '{id}' has invalid content label '{label}'.");
            if (addressValues.Contains(label, StringComparer.Ordinal))
                errors.Add($"Entry '{id}' uses the same value for a Label and Address: '{label}'.");
        }

        foreach (string businessId in entry["businessIds"]?.Values<string>() ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(businessId))
                errors.Add($"Entry '{id}' contains an empty business ID.");
            if (addressValues.Contains(businessId, StringComparer.Ordinal))
                errors.Add($"Entry '{id}' uses the same value for a business ID and Address: '{businessId}'.");
        }

        var excludedPaths = new HashSet<string>(
            entry["excludePaths"]?.Values<string>() ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        var sourcePaths = entry["sourcePaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
        if (state == "existing" && sourcePaths.Length == 0)
            errors.Add($"Existing entry '{id}' has no sourcePaths.");
        if (state == "reserved" && sourcePaths.Length != 0)
            errors.Add($"Reserved entry '{id}' must not claim source assets.");

        foreach (string sourcePath in sourcePaths)
        {
            foreach (string assetPath in ExpandSourcePath(sourcePath, errors, id))
            {
                if (excludedPaths.Contains(assetPath))
                    continue;
                if (ownedAssets.TryGetValue(assetPath, out string existingOwner))
                    errors.Add($"Source asset '{assetPath}' has both '{existingOwner}' and '{id}' as owners.");
                else
                    ownedAssets.Add(assetPath, id);

                if (location == "Remote" && state != "excluded")
                    remoteAssets[assetPath] = id;
            }
        }
    }

    private static IEnumerable<string> ExpandSourcePath(
        string sourcePath,
        ICollection<string> errors,
        string entryId)
    {
        if (!sourcePath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            errors.Add($"Entry '{entryId}' source path is not project-relative: '{sourcePath}'.");
            return Array.Empty<string>();
        }

        if (AssetDatabase.IsValidFolder(sourcePath))
        {
            return AssetDatabase.FindAssets(string.Empty, new[] { sourcePath })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sourcePath)))
        {
            errors.Add($"Entry '{entryId}' source asset does not exist: '{sourcePath}'.");
            return Array.Empty<string>();
        }
        return new[] { sourcePath };
    }

    private static void ValidateCoverage(
        IReadOnlyDictionary<string, string> ownedAssets,
        ICollection<string> errors)
    {
        foreach (string root in RequiredInventoryRoots)
        {
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
                    continue;
                if (!ownedAssets.ContainsKey(path))
                    errors.Add($"Asset in content scope has no inventory owner: '{path}'.");
            }
        }
    }

    private static void ValidateSceneReferences(
        string sampleScene,
        IReadOnlyDictionary<string, string> remoteAssets,
        IEnumerable<JToken> entries,
        ICollection<string> errors)
    {
        var entryById = entries.OfType<JObject>().ToDictionary(
            value => value.Value<string>("id") ?? string.Empty,
            StringComparer.Ordinal);
        foreach (string dependency in AssetDatabase.GetDependencies(sampleScene, false))
        {
            if (!remoteAssets.TryGetValue(dependency, out string entryId))
                continue;

            string disposition = entryById[entryId].Value<string>("sceneReferenceDisposition") ?? string.Empty;
            if (string.Equals(disposition, "none", StringComparison.OrdinalIgnoreCase) ||
                disposition.IndexOf("A", StringComparison.Ordinal) < 0)
            {
                errors.Add(
                    $"Remote candidate '{dependency}' is referenced by SampleScene without an explicit migration-stage disposition.");
            }
        }
    }

    private static bool IsLogicalAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("Assets/", StringComparison.Ordinal) ||
            value.Contains('\\') || value.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            return false;

        return value.Split('/').All(segment =>
            segment.Length > 0 && segment.All(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '.' ||
                character == '{' || character == '}'));
    }

    private static string RequireString(JObject value, string property, string context)
    {
        string result = value.Value<string>(property);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException($"{context} is missing '{property}'.");
        return result;
    }

    private static string ToAbsoluteProjectPath(string assetPath)
    {
        DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath) ??
                                         throw new InvalidOperationException("Unity project path is unavailable.");
        return Path.Combine(projectDirectory.FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetInventoryPath()
    {
        DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath) ??
                                         throw new InvalidOperationException("Unity project path is unavailable.");
        DirectoryInfo repositoryDirectory = projectDirectory.Parent ??
                                            throw new InvalidOperationException("Repository path is unavailable.");
        return Path.Combine(
            repositoryDirectory.FullName,
            InventoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
