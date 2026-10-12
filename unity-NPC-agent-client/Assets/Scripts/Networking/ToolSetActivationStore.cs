using System;
using UnityEngine;

public static class ToolSetActivationStore
{
    private const string Key = "gamewithllm.hot-update.last-successful";

    [Serializable]
    private sealed class Record
    {
        public string releaseId;
        public string toolSetVersion;
        public string catalogVersion;
    }

    public static void Save(ToolSetSnapshot snapshot, ToolMetadataCatalog catalog = null)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(new Record
        {
            releaseId = snapshot.ReleaseId,
            toolSetVersion = snapshot.ToolSetVersion,
            catalogVersion = catalog?.ContentVersion ?? snapshot.CatalogVersion
        }));
        PlayerPrefs.Save();
    }

    public static bool TryLoad(
        out string releaseId,
        out string toolSetVersion,
        out string catalogVersion)
    {
        releaseId = null;
        toolSetVersion = null;
        catalogVersion = null;
        string json = PlayerPrefs.GetString(Key, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return false;
        Record record;
        try { record = JsonUtility.FromJson<Record>(json); }
        catch (Exception) { return false; }
        if (record == null || string.IsNullOrWhiteSpace(record.releaseId) ||
            string.IsNullOrWhiteSpace(record.toolSetVersion) ||
            string.IsNullOrWhiteSpace(record.catalogVersion))
            return false;
        releaseId = record.releaseId;
        toolSetVersion = record.toolSetVersion;
        catalogVersion = record.catalogVersion;
        return true;
    }
}
