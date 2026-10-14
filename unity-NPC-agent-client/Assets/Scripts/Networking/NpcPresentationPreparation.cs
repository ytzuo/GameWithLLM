using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Installation, restore preflight and spawning exercise the same real instance and
// driver contract. Ownership transfers only after every check has succeeded.
public sealed class NpcPresentationPreparation : IDisposable
{
    private ContentInstanceLease _visual;
    private ContentAssetLease<TextAsset> _assembly;
    public INpcAnimationDriver Driver { get; private set; }
    public GameObject Instance => _visual?.Instance;

    public static async Task RequireAddressesAsync(IContentAssetProvider provider,
        RemoteNpcManifest manifest, CancellationToken token)
    {
        if (!await provider.AddressExistsAsync(manifest.PrefabAddress, typeof(GameObject), token) ||
            !manifest.UsesBuiltinAnimationDriver &&
            !await provider.AddressExistsAsync(manifest.AnimationScriptAddress, typeof(TextAsset), token))
            throw new InvalidOperationException("NPC_RESOURCE_RESTART_REQUIRED");
    }

    public static async Task<NpcPresentationPreparation> PrepareAsync(
        IContentAssetProvider provider, RemoteNpcManifest manifest,
        Transform parent, CancellationToken token)
    {
        if (parent == null || parent.gameObject.activeInHierarchy)
            throw new InvalidOperationException("NPC presentation preparation requires an inactive parent.");
        var prepared = new NpcPresentationPreparation();
        try
        {
            token.ThrowIfCancellationRequested();
            await RequireAddressesAsync(provider, manifest, token);
            // GetDownloadSize can return zero for an unresolved address. Resolve each
            // typed asset independently before treating cached dependencies as usable.
            using (var prefab = await provider.LoadAssetAsync<GameObject>(manifest.PrefabAddress, token))
                CharacterVisualController.ValidateVisualInstance(prefab.Asset, true);
            prepared._visual = await provider.InstantiateAsync(manifest.PrefabAddress, parent, token);
            CharacterVisualController.ValidateVisualInstance(prepared.Instance, true);
            Animator animator = prepared.Instance.GetComponentsInChildren<Animator>(true)
                .First(value => value.avatar != null && value.avatar.isValid && value.runtimeAnimatorController != null);
            if (manifest.UsesBuiltinAnimationDriver)
                prepared.Driver = StandardLocomotionAnimationDriver.Create(manifest.BuiltinAnimationDriverId);
            else
            {
                prepared._assembly = await provider.LoadAssetAsync<TextAsset>(manifest.AnimationScriptAddress, token);
                byte[] bytes = prepared._assembly.Asset?.bytes;
                if (bytes == null || bytes.LongLength != manifest.AnimationLength ||
                    RemoteNpcContract.ComputeSha256(bytes) != manifest.AnimationSha256)
                    throw new InvalidDataException("NPC animation assembly integrity check failed.");
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    value => value.GetName().Name == manifest.AnimationAssemblyName) ?? Assembly.Load(bytes);
                if (assembly.GetName().Name != manifest.AnimationAssemblyName)
                    throw new InvalidDataException("NPC animation assembly name mismatch.");
                Type type = assembly.GetType(manifest.AnimationEntryType, true, false);
                if (type.IsAbstract || !typeof(INpcAnimationDriver).IsAssignableFrom(type) ||
                    type.GetConstructor(Type.EmptyTypes) == null)
                    throw new InvalidDataException("NPC animation entry contract is invalid.");
                prepared.Driver = (INpcAnimationDriver)Activator.CreateInstance(type);
            }
            prepared.Driver.Bind(animator);
            token.ThrowIfCancellationRequested();
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    public static async Task ValidateAsync(IContentAssetProvider provider,
        RemoteNpcManifest manifest, CancellationToken token)
    {
        var staging = new GameObject("NPC presentation validation");
        staging.SetActive(false);
        try
        {
            using var prepared = await PrepareAsync(provider, manifest, staging.transform, token);
        }
        finally
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(staging);
            else UnityEngine.Object.DestroyImmediate(staging);
        }
    }

    public void Dispose()
    {
        try { Driver?.Dispose(); }
        finally
        {
            Driver = null;
            try { _visual?.Dispose(); }
            finally { _visual = null; _assembly?.Dispose(); _assembly = null; }
        }
    }
}
