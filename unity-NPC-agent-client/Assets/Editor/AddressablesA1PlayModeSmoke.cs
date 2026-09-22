using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class AddressablesA1PlayModeSmoke
{
    private const string LastSuccessKey = "gamewithllm.content.last-success.v1";
    private const string ActiveSessionKey = "gamewithllm.a1-smoke.active";
    private const string DeadlineSessionKey = "gamewithllm.a1-smoke.deadline";
    private const string FinishingSessionKey = "gamewithllm.a1-smoke.finishing";
    private const string ExitCodeSessionKey = "gamewithllm.a1-smoke.exit-code";
    private const string ModeSessionKey = "gamewithllm.a1-smoke.mode";

    static AddressablesA1PlayModeSmoke()
    {
        if (SessionState.GetBool(ActiveSessionKey, false))
            AttachPoller();
    }

    public static void RunFromCommandLine()
    {
        Start("online");
    }

    public static void RunOfflineFromCommandLine()
    {
        Start("offline");
    }

    private static void Start(string mode)
    {
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Editor is already entering Play Mode.");
            PlayerPrefs.DeleteKey(LastSuccessKey);
            PlayerPrefs.Save();
            EditorSceneManager.OpenScene(
                "Assets/Scenes/SampleScene.unity",
                OpenSceneMode.Single);
            SessionState.SetBool(ActiveSessionKey, true);
            SessionState.SetString(
                DeadlineSessionKey,
                DateTime.UtcNow.AddSeconds(45).Ticks.ToString());
            SessionState.SetBool(FinishingSessionKey, false);
            SessionState.SetInt(ExitCodeSessionKey, 1);
            SessionState.SetString(ModeSessionKey, mode);
            AttachPoller();
            EditorApplication.EnterPlaymode();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(ActiveSessionKey, false))
        {
            EditorApplication.update -= Poll;
            return;
        }

        if (SessionState.GetBool(FinishingSessionKey, false))
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                int exitCode = SessionState.GetInt(ExitCodeSessionKey, 1);
                ClearSession();
                EditorApplication.Exit(exitCode);
            }
            return;
        }
        if (!EditorApplication.isPlaying)
            return;
        try
        {
            AgentHostClient host = UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
            string mode = SessionState.GetString(ModeSessionKey, "online");
            if (mode == "offline" && host != null &&
                host.ContentBootstrapState == ClientContentBootstrapState.Failed)
            {
                PlayerMock[] blockedPlayers = UnityEngine.Object.FindObjectsByType<PlayerMock>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                ContentBootstrapOverlay overlay =
                    UnityEngine.Object.FindFirstObjectByType<ContentBootstrapOverlay>();
                if (blockedPlayers.Length == 0 || blockedPlayers.Any(player => player.enabled))
                    throw new InvalidOperationException(
                        "Offline bootstrap failure did not keep gameplay input blocked.");
                if (overlay == null || !overlay.isActiveAndEnabled)
                    throw new InvalidOperationException(
                        "Offline bootstrap failure has no active local error overlay.");

                Debug.Log(
                    "[Content] A1 OFFLINE UI READY: no success marker, runtime/game blocked, " +
                    "local retry overlay active.");
                Finish(0);
                return;
            }

            if (host != null && host.IsContentReady)
            {
                PlayerMock[] players = UnityEngine.Object.FindObjectsByType<PlayerMock>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                if (players.Length == 0 || players.Any(player => !player.enabled))
                    throw new InvalidOperationException(
                        "Content activated but gameplay input was not re-enabled.");
                if (host.ContentBootstrapState !=
                    ClientContentBootstrapState.EnableRuntimeGameUi)
                    throw new InvalidOperationException(
                        $"Unexpected final content state: {host.ContentBootstrapState}.");

                Debug.Log(mode == "offline"
                    ? "[Content] A1 OFFLINE CACHE READY: cached catalog/probe activated and runtime/game/UI enabled."
                    : "[Content] A1 PLAY MODE READY: required remote probe downloaded, runtime/game/UI enabled.");
                Finish(0);
                return;
            }

            long deadlineTicks = long.Parse(SessionState.GetString(
                DeadlineSessionKey,
                DateTime.UtcNow.Ticks.ToString()));
            if (DateTime.UtcNow.Ticks >= deadlineTicks)
            {
                string state = host == null ? "host-missing" : host.ContentBootstrapState.ToString();
                throw new TimeoutException(
                    $"A1 Play Mode bootstrap did not become ready; state={state}.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            Finish(1);
        }
    }

    private static void Finish(int exitCode)
    {
        if (SessionState.GetBool(FinishingSessionKey, false))
            return;
        SessionState.SetBool(FinishingSessionKey, true);
        SessionState.SetInt(ExitCodeSessionKey, exitCode);
        EditorApplication.ExitPlaymode();
    }

    private static void AttachPoller()
    {
        EditorApplication.update -= Poll;
        EditorApplication.update += Poll;
    }

    private static void ClearSession()
    {
        EditorApplication.update -= Poll;
        SessionState.EraseBool(ActiveSessionKey);
        SessionState.EraseString(DeadlineSessionKey);
        SessionState.EraseBool(FinishingSessionKey);
        SessionState.EraseInt(ExitCodeSessionKey);
        SessionState.EraseString(ModeSessionKey);
    }
}
