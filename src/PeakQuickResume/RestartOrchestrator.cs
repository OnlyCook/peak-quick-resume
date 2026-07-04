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
        private CheckpointInterop _checkpoint; // only used for on-screen messages
        private bool _running;

        private bool _lastWaitOk;

        public bool IsRunning => _running;

        public void Init(ManualLogSource log, PluginConfig cfg, CheckpointInterop checkpoint)
        {
            _log = log;
            _cfg = cfg;
            _checkpoint = checkpoint;
        }

        /// <summary>
        /// Kick off a restart of the run currently in progress. Only valid while
        /// mid-run (in a Level); the ascent/custom-run flag is captured from the
        /// CURRENT run before anything moves
        /// </summary>
        public void RequestRestart()
        {
            if (_running)
            {
                _log.LogInfo("Restart already in progress; ignoring request.");
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

            int ascent;
            bool custom;
            try { ascent = Ascents.currentAscent; }
            catch (Exception e) { _log.LogError($"Could not read Ascents.currentAscent: {e}"); ascent = 0; }
            custom = RunLauncher.IsCustomRun;

            StartCoroutine(RestartRoutine(ascent, custom));
        }

        private IEnumerator RestartRoutine(int ascent, bool custom)
        {
            _running = true;
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
                    _log.LogInfo($"[stage] Coop: waiting {coopWait:F1}s for other players to reach the Airport.");
                    yield return new WaitForSeconds(coopWait);
                }
            }

            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "loading to finish before StartRun");
            if (!_lastWaitOk) { Fail("Timed out waiting for loading to clear before StartRun"); yield break; }

            if (!RunLauncher.StartRun(ascent, _log)) { Fail("StartRun failed"); yield break; }

            _log.LogInfo("=== Restart: sequence COMPLETE (fresh run started) ===");
            Msg(MessagesLocalization.Get(MsgKey.RunRestarted), MsgSuccess);
            _running = false;
        }

        private static readonly Color MsgInfo = new Color(0.6f, 0.8f, 1f, 1f);
        private static readonly Color MsgSuccess = new Color(0.5f, 1f, 0.5f, 1f);
        private static readonly Color MsgError = new Color(1f, 0.5f, 0.5f, 1f);

        private void Msg(string text, Color color) => _checkpoint?.TryShowMessage(text, color, 4f);

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
        }
    }
}
