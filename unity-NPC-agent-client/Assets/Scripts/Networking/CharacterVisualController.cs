using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;

// AOT 权威实体只持有此表现控制器。远端实例永远挂在稳定 VisualRoot 下，
// 失败时保留随 Player 发布的 fallback；实例 lease 与实体生命周期一致。
public sealed class CharacterVisualController : MonoBehaviour
{
    [SerializeField] private string characterId;
    [SerializeField] private string appearanceId = "default";
    [SerializeField] private Transform visualRoot;
    [SerializeField] private GameObject fallbackVisual;

    private ContentInstanceLease _instanceLease;
    private bool _loadAttempted;

    public string CharacterId => characterId;
    public string AppearanceId => appearanceId;
    public Transform VisualRoot => visualRoot;
    public GameObject FallbackVisual => fallbackVisual;
    public bool IsRemoteVisualActive => _instanceLease != null && !_instanceLease.IsReleased;

    public async Task LoadAsync(
        CharacterContentCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (_loadAttempted)
            return;
        _loadAttempted = true;
        EnsureLocalContract();
        fallbackVisual.SetActive(true);

        try
        {
            ContentInstanceLease candidate = await catalog.InstantiateAsync(
                characterId,
                appearanceId,
                visualRoot,
                cancellationToken);
            GameObject instance = candidate.Instance;
            try
            {
                ValidateVisualInstance(instance);
                instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                _instanceLease = candidate;
                fallbackVisual.SetActive(false);
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 表现失败不得阻断 Entity、Inventory、Runtime Manifest 或工具执行。
            Debug.LogWarning(
                $"[Content] Character visual '{characterId}/{appearanceId}' failed; " +
                $"using local fallback: {ex.GetBaseException().Message}",
                this);
        }
    }

    public void ReleaseVisual()
    {
        _instanceLease?.Dispose();
        _instanceLease = null;
        if (fallbackVisual != null)
            fallbackVisual.SetActive(true);
    }

    public static void ValidateVisualInstance(GameObject instance)
    {
        if (instance == null)
            throw new InvalidOperationException("Character visual Prefab resolved to null.");
        if (instance.GetComponentInChildren<NpcEntity>(true) != null ||
            instance.GetComponentInChildren<InventoryComponent>(true) != null ||
            instance.GetComponentInChildren<NavMeshAgent>(true) != null)
            throw new InvalidOperationException(
                "Character visual Prefab contains an authoritative entity, inventory, or NavMesh component.");
        foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            string typeName = behaviour.GetType().Name;
            if (typeName.Contains("Registry", StringComparison.Ordinal) ||
                typeName.Contains("Tool", StringComparison.Ordinal) ||
                typeName.Contains("Network", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Character visual Prefab contains forbidden behaviour '{typeName}'.");
        }
    }

    private void EnsureLocalContract()
    {
        if (!CharacterContentCatalog.IsStableId(characterId) ||
            !CharacterContentCatalog.IsStableId(appearanceId))
            throw new InvalidOperationException("CharacterVisualController has invalid stable IDs.");
        if (visualRoot == null || visualRoot.parent != transform || fallbackVisual == null ||
            fallbackVisual.transform.parent != visualRoot)
            throw new InvalidOperationException(
                "CharacterVisualController requires a direct VisualRoot and local fallback child.");
    }

    private void OnDestroy() => ReleaseVisual();
}
