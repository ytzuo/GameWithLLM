using System;
using System.Linq;
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

    // Dynamic NPCs configure this stable AOT controller before their inactive root is activated.
    public void ConfigureDynamic(string configuredCharacterId, Transform configuredVisualRoot)
    {
        if (isActiveAndEnabled || !CharacterContentCatalog.IsStableId(configuredCharacterId) ||
            configuredVisualRoot == null || configuredVisualRoot.parent != transform)
            throw new InvalidOperationException("Dynamic character visual configuration is invalid.");
        characterId = configuredCharacterId;
        appearanceId = "remote";
        visualRoot = configuredVisualRoot;
        fallbackVisual = null;
    }

    public async Task<Animator> LoadStrictAsync(
        IContentAssetProvider provider,
        RemoteNpcManifest manifest,
        CancellationToken cancellationToken)
    {
        if (_loadAttempted)
            throw new InvalidOperationException("Character visual load was already attempted.");
        _loadAttempted = true;
        if (provider == null || visualRoot == null || visualRoot.parent != transform)
            throw new InvalidOperationException("Dynamic character visual contract is incomplete.");

        ContentInstanceLease candidate = null;
        ContentAssetLease<RuntimeAnimatorController> controller = null;
        try
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            candidate = await provider.InstantiateAsync(manifest.PrefabAddress, visualRoot, cancellationToken);
            GameObject instance = candidate.Instance;
            ValidateVisualInstance(instance, manifest.SchemaVersion >= 2);
            Animator animator = FindVisualAnimator(instance, manifest.SchemaVersion >= 2);
            if (animator == null)
                throw new InvalidOperationException("Remote NPC visual does not contain an Animator.");
            if (manifest.SchemaVersion == 1)
            {
                controller = await provider.LoadAssetAsync<RuntimeAnimatorController>(
                    manifest.AnimatorControllerAddress,
                    cancellationToken);
                if (controller.Asset == null)
                    throw new InvalidOperationException("Remote NPC animator controller resolved to null.");
                animator.runtimeAnimatorController = controller.Asset;
            }
            instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            _instanceLease = candidate;
            candidate = null;
            var owner = gameObject.AddComponent<DynamicNpcVisualLeaseOwner>();
            owner.Initialize(controller);
            controller = null;
            return animator;
        }
        catch
        {
            candidate?.Dispose();
            controller?.Dispose();
            throw;
        }
    }

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

    public static void ValidateVisualInstance(GameObject instance, bool requireCompleteAnimator = false)
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
                typeName.Contains("Network", StringComparison.Ordinal) ||
                string.Equals(typeName, "AgentHostClient", StringComparison.Ordinal) ||
                string.Equals(typeName, "CommandDispatcher", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Character visual Prefab contains forbidden behaviour '{typeName}'.");
        }
        if (!requireCompleteAnimator) return;
        Animator[] animators = instance.GetComponentsInChildren<Animator>(true);
        if (animators.Length == 0)
            throw new InvalidOperationException("NPC visual Prefab does not contain an Animator.");
        if (!animators.Any(animator => animator.avatar != null && animator.avatar.isValid))
            throw new InvalidOperationException("NPC visual Prefab Animator does not contain a valid Avatar.");
        if (!animators.Any(animator => animator.avatar != null && animator.avatar.isValid &&
                                      animator.runtimeAnimatorController != null))
            throw new InvalidOperationException("NPC visual Prefab Animator does not contain a RuntimeAnimatorController.");
    }

    private static Animator FindVisualAnimator(GameObject instance, bool requireCompleteAnimator) =>
        instance.GetComponentsInChildren<Animator>(true).FirstOrDefault(animator =>
            !requireCompleteAnimator || animator.avatar != null && animator.avatar.isValid &&
            animator.runtimeAnimatorController != null);

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

internal sealed class DynamicNpcVisualLeaseOwner : MonoBehaviour
{
    private IDisposable _lease;
    public void Initialize(IDisposable lease) => _lease = lease;
    private void OnDestroy() { _lease?.Dispose(); _lease = null; }
}
