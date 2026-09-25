using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class AddressablesA6PackedPlaySmoke
{
    private const string ActiveKey = "GameWithLLM.A6PackedPlay.Active";
    private const string FinishingKey = "GameWithLLM.A6PackedPlay.Finishing";
    private const string SuccessKey = "GameWithLLM.A6PackedPlay.Success";
    private const string FailureKey = "GameWithLLM.A6PackedPlay.Failure";
    private const string DeadlineKey = "GameWithLLM.A6PackedPlay.Deadline";
    private const string OriginalBuilderKey = "GameWithLLM.A6PackedPlay.OriginalBuilder";
    private const string OriginalProfileKey = "GameWithLLM.A6PackedPlay.OriginalProfile";
    private static bool _bound;
    private static Task _returnTask;
    private static Task _fallbackTask;

    static AddressablesA6PackedPlaySmoke()
    {
        if (SessionState.GetBool(ActiveKey, false))
            Bind();
    }

    public static void RunFromCommandLine()
    {
        try
        {
            AddressablesA6ProjectSetup.Verify();
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                                throw new InvalidOperationException("Addressables settings are missing.");
            SessionState.SetInt(OriginalBuilderKey, settings.ActivePlayerDataBuilderIndex);
            SessionState.SetString(OriginalProfileKey, settings.activeProfileId);
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(FinishingKey, false);
            SessionState.SetBool(SuccessKey, false);
            SessionState.SetString(FailureKey, string.Empty);
            SessionState.SetString(
                DeadlineKey,
                (EditorApplication.timeSinceStartup + 120).ToString("R", CultureInfo.InvariantCulture));
            settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
            settings.ActivePlayerDataBuilderIndex = 1;
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene("Assets/Scenes/BootstrapScene.unity", OpenSceneMode.Single);
            Bind();
            EditorApplication.EnterPlaymode();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            RestoreAndExit(false, ex.Message);
        }
    }

    private static void Bind()
    {
        if (_bound) return;
        _bound = true;
        Application.logMessageReceived += OnLog;
        EditorApplication.update += OnUpdate;
    }

    private static void OnLog(string condition, string _, LogType type)
    {
        if (type == LogType.Error &&
            (condition.Contains("[Content] Fatal bootstrap error", StringComparison.Ordinal) ||
             condition.Contains("Initial remote scene failed", StringComparison.Ordinal) ||
             condition.Contains("Missing Script", StringComparison.Ordinal)))
        {
            SessionState.SetString(FailureKey, condition);
            BeginFinish(false);
        }
    }

    private static void OnUpdate()
    {
        if (!SessionState.GetBool(ActiveKey, false)) return;
        if (SessionState.GetBool(FinishingKey, false))
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                RestoreAndExit(
                    SessionState.GetBool(SuccessKey, false),
                    SessionState.GetString(FailureKey, string.Empty));
            return;
        }
        if (!EditorApplication.isPlaying) return;

        try
        {
            RemoteSceneCoordinator coordinator =
                UnityEngine.Object.FindFirstObjectByType<RemoteSceneCoordinator>();
            AgentHostClient host = UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
            if (_returnTask == null && host != null && host.IsContentReady &&
                coordinator != null && coordinator.HasActiveRemoteScene)
            {
                if (SceneManager.GetActiveScene().name != "WarehouseRemote" ||
                    coordinator.ActiveSceneId != RemoteSceneCoordinator.WarehouseSceneId ||
                    UnityEngine.Object.FindObjectsByType<AgentHostClient>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                    UnityEngine.Object.FindObjectsByType<UIManager>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                    UnityEngine.Object.FindObjectsByType<PlayerMock>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None).Count(player => player.enabled) != 1 ||
                    UnityEngine.Object.FindObjectsByType<NpcEntity>(
                        FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length < 1)
                    throw new InvalidOperationException("A6 remote warehouse runtime boundary is invalid.");
                _returnTask = coordinator.ReturnToBootstrapAsync(CancellationToken.None);
                return;
            }

            if (_returnTask != null && _returnTask.IsCompleted)
            {
                _returnTask.GetAwaiter().GetResult();
                if (_fallbackTask == null)
                {
                    _fallbackTask = VerifyFailedCandidateFallbackAsync(coordinator);
                    return;
                }
                if (!_fallbackTask.IsCompleted)
                    return;
                _fallbackTask.GetAwaiter().GetResult();
                if (SceneManager.GetActiveScene().name != "BootstrapScene" ||
                    coordinator == null || coordinator.HasActiveRemoteScene ||
                    UnityEngine.Object.FindObjectsByType<PlayerMock>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 0 ||
                    UnityEngine.Object.FindObjectsByType<NpcEntity>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 0)
                    throw new InvalidOperationException("A6 remote scene did not release back to Bootstrap.");
                Debug.Log("[Content] Addressables A6 PACKED PLAY PASSED: remote load, bind, unload and failed-candidate Bootstrap fallback verified.");
                BeginFinish(true);
                return;
            }

            if (double.TryParse(SessionState.GetString(DeadlineKey, "0"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double deadline) &&
                EditorApplication.timeSinceStartup >= deadline)
                throw new TimeoutException("Timed out waiting for the A6 remote scene lifecycle.");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            SessionState.SetString(FailureKey, ex.GetBaseException().Message);
            BeginFinish(false);
        }
    }

    private static async Task VerifyFailedCandidateFallbackAsync(
        RemoteSceneCoordinator coordinator)
    {
        bool rejected = false;
        try
        {
            await coordinator.SwitchAsync(
                "scene/a6/missing-candidate",
                "missing-candidate",
                CancellationToken.None);
        }
        catch
        {
            rejected = true;
        }
        if (!rejected)
            throw new InvalidOperationException("Missing A6 candidate unexpectedly activated.");
    }

    private static void BeginFinish(bool success)
    {
        if (SessionState.GetBool(FinishingKey, false)) return;
        SessionState.SetBool(SuccessKey, success);
        SessionState.SetBool(FinishingKey, true);
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.ExitPlaymode();
    }

    private static void RestoreAndExit(bool success, string failure)
    {
        Application.logMessageReceived -= OnLog;
        EditorApplication.update -= OnUpdate;
        _bound = false;
        _returnTask = null;
        _fallbackTask = null;
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings != null)
        {
            settings.ActivePlayerDataBuilderIndex = SessionState.GetInt(OriginalBuilderKey, 2);
            string profile = SessionState.GetString(OriginalProfileKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(profile)) settings.activeProfileId = profile;
            AssetDatabase.SaveAssets();
        }
        SessionState.EraseBool(ActiveKey);
        if (success) EditorApplication.Exit(0);
        else
        {
            Debug.LogError("[Content] Addressables A6 Packed Play failed: " + failure);
            EditorApplication.Exit(1);
        }
    }
}
