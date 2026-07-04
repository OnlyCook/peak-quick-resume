using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Localizes the ONE other piece of checkpoint-mod UI text we override: the
    /// "Save game loaded!" on-screen message it shows once a checkpoint load finishes
    /// (ours via Quick Resume, or its own F6). Same reasoning as
    /// <see cref="LoadingScreenPatch"/>: this fires as a direct result of our own
    /// load/resume flow just as often as the checkpoint mod's own key, so leaving it
    /// English-only would be jarring
    ///
    /// Implemented as a Harmony prefix on the checkpoint mod's public
    /// <c>ShowMessage(string text, Color color, float duration, bool disableMessage)</c>,
    /// swapping the message just before it's used, only when it's still that exact
    /// literal (any other message shown through this same method, ours included, is
    /// left alone). This also covers the coop case: <c>RPC_SendMessage</c> resolves to
    /// this same method call on each connected client, so every client localizes it
    /// against its own language setting rather than the host's
    /// </summary>
    public static class SavegameLoadedMessagePatch
    {
        private const string DefaultMessage = "Save game loaded!";

        private static ManualLogSource _log;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            _log = log;
            try
            {
                var target = AccessTools.Method(checkpointType, "ShowMessage",
                    new[] { typeof(string), typeof(Color), typeof(float), typeof(bool) });
                if (target == null)
                {
                    log.LogWarning("SavegameLoadedMessagePatch: ShowMessage(string, Color, float, bool) not found; "
                        + "the \"Save game loaded!\" message will stay English-only.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SavegameLoadedMessagePatch), nameof(Prefix)));
                log.LogInfo("SavegameLoadedMessagePatch: patched ShowMessage (localized savegame-loaded message).");
            }
            catch (Exception e)
            {
                log.LogError($"SavegameLoadedMessagePatch.Apply failed (non-fatal): {e}");
            }
        }

        private static void Prefix(ref string text)
        {
            try
            {
                if (!string.Equals(text, DefaultMessage, StringComparison.Ordinal)) return;
                text = MessagesLocalization.Get(MsgKey.SavegameLoaded);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"SavegameLoadedMessagePatch.Prefix failed (non-fatal): {e.Message}");
            }
        }
    }
}
