using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class AddressablesA3PackedPlaySmoke
{
    private const double TimeoutSeconds = 90;
    private const string ActiveKey = "GameWithLLM.A3PackedPlay.Active";
    private const string FinishingKey = "GameWithLLM.A3PackedPlay.Finishing";
    private const string SuccessKey = "GameWithLLM.A3PackedPlay.Success";
    private const string FailureKey = "GameWithLLM.A3PackedPlay.Failure";
    private const string DeadlineKey = "GameWithLLM.A3PackedPlay.Deadline";
    private const string OriginalBuilderKey = "GameWithLLM.A3PackedPlay.OriginalBuilder";
    private const string OriginalProfileKey = "GameWithLLM.A3PackedPlay.OriginalProfile";
    private const string HudConfirmationFrameKey = "GameWithLLM.A3PackedPlay.HudConfirmationFrame";
    private static bool _bound;

    static AddressablesA3PackedPlaySmoke()
    {
        if (SessionState.GetBool(ActiveKey, false))
            Bind();
    }

    public static void Run()
    {
        try
        {
            AddressablesA3ProjectSetup.Verify();
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                                throw new InvalidOperationException("Addressables settings are missing.");
            SessionState.SetInt(OriginalBuilderKey, settings.ActivePlayerDataBuilderIndex);
            SessionState.SetString(OriginalProfileKey, settings.activeProfileId);
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(FinishingKey, false);
            SessionState.SetBool(SuccessKey, false);
            SessionState.SetString(FailureKey, string.Empty);
            SessionState.SetInt(HudConfirmationFrameKey, -1);
            SessionState.SetString(
                DeadlineKey,
                (EditorApplication.timeSinceStartup + TimeoutSeconds)
                .ToString("R", CultureInfo.InvariantCulture));

            settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
            settings.ActivePlayerDataBuilderIndex = 1; // BuildScriptPackedPlayMode
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
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
        if (_bound)
            return;
        _bound = true;
        Application.logMessageReceived += OnLog;
        EditorApplication.update += OnUpdate;
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (condition.Contains("[Content] Gameplay HUD attached to the stable UIDocument root.",
                StringComparison.Ordinal))
        {
            // 不在首次挂载日志出现时立即成功；再等待十帧，覆盖 UIDocument
            // 延迟重建导致 HUD 只显示一帧的回归。
            SessionState.SetInt(HudConfirmationFrameKey, Time.frameCount + 10);
        }
        else if (type == LogType.Error &&
                 (condition.Contains("[Content] Fatal bootstrap error", StringComparison.Ordinal) ||
                  condition.Contains("Missing Script", StringComparison.Ordinal)))
        {
            SessionState.SetString(FailureKey, condition);
            BeginFinish();
        }
    }

    private static void OnUpdate()
    {
        if (!SessionState.GetBool(ActiveKey, false))
            return;

        if (SessionState.GetBool(FinishingKey, false))
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                RestoreAndExit(
                    SessionState.GetBool(SuccessKey, false),
                    SessionState.GetString(FailureKey, string.Empty));
            }
            return;
        }

        int confirmationFrame = SessionState.GetInt(HudConfirmationFrameKey, -1);
        if (EditorApplication.isPlaying && confirmationFrame >= 0 &&
            Time.frameCount >= confirmationFrame)
        {
            UIManager uiManager = UIManager.Instance;
            if (uiManager != null && uiManager.IsGameplayHudAttachedAndVisible)
            {
                SessionState.SetBool(SuccessKey, true);
            }
            else
            {
                SessionState.SetString(
                    FailureKey,
                    "Gameplay HUD detached or became hidden after its initial frame.");
            }
            BeginFinish();
            return;
        }

        if (double.TryParse(
                SessionState.GetString(DeadlineKey, "0"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double deadline) &&
            EditorApplication.timeSinceStartup >= deadline)
        {
            SessionState.SetString(
                FailureKey,
                "Timed out waiting for the packed-play UI catalog activation marker.");
            BeginFinish();
        }
    }

    private static void BeginFinish()
    {
        if (SessionState.GetBool(FinishingKey, false))
            return;
        SessionState.SetBool(FinishingKey, true);
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.ExitPlaymode();
    }

    private static void RestoreAndExit(bool succeeded, string failure)
    {
        Application.logMessageReceived -= OnLog;
        EditorApplication.update -= OnUpdate;
        _bound = false;
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings != null)
        {
            settings.ActivePlayerDataBuilderIndex = SessionState.GetInt(OriginalBuilderKey, 2);
            string originalProfile = SessionState.GetString(OriginalProfileKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(originalProfile))
                settings.activeProfileId = originalProfile;
            AssetDatabase.SaveAssets();
        }
        SessionState.EraseBool(ActiveKey);

        if (succeeded)
        {
            Debug.Log("[Content] Addressables A3 Packed Play Mode smoke passed.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[Content] Addressables A3 Packed Play Mode smoke failed: " + failure);
            EditorApplication.Exit(1);
        }
    }
}
