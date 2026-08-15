using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Shared "one of these at a time" guard between <see cref="ResumeOrchestrator"/>,
    /// <see cref="RestartOrchestrator"/>, and <see cref="Plugin.RequestReturnToAirport"/>,
    /// since each one only guarded against re-entering itself, not against one of the
    /// others starting mid-flight. Whichever asks first wins; the loser's request is
    /// dropped, not queued.
    ///
    /// Also arms a short post-completion cooldown (a full Airport→Level scene transition
    /// immediately after the previous one confirms breaks clients via a Photon timing
    /// issue), during which a new request is queued instead of dropped and auto-runs once
    /// the cooldown clears. A second queued request replaces the first (last wins).
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

        private static MonoBehaviour _coroutineHost;

        private static float _cooldownUntil = -1f; // Time.unscaledTime; < 0 = no active cooldown
        private static Coroutine _queuedCoroutine;
        private static Action _queuedAction;
        private static string _queuedDescription;

        public static void Init(MonoBehaviour coroutineHost) => _coroutineHost = coroutineHost;

        /// <summary>Called after a genuine completion (not a failed/aborted attempt). <paramref name="seconds"/> &lt;= 0 disables the cooldown.</summary>
        public static void ArmCooldown(float seconds)
        {
            _cooldownUntil = seconds > 0f ? Time.unscaledTime + seconds : -1f;
        }

        public static float RemainingCooldown =>
            _cooldownUntil < 0f ? 0f : Mathf.Max(0f, _cooldownUntil - Time.unscaledTime);

        /// <summary>
        /// Runs <paramref name="action"/> immediately if the lock is free and no cooldown is
        /// active, otherwise queues it to auto-run once both clear (replacing any previously
        /// queued request). <paramref name="action"/> should be the full request body including
        /// its own guard checks, since those are re-evaluated fresh when it actually runs.
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
