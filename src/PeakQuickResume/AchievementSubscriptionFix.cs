using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Fixes every GlobalEvents-driven achievement counter being credited twice after a
    /// resume. AchievementManager.SubscribeToEvents combines onto static GlobalEvents
    /// delegates; vanilla calls it once, but our achievement restore
    /// (<see cref="AchievementProgressIO.ApplyLocal"/>) must call InitRunBasedValues a
    /// second time to race-safely replace runBasedValueData, which re-subscribes the
    /// same manager and double-fires every event. Duplicates also leaked across loads
    /// since UnsubscribeFromEvents only removes one matching entry.
    ///
    /// Fix: a prefix on SubscribeToEvents drops any subscription this instance already
    /// holds and sweeps out handlers left by destroyed AchievementManagers, so exactly
    /// one live handler remains per event. <see cref="RespawnChestDoubleCreditFix"/>
    /// fixes an unrelated vanilla defect and stays in place independently.
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

        // Drops handlers targeting an already-destroyed UnityEngine.Object, leftovers
        // from a previous scene that would otherwise still fire.
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
