using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Watches the local player after a checkpoint-mod teleport for the known
    /// symptoms of its intermittent upstream bug (see ROADMAP.md Phase 6):
    ///   - never teleported at all (client stays wherever it was, checked once
    ///     immediately once the load finishes)
    ///   - falling through the world (drifted far below the intended target)
    ///   - died shortly after the load (catches an out-of-bounds/void kill that can
    ///     happen faster than the fall-distance threshold below)
    ///   - warp-loop glitching, detected by counting repeat <c>WarpPlayerRPC</c>
    ///     calls for the local player AFTER the load already reported itself done
    ///     (the checkpoint mod's own re-teleport-correction loop can keep running
    ///     for up to 30s in the background after "Save game loaded!" already
    ///     showed, see ROADMAP.md session 9 test notes - this is NOT inferred from
    ///     position/velocity sampling, which turned out unreliable: a teleport RPC
    ///     snaps position directly rather than producing a smooth, sign-flipping
    ///     velocity, and repeated corrections keep resetting any "peak height"
    ///     baseline before a rolling-peak fall check could ever accumulate)
    ///
    /// Always flags, logs, and shows the on-screen hint first, then (steps 4-5, both
    /// gated on that same flag, never unconditional) <see cref="RevertFallDamageRoutine"/>
    /// refunds any Injury gained in the following window and <see cref="PositionRecoveryRoutine"/>
    /// forces the player back to the real target if the checkpoint mod's own correction
    /// loop still hasn't settled them there by then - see ROADMAP.md Phase 6 steps 4-5
    ///
    /// Separately (not gated on a flag - see <see cref="ShouldSuppressWarp"/>), also
    /// cancels every <c>WarpPlayerRPC</c> for the local player for a window after the
    /// load reports itself done: real session logs root-caused the warp-loop glitch to
    /// the checkpoint mod's own correction loop re-warping far more often than it needs
    /// to (its retry cadence checks too infrequently for gravity between checks to ever
    /// reliably land back within its own tolerance), so cancelling those corrections
    /// outright, rather than just detecting and reacting to their effects, is the more
    /// direct fix - see ROADMAP.md Phase 6 "warp suppression"
    /// </summary>
    public class TeleportWatchdog : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private CheckpointInterop _checkpoint;
        private Coroutine _running;
        private Vector3? _pendingTargetPos;
        private bool _loadInProgress;

        // Repeat-warp bookkeeping for the post-load glitch check, see OnLocalWarp. Kept
        // updated even after a glitch is flagged (not just while _watching), so steps
        // 4-5's recovery coroutines can tell whether the checkpoint mod's own
        // correction loop is still actively re-warping at their check time
        private readonly List<float> _postLoadWarpTimes = new List<float>();
        private bool _watching;

        // The real teleport target for whichever watch window is currently active (or
        // most recently was), used by FlagBadTeleport as the default recovery target
        // for steps 4-5 when a call site doesn't have a more specific position to hand it
        private Vector3 _currentTargetPos;

        // Warp suppression (see ArmPendingWatch/TeleportWatchdogPatch): true from the
        // moment the load reports itself done until watchdog-window-seconds later,
        // cancels every further WarpPlayerRPC for the local player in between. Root
        // cause confirmed from real session logs (ROADMAP.md Phase 6): the checkpoint
        // mod's own TeleportClientsToHost re-warps a client whenever ITS view of that
        // client's position looks too far off, but checks so infrequently
        // (teleportFramesToWait frames apart) that ordinary gravity pulls the client
        // back out of tolerance between checks nearly every time, so it can loop 30+
        // times fighting its own retry cadence instead of converging
        private bool _suppressPostLoadWarps;
        private Coroutine _suppressionResetRoutine;

        // Sidesteps steps 4-5's own recovery warp (below) suppressing itself: it calls
        // the same Character.WarpPlayerRPC our Harmony prefix intercepts, so without
        // this it would silently cancel its own fix while _suppressPostLoadWarps is set
        private bool _isOwnRecoveryWarp;

        /// <summary>Set once when the current/most recent watch window flags a bad teleport; null otherwise</summary>
        public (float time, Vector3 targetPos)? LastFlaggedTeleport { get; private set; }

        public void Init(ManualLogSource log, PluginConfig cfg, CheckpointInterop checkpoint)
        {
            _log = log;
            _cfg = cfg;
            _checkpoint = checkpoint;
        }

        /// <summary>
        /// Called from <see cref="LoadingScreenPatch"/> the moment the checkpoint mod's
        /// loading screen comes up (fires identically on host and every client, since
        /// it's relayed there via its own RPC). Marks a load as in progress and clears
        /// any stale pending target, so <see cref="OnLocalWarp"/> below only ever
        /// attributes warps that happen during an actual checkpoint load to it
        /// </summary>
        public void BeginLoadWindow()
        {
            _loadInProgress = true;
            _pendingTargetPos = null;

            _suppressPostLoadWarps = false;
            if (_suppressionResetRoutine != null) { StopCoroutine(_suppressionResetRoutine); _suppressionResetRoutine = null; }
        }

        /// <summary>
        /// Called from <see cref="TeleportWatchdogPatch"/>'s prefix on every
        /// <c>WarpPlayerRPC</c> for the local player, before it's allowed to run - see
        /// the <see cref="_suppressPostLoadWarps"/> field comment for why this exists
        /// </summary>
        public bool ShouldSuppressWarp() => _suppressPostLoadWarps && !_isOwnRecoveryWarp;

        /// <summary>
        /// Called from <see cref="TeleportWatchdogPatch"/>'s postfix on the vanilla
        /// <c>Character.WarpPlayerRPC</c) every time the local player is warped, for
        /// ANY reason, at ANY time - this method decides what (if anything) that means
        ///
        /// While a load is in progress (<see cref="BeginLoadWindow"/>): records it as
        /// the pending teleport target, does NOT start watching yet, more warps (or
        /// none at all, on the failure case that exists for) may still follow before
        /// the load is actually done. The watch window itself starts later, from
        /// <see cref="ArmPendingWatch"/>
        ///
        /// While a watch window is active (after a load already reported itself
        /// done): counts it as a "repeat correction", the checkpoint mod re-issuing
        /// its warp because the previous one didn't land right, this is the actual
        /// up/down glitch the player sees. Enough repeats in a short span flags a
        /// warp-loop glitch immediately
        /// </summary>
        public void OnLocalWarp(Vector3 position)
        {
            if (_loadInProgress)
            {
                _pendingTargetPos = position;
                return;
            }

            // Recorded regardless of _watching (see the field comment above) so a
            // still-running position-recovery coroutine from an already-flagged glitch
            // can see whether the checkpoint mod is still actively re-warping
            _postLoadWarpTimes.Add(Time.time);
            const float repeatWindow = 5f;
            _postLoadWarpTimes.RemoveAll(t => Time.time - t > repeatWindow);

            if (!_watching) return;

            int threshold = _cfg?.GlitchOscillationCount.Value ?? 4;
            if (_postLoadWarpTimes.Count >= threshold)
            {
                FlagBadTeleport("warp-loop glitch", $"{_postLoadWarpTimes.Count} repeat WarpPlayerRPC calls "
                    + $"within {repeatWindow}s after the load reported itself done (latest target={position}).");
            }
        }

        /// <summary>
        /// Called once the checkpoint mod shows its "Save game loaded!" message (see
        /// <see cref="SavegameLoadedMessagePatch"/>'s postfix), the actual end of a
        /// load. Starts the watch window using whichever teleport target was last
        /// recorded via <see cref="OnLocalWarp"/> - or, if none was ever recorded,
        /// flags immediately: the load reported itself done without this player ever
        /// receiving a single warp RPC, which is itself proof of a bad teleport (this
        /// is exactly the case a slower host's client hit: no glitching, no falling,
        /// simply never moved)
        /// </summary>
        public void ArmPendingWatch()
        {
            _loadInProgress = false;

            // Cancel every further WarpPlayerRPC for the rest of this window - see the
            // _suppressPostLoadWarps field comment. Armed unconditionally here (not just
            // once a glitch is actually detected) since the checkpoint mod's own retry
            // loop is the direct cause and we want to stop it before it ever needs to
            // repeat 4+ times to trip the detection below
            if (_cfg != null && _cfg.EnableWarpSuppression.Value)
            {
                _suppressPostLoadWarps = true;
                if (_suppressionResetRoutine != null) StopCoroutine(_suppressionResetRoutine);
                _suppressionResetRoutine = StartCoroutine(ClearWarpSuppressionAfterDelay());
            }

            if (_pendingTargetPos == null)
            {
                var pos = Character.localCharacter != null ? Character.localCharacter.Head : Vector3.zero;
                FlagBadTeleport("never teleported",
                    "no warp RPC received before the load reported itself done.", pos);
                return;
            }

            BeginWatch(_pendingTargetPos.Value);
            _pendingTargetPos = null;
        }

        private IEnumerator ClearWarpSuppressionAfterDelay()
        {
            // A small buffer on top of watchdog-window-seconds (warp-suppression-extra-
            // seconds, default 2s): the checkpoint mod's own retry loop times out on its
            // own ~30s clock, started at a slightly different moment than ours, so
            // without this a straggler correction can sneak through right at the
            // boundary just as suppression lifts (observed in testing)
            float delay = Mathf.Max(1f, _cfg.WatchdogWindowSeconds.Value) + Mathf.Max(0f, _cfg.WarpSuppressionExtraSeconds.Value);
            yield return new WaitForSeconds(delay);
            _suppressPostLoadWarps = false;
            _suppressionResetRoutine = null;
        }

        /// <summary>
        /// Stops any watch window in progress (or pending) without flagging anything.
        /// Called right before OUR OWN code intentionally moves the player away from a
        /// just-loaded position (e.g. RestartOrchestrator/ResumeOrchestrator returning to
        /// the Airport, or Plugin.RequestReturnToAirport), since that legitimate move
        /// looks identical to "never teleported"/"fall-through"/warp-loop symptoms to a
        /// watch window still running from a PRIOR load - the checkpoint mod's own load
        /// path already resets this via BeginLoadWindow/ArmPendingWatch, but a plain scene
        /// return we drive ourselves never goes through that
        /// </summary>
        public void LiftWatch()
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            _watching = false;
            _postLoadWarpTimes.Clear();
            _pendingTargetPos = null;
            _loadInProgress = false;
        }

        /// <summary>Start (or restart) watching the local player after a teleport to <paramref name="targetPos"/></summary>
        public void BeginWatch(Vector3 targetPos)
        {
            if (_cfg == null || !_cfg.EnableTeleportWatchdog.Value) return;

            if (_running != null) StopCoroutine(_running);
            _postLoadWarpTimes.Clear();
            _currentTargetPos = targetPos;
            _running = StartCoroutine(WatchRoutine(targetPos));
        }

        private IEnumerator WatchRoutine(Vector3 targetPos)
        {
            // Give the teleport RPC(s) a moment to actually land before we start
            // sampling, otherwise the pre-teleport position poisons the baseline
            yield return new WaitForSeconds(1f);

            var c = Character.localCharacter;
            if (c == null)
            {
                _log.LogWarning("TeleportWatchdog: no local character to watch, aborting this window.");
                yield break;
            }

            float window = _cfg.WatchdogWindowSeconds.Value;
            float fallThreshold = _cfg.FallDistanceThreshold.Value;
            float neverTeleportedThreshold = _cfg.NeverTeleportedDistanceThreshold.Value;

            // --- never teleported at all --- checked once, immediately: nothing to
            // sample over time when the player simply never moved. Backstops the
            // "no warp RPC at all" check in ArmPendingWatch for the case where a warp
            // DID fire but landed nowhere near the actual target
            float distFromTarget = Vector3.Distance(c.Head, targetPos);
            if (distFromTarget >= neverTeleportedThreshold)
            {
                FlagBadTeleport("never teleported",
                    $"{distFromTarget:F0}m from target right after load (target={targetPos}, current={c.Head}).");
                yield break;
            }

            float startTime = Time.time;
            _watching = true;

            while (Time.time - startTime < window)
            {
                c = Character.localCharacter;
                if (c == null) break; // scene changed / character despawned; nothing left to watch

                // --- knocked out / died shortly after loading --- catches an instant
                // out-of-bounds/void kill, which can happen faster than any
                // fall-distance threshold. Checked at "knocked out" (fullyPassedOut),
                // not full death, since actually dying takes a noticeable few seconds
                // (bleed-out) that a knock-out already tells us plenty about
                if (c.data != null && (c.data.dead || c.data.fullyPassedOut))
                {
                    FlagBadTeleport("knocked out / died shortly after load",
                        $"local character was knocked out or died within {Time.time - startTime:F1}s of the "
                        + $"load finishing (target={targetPos}).");
                    break;
                }

                // --- falling through the world --- fixed baseline off the actual
                // teleport target, NOT a rolling peak: a rolling peak resets upward
                // every time the checkpoint mod's correction loop snaps the player back
                // up, so a real fall-through was never accumulating past it
                float y = c.Head.y;
                if (targetPos.y - y > fallThreshold)
                {
                    FlagBadTeleport("fall-through", $"{targetPos.y - y:F0}m below target "
                        + $"(target y={targetPos.y:F1}, current y={y:F1}).");
                    break;
                }

                // Warp-loop glitch is flagged directly from OnLocalWarp as repeat
                // WarpPlayerRPC calls come in; nothing to poll for here

                yield return null;
            }

            _watching = false;
            _running = null;
        }

        private void FlagBadTeleport(string kind, string detail, Vector3? targetPosOverride = null)
        {
            _log.LogWarning($"TeleportWatchdog: flagged bad teleport ({kind}). {detail}");

            // Falls back to _currentTargetPos (the real intended teleport target, set in
            // BeginWatch) rather than the character's current position, so steps 4-5
            // below know where "correct" actually is - a targetPosOverride is only ever
            // passed when even that isn't known (the "no warp RPC at all" case, where
            // there's genuinely nothing better to fall back on than "wherever we are")
            Vector3 target = targetPosOverride ?? _currentTargetPos;
            LastFlaggedTeleport = (Time.time, target);

            // The log line above is authoritative regardless of what's on screen, but
            // try to actually show it too. The checkpoint mod's message overlay is a
            // single shared text+timer, so a later ShowMessage call from IT (e.g. a
            // "Save game loaded!" RPC that arrives late on a slow host) can stomp ours
            // right after we show it. Re-showing a couple more times over the next
            // several seconds gives the player a real shot at actually seeing it even
            // if the first attempt loses that race (harmless if it doesn't: it's the
            // same message either way)
            StartCoroutine(ShowMessageResiliently());

            if (_running != null) { StopCoroutine(_running); _running = null; }
            _watching = false;

            // Phase 6 steps 4-5: auto-fixes, only ever engaged off a flag raised above,
            // never unconditionally on every teleport
            if (_cfg != null && _cfg.EnableFallDamageRevert.Value)
                StartCoroutine(RevertFallDamageRoutine());
            if (_cfg != null && _cfg.EnablePositionRecovery.Value)
                StartCoroutine(PositionRecoveryRoutine(target));
        }

        /// <summary>
        /// Step 4: snapshots Injury right when a bad teleport is flagged, waits
        /// <see cref="PluginConfig.DamageRevertDelaySeconds"/>, then refunds any net
        /// increase since (once). Deliberately a net-delta comparison, not a hook into
        /// whatever specifically caused the damage - simplest way to catch fall damage
        /// from being repeatedly warped mid-air before landing (see ROADMAP.md Phase 6
        /// step 4 for the accepted trade-off: any other damage taken in the same short
        /// window gets refunded too)
        /// </summary>
        private IEnumerator RevertFallDamageRoutine()
        {
            var c = Character.localCharacter;
            if (c?.refs?.afflictions == null) yield break;

            float before = c.refs.afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Injury);

            yield return new WaitForSeconds(Mathf.Max(1f, _cfg.DamageRevertDelaySeconds.Value));

            c = Character.localCharacter;
            if (c?.refs?.afflictions == null) yield break;

            float after = c.refs.afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Injury);
            float delta = after - before;
            if (delta > 0f)
            {
                c.refs.afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Injury, delta);
                _log.LogInfo($"TeleportWatchdog: reverted {delta:F3} Injury gained in the "
                    + $"{_cfg.DamageRevertDelaySeconds.Value:F0}s after a flagged bad teleport.");
            }
        }

        /// <summary>
        /// Step 5: waits <see cref="PluginConfig.PositionRecoveryDelaySeconds"/> after a
        /// flag, then forces the local player directly to <paramref name="targetPos"/>
        /// if still further than <see cref="PluginConfig.PositionRecoveryDistanceThreshold"/>
        /// away - cuts a warp-loop glitch short instead of waiting out the checkpoint
        /// mod's own correction loop (observed taking up to ~15s in testing). Calls the
        /// vanilla <c>WarpPlayerRPC</c> directly (not through Photon) since this only
        /// ever needs to move the LOCAL player's own view of themselves, exactly like
        /// the checkpoint mod's own correction would; harmless no-op if by this point
        /// the player already settled near the target on their own
        /// </summary>
        private IEnumerator PositionRecoveryRoutine(Vector3 targetPos)
        {
            yield return new WaitForSeconds(Mathf.Max(1f, _cfg.PositionRecoveryDelaySeconds.Value));

            var c = Character.localCharacter;
            if (c?.data == null) yield break;
            if (c.data.dead || c.data.fullyPassedOut) yield break; // don't yank a dead/knocked-out character around

            float dist = Vector3.Distance(c.Head, targetPos);
            if (dist < _cfg.PositionRecoveryDistanceThreshold.Value) yield break;

            _log.LogWarning($"TeleportWatchdog: still {dist:F0}m from target "
                + $"{_cfg.PositionRecoveryDelaySeconds.Value:F0}s after a flagged bad teleport; "
                + $"forcing position recovery to {targetPos}.");

            // Bypass our own warp suppression for this one call - see the field comment
            _isOwnRecoveryWarp = true;
            try { c.WarpPlayerRPC(targetPos, false); }
            finally { _isOwnRecoveryWarp = false; }
        }

        private IEnumerator ShowMessageResiliently()
        {
            string text = MessagesLocalization.Get(MsgKey.TeleportBugHint);
            var color = new Color(1f, 0.7f, 0.2f, 1f);

            _checkpoint?.TryShowMessage(text, color, 6f);
            yield return new WaitForSeconds(2f);
            _checkpoint?.TryShowMessage(text, color, 6f);
            yield return new WaitForSeconds(3f);
            _checkpoint?.TryShowMessage(text, color, 6f);
        }
    }
}
