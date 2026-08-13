using System;
using System.Collections;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Miscellaneous QoL: "Restart" the current run without quitting or dying.
    ///
    /// Reproduces the FIRST half of <see cref="ResumeOrchestrator"/>'s sequence
    /// (return to Airport, start a fresh run of the same difficulty) and stops
    /// there, it never touches the checkpoint mod or restores a save. The result
    /// is exactly what vanilla does after death/quitting-and-rejoining, minus the
    /// travel time: everyone lands back at the Airport and a brand-new run of the
    /// same ascent begins immediately
    ///
    /// Deliberately has NO dependency on the checkpoint mod, unlike ResumeOrchestrator
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

        /// <summary>
        /// Kick off a restart of the run currently in progress. Only valid while
        /// mid-run (in a Level); the ascent/custom-run flag is captured from the
        /// CURRENT run before anything moves
        /// </summary>
        public void RequestRestart()
        {
            // Route through the shared cooldown/queue first - see OrchestrationLock's
            // remarks. The whole guard chain below is re-evaluated fresh whenever this
            // actually runs (now, or after the queued wait), not stale-checked up front
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

            // Acquire the shared lock right before actually starting - see
            // OrchestrationLock's remarks (this is the exact bug it fixes: a Restart
            // firing while a Resume is still mid-flight raced GameOverHandler.LoadAirport()
            // underneath the Resume and won with a fresh, unrelated run)
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

            // Capture the CURRENT island's scene name while we're still standing in it
            // (RunLauncher.InLevel was just confirmed above) - see
            // OwnLoadEntryPoints.ForceSelectedLevel's remarks for why this is needed at
            // all: without it, the fresh run below re-rolls onto today's daily-rotation
            // scene instead of a fresh run of the island the player was actually just on
            string currentScene = RunLauncher.ActiveSceneName;

            StartCoroutine(RestartRoutine(ascent, custom, currentScene));
        }

        private const string LockOwner = "restart";

        private IEnumerator RestartRoutine(int ascent, bool custom, string currentScene)
        {
            _running = true;

            // See ResumeOrchestrator.ResumeRoutine's equivalent call: the Airport return
            // below is us intentionally moving the player, not a checkpoint-mod teleport,
            // and would otherwise false-positive a watch window still active from a
            // prior load
            _watchdog?.LiftWatch();

            // Same reason as ResumeOrchestrator's equivalent call - a restart drives the
            // very same Airport return, and hangs the same way without this
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


            // Every client must have finished spawning into the Airport before we start the run.
            // RunLauncher.IsLoading above only reports the HOST's loading screen; a client still
            // spawning in has its own LoadingScreenHandler busy, and LoadingScreenHandler.Load
            // REFUSES while that is true ("Tried to load while already loading!"), so the island
            // load RPC we are about to send is silently dropped on their end and they are left
            // standing in the previous level - session-reported as "the client stayed where they
            // were, in another biome which wasn't loaded in". The host log showed the client's
            // spawn requests still repeating on both sides of StartRun. See PlayerRegistration
            if (!PhotonNetwork.OfflineMode)
            {
                yield return WaitFor(PlayerRegistration.AllRegistered,
                    Mathf.Max(1f, _cfg.CoopReadyTimeout.Value), "all players to finish spawning at the Airport");
                if (!_lastWaitOk)
                    _log?.LogWarning($"[stage] Not every player had finished spawning ({PlayerRegistration.Describe()}); "
                        + "starting the run anyway. A client still loading may not follow into the new level.");
            }

            // Force the fresh run onto the SAME island we just left, not whatever
            // vanilla/today's daily rotation would otherwise pick - see
            // OwnLoadEntryPoints.ForceSelectedLevel's remarks
            OwnLoadEntryPoints.ForceSelectedLevel(currentScene);

            if (!RunLauncher.StartRun(ascent, _log)) { Fail("StartRun failed"); yield break; }

            _log.LogInfo("=== Restart: sequence COMPLETE (fresh run started) ===");
            Msg(MessagesLocalization.Get(MsgKey.RunRestarted), MsgSuccess);

            // Arm the post-orchestration cooldown (coop only) - see
            // PostOrchestrationCooldown's remarks. A genuinely FAILED restart (Fail()
            // above) does NOT arm this - nothing actually changed
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
