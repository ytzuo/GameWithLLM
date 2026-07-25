using UnityEngine;

[DisallowMultipleComponent]
public sealed class NpcLandmark : MonoBehaviour
{
    [SerializeField, Tooltip("稳定地标标识；无需包含 landmark: 前缀。")]
    private string landmarkId;

    [SerializeField, Tooltip("提供给模型和玩家看的名称；留空时使用 GameObject 名称。")]
    private string displayName;

    public string TargetId => AddPrefix(landmarkId, "landmark");
    public string DisplayName =>
        string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName.Trim();

    private static string AddPrefix(string value, string prefix)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalized.StartsWith(prefix + ":", System.StringComparison.OrdinalIgnoreCase))
            return normalized;
        return string.IsNullOrEmpty(normalized) ? string.Empty : $"{prefix}:{normalized}";
    }

    private void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(landmarkId))
            Debug.LogWarning($"[NpcLandmark] '{gameObject.name}' 缺少 landmarkId。", this);
    }
}