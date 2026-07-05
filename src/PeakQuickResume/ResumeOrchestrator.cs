using System;
using System.Collections;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Drives the whole "quick resume" sequence as a single coroutine that survives
    /// scene loads (it lives on a DontDestroyOnLoad object). The sequence:
    ///
    ///   1. Capture the target ascent (difficulty) of the run we just left
    ///   2. If not already at the Airport, go there (EndScreen -> ReturnToAirport)
    ///   3. At the Airport: tell the checkpoint mod which ascent to load, confirm a
    ///      save exists, then start a fresh run of that ascent (kiosk.StartGame)
    ///      The checkpoint mod's MapBaker.GetLevel patch forces the SAVED scene
    ///   4. Once the level scene + local character are ready, trigger the checkpoint
    ///      restore (its offline/coop load), settling any state first
    ///
    /// Each stage has a timeout; anything unexpected aborts loudly rather than
    /// leaving the player in a half-loaded state
    /// </summary>
    public class ResumeOrchestrator : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private CheckpointInterop _checkpoint;
        private TeleportConfigOverride _teleportOverride;
        private TeleportWatchdog _watchdog;
        private bool _running;
        private bool _lastWaitOk;

        // When set, resume this specific archived save (restore it over the checkpoint
        // mod's canonical file before starting). Null = auto (current run / latest on disk)
        private ArchivedSave _chosen;

        // Captured at confirm time (see Plugin.ConfirmLoad); the teleportJumpLogic value
        // to temporarily apply for this one load, or null for no override. See
        // TeleportConfigOverride / ROADMAP.md Phase 6 step 2
        private int? _teleportOverrideValue;

        public bool IsRunning => _running;

        public void Init(ManualLogSource log, PluginConfig cfg, CheckpointInterop checkpoint,
            TeleportConfigOverride teleportOverride = null, TeleportWatchdog watchdog = null)
        {
            _log = log;
            _cfg = cfg;
            _checkpoint = checkpoint;
            _teleportOverride = teleportOverride;
            _watchdog = watchdog;
        }

        /// <summary>Kick off the resume sequence for the current run / latest save</summary>
        public void RequestResume() => RequestResume(null, null);

        /// <summary>
        /// Kick off the resume sequence. When <paramref name="chosen"/> is set, that
        /// specific archived checkpoint is restored over the checkpoint mod's canonical
        /// file before the run starts, so an OLDER checkpoint can be loaded on demand.
        /// <paramref name="teleportOverrideValue"/> is the teleportJumpLogic value
        /// (captured at confirm time) to temporarily apply for just this load's
        /// checkpoint restore, or null for no override
        /// </summary>
        public void RequestResume(ArchivedSave chosen, int? teleportOverrideValue = null)
        {
            if (_running)
            {
                _log.LogInfo("Resume already in progress; ignoring request.");
                return;
            }

            _chosen = chosen;
            _teleportOverrideValue = teleportOverrideValue;

            if (!_checkpoint.IsAvailable)
            {
                _log.LogError("Cannot resume: checkpoint mod interop is not available.");
                return;
            }

            if (!RunLauncher.IsHost)
            {
                _log.LogWarning("Cannot resume: only the host / offline player can start and load a run.");
                Msg(MessagesLocalization.Get(MsgKey.OnlyHostResume), MsgError);
                return;
            }

            if (RunLauncher.InTitle)
            {
                _log.LogWarning("Cannot resume from the Title screen. Load into the game first.");
                Msg(MessagesLocalization.Get(MsgKey.LoadIntoGameFirst), MsgError);
                return;
            }

            StartCoroutine(ResumeRoutine());
        }

        private IEnumerator ResumeRoutine()
        {
            _running = true;

            // Lift any watch window still running from a prior load right away: the
            // Airport-return/fresh-run-start below is US legitimately moving the player,
            // not the checkpoint mod's own teleport, and would otherwise look like a bad
            // teleport to a watch window that's still active from that prior load (see
            // TeleportWatchdog.LiftWatch)
            _watchdog?.LiftWatch();

            float timeout = Mathf.Max(1f, _cfg.StepTimeout.Value);
            _log.LogInfo("=== Quick Resume: sequence START ===");
            Msg(MessagesLocalization.Get(MsgKey.QuickResumeStarting), MsgInfo);

            // --- 1. Decide which run to resume (ascent or custom) ---
            // If the player picked a specific save from the F7 menu, honour it; otherwise
            // resolve from context (current run mid-run, or newest on disk at the Airport)
            SaveTarget target = _chosen != null ? _chosen.Target : ResolveTarget();
            int ascent = target.Ascent; // custom runs force ascent 0 in the game anyway
            _log.LogInfo($"[stage] Target={target} (ascent={ascent}, custom={target.IsCustom}, "
                + $"chosen={_chosen != null}). Starting scene='{RunLauncher.ActiveSceneName}'.");

            // --- 2. Ensure we are at the Airport ---
            if (!RunLauncher.InAirport)
            {
                _log.LogInfo("[stage] Not at Airport; requesting return to Airport.");
                // Don't kick off a second airport load if one is already running
                // (e.g. solo auto-returns to the Airport a few seconds after death)
                if (!RunLauncher.IsLoading)
                {
                    if (!RunLauncher.ReturnToAirport(_log)) { Fail("ReturnToAirport failed"); yield break; }
                }
                else
                {
                    _log.LogInfo("[stage] A load is already in progress; waiting for the Airport instead of forcing a return.");
                }

                yield return WaitFor(() => RunLauncher.InAirport, timeout, "Airport scene");
                if (!_lastWaitOk) { Fail("Timed out waiting for the Airport scene"); yield break; }
            }
            _log.LogInfo("[stage] At Airport.");

            // Wait for any loading screen to clear, kiosk.StartGame() no-ops while loading
            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "airport loading to finish");
            if (!_lastWaitOk) { Fail("Timed out waiting for the airport loading screen to clear"); yield break; }
            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.SettleAfterAirport.Value));

            yield return WaitFor(
                () => UnityEngine.Object.FindObjectOfType<AirportCheckInKiosk>() != null,
                timeout, "AirportCheckInKiosk");
            if (!_lastWaitOk) { Fail("Timed out waiting for the check-in kiosk"); yield break; }
            _log.LogInfo("[stage] Found check-in kiosk.");

            // --- 3. Point the checkpoint mod at the saved run & start it ---
            // If a specific archived checkpoint was chosen, copy it over the checkpoint
            // mod's canonical file now (before it reads it in PreStartSetSegment/load)
            if (_chosen != null && !SaveArchive.Restore(_chosen, _log))
            { Fail("Could not restore the chosen save over the checkpoint file"); yield break; }

            // The checkpoint mod picks its save-file name off RunSettings.IsCustomRun,
            // so set that FIRST (both ways), otherwise a stale flag resumes the wrong
            // save, or PreStartSetSegment looks in the wrong file
            if (!RunLauncher.TrySetCustomRun(target.IsCustom, _log))
            { Fail("Could not set custom-run flag before starting"); yield break; }
            if (!_checkpoint.TrySetSelectedAscent(ascent)) { Fail("Could not set selected ascent on checkpoint mod"); yield break; }

            if (!_checkpoint.TryPreStartSetSegment())
            {
                Fail($"No checkpoint save found for {target} (PreStartSetSegment returned false)");
                Msg(target.IsCustom
                    ? MessagesLocalization.Get(MsgKey.NoSaveCustom)
                    : MessagesLocalization.Get(MsgKey.NoSaveDifficulty, ascent), MsgError);
                yield break;
            }
            _log.LogInfo("[stage] Save confirmed for this difficulty; starting fresh run.");
            Msg(MessagesLocalization.Get(MsgKey.StartingFreshRun), MsgInfo);

            // Coop: give other players time to finish loading the Airport before we fire
            // the run-start. The run-start RPC lives on the kiosk (a scene object), so a
            // client still loading the Airport wouldn't receive it
            if (!PhotonNetwork.OfflineMode)
            {
                float coopWait = Mathf.Max(0f, _cfg.CoopAirportSettle.Value);
                if (coopWait > 0f)
                {
                    _log.LogInfo($"[stage] Coop: waiting {coopWait:F1}s for other players to reach the Airport.");
                    yield return new WaitForSeconds(coopWait);
                }
            }

            // Final guard right before the call that is sensitive to loading state
            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "loading to finish before StartRun");
            if (!_lastWaitOk) { Fail("Timed out waiting for loading to clear before StartRun"); yield break; }

            if (!RunLauncher.StartRun(ascent, _log)) { Fail("StartRun failed"); yield break; }
            _log.LogInfo("[stage] StartRun invoked; waiting for the level to load.");

            // --- 4. Wait for the level, then trigger the checkpoint load ---
            // First wait to LEAVE the Airport so we don't mistake the current scene
            // for the new level, then wait for the level scene itself
            yield return WaitFor(() => !RunLauncher.InAirport, timeout, "leaving the Airport");
            if (!_lastWaitOk) { Fail("Run did not start (still at the Airport after StartRun)"); yield break; }

            yield return WaitFor(() => RunLauncher.InLevel, timeout, "level scene");
            if (!_lastWaitOk) { Fail("Timed out waiting for the level scene to load"); yield break; }
            _log.LogInfo($"[stage] Level scene loaded: '{RunLauncher.ActiveSceneName}'.");

            // Wait for the level's own loading screen to finish and the character to exist
            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "level loading to finish");
            if (!_lastWaitOk) { Fail("Timed out waiting for the level loading screen to clear"); yield break; }

            yield return WaitFor(() => LocalCharacterExists(), timeout, "local character");
            if (!_lastWaitOk) { Fail("Timed out waiting for the local character to spawn"); yield break; }
            _log.LogInfo("[stage] Local character present.");

            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.SettleAfterLevel.Value));

            // Coop: LoadPlayerCoop refuses ("Please wait until everybody is ready!")
            // until every client has reported ready. Clients auto-report once they're
            // in the level, so wait that out here instead of firing a doomed load
            if (!PhotonNetwork.OfflineMode && _checkpoint.ReadyCheckEnabled())
            {
                _log.LogInfo("[stage] Coop: waiting for all clients to report ready...");
                Msg(MessagesLocalization.Get(MsgKey.WaitingForPlayers), MsgInfo);
                yield return WaitFor(() => _checkpoint.AllClientsReady(), timeout, "all clients ready");
                if (!_lastWaitOk)
                {
                    Fail("Timed out waiting for all clients to be ready (some players may still be loading)");
                    Msg(MessagesLocalization.Get(MsgKey.PlayersTimedOut), MsgError);
                    yield break;
                }
                _log.LogInfo("[stage] Coop: all clients ready.");
            }

            // Don't fight the checkpoint mod if it's mid-load for some reason
            float guard = 0f;
            while (_checkpoint.IsCurrentlyLoading && guard < timeout)
            {
                guard += Time.deltaTime;
                yield return null;
            }

            _log.LogInfo("[stage] Triggering checkpoint restore.");

            // Apply the Shift/Alt override (if any) for exactly the duration of this
            // synchronous call - see TeleportConfigOverride for why this needs to be a
            // flag rather than relying on ambient Input at this point (the user likely
            // released the key seconds ago, back when they confirmed the load)
            bool loadOk;
            if (_teleportOverride != null) _teleportOverride.IsDrivingOurOwnLoad = true;
            try
            {
                _teleportOverride?.ApplyForOurOwnLoad(_teleportOverrideValue);
                loadOk = _checkpoint.TryLoadPlayer();
            }
            finally
            {
                if (_teleportOverride != null) _teleportOverride.IsDrivingOurOwnLoad = false;
            }
            if (!loadOk) { Fail("Checkpoint load call failed"); yield break; }

            _log.LogInfo("=== Quick Resume: sequence COMPLETE (checkpoint load invoked) ===");
            Msg(MessagesLocalization.Get(MsgKey.SaveLoadedWelcomeBack)
                + TeleportConfigOverride.FormatIndicator(_teleportOverride?.LastAppliedOverride), MsgSuccess);
            _chosen = null;
            _teleportOverrideValue = null;
            _running = false;
        }

        // On-screen message colours (reuses the checkpoint mod's overlay via interop)
        private static readonly Color MsgInfo = new Color(0.6f, 0.8f, 1f, 1f);
        private static readonly Color MsgSuccess = new Color(0.5f, 1f, 0.5f, 1f);
        private static readonly Color MsgError = new Color(1f, 0.5f, 0.5f, 1f);

        private void Msg(string text, Color color) => _checkpoint?.TryShowMessage(text, color, 4f);

        /// <summary>
        /// Which save should we load, a normal difficulty (ascent) or a custom run?
        ///  - Mid-run / post-death inside a level: the run you were just in tells us
        ///    everything, <c>Ascents.currentAscent</c> for the difficulty and
        ///    <c>RunSettings.IsCustomRun</c> for whether it was a custom run
        ///  - At the Airport: <c>currentAscent</c> is only the boarding-pass default (0)
        ///    and the custom flag may be stale, so instead pick the newest save on disk
        ///    ("choose the latest"), which also tells us if it was custom
        /// </summary>
        private SaveTarget ResolveTarget()
        {
            int current;
            try { current = Ascents.currentAscent; }
            catch (Exception e) { _log.LogError($"Could not read Ascents.currentAscent: {e}"); current = 0; }

            if (!RunLauncher.InAirport)
            {
                if (RunLauncher.IsCustomRun)
                {
                    _log.LogInfo("[stage] In a custom run: resuming the custom-run save.");
                    return SaveTarget.Custom();
                }
                _log.LogInfo($"[stage] In a run: resuming current difficulty (ascent {current}).");
                return SaveTarget.Normal(current);
            }

            bool offline;
            try { offline = PhotonNetwork.OfflineMode; } catch { offline = true; }

            if (SaveDiscovery.TryGetLatestSave(_log, offline, out SaveTarget latest))
            {
                _log.LogInfo($"[stage] At Airport: using latest save on disk ({latest}).");
                return latest;
            }

            _log.LogWarning($"[stage] At Airport: no saves found on disk; falling back to currentAscent ({current}).");
            return SaveTarget.Normal(current);
        }

        private static bool LocalCharacterExists()
        {
            try { return Character.localCharacter != null; }
            catch { return false; }
        }

        // Polls a condition until true or timeout, storing the outcome in
        // _lastWaitOk (Unity coroutines can't return a value from `yield return`)
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
            _log.LogError($"Quick Resume aborted: {reason}.");
            _chosen = null;
            _running = false;
        }
    }
}
