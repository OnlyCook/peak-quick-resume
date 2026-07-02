using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Archives every checkpoint save the moment the checkpoint mod writes it, by
    /// Harmony-postfixing its <c>SavePlayerOffline</c> / <c>SavePlayerCoop</c> methods
    /// Both write their file synchronously before returning, so by the time our postfix
    /// runs the file exists and <see cref="SaveArchive.Sync"/> can copy it. We never
    /// touch the mod's DLL, if either method is renamed in a future version, patching
    /// simply no-ops (logged) and only the growing-archive feature is lost
    /// </summary>
    public static class SavePatch
    {
        private static ManualLogSource _log;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            _log = log;
            TryPatch(harmony, checkpointType, "SavePlayerOffline", nameof(PostfixOffline), log);
            TryPatch(harmony, checkpointType, "SavePlayerCoop", nameof(PostfixCoop), log);
        }

        private static void TryPatch(Harmony harmony, Type type, string method, string postfix, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    log.LogWarning($"SavePatch: {method} not found; new saves of that type won't auto-archive.");
                    return;
                }
                var hm = new HarmonyMethod(typeof(SavePatch).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(target, postfix: hm);
                log.LogInfo($"SavePatch: patched {method} (auto-archiving saves).");
            }
            catch (Exception e)
            {
                log.LogError($"SavePatch.Apply({method}) failed (non-fatal): {e}");
            }
        }

        private static void PostfixOffline() => SaveArchive.Sync(offline: true, _log);
        private static void PostfixCoop() => SaveArchive.Sync(offline: false, _log);
    }
}
