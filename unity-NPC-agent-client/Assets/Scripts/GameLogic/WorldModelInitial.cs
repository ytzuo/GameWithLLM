using UnityEngine;

/// <summary>
/// 在人物模型上方显示始终朝向主摄像机的单字母标识。
/// </summary>
public sealed class WorldModelInitial : MonoBehaviour
{
    private const string LabelObjectName = "ModelInitial";
    private const string MainTextObjectName = "MainText";
    private const float OutlineOffset = 0.015f;

    private static readonly Vector2[] OutlineOffsets =
    {
        new Vector2(-OutlineOffset, 0f),
        new Vector2(OutlineOffset, 0f),
        new Vector2(0f, -OutlineOffset),
        new Vector2(0f, OutlineOffset)
    };

    [SerializeField, Min(1f)] private float maximumVisibleDistance = 20f;

    private Transform _labelTransform;
    private Camera _camera;

    public static void Attach(GameObject owner, string initial)
    {
        if (owner == null || string.IsNullOrWhiteSpace(initial))
            return;

        WorldModelInitial marker = owner.GetComponent<WorldModelInitial>();
        if (marker == null)
            marker = owner.AddComponent<WorldModelInitial>();

        marker.SetInitial(initial.Trim().Substring(0, 1).ToUpperInvariant());
    }

    private void SetInitial(string initial)
    {
        Transform existing = transform.Find(LabelObjectName);
        GameObject labelObject = existing != null
            ? existing.gameObject
            : new GameObject(LabelObjectName);
        labelObject.transform.SetParent(transform, false);
        labelObject.transform.localPosition = new Vector3(0f, CalculateLabelHeight(), 0f);

        CreateText(labelObject.transform, MainTextObjectName, initial, Color.white, Vector3.zero, 11);
        for (int index = 0; index < OutlineOffsets.Length; index++)
        {
            Vector2 offset = OutlineOffsets[index];
            CreateText(
                labelObject.transform,
                $"Outline{index}",
                initial,
                new Color(0.05f, 0.05f, 0.05f, 0.9f),
                new Vector3(offset.x, offset.y, 0.005f),
                10);
        }

        _labelTransform = labelObject.transform;
    }

    private static void CreateText(
        Transform parent,
        string objectName,
        string text,
        Color color,
        Vector3 localPosition,
        int sortingOrder)
    {
        Transform existing = parent.Find(objectName);
        GameObject textObject = existing != null ? existing.gameObject : new GameObject(objectName);
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localRotation = Quaternion.identity;
        textObject.transform.localScale = Vector3.one;

        TextMesh textMesh = textObject.GetComponent<TextMesh>();
        if (textMesh == null)
            textMesh = textObject.AddComponent<TextMesh>();

        textMesh.text = text;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.fontSize = 64;
        textMesh.characterSize = 0.12f;
        textMesh.fontStyle = FontStyle.Bold;
        textMesh.color = color;

        MeshRenderer textRenderer = textMesh.GetComponent<MeshRenderer>();
        if (textRenderer != null)
            textRenderer.sortingOrder = sortingOrder;
    }

    private float CalculateLabelHeight()
    {
        Renderer ownerRenderer = GetComponent<Renderer>();
        return ownerRenderer == null
            ? 1.5f
            : ownerRenderer.bounds.max.y - transform.position.y + 0.35f;
    }

    private void LateUpdate()
    {
        if (_labelTransform == null)
            return;

        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null)
            return;

        Vector3 toCamera = _camera.transform.position - _labelTransform.position;
        float visibleDistance = Mathf.Max(1f, maximumVisibleDistance);
        bool shouldShow = toCamera.sqrMagnitude <= visibleDistance * visibleDistance;
        if (_labelTransform.gameObject.activeSelf != shouldShow)
            _labelTransform.gameObject.SetActive(shouldShow);
        if (!shouldShow)
            return;

        _labelTransform.rotation = Quaternion.LookRotation(
            -toCamera,
            _camera.transform.up);
    }
}