using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Watches the local player after our own teleport for known bad-teleport symptoms
    /// (see ROADMAP.md Phase 6): never teleported at all, falling through the world, dying
    /// shortly after load, or warp-loop glitching (detected via repeat <c>WarpPlayerRPC</c>
    /// calls, not position/velocity sampling, which proved unreliable against snap-teleports).
    ///
    /// Always logs + shows an on-screen hint first; <see cref="RevertFallDamageRoutine"/> and
    /// <see cref="PositionRecoveryRoutine"/> are gated auto-fixes that only engage after a flag.
    /// <see cref="SetKnownTarget"/> hands us the real target up front so even a total miss can
    /// self-heal via position recovery.
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
        // updated even after a glitch is flagged so recovery coroutines can tell if warping is still ongoing.
        private readonly List<float> _postLoadWarpTimes = new List<float>();
        private bool _watching;

        // Real teleport target for the current/most recent watch window; FlagBadTeleport's default recovery target.
        private Vector3 _currentTargetPos;

        // Ground-truth target from OwnTeleportSequence.SetKnownTarget, known up front (unlike
        // _pendingTargetPos, which requires observing a landed WarpPlayerRPC), so a total-miss
        // "never teleported" can still recover. Stays null on the external checkpoint-mod (F6) path.
        private Vector3? _knownTarget;

        /// <summary>Ground-truth target for the current load, if recorded (see <see cref="SetKnownTarget"/>).</summary>
        public Vector3? KnownTarget => _knownTarget;

        /// <summary>Set once when the current/most recent watch window flags a bad teleport; null otherwise</summary>
        public (float time, Vector3 targetPos)? LastFlaggedTeleport { get; private set; }

        public void Init(ManualLogSource log, PluginConfig cfg, OwnMessageOverlay messageOverlay)
        {
            _log = log;
            _cfg = cfg;
            _messageOverlay = messageOverlay;
        }

        /// <summary>Marks a load as in progress and clears any stale pending/known target.</summary>
        public void BeginLoadWindow()
        {
            _loadInProgress = true;
            _pendingTargetPos = null;
            _knownTarget = null;
            _suppressedForRestoredDeath = false;
        }

        // Set when this machine's own character is deliberately restored as dead by a checkpoint load (see DeathStateRestore).
        private bool _suppressedForRestoredDeath;

        /// <summary>
        /// Called when a checkpoint load is deliberately restoring this machine's character
        /// as dead. Every watched symptom is meaningless on a spectating ghost, so the whole
        /// watch is dropped for this load. Deliberately sticky (a flag, not just LiftWatch):
        /// the host applies the death while the loading screen is still up, which can race
        /// this machine's own <see cref="ArmPendingWatch"/> either direction.
        /// </summary>
        public void SuppressForRestoredDeath()
        {
            _suppressedForRestoredDeath = true;
            _log.Trace("TeleportWatchdog: this load is restoring us as dead on purpose; standing down for it.");
            LiftWatch();
        }

        /// <summary>Records the real target up front. Host-only in practice; see <see cref="_knownTarget"/>.</summary>
        public void SetKnownTarget(Vector3 target) => _knownTarget = target;

        /// <summary>
        /// Called from <see cref="TeleportWatchdogPatch"/>'s postfix whenever the local player
        /// is warped, for any reason. During a load, just records the pending target (the
        /// watch window starts later, from <see cref="ArmPendingWatch"/>). Once a watch window
        /// is active, counts it as a repeat correction; enough repeats in a short span flags a
        /// warp-loop glitch immediately.
        /// </summary>
        public void OnLocalWarp(Vector3 position)
        {
            if (_loadInProgress)
            {
                _pendingTargetPos = position;
                return;
            }

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
        /// Called once our own load reports itself done. Starts the watch window using the
        /// last-recorded teleport target, or flags immediately if none was ever recorded
        /// (proof of a bad teleport: the load finished without this player ever receiving a
        /// warp RPC). <paramref name="knownTargetOverride"/> lets a client that arms via RPC
        /// carry the host's real target across the wire.
        /// </summary>
        public void ArmPendingWatch(Vector3? knownTargetOverride = null)
        {
            _loadInProgress = false;

            if (_suppressedForRestoredDeath)
            {
                _pendingTargetPos = null;
                return;
            }

            if (knownTargetOverride.HasValue) _knownTarget = knownTargetOverride;

            if (_pendingTargetPos == null)
            {
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
        /// Stops any watch window without flagging anything. Called before our own code
        /// intentionally moves the player, since that legitimate move looks identical to a
        /// watched symptom to a window still running from a prior load.
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
            if (_suppressedForRestoredDeath) return; // see SuppressForRestoredDeath

            if (_running != null) StopCoroutine(_running);
            _postLoadWarpTimes.Clear();
            _currentTargetPos = targetPos;
            _running = StartCoroutine(WatchRoutine(targetPos));
        }

        private IEnumerator WatchRoutine(Vector3 targetPos)
        {
            // Give the teleport RPC(s) a moment to land before sampling, or the pre-teleport position poisons the baseline.
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

            // Checked once, immediately: backstops ArmPendingWatch's "no warp RPC" check
            // for the case where a warp DID fire but landed nowhere near the target.
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

            // Falls back to _currentTargetPos rather than current position; targetPosOverride
            // is only passed when even that isn't known (the "no warp RPC at all" case).
            Vector3 target = targetPosOverride ?? _currentTargetPos;
            LastFlaggedTeleport = (Time.time, target);

            // The overlay is a single shared text+timer that a later message can stomp, so
            // re-show a couple more times to give the player a real shot at seeing it.
            StartCoroutine(ShowMessageResiliently());

            if (_running != null) { StopCoroutine(_running); _running = null; }
            _watching = false;

            if (_cfg != null && _cfg.EnableFallDamageRevert.Value)
                StartCoroutine(RevertFallDamageRoutine());
            if (_cfg != null && _cfg.EnablePositionRecovery.Value)
                StartCoroutine(PositionRecoveryRoutine(target));
        }

        /// <summary>
        /// Snapshots Injury when flagged, waits, then refunds any net increase since (once).
        /// A net-delta comparison rather than hooking the specific cause; simplest way to
        /// catch fall damage from repeated mid-air warps (see ROADMAP.md Phase 6 step 4).
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
                _log.Trace($"TeleportWatchdog: reverted {delta:F3} Injury gained in the "
                    + $"{_cfg.DamageRevertDelaySeconds.Value:F0}s after a flagged bad teleport.");
            }
        }

        /// <summary>
        /// Last-resort backstop: after a delay, forces the local player directly to
        /// <paramref name="targetPos"/> if still too far away. Calls vanilla's
        /// <c>WarpPlayerRPC</c> directly since this only needs to move the local view.
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
            string helpKey = _cfg != null ? _cfg.HelpKey.Value.ToString() : "F4";
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
