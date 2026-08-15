using System;
using System.Collections;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// QoL: "Restart" the current run without quitting or dying. Reproduces only the first
    /// half of <see cref="ResumeOrchestrator"/>'s sequence (return to Airport, start a fresh
    /// run of the same difficulty) and has no dependency on the checkpoint mod.
    /// </summary>
    public class RestartOrchestrator : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnMessageOverlay _messageOverlay;
        private TeleportWatchdog _watchdog;
        private bool _running;

        private bool _lastWaitOk;

        public bool IsRunning => _running;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnMessageOverlay messageOverlay, TeleportWatchdog watchdog = null)
        {
            _log = log;
            _cfg = cfg;
            _messageOverlay = messageOverlay;
            _watchdog = watchdog;
        }

        /// <summary>Only valid mid-run; the ascent/custom-run flag is captured from the current run.</summary>
        public void RequestRestart()
        {
            // Routes through the shared cooldown/queue; see OrchestrationLock.
            OrchestrationLock.RunOrQueue("restart", RequestRestartNow, _log);
        }

        private void RequestRestartNow()
        {
            if (_running)
            {
                _log.Trace("Restart already in progress; ignoring request.");
                return;
            }

            if (!RunLauncher.IsHost)
            {
                _log.LogWarning("Cannot restart: only the host / offline player can start a new run.");
                Msg(MessagesLocalization.Get(MsgKey.OnlyHostRestart), MsgError);
                return;
            }

            if (!RunLauncher.InLevel)
            {
                _log.LogWarning($"Restart requested outside a level (scene='{RunLauncher.ActiveSceneName}'); ignoring.");
                return;
            }

            // Prevents a Restart racing GameOverHandler.LoadAirport() under an in-flight Resume; see OrchestrationLock.
            if (!OrchestrationLock.TryAcquire(LockOwner))
            {
                _log.Trace("Cannot restart: a resume is already in progress; ignoring request.");
                return;
            }

            int ascent;
            bool custom;
            try { ascent = Ascents.currentAscent; }
            catch (Exception e) { _log.LogError($"Could not read Ascents.currentAscent: {e}"); ascent = 0; }
            custom = RunLauncher.IsCustomRun;

            // Captured now so the fresh run below targets this island, not today's daily rotation.
            string currentScene = RunLauncher.ActiveSceneName;

            StartCoroutine(RestartRoutine(ascent, custom, currentScene));
        }

        private const string LockOwner = "restart";

        private IEnumerator RestartRoutine(int ascent, bool custom, string currentScene)
        {
            _running = true;

            // Avoids false-positiving a watch window still active from a prior load; see ResumeOrchestrator.
            _watchdog?.LiftWatch();
            RunLauncher.ClearVanillaQuicksaveResume(_log);

            float timeout = Mathf.Max(1f, _cfg.StepTimeout.Value);
            _log.LogInfo($"=== Restart: sequence START (ascent={ascent}, custom={custom}) ===");
            Msg(MessagesLocalization.Get(MsgKey.RestartingRun), MsgInfo);

            if (!RunLauncher.IsLoading)
            {
                if (!RunLauncher.ReturnToAirport(_log)) { Fail("ReturnToAirport failed"); yield break; }
            }

            yield return WaitFor(() => RunLauncher.InAirport, timeout, "Airport scene");
            if (!_lastWaitOk) { Fail("Timed out waiting for the Airport scene"); yield break; }

            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "airport loading to finish");
            if (!_lastWaitOk) { Fail("Timed out waiting for the airport loading screen to clear"); yield break; }
            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.SettleAfterAirport.Value));

            yield return WaitFor(
                () => UnityEngine.Object.FindObjectOfType<AirportCheckInKiosk>() != null,
                timeout, "AirportCheckInKiosk");
            if (!_lastWaitOk) { Fail("Timed out waiting for the check-in kiosk"); yield break; }

            if (!RunLauncher.TrySetCustomRun(custom, _log))
            { Fail("Could not set custom-run flag before starting"); yield break; }

            if (!PhotonNetwork.OfflineMode)
            {
                float coopWait = Mathf.Max(0f, _cfg.CoopAirportSettle.Value);
                if (coopWait > 0f)
                {
                    _log.Trace($"[stage] Coop: waiting {coopWait:F1}s for other players to reach the Airport.");
                    yield return new WaitForSeconds(coopWait);
                }
            }

            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "loading to finish before StartRun");
            if (!_lastWaitOk) { Fail("Timed out waiting for loading to clear before StartRun"); yield break; }


            // RunLauncher.IsLoading only reports the host's loading screen; a client still
            // spawning in refuses LoadingScreenHandler.Load, silently dropping the island load
            // RPC and leaving it stuck in the previous level. See PlayerRegistration.
            if (!PhotonNetwork.OfflineMode)
            {
                yield return WaitFor(PlayerRegistration.AllRegistered,
                    Mathf.Max(1f, _cfg.CoopReadyTimeout.Value), "all players to finish spawning at the Airport");
                if (!_lastWaitOk)
                    _log?.LogWarning($"[stage] Not every player had finished spawning ({PlayerRegistration.Describe()}); "
                        + "starting the run anyway. A client still loading may not follow into the new level.");
            }

            OwnLoadEntryPoints.ForceSelectedLevel(currentScene);
            RunLauncher.ClearBufferedRpcs(_log);

            if (!RunLauncher.StartRun(ascent, _log)) { Fail("StartRun failed"); yield break; }

            _log.LogInfo("=== Restart: sequence COMPLETE (fresh run started) ===");
            Msg(MessagesLocalization.Get(MsgKey.RunRestarted), MsgSuccess);

            if (!PhotonNetwork.OfflineMode)
                OrchestrationLock.ArmCooldown(_cfg.PostOrchestrationCooldown.Value);

            _running = false;
            OrchestrationLock.Release(LockOwner);
        }

        private static readonly Color MsgInfo = new Color(0.6f, 0.8f, 1f, 1f);
        private static readonly Color MsgSuccess = new Color(0.5f, 1f, 0.5f, 1f);
        private static readonly Color MsgError = new Color(1f, 0.5f, 0.5f, 1f);

        private void Msg(string text, Color color) => _messageOverlay?.Show(text, color, 4f);

        private IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string what)
        {
            float elapsed = 0f;
            while (elapsed < timeoutSeconds)
            {
                bool ok;
                try { ok = condition(); }
                catch (Exception e) { _log.LogError($"WaitFor({what}) predicate threw: {e}"); ok = false; }
                if (ok) { _lastWaitOk = true; yield break; }
                elapsed += Time.deltaTime;
                yield return null;
            }
            _log.LogWarning($"WaitFor({what}) timed out after {timeoutSeconds:F1}s.");
            _lastWaitOk = false;
        }

        private void Fail(string reason)
        {
            _log.LogError($"Restart aborted: {reason}.");
            Msg(MessagesLocalization.Get(MsgKey.RestartFailed), MsgError);
            _running = false;
            OrchestrationLock.Release(LockOwner);
        }
    }
}
