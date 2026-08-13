using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Mod bug fix (session-diagnosed 2026-08-13, via the [achievement-debug] JesterBadge
    /// subscriber dump added to chase a Jester Badge that unlocked after only TWO Clown
    /// Luggage): every achievement counter driven by a <c>GlobalEvents</c> callback was
    /// being credited TWICE per real event for the rest of any run that had been resumed.
    ///
    /// <c>AchievementManager.InitRunBasedValues</c> (decompile 213-228) ends its
    /// coroutine with an unconditional <c>SubscribeToEvents()</c>, which is a plain
    /// <c>Delegate.Combine</c> onto ten static <c>GlobalEvents</c> fields
    /// (OnLuggageOpened, OnRespawnChestOpened, OnItemConsumed, OnCharacterDied, ...).
    /// Vanilla only ever calls it once per manager, so vanilla never notices. Our own
    /// achievement restore (<see cref="AchievementProgressIO.ApplyLocal"/>) has to call it
    /// a second time - that overload is the only race-safe way to replace
    /// <c>runBasedValueData</c>, since it waits out the same GotStats gate vanilla's own
    /// pending init coroutine is sitting on - and that second call re-subscribed the SAME
    /// manager, giving every event two handlers. Confirmed straight from the log:
    /// <code>
    /// [Unity] Init Run Based Values                   (vanilla, from CharacterSpawner)
    /// SubscribeToEvents on 2188920 -> subscribers: 1 -> [TestLuggageOpened#2188920]
    /// [stage] Triggering our own restore.
    /// [Unity] Init Run Based Values                   (ours, AchievementProgressIO)
    /// SubscribeToEvents on 2188920 -> subscribers: 2 -> [TestLuggageOpened#2188920, TestLuggageOpened#2188920]
    /// ... one TriggerLuggageOpened for one box -> ClownLuggageOpened 0 -> 1 -> 2
    /// </code>
    ///
    /// It also LEAKED across loads: <c>OnDestroy</c> calls <c>UnsubscribeFromEvents</c>
    /// exactly once, and <c>Delegate.Remove</c> drops a single matching entry, so the
    /// duplicate outlived its own AchievementManager. The next resume showed three
    /// handlers - one belonging to a destroyed manager (logged as <c>#0</c>, Unity's
    /// fake-null) plus two live ones - and a single box then credited three times, which
    /// is exactly how a two-box run threw JesterBadge at "3/3".
    ///
    /// Every run-based achievement rode on this, not just Jester: Plunderer
    /// (LuggageOpened), Clutch (ScoutsResurrected), the food/mushroom counters, the
    /// passed-out and death trackers. It is almost certainly the real mechanism behind
    /// the Clutch over-credit chased on 2026-08-06 - see
    /// <see cref="RespawnChestDoubleCreditFix"/>, which stays in place because it fixes a
    /// genuinely separate vanilla defect (a repeat cast on an already-open statue).
    ///
    /// The fix makes subscription idempotent instead of removing our second init call:
    /// a prefix on <c>SubscribeToEvents</c> that first drops any subscription this
    /// instance already holds (<c>Delegate.Remove</c> on a non-subscribed handler is a
    /// no-op, so a first, legitimate subscribe is untouched), and sweeps out any handler
    /// left behind by an already-destroyed AchievementManager. Net effect: exactly one
    /// live handler per event, which is what vanilla intends
    /// </summary>
    public static class AchievementSubscriptionFix
    {
        private static ManualLogSource _log;
        private static MethodInfo _unsubscribe;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                _unsubscribe = AccessTools.Method(typeof(AchievementManager), "UnsubscribeFromEvents");
                harmony.Patch(AccessTools.Method(typeof(AchievementManager), "SubscribeToEvents"),
                    prefix: new HarmonyMethod(typeof(AchievementSubscriptionFix), nameof(Prefix)));
                log.LogInfo("AchievementSubscriptionFix: patched AchievementManager.SubscribeToEvents "
                    + "(one achievement-event handler per manager, no double credit after a resume).");
            }
            catch (Exception e)
            {
                log.LogError($"AchievementSubscriptionFix.Apply failed (non-fatal): {e}");
            }
        }

        // Never returns false: the original SubscribeToEvents always runs, this only makes
        // sure it can't combine a handler that is already there. Fully wrapped, because a
        // failure here must not be able to stop the game subscribing at all
        private static void Prefix(AchievementManager __instance)
        {
            try
            {
                PurgeDestroyedSubscribers();
                if (__instance != null) _unsubscribe?.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"AchievementSubscriptionFix.Prefix failed (non-fatal, subscribing anyway): {e.Message}");
            }
        }

        // Drops every handler on GlobalEvents whose target is an already-destroyed
        // UnityEngine.Object. Those can only be leftovers from a previous scene: the
        // object is gone, so the handler can no longer do anything useful, but it still
        // runs and still writes to that dead manager's own counters (which is what threw
        // the badge). Generic over every delegate field so the other nine events are
        // cleaned up too, not just OnLuggageOpened
        private static void PurgeDestroyedSubscribers()
        {
            int removed = 0;
            foreach (FieldInfo field in typeof(GlobalEvents).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                if (!(field.GetValue(null) is Delegate current)) continue;

                var alive = new List<Delegate>();
                foreach (Delegate d in current.GetInvocationList())
                {
                    // Unity's == override reports a destroyed object as null
                    if (d.Target is UnityEngine.Object uo && uo == null) { removed++; continue; }
                    alive.Add(d);
                }

                if (alive.Count == current.GetInvocationList().Length) continue;
                field.SetValue(null, alive.Count == 0 ? null : Delegate.Combine(alive.ToArray()));
            }

            if (removed > 0)
                _log?.LogInfo($"AchievementSubscriptionFix: dropped {removed} GlobalEvents handler(s) "
                    + "belonging to destroyed objects (leftovers from a previous run).");
        }
    }
}
