using System;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;

public sealed class AddressablesProfileScope : IDisposable
{
    private readonly AddressableAssetSettings _settings;
    private readonly string _originalProfileId;
    private readonly string _profileId;
    private readonly string _originalRemoteLoadPath;
    private readonly string _originalCatalogLoadPath;
    private bool _disposed;

    public AddressablesProfileScope(AddressableAssetSettings settings, string profileName)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _originalProfileId = settings.activeProfileId;
        string profileId = settings.profileSettings.GetProfileId(profileName);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Addressables profile '{profileName}' is missing.");
        settings.activeProfileId = profileId;
        _profileId = profileId;
        string baseUrl = Environment.GetEnvironmentVariable("CONTENT_RELEASE_BASE_URL");
        if (profileName == "Production" && !string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("CONTENT_RELEASE_BASE_URL must be an HTTP(S) root without credentials, query or fragment.");
            _originalRemoteLoadPath = settings.profileSettings.GetValueByName(profileId, "Remote.LoadPath");
            _originalCatalogLoadPath = settings.profileSettings.GetValueByName(profileId, "RemoteCatalog.LoadPath");
            settings.profileSettings.SetValue(profileId, "Remote.LoadPath", baseUrl.TrimEnd('/') + "/[BuildTarget]/[ReleaseId]");
            settings.profileSettings.SetValue(profileId, "RemoteCatalog.LoadPath", baseUrl.TrimEnd('/') + "/[BuildTarget]/catalog");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _settings.activeProfileId = _originalProfileId;
        if (_originalRemoteLoadPath != null)
        {
            _settings.profileSettings.SetValue(_profileId, "Remote.LoadPath", _originalRemoteLoadPath);
            _settings.profileSettings.SetValue(_profileId, "RemoteCatalog.LoadPath", _originalCatalogLoadPath);
        }
        AssetDatabase.SaveAssets();
        _disposed = true;
    }
}
