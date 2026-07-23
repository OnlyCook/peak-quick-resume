using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's <c>GetLevel_Override</c> Harmony prefix
    /// (decompile 347-373): forces <c>MapBaker.GetLevel</c> to return
    /// <see cref="OwnLoadEntryPoints.SelectedLevel"/> instead of today's daily-rotation
    /// scene, whenever a real value has actually been set (same null/empty/"null"-
    /// sentinel guard as the original)
    ///
    /// Safe to apply alongside the checkpoint mod's own equivalent patch during the
    /// Phase 8 transition window: <see cref="OwnLoadEntryPoints.SelectedLevel"/> starts
    /// (and stays, until something calls <see cref="OwnLoadEntryPoints.TryPreStartSetSegment"/>)
    /// at "null", so this prefix is a pure no-op (returns true, falls through to whatever
    /// else would otherwise run) until a later milestone wires a real resume flow through
    /// <see cref="OwnLoadEntryPoints"/>
    ///
    /// Deliberately does not (yet) gate on a "use saved island" config toggle the way the
    /// original's own <c>configLoadLevelScene</c> does - Quick Resume's own flow has
    /// always forced that setting on unconditionally for every load (see
    /// <c>ResumeOrchestrator.TrySetUseSavedLevel</c>), so porting a togglable gate we'd
    /// only ever force to `true` anyway would be dead weight. Revisit only if this ever
    /// needs to be independently toggleable (e.g. when M8 repoints the boarding-pass
    /// island-toggle button onto our own config instead of the checkpoint mod's)
    /// </summary>
    public static class MapBakerLevelOverridePatch
    {
        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(MapBaker), "GetLevel");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(MapBakerLevelOverridePatch), nameof(Prefix)));
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
            // One-shot: consumed exactly once per resume we ourselves triggered. Without
            // this, the override stays armed forever and silently hijacks the NEXT plain
            // Boarding Pass start too (see SelectedLevel's remarks)
            OwnLoadEntryPoints.ClearSelectedLevel();
            return false;
        }
    }
}
