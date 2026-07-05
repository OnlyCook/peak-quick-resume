using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Phase 6 step 2: a temporary, one-load-only override of the checkpoint mod's own
    /// teleportJumpLogic / teleportFramesToWait / jumpLogicWaitTime settings, applied
    /// for a load (native F6, or this mod's F7/Enter) whenever <see cref="ResolveOverride"/>
    /// resolves to a non-null value (Alt held, or a plain coop load with the
    /// optimized-coop-loading setting on - see that method), then restored back to
    /// whatever the user actually has configured a bit later
    ///
    /// Two entry points feed into the same <see cref="Apply"/>, since the two trigger
    /// paths need the modifier state read at different times (see ROADMAP.md Phase 6
    /// step 2 for why):
    ///   - <see cref="ApplyForOurOwnLoad"/>: called by ResumeOrchestrator right before
    ///     it invokes the checkpoint load, with whichever override was captured back
    ///     at CONFIRM time (the user may have already released the key by now, our own
    ///     load doesn't happen synchronously with the keypress)
    ///   - <see cref="ApplyFromAmbientModifiers"/>: called by LoadingScreenPatch's
    ///     prefix, which fires synchronously with the checkpoint mod's OWN F6 handling
    ///     (no delay), so reading ambient Input there IS reading it at keypress time.
    ///     Skipped whenever <see cref="IsDrivingOurOwnLoad"/> is true, so this doesn't
    ///     double-apply (or stomp with an ambient read of nothing, since the user's
    ///     since let go) during our own flow
    ///
    /// Only ever meaningful on whichever machine actually executes the checkpoint
    /// mod's teleport code, which is always the host - callers are expected to already
    /// be host-gated (native F6 already no-ops for non-hosts; our own F7 flow already
    /// requires <see cref="RunLauncher.IsHost"/>)
    /// </summary>
    public class TeleportConfigOverride
    {
        private readonly ManualLogSource _log;
        private readonly PluginConfig _cfg;
        private readonly CheckpointInterop _checkpoint;
        private readonly MonoBehaviour _coroutineHost;

        private bool _hasSnapshot;
        private int _originalJumpLogic;
        private int _originalFramesToWait;
        private float _originalWaitTime;
        private Coroutine _restoreCoroutine;

        /// <summary>
        /// Set true for the brief synchronous window around our own orchestrator's
        /// checkpoint-load call, so <see cref="LoadingScreenPatch"/>'s ambient-modifier
        /// check (meant for native F6) knows to skip itself, we already applied
        /// whatever override was requested at confirm time
        /// </summary>
        public bool IsDrivingOurOwnLoad { get; set; }

        /// <summary>
        /// The jumpLogic value of whichever Shift/Alt override is currently in effect
        /// (i.e. applied and not yet restored), or null when the base default (0, no
        /// override) is in play. Read by the "Save game loaded!"/"Save loaded. Welcome
        /// back!" on-screen messages to append a subtle "(1)"/"(2)" indicator
        /// </summary>
        public int? LastAppliedOverride { get; private set; }

        /// <summary>Subtle on-screen suffix for the given override value, e.g. " (1)" — empty when null</summary>
        public static string FormatIndicator(int? overrideValue) => overrideValue.HasValue ? $" ({overrideValue.Value})" : "";

        public TeleportConfigOverride(ManualLogSource log, PluginConfig cfg, CheckpointInterop checkpoint, MonoBehaviour coroutineHost)
        {
            _log = log;
            _cfg = cfg;
            _checkpoint = checkpoint;
            _coroutineHost = coroutineHost;
        }

        /// <summary>
        /// Resolves which teleportJumpLogic value the NEXT load should use, given
        /// whichever modifier is held right now - null means "no override, use my own
        /// base config as-is". Single source of truth for both applying an override
        /// (see the two Apply* entry points below) and read-only display (the save
        /// picker's live footer indicator, F1 help screen)
        ///
        ///   - Shift: always null (explicit "use my own base config anyway", the
        ///     escape hatch from the coop default below)
        ///   - Alt: always AltTeleportJumpLogic
        ///   - neither, in COOP, with EnableOptimizedCoopLoading on: OptimizedCoopJumpLogic
        ///     (extensive maintainer testing found this avoids nearly every case of the
        ///     checkpoint mod's intermittent teleport bug - see PluginConfig)
        ///   - neither, otherwise (solo, or the setting above disabled): null (base config)
        /// </summary>
        public int? ResolveOverride()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (shift) return null;
            if (alt) return _cfg.AltTeleportJumpLogic.Value;

            if (IsCoop() && _cfg.EnableOptimizedCoopLoading.Value)
                return _cfg.OptimizedCoopJumpLogic.Value;

            return null;
        }

        private static bool IsCoop()
        {
            try { return !Photon.Pun.PhotonNetwork.OfflineMode; }
            catch { return false; }
        }

        /// <summary>
        /// The user's own teleportJumpLogic, ignoring any override currently in effect -
        /// i.e. the value that would apply with Shift held, or with no modifier held in
        /// solo. Correctly returns the pre-override snapshot even while an override is
        /// currently applied (<see cref="CheckpointInterop.TryGetTeleportConfig"/> alone
        /// would return the live, temporarily-overridden value in that window). Used for display
        /// (F1 help screen, footer indicator) where showing the override's own value
        /// back at itself would be misleading
        /// </summary>
        public bool TryGetBaseJumpLogic(out int jumpLogic)
        {
            if (_hasSnapshot) { jumpLogic = _originalJumpLogic; return true; }
            return _checkpoint.TryGetTeleportConfig(out jumpLogic, out _, out _);
        }

        /// <summary>Called by ResumeOrchestrator right before it triggers the checkpoint load</summary>
        public void ApplyForOurOwnLoad(int? jumpLogicOverride) => Apply(jumpLogicOverride);

        /// <summary>Called by LoadingScreenPatch's prefix, for the native-F6 path only</summary>
        public void ApplyFromAmbientModifiers()
        {
            if (IsDrivingOurOwnLoad) return; // our own flow already applied its own capture
            Apply(ResolveOverride());
        }

        private void Apply(int? jumpLogicOverride)
        {
            if (jumpLogicOverride == null) return; // nothing requested; leave any still-pending override alone

            if (!_checkpoint.TryGetTeleportConfig(out int curJump, out int curFrames, out float curWait))
                return; // reflection unavailable; non-fatal, feature just doesn't apply

            if (!_hasSnapshot)
            {
                _originalJumpLogic = curJump;
                _originalFramesToWait = curFrames;
                _originalWaitTime = curWait;
                _hasSnapshot = true;

                // Persist the pre-override values to disk immediately, so a crash/quit
                // before RestoreAfterDelay below gets to run doesn't leave the checkpoint
                // mod's teleport config stuck on the override - see ReconcileAfterRestart
                _cfg.PendingOverrideOriginalJumpLogic.Value = curJump;
                _cfg.PendingOverrideOriginalFramesToWait.Value = curFrames;
                _cfg.PendingOverrideOriginalWaitTime.Value = curWait;
                _cfg.PendingOverrideResetOwed.Value = true;
            }

            int newFrames = Mathf.Max(_originalFramesToWait, _cfg.OverrideFramesToWait.Value);
            float newWait = Mathf.Max(_originalWaitTime, _cfg.OverrideJumpLogicWaitTime.Value);

            if (!_checkpoint.TrySetTeleportConfig(jumpLogicOverride.Value, newFrames, newWait)) return;

            LastAppliedOverride = jumpLogicOverride.Value;

            _log.LogInfo($"TeleportConfigOverride: applied jumpLogic={jumpLogicOverride.Value}, "
                + $"framesToWait={newFrames}, waitTime={newWait} (originals: {_originalJumpLogic}/"
                + $"{_originalFramesToWait}/{_originalWaitTime}).");

            if (_restoreCoroutine != null) _coroutineHost.StopCoroutine(_restoreCoroutine);
            _restoreCoroutine = _coroutineHost.StartCoroutine(RestoreAfterDelay());
        }

        private IEnumerator RestoreAfterDelay()
        {
            yield return new WaitForSeconds(Mathf.Max(1f, _cfg.OverrideRestoreDelaySeconds.Value));

            if (_hasSnapshot)
            {
                if (_checkpoint.TrySetTeleportConfig(_originalJumpLogic, _originalFramesToWait, _originalWaitTime))
                {
                    _log.LogInfo("TeleportConfigOverride: restored original teleport config.");
                    _cfg.PendingOverrideResetOwed.Value = false;
                }
                _hasSnapshot = false;
            }
            LastAppliedOverride = null;
            _restoreCoroutine = null;
        }

        /// <summary>
        /// Crash-safety net for the window above: if the game closed (crash or quit)
        /// after an override was applied but before <see cref="RestoreAfterDelay"/> got
        /// to run, <see cref="PluginConfig.PendingOverrideResetOwed"/> is left true on
        /// disk with the pre-override values still recorded alongside it. Call this once
        /// it's safe to check (the F7 picker opening) - if the flag is stuck true and the
        /// checkpoint mod's live jumpLogic still doesn't match what it should be restored
        /// to, fixes it immediately. A no-op otherwise (including if the flag is true but
        /// the value already happens to match, e.g. the user changed it back by hand -
        /// then this just clears the flag without touching the config)
        /// </summary>
        public void ReconcileAfterRestart()
        {
            if (!_cfg.PendingOverrideResetOwed.Value) return;

            if (!_checkpoint.TryGetTeleportConfig(out int curJump, out _, out _))
                return; // reflection unavailable; leave the flag set, try again next open

            int shouldBeJump = _cfg.PendingOverrideOriginalJumpLogic.Value;
            if (curJump == shouldBeJump)
            {
                _cfg.PendingOverrideResetOwed.Value = false;
                return;
            }

            if (_checkpoint.TrySetTeleportConfig(shouldBeJump, _cfg.PendingOverrideOriginalFramesToWait.Value,
                _cfg.PendingOverrideOriginalWaitTime.Value))
            {
                _log.LogWarning($"TeleportConfigOverride: found teleport config stuck at jumpLogic={curJump} from "
                    + $"an override that never got to restore (likely a crash/quit); reset it back to "
                    + $"jumpLogic={shouldBeJump}.");
                _cfg.PendingOverrideResetOwed.Value = false;
            }
        }
    }
}
