using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Forces <c>MapBaker.GetLevel</c> to return <see cref="OwnLoadEntryPoints.SelectedLevel"/>
    /// instead of today's daily-rotation scene, whenever a value has actually been set.
    /// Runs at <c>Priority.Last</c> so it wins over other mods patching the same method
    /// (e.g. MorePeak), since Harmony lets the last prefix's <c>__result</c> write stick.
    /// </summary>
    public static class MapBakerLevelOverridePatch
    {
        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(MapBaker), "GetLevel");
                var prefix = new HarmonyMethod(typeof(MapBakerLevelOverridePatch), nameof(Prefix)) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix);
                log.LogInfo("MapBakerLevelOverridePatch: patched MapBaker.GetLevel.");
            }
            catch (Exception e)
            {
                log.LogError($"MapBakerLevelOverridePatch.Apply failed (non-fatal): {e}");
            }
        }

        private static bool Prefix(int levelIndex, ref string __result)
        {
            string selected = OwnLoadEntryPoints.SelectedLevel;
            if (string.IsNullOrEmpty(selected) || selected == "null") return true;

            __result = selected;
            // One-shot: otherwise stays armed and hijacks the next plain Boarding Pass start too.
            OwnLoadEntryPoints.ClearSelectedLevel();
            return false;
        }
    }
}
