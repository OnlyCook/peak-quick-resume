using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Zorro.Core;

namespace PEAKQuickResume
{
    // fixes certain biome gated achievements (Medieval History Badge: "Climb past THE CITADEL without being hit by any traps.") 
    // never being able to unlock after a segment jump lands mid-biome
    //
    // fix: right after JumpToSegment runs, remove every occurrence of the just landed in segment's biome from the skip list
    // applied as a postfix on the vanilla method itself (not just from our own load path)
    // so it also covers coop clients, who receive this call
    // via MapHandler's rpc dispatch rather than running the mod's own load coroutine
    public static class BiomeSkipResumeFix
    {
        private static ManualLogSource _log;

        private static readonly Type MphType = typeof(MountainProgressHandler);
        private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo FSkippedBiomes = MphType.GetField("_skippedBiomes", NonPublicInstance);

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                if (FSkippedBiomes == null)
                {
                    log.LogError("BiomeSkipResumeFix: MountainProgressHandler._skippedBiomes field not found (game update?) - biome-gated achievements may not unlock correctly after a jump/load.");
                    return;
                }

                harmony.Patch(AccessTools.Method(MphType, nameof(MountainProgressHandler.JumpToSegment)),
                    postfix: new HarmonyMethod(typeof(BiomeSkipResumeFix), nameof(JumpToSegment_Postfix)));

                log.LogInfo("BiomeSkipResumeFix: patched MountainProgressHandler.JumpToSegment.");
            }
            catch (Exception e)
            {
                log.LogError($"BiomeSkipResumeFix.Apply failed (non-fatal): {e}");
            }
        }

        private static void JumpToSegment_Postfix(int segment, float delayTitle)
        {
            try
            {
                var mph = Singleton<MountainProgressHandler>.Instance;
                if (mph?.progressPoints == null || segment < 0 || segment >= mph.progressPoints.Length) return;

                var landedBiome = mph.progressPoints[segment].biome;
                var skipped = FSkippedBiomes.GetValue(mph) as List<Biome.BiomeType>;
                int removed = skipped?.RemoveAll(b => b == landedBiome) ?? 0;

                if (removed > 0)
                    _log?.LogInfo($"BiomeSkipResumeFix: un-skipped biome '{landedBiome}' ({removed} entr{(removed == 1 ? "y" : "ies")} removed) after jumping to segment {segment} ('{mph.progressPoints[segment].title}') - an earlier progress point shared this biome and had marked it skipped, which would have silently blocked this area's biome-gated achievement checks (e.g. no-traps badges) for the rest of the run.");
            }
            catch (Exception e) { _log?.LogWarning($"BiomeSkipResumeFix.JumpToSegment_Postfix failed (non-fatal): {e.Message}"); }
        }
    }
}
