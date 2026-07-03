using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Localizes the ONE piece of checkpoint-mod UI text we override: the "Loading
    /// savegame..." caption its own loading-screen overlay shows during any checkpoint
    /// load (ours via Quick Resume, or its own F6). We never touch anything else in that
    /// mod's UI, this single overlay is shared by both mods' load paths often enough that
    /// leaving it English-only while everything we control is localized would be jarring
    ///
    /// Implemented as a Harmony prefix on the checkpoint mod's private
    /// <c>LoadingScreen(bool enableLoadingScreen, string msg = "Loading savegame...")</c>,
    /// swapping the message just before it's used, only when it's still that exact default
    /// (a caller-supplied custom message, if the mod ever adds one, is left alone)
    /// </summary>
    public static class LoadingScreenPatch
    {
        private const string DefaultMessage = "Loading savegame...";

        private static ManualLogSource _log;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            _log = log;
            try
            {
                var target = AccessTools.Method(checkpointType, "LoadingScreen",
                    new[] { typeof(bool), typeof(string) });
                if (target == null)
                {
                    log.LogWarning("LoadingScreenPatch: LoadingScreen(bool, string) not found; "
                        + "the loading-screen caption will stay English-only.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(LoadingScreenPatch), nameof(Prefix)));
                log.LogInfo("LoadingScreenPatch: patched LoadingScreen (localized loading caption).");
            }
            catch (Exception e)
            {
                log.LogError($"LoadingScreenPatch.Apply failed (non-fatal): {e}");
            }
        }

        private static void Prefix(bool enableLoadingScreen, ref string msg)
        {
            try
            {
                if (!enableLoadingScreen) return;
                if (!string.Equals(msg, DefaultMessage, StringComparison.Ordinal)) return;
                msg = MessagesLocalization.Get(MsgKey.LoadingSavegame);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"LoadingScreenPatch.Prefix failed (non-fatal): {e.Message}");
            }
        }
    }
}
