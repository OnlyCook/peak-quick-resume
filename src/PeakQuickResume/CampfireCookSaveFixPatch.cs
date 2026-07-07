using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Fixes a "PEAK Checkpoint Save" bug: its own autosave-on-campfire-lit logic is a
    /// Harmony postfix on <c>Campfire.Interact_CastFinished</c>, but that same vanilla
    /// method ALSO fires when a player finishes COOKING on an already-lit campfire (it
    /// branches internally on <c>Campfire.Lit</c> - lit means "finish cooking", unlit
    /// means "this finished the light"). Its own guard against treating a cook as a save
    /// trigger is <c>beenBurningFor > 2f</c>, a time-window guess: a client who finishes
    /// a cook cast within ~2s of the fire being lit (e.g. already holding food, ready to
    /// cook the moment a friend lights it) slips through it. For a non-host client that
    /// fires its own <c>RPC_RequestSave</c> at the host, causing an extra save (and its
    /// own "Saved!" message) moments after the real one from lighting
    ///
    /// <c>Interact_CastFinished</c> only ever sets Campfire's private
    /// <c>currentlyCookingItem</c> field when a cook interaction begins, and an unlit
    /// fire can't be cooked on, so at that point <c>Lit</c> was already true - the fire
    /// was lit some interaction earlier. It's also never cleared after a normal finish.
    /// So its presence is a reliable, state-independent signal that a given
    /// <c>Interact_CastFinished</c> call was a cook rather than the one-time light
    /// transition, unlike the checkpoint mod's own timing guess. We prefix its patch
    /// method directly (via Harmony's private-field injection, keyed off the campfire
    /// type of the __instance parameter) and skip it whenever that's the case
    /// </summary>
    public static class CampfireCookSaveFixPatch
    {
        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            try
            {
                var patchType = checkpointType.GetNestedType("Campfire_AutoSave_Patch",
                    BindingFlags.Public | BindingFlags.NonPublic);
                var target = patchType != null ? AccessTools.Method(patchType, "AutoSaveOnCampfire") : null;
                if (target == null)
                {
                    log.LogWarning("CampfireCookSaveFixPatch: AutoSaveOnCampfire not found (checkpoint mod "
                        + "likely changed); cook-triggered duplicate saves may still occur.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(CampfireCookSaveFixPatch)
                    .GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(target, prefix: prefix);
                log.LogInfo("CampfireCookSaveFixPatch: patched AutoSaveOnCampfire (skips cook-triggered duplicate saves).");
            }
            catch (Exception e)
            {
                log.LogError($"CampfireCookSaveFixPatch.Apply failed (non-fatal): {e}");
            }
        }

        // Skips the checkpoint mod's autosave entirely when this Interact_CastFinished
        // call was a cook finishing, not a campfire being lit - see class remarks
        private static bool Prefix(Item ___currentlyCookingItem)
        {
            return ___currentlyCookingItem == null;
        }
    }
}
