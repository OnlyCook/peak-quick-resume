using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Shared "one of these at a time" guard between <see cref="ResumeOrchestrator"/>,
    /// <see cref="RestartOrchestrator"/>, and <see cref="Plugin.RequestReturnToAirport"/>.
    /// Each one already refused to re-enter itself via its own private <c>_running</c>
    /// flag, but that did nothing to stop ANOTHER one of these three from starting while
    /// the first was still mid-flight - confirmed via a real session log (2026-07-25): a
    /// Restart pressed while a Resume was sitting at "waiting for all clients ready" fired
    /// <c>GameOverHandler.LoadAirport()</c> underneath the still-running Resume, which then
    /// tried to load its checkpoint save while sitting at the Airport and aborted, letting
    /// the Restart's fresh, unrelated run win instead - the host ended up on the wrong map
    /// while a client (never running either orchestrator) followed the correct RPC
    ///
    /// Owner-tag lock: whichever of the three asks first wins, the other's request is
    /// dropped outright (not queued) while the lock is BUSY - matches the existing
    /// same-orchestrator re-entrancy guards' own behavior exactly, just widened to cover
    /// all three
    ///
    /// Own addition (cooldown/queue, 2026-07-25 follow-up): even with the lock above,
    /// firing a brand-new full scene transition (Airport -> Level) the INSTANT the
    /// previous one's client-side confirmation lands turned out to still break a client -
    /// confirmed via session testing to be a Photon scene-sync timing issue below our own
    /// code (waiting a few real seconds before the next action always worked fine). Rather
    /// than blocking the player outright for those few seconds, every completed
    /// orchestration arms a short cooldown window (<see cref="ArmCooldown"/>); a new
    /// request arriving DURING that window (lock already free again by then) is queued and
    /// auto-runs the instant the cooldown clears, rather than being rejected. A second
    /// request arriving while one is already queued replaces it outright (last request
    /// wins) - per the maintainer's explicit direction, since the player pressing
    /// resume/restart/return-to-airport again while already queued means they want THAT
    /// one, not both
    /// </summary>
    internal static class OrchestrationLock
    {
        private static string _owner;

        public static bool TryAcquire(string owner)
        {
            if (_owner != null) return false;
            _owner = owner;
            return true;
        }

        public static void Release(string owner)
        {
            if (_owner == owner) _owner = null;
        }

        public static bool IsBusy => _owner != null;

        // --- Cooldown / queue-latest-request-only ---

        // The coroutine host that runs the queued-request wait loop. Set once from
        // Plugin.Awake (any DontDestroyOnLoad MonoBehaviour on the orchestrator object
        // works - see Plugin.Awake's own remarks on that GameObject's lifetime)
        private static MonoBehaviour _coroutineHost;

        private static float _cooldownUntil = -1f; // Time.unscaledTime; < 0 = no active cooldown
        private static Coroutine _queuedCoroutine;
        private static Action _queuedAction;
        private static string _queuedDescription;

        public static void Init(MonoBehaviour coroutineHost) => _coroutineHost = coroutineHost;

        /// <summary>
        /// Called by each of the three entry points right after it genuinely finishes
        /// (NOT on a failed/aborted attempt - nothing meaningfully changed then, so
        /// there's nothing that needs settling). <paramref name="seconds"/> &lt;= 0
        /// disables the cooldown entirely (the config's own "fully disable this" escape
        /// hatch - see PluginConfig.PostOrchestrationCooldown)
        /// </summary>
        public static void ArmCooldown(float seconds)
        {
            _cooldownUntil = seconds > 0f ? Time.unscaledTime + seconds : -1f;
        }

        public static float RemainingCooldown =>
            _cooldownUntil < 0f ? 0f : Mathf.Max(0f, _cooldownUntil - Time.unscaledTime);

        /// <summary>
        /// Runs <paramref name="action"/> immediately if the lock is free AND no cooldown
        /// is currently active, otherwise queues it to run automatically once BOTH clear -
        /// replacing whatever was previously queued, if anything (see class remarks).
        /// Callers pass their FULL request body (guard checks included) as
        /// <paramref name="action"/>, so those guards are re-evaluated fresh at whatever
        /// moment the action actually runs, not stale-checked once up front before a
        /// multi-second queue wait
        ///
        /// Own fix (2026-07-25 follow-up): originally only checked the cooldown timer, not
        /// whether the lock was still BUSY (an orchestration genuinely still running, not
        /// yet in its post-completion cooldown at all) - a request arriving THEN fell
        /// through to "run immediately", hit the target orchestrator's own TryAcquire
        /// failure, and was silently dropped instead of queued. Confirmed via a real
        /// session log: a Restart pressed while a Resume was still waiting on a client's
        /// presentation confirmation (cooldown not armed yet) logged "already in progress;
        /// ignoring" and never queued, even though the player then waited several seconds
        /// for something that was never going to happen
        /// </summary>
        public static void RunOrQueue(string description, Action action, ManualLogSource log)
        {
            if (!IsBusy && RemainingCooldown <= 0f) { action(); return; }

            _queuedAction = action;
            _queuedDescription = description;
            log.Trace($"[cooldown] '{description}' queued (busy={IsBusy}, {RemainingCooldown:F1}s cooldown "
                + "remaining); will auto-run once clear (replaces any previously queued request).");

            if (_queuedCoroutine == null && _coroutineHost != null)
                _queuedCoroutine = _coroutineHost.StartCoroutine(RunQueuedWhenClear(log));
        }

        private static IEnumerator RunQueuedWhenClear(ManualLogSource log)
        {
            while (IsBusy || RemainingCooldown > 0f) yield return null;
            _queuedCoroutine = null;

            Action toRun = _queuedAction;
            string description = _queuedDescription;
            _queuedAction = null;
            _queuedDescription = null;

            if (toRun == null) yield break;
            log.Trace($"[cooldown] running queued '{description}'.");
            toRun();
        }
    }
}
