using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Watches the local player after our own teleport (<see cref="OwnTeleportSequence"/>)
    /// for the known bad-teleport symptoms (see ROADMAP.md Phase 6):
    ///   - never teleported at all (client stays wherever it was, checked once
    ///     immediately once the load finishes)
    ///   - falling through the world (drifted far below the intended target)
    ///   - died shortly after the load (catches an out-of-bounds/void kill that can
    ///     happen faster than the fall-distance threshold below)
    ///   - warp-loop glitching, detected by counting repeat <c>WarpPlayerRPC</c>
    ///     calls for the local player AFTER the load already reported itself done
    ///     (this is NOT inferred from position/velocity sampling, which turned out
    ///     unreliable: a teleport RPC snaps position directly rather than producing a
    ///     smooth, sign-flipping velocity, and repeated corrections keep resetting any
    ///     "peak height" baseline before a rolling-peak fall check could ever accumulate)
    ///
    /// Always flags, logs, and shows the on-screen hint first, then (steps 4-5, both
    /// gated on that same flag, never unconditional) <see cref="RevertFallDamageRoutine"/>
    /// refunds any Injury gained in the following window and <see cref="PositionRecoveryRoutine"/>
    /// forces the player back to the real target if they still haven't settled there by then.
    ///
    /// Our own teleport (<see cref="OwnTeleportSequence.TeleportClientsToHost"/>) is bounded
    /// and finishes before this watch window even arms, and hands us the real target up front
    /// (<see cref="SetKnownTarget"/>) so even a total-miss "never teleported" can self-heal via
    /// position recovery. There is deliberately NO blanket "cancel every warp for a while"
    /// mitigation: that only ever existed to strangle the old external checkpoint mod's
    /// uncontrolled re-warp loop, which this mod no longer uses - see ROADMAP.md Phase 6
    /// </summary>
    public class TeleportWatchdog : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnMessageOverlay _messageOverlay;
        private Coroutine _running;
        private Vector3? _pendingTargetPos;
        private bool _loadInProgress;

        // Repeat-warp bookkeeping for the post-load glitch check, see OnLocalWarp. Kept
        // updated even after a glitch is flagged (not just while _watching), so steps
        // 4-5's recovery coroutines can tell whether something is still actively
        // re-warping the local player at their check time
        private readonly List<float> _postLoadWarpTimes = new List<float>();
        private bool _watching;

        // The real teleport target for whichever watch window is currently active (or
        // most recently was), used by FlagBadTeleport as the default recovery target
        // for steps 4-5 when a call site doesn't have a more specific position to hand it
        private Vector3 _currentTargetPos;

        // Ground-truth teleport target handed in by our OWN teleport sequence
        // (OwnTeleportSequence.SetKnownTarget) the moment it computes where it's warping
        // the player, cleared at the start of every load. Unlike _pendingTargetPos (which
        // is only ever set by actually OBSERVING a WarpPlayerRPC land), this is known up
        // front, so on the native path it lets the "never teleported" case - where no warp
        // RPC ever arrives - still recover the player to the real target instead of only
        // showing a hint. The external checkpoint-mod (F6) path never sets it (we don't
        // drive that teleport), so there it stays null and the old head-only fallback stands
        private Vector3? _knownTarget;

        /// <summary>
        /// The ground-truth teleport target for the current load, if our own teleport
        /// sequence recorded one (see <see cref="SetKnownTarget"/>). Read by the host's
        /// own load path to forward it to clients over <c>RPC_Loadingscreen</c> so a
        /// client that never received a warp can still recover to the right spot
        /// </summary>
        public Vector3? KnownTarget => _knownTarget;

        /// <summary>Set once when the current/most recent watch window flags a bad teleport; null otherwise</summary>
        public (float time, Vector3 targetPos)? LastFlaggedTeleport { get; private set; }

        public void Init(ManualLogSource log, PluginConfig cfg, OwnMessageOverlay messageOverlay)
        {
            _log = log;
            _cfg = cfg;
            _messageOverlay = messageOverlay;
        }

        /// <summary>
        /// Called from <see cref="OwnTeleportSequence"/> (host, direct) and via
        /// <c>RPC_Loadingscreen</c> (every client) the moment our own load begins. Marks a
        /// load as in progress and clears any stale pending/known target, so
        /// <see cref="OnLocalWarp"/> below only ever attributes warps that happen during an
        /// actual load to it
        /// </summary>
        public void BeginLoadWindow()
        {
            _loadInProgress = true;
            _pendingTargetPos = null;
            _knownTarget = null;
        }

        /// <summary>
        /// Called from <see cref="OwnTeleportSequence"/> the moment it computes where it's
        /// about to warp the player, recording the real target up front (see the
        /// <see cref="_knownTarget"/> field comment). Host-only in practice - clients learn
        /// their target by receiving the warp, or via the RPC-forwarded copy on a total miss
        /// </summary>
        public void SetKnownTarget(Vector3 target) => _knownTarget = target;

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
            // can see whether something is still actively re-warping the local player
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
        /// Called once our own load reports itself done (host: end of
        /// <see cref="OwnInventoryRestore.RestoreAll"/>; every client: via
        /// <c>RPC_Loadingscreen</c>). Starts the watch window using whichever teleport
        /// target was last recorded via <see cref="OnLocalWarp"/> - or, if none was ever
        /// recorded, flags immediately: the load reported itself done without this player
        /// ever receiving a single warp RPC, which is itself proof of a bad teleport (this
        /// is exactly the case a slower host's client hit: no glitching, no falling, simply
        /// never moved)
        ///
        /// <paramref name="knownTargetOverride"/> lets a client that arms via RPC carry the
        /// host's real teleport target across the wire (see <see cref="_knownTarget"/>), so a
        /// total-miss "never teleported" on a client can still recover to the right spot
        /// </summary>
        public void ArmPendingWatch(Vector3? knownTargetOverride = null)
        {
            _loadInProgress = false;
            if (knownTargetOverride.HasValue) _knownTarget = knownTargetOverride;

            if (_pendingTargetPos == null)
            {
                // Prefer the ground-truth target our own sequence recorded so position
                // recovery in FlagBadTeleport can actually put the player where they belong;
                // fall back to their current head only when even that is somehow unknown
                Vector3 pos = _knownTarget
                    ?? (Character.localCharacter != null ? Character.localCharacter.Head : Vector3.zero);
                string recoverNote = _knownTarget.HasValue
                    ? $" Recovering to known target {_knownTarget.Value}."
                    : "";
                FlagBadTeleport("never teleported",
                    "no warp RPC received before the load reported itself done." + recoverNote, pos);
                return;
            }

            BeginWatch(_pendingTargetPos.Value);
            _pendingTargetPos = null;
        }

        /// <summary>
        /// Stops any watch window in progress (or pending) without flagging anything.
        /// Called right before OUR OWN code intentionally moves the player away from a
        /// just-loaded position (e.g. RestartOrchestrator/ResumeOrchestrator returning to
        /// the Airport, or Plugin.RequestReturnToAirport), since that legitimate move
        /// looks identical to "never teleported"/"fall-through"/warp-loop symptoms to a
        /// watch window still running from a PRIOR load - our own load path already resets
        /// this via BeginLoadWindow/ArmPendingWatch, but a plain scene return we drive
        /// ourselves never goes through that
        /// </summary>
        public void LiftWatch()
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            _watching = false;
            _postLoadWarpTimes.Clear();
            _pendingTargetPos = null;
            _knownTarget = null;
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
                // every time a correction snaps the player back up, so a real
                // fall-through was never accumulating past it
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
            // try to actually show it too. The message overlay is a single shared
            // text+timer, so a later ShowMessage call (e.g. a "Save loaded" message that
            // arrives late on a slow host) can stomp ours right after we show it.
            // Re-showing a couple more times over the next several seconds gives the
            // player a real shot at actually seeing it even if the first attempt loses
            // that race (harmless if it doesn't: it's the same message either way)
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
        /// away - the last-resort backstop that puts the player where the save intended if
        /// our own bounded teleport somehow didn't land them there. Calls the vanilla
        /// <c>WarpPlayerRPC</c> directly (not through Photon) since this only ever needs to
        /// move the LOCAL player's own view of themselves; harmless no-op if by this point
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

            c.WarpPlayerRPC(targetPos, false);
        }

        private IEnumerator ShowMessageResiliently()
        {
            string helpKey = _cfg != null ? _cfg.HelpKey.Value.ToString() : "F2";
            string text = MessagesLocalization.Get(MsgKey.TeleportBugHint, helpKey);
            var color = new Color(1f, 0.7f, 0.2f, 1f);

            _messageOverlay?.Show(text, color, 6f);
            yield return new WaitForSeconds(2f);
            _messageOverlay?.Show(text, color, 6f);
            yield return new WaitForSeconds(3f);
            _messageOverlay?.Show(text, color, 6f);
        }
    }
}
