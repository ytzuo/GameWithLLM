using System;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;

public sealed class AddressablesProfileScope : IDisposable
{
    private readonly AddressableAssetSettings _settings;
    private readonly string _originalProfileId;
    private bool _disposed;

    public AddressablesProfileScope(AddressableAssetSettings settings, string profileName)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _originalProfileId = settings.activeProfileId;
        string profileId = settings.profileSettings.GetProfileId(profileName);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Addressables profile '{profileName}' is missing.");
        settings.activeProfileId = profileId;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _settings.activeProfileId = _originalProfileId;
        AssetDatabase.SaveAssets();
        _disposed = true;
    }
}
