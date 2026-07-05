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
    ///
    /// This "Save game loaded!" moment is also the checkpoint mod's own signal that a
    /// load has actually finished (it fires from <c>LoadInventoryDelayed</c>, well
    /// after the teleport itself and its loading screen), so a postfix here also arms
    /// <see cref="TeleportWatchdog"/>'s watch window, see Phase 6 in ROADMAP.md.
    /// Postfixes see the final (already-localized) parameter value, so matching
    /// against <see cref="MessagesLocalization.Get(MsgKey)"/> here works regardless
    /// of patch ordering with the prefix below
    ///
    /// Also appends a subtle "(1)"/"(2)" suffix when a Phase 6 step 2 Shift/Alt
    /// teleport-config override is currently active (<see cref="TeleportConfigOverride.LastAppliedOverride"/>),
    /// nothing appended for the base default. The postfix's text match is a
    /// <c>StartsWith</c> rather than exact equality to still recognize the message
    /// once that suffix is appended
    /// </summary>
    public static class SavegameLoadedMessagePatch
    {
        private const string DefaultMessage = "Save game loaded!";

        private static ManualLogSource _log;
        private static TeleportWatchdog _watchdog;
        private static TeleportConfigOverride _teleportOverride;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log, TeleportWatchdog watchdog = null,
            TeleportConfigOverride teleportOverride = null)
        {
            _log = log;
            _watchdog = watchdog;
            _teleportOverride = teleportOverride;
            try
            {
                var target = AccessTools.Method(checkpointType, "ShowMessage",
                    new[] { typeof(string), typeof(Color), typeof(float), typeof(bool) });
                if (target == null)
                {
                    log.LogWarning("SavegameLoadedMessagePatch: ShowMessage(string, Color, float, bool) not found; "
                        + "the \"Save game loaded!\" message will stay English-only, and the teleport watchdog "
                        + "will never arm.");
                    return;
                }

                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(SavegameLoadedMessagePatch), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(SavegameLoadedMessagePatch), nameof(Postfix)));
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
                text = MessagesLocalization.Get(MsgKey.SavegameLoaded)
                    + TeleportConfigOverride.FormatIndicator(_teleportOverride?.LastAppliedOverride);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"SavegameLoadedMessagePatch.Prefix failed (non-fatal): {e.Message}");
            }
        }

        private static void Postfix(string text)
        {
            try
            {
                if (_watchdog == null) return;
                if (!text.StartsWith(MessagesLocalization.Get(MsgKey.SavegameLoaded), StringComparison.Ordinal)) return;
                _watchdog.ArmPendingWatch();
            }
            catch (Exception e)
            {
                _log?.LogWarning($"SavegameLoadedMessagePatch.Postfix failed (non-fatal): {e.Message}");
            }
        }
    }
}
