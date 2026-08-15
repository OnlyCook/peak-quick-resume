using System;
using System.Collections;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Drives the whole "quick resume" sequence as a single coroutine that survives scene
    /// loads (lives on a DontDestroyOnLoad object): return to Airport if needed, start a
    /// fresh run of the saved ascent (forced onto the saved scene via
    /// MapBakerLevelOverridePatch), then trigger the checkpoint restore once the level and
    /// local character are ready. Each stage has a timeout; anything unexpected aborts loudly.
    /// </summary>
    public class ResumeOrchestrator : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnMessageOverlay _messageOverlay;
        private OwnLoadEntryPoints _ownLoadEntryPoints;
        private TeleportWatchdog _watchdog;
        private bool _running;
        private bool _lastWaitOk;

        // When set, resume this specific archived save. Null = auto (current run / latest on disk).
        private ArchivedSave _chosen;

        public bool IsRunning => _running;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnMessageOverlay messageOverlay,
            OwnLoadEntryPoints ownLoadEntryPoints, TeleportWatchdog watchdog = null)
        {
            _log = log;
            _cfg = cfg;
            _messageOverlay = messageOverlay;
            _ownLoadEntryPoints = ownLoadEntryPoints;
            _watchdog = watchdog;
        }

        /// <summary>Kick off the resume sequence for the current run / latest save</summary>
        public void RequestResume() => RequestResume(null);

        /// <summary>
        /// When <paramref name="chosen"/> is set, that specific archived checkpoint is loaded
        /// instead of the latest; nothing on disk is modified, only which files get read.
        /// </summary>
        public void RequestResume(ArchivedSave chosen)
        {
            // Routes through the shared cooldown/queue; see OrchestrationLock.
            OrchestrationLock.RunOrQueue("resume", () => RequestResumeNow(chosen), _log);
        }

        private void RequestResumeNow(ArchivedSave chosen)
        {
            if (_running)
            {
                _log.Trace("Resume already in progress; ignoring request.");
                return;
            }

            _chosen = chosen;

            if (_ownLoadEntryPoints == null)
            {
                _log.LogError("Cannot resume: our own restore path is unavailable (components failed to initialize).");
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

            if (!OrchestrationLock.TryAcquire(LockOwner))
            {
                _log.Trace("Cannot resume: a restart is already in progress; ignoring request.");
                return;
            }

            StartCoroutine(ResumeRoutine());
        }

        private const string LockOwner = "resume";

        private IEnumerator ResumeRoutine()
        {
            _running = true;

            // Avoids false-positiving a watch window still active from a prior load; see TeleportWatchdog.
            _watchdog?.LiftWatch();
            RunLauncher.ClearVanillaQuicksaveResume(_log);

            float timeout = Mathf.Max(1f, _cfg.StepTimeout.Value);
            _log.LogInfo("=== Quick Resume: sequence START ===");
            // No HeightClimbed credit for the airport->spawn->warp altitude swings a resume causes.
            HeightAchievementGuard.Suppress("resume sequence");
            Msg(MessagesLocalization.Get(MsgKey.QuickResumeStarting), MsgInfo);

            // --- 1. Decide which run to resume (ascent or custom) ---
            SaveTarget target = _chosen != null ? _chosen.Target : ResolveTarget();
            int ascent = target.Ascent; // custom runs force ascent 0 in the game anyway
            _log.Trace($"[stage] Target={target} (ascent={ascent}, custom={target.IsCustom}, "
                + $"chosen={_chosen != null}). Starting scene='{RunLauncher.ActiveSceneName}'.");

            // --- 2. Ensure we are at the Airport ---
            if (!RunLauncher.InAirport)
            {
                _log.Trace("[stage] Not at Airport; requesting return to Airport.");
                // Avoid a second airport load if one is already running (e.g. solo auto-return after death).
                if (!RunLauncher.IsLoading)
                {
                    if (!RunLauncher.ReturnToAirport(_log)) { Fail("ReturnToAirport failed"); yield break; }
                }
                else
                {
                    _log.Trace("[stage] A load is already in progress; waiting for the Airport instead of forcing a return.");
                }

                yield return WaitFor(() => RunLauncher.InAirport, timeout, "Airport scene");
                if (!_lastWaitOk) { Fail("Timed out waiting for the Airport scene"); yield break; }
            }
            _log.Trace("[stage] At Airport.");

            // Wait for any loading screen to clear, kiosk.StartGame() no-ops while loading
            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "airport loading to finish");
            if (!_lastWaitOk) { Fail("Timed out waiting for the airport loading screen to clear"); yield break; }
            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.SettleAfterAirport.Value));

            yield return WaitFor(
                () => UnityEngine.Object.FindObjectOfType<AirportCheckInKiosk>() != null,
                timeout, "AirportCheckInKiosk");
            if (!_lastWaitOk) { Fail("Timed out waiting for the check-in kiosk"); yield break; }
            _log.Trace("[stage] Found check-in kiosk.");

            // --- 3. Point our own loader at the saved run & start it ---
            bool offline = PhotonNetwork.OfflineMode;
            SaveSelection selection = _chosen != null
                ? SaveArchive.BuildSelection(_chosen, _log)
                : SaveArchive.TryGetLatestSelection(offline, target, _log);

            // Should be unreachable (the picker only lists the current network mode's saves),
            // but running the solo load path in a coop session would leave clients behind.
            if (selection != null && selection.Offline != offline)
            {
                Fail($"The chosen save is a {(selection.Offline ? "solo" : "co-op")} save but we are "
                    + $"{(offline ? "solo" : "in co-op")}");
                yield break;
            }

            if (selection == null)
            {
                Fail($"No checkpoint save found for {target}");
                Msg(target.IsCustom
                    ? MessagesLocalization.Get(MsgKey.NoSaveCustom)
                    : MessagesLocalization.Get(MsgKey.NoSaveDifficulty, ascent), MsgError);
                yield break;
            }

            // Set before starting: the run started is picked off RunSettings.IsCustomRun.
            if (!RunLauncher.TrySetCustomRun(target.IsCustom, _log))
            { Fail("Could not set custom-run flag before starting"); yield break; }

            if (!_ownLoadEntryPoints.TryPreStartSetSegment(selection))
            {
                Fail($"No usable checkpoint save for {target} (TryPreStartSetSegment returned false)");
                Msg(target.IsCustom
                    ? MessagesLocalization.Get(MsgKey.NoSaveCustom)
                    : MessagesLocalization.Get(MsgKey.NoSaveDifficulty, ascent), MsgError);
                yield break;
            }
            _log.Trace("[stage] Save confirmed for this difficulty; starting fresh run.");
            Msg(MessagesLocalization.Get(MsgKey.StartingFreshRun), MsgInfo);

            // Coop: give other players time to finish loading the Airport; the run-start RPC
            // lives on the kiosk (a scene object) so a client still loading wouldn't receive it.
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

            // Must land before StartRun's networked scene load: MapHandler.InitializeMap
            // (patched by TerrainRandomiserCompat) runs the instant each peer's scene loads.
            _ownLoadEntryPoints.Network?.ArmTerrainRandomizerSuppressionAll();

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

            RunLauncher.ClearBufferedRpcs(_log);

            if (!RunLauncher.StartRun(ascent, _log)) { Fail("StartRun failed"); yield break; }
            _log.Trace("[stage] StartRun invoked; waiting for the level to load.");

            // --- 4. Wait for the level, then trigger the checkpoint load ---
            // Wait to leave the Airport first so the current scene isn't mistaken for the new level.
            yield return WaitFor(() => !RunLauncher.InAirport, timeout, "leaving the Airport");
            if (!_lastWaitOk) { Fail("Run did not start (still at the Airport after StartRun)"); yield break; }

            yield return WaitFor(() => RunLauncher.InLevel, timeout, "level scene");
            if (!_lastWaitOk) { Fail("Timed out waiting for the level scene to load"); yield break; }
            _log.Trace($"[stage] Level scene loaded: '{RunLauncher.ActiveSceneName}'.");

            yield return WaitFor(() => !RunLauncher.IsLoading, timeout, "level loading to finish");
            if (!_lastWaitOk) { Fail("Timed out waiting for the level loading screen to clear"); yield break; }

            yield return WaitFor(() => LocalCharacterExists(), timeout, "local character");
            if (!_lastWaitOk) { Fail("Timed out waiting for the local character to spawn"); yield break; }
            _log.Trace("[stage] Local character present.");

            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.SettleAfterLevel.Value));

            // Coop: LoadPlayerCoop refuses until every client has reported ready; wait it out
            // here instead of firing a doomed load. See OwnNetwork.CheckReadyStatusForPlayers.
            if (!PhotonNetwork.OfflineMode)
            {
                if (_cfg.OwnEnableClientReadyStatusCheck.Value)
                {
                    _log.Trace("[stage] Coop: waiting for all clients to report ready...");
                    Msg(MessagesLocalization.Get(MsgKey.WaitingForPlayers), MsgInfo);
                    Func<bool> allReady = () => _ownLoadEntryPoints.Network.CheckReadyStatusForPlayers();
                    // Own (longer) timeout: a slower client's ready RPC retries for up to a
                    // minute (see OwnNetwork.SendReadyStatusToMaster).
                    float readyTimeout = Mathf.Max(1f, _cfg.CoopReadyTimeout.Value);
                    yield return WaitFor(allReady, readyTimeout, "all clients ready");
                    if (!_lastWaitOk)
                    {
                        Fail("Timed out waiting for all clients to be ready (some players may still be loading)");
                        Msg(MessagesLocalization.Get(MsgKey.PlayersTimedOut), MsgError);
                        yield break;
                    }
                    _log.Trace("[stage] Coop: all clients ready.");
                }
            }

            _log.Trace("[stage] Triggering our own restore.");
            if (!_ownLoadEntryPoints.TryLoadPlayer(selection)) { Fail("Load call failed"); yield break; }

            // TryLoadPlayer is fire-and-forget; wait for RestoreComplete (not the full
            // TeleportInProgress, which also covers the purely-cosmetic wake-up animation)
            // before declaring success. Don't hard-fail on timeout, the load already succeeded.
            yield return WaitFor(() => _ownLoadEntryPoints.RestoreComplete, timeout, "restore to finish");
            if (!_lastWaitOk)
                _log.LogWarning("[stage] Restore didn't report done in time; showing the completion message anyway.");

            _log.LogInfo("=== Quick Resume: sequence COMPLETE (checkpoint load invoked) ===");
            Msg(MessagesLocalization.Get(MsgKey.SaveLoadedWelcomeBack), MsgSuccess);
            _chosen = null;

            // Hold the lock until OwnTeleportSequence's tail (incl. TeleportClientsToHost's
            // up-to-32s client-arrival confirmation) finishes, not just RestoreComplete above.
            // Releasing early let a second Resume/Restart start while a client was still
            // mid-warp, stomping the same OwnTeleportSequence singleton instance.
            float teleportTailTimeout = Mathf.Max(timeout, 40f);
            yield return WaitFor(() => !_ownLoadEntryPoints.TeleportInProgress, teleportTailTimeout,
                "teleport sequence to fully finish");
            if (!_lastWaitOk)
                _log.LogWarning("[stage] Teleport sequence still running after its own tail timeout; "
                    + "releasing the lock anyway to avoid a permanent stall.");

            // Each client runs its own independently-timed local wake-up (RunClientPresentationExit);
            // wait for every client to confirm theirs is done too before releasing the lock,
            // or a Restart's LoadAirport() can tear the scene down mid-animation. See AllClientsPresentationDone.
            if (!PhotonNetwork.OfflineMode && _cfg.OwnWakeUpAnimationEnabled.Value)
            {
                yield return WaitFor(() => _ownLoadEntryPoints.Network.AllClientsPresentationDone(),
                    teleportTailTimeout, "all clients to finish their own wake-up presentation");
                if (!_lastWaitOk)
                    _log.LogWarning("[stage] Not every client confirmed their wake-up presentation finished; "
                        + "releasing the lock anyway to avoid a permanent stall.");
            }

            if (!PhotonNetwork.OfflineMode)
                OrchestrationLock.ArmCooldown(_cfg.PostOrchestrationCooldown.Value);

            HeightAchievementGuard.Release("resume sequence");

            _running = false;
            OrchestrationLock.Release(LockOwner);
        }

        // On-screen message colours (reuses the checkpoint mod's overlay via interop)
        private static readonly Color MsgInfo = new Color(0.6f, 0.8f, 1f, 1f);
        private static readonly Color MsgSuccess = new Color(0.5f, 1f, 0.5f, 1f);
        private static readonly Color MsgError = new Color(1f, 0.5f, 0.5f, 1f);

        private void Msg(string text, Color color) => _messageOverlay?.Show(text, color, 4f);

        /// <summary>
        /// Mid-run: use the current run's ascent/custom flag. At the Airport those flags are
        /// stale (currentAscent resets to the boarding-pass default), so pick the newest save
        /// on disk instead.
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
                    _log.Trace("[stage] In a custom run: resuming the custom-run save.");
                    return SaveTarget.Custom();
                }
                _log.Trace($"[stage] In a run: resuming current difficulty (ascent {current}).");
                return SaveTarget.Normal(current);
            }

            bool offline;
            try { offline = PhotonNetwork.OfflineMode; } catch { offline = true; }

            if (SaveDiscovery.TryGetLatestSave(_log, offline, out SaveTarget latest))
            {
                _log.Trace($"[stage] At Airport: using latest save on disk ({latest}).");
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
            // Must release on the abort paths too, or a failed resume would leave height
            // credit paused until the guard's own safety timeout expires
            HeightAchievementGuard.Release("resume aborted");
            _chosen = null;
            _running = false;
            OrchestrationLock.Release(LockOwner);
        }
    }
}
