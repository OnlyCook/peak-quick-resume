using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Zorro.UI.Modal;

namespace PEAKQuickResume
{
    /// <summary>
    /// Suppresses vanilla's "game has been updated, close the game to continue" modal
    /// and replaces it with a non-blocking heads-up. This check can fire hours into an
    /// already-running session (e.g. at the Airport kiosk), and RunLauncher routes
    /// players through the Airport mid-run as a pit stop for loading unfinished saves -
    /// so the vanilla forced-quit there would brick an otherwise-recoverable save.
    ///
    /// Both known call sites funnel through Modal.OpenModal, so we patch that single
    /// choke point and match on the modal's localized out-of-date title, leaving every
    /// other modal (disconnect notices, kick messages) untouched.
    ///
    /// Trade-off: a save loaded after this point may point at content that no longer
    /// matches other clients (e.g. a map-pool rotation) - accepted deliberately since
    /// it's still better than being unable to load the save at all; the player is told
    /// plainly so they can act on it.
    /// </summary>
    public static class GameUpdateModalSuppressPatch
    {
        private static ManualLogSource _log;
        private static OwnMessageOverlay _messageOverlay;
        private static bool _noticeShownThisSession;

        public static void Apply(Harmony harmony, ManualLogSource log, OwnMessageOverlay messageOverlay)
        {
            _log = log;
            _messageOverlay = messageOverlay;
            try
            {
                var target = AccessTools.Method(typeof(Modal), nameof(Modal.OpenModal));
                if (target == null)
                {
                    log.LogWarning("GameUpdateModalSuppressPatch: Modal.OpenModal not found; the vanilla "
                        + "'game updated, close to continue' popup can still force-quit the game at the Airport.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(GameUpdateModalSuppressPatch), nameof(Prefix)));
                log.LogInfo("GameUpdateModalSuppressPatch: patched Modal.OpenModal.");
            }
            catch (Exception e)
            {
                log.LogError($"GameUpdateModalSuppressPatch.Apply failed (non-fatal): {e}");
            }
        }

        private static bool Prefix(HeaderModalOption headerContent)
        {
            try
            {
                if (headerContent is not DefaultHeaderModalOption defaultHeader) return true;
                if (!IsOutOfDateTitle(defaultHeader.Title)) return true;

                _log.LogWarning("Suppressed the vanilla game-update modal (would have force-quit on the only "
                    + "button). The game server reports a newer build is available. DO NOT update or restart "
                    + "PEAK until everyone is done with this save - the currently running game keeps working "
                    + "fine, but an update mid-save (especially a map-pool rotation) can make this save load "
                    + "the wrong island for good.");

                if (_messageOverlay != null && !_noticeShownThisSession)
                {
                    _noticeShownThisSession = true;
                    _messageOverlay.Show(
                        MessagesLocalization.Get(MsgKey.GameUpdateDialogSuppressed),
                        new Color(1f, 0.55f, 0.3f, 1f), 12f);
                }

                return false; // skip Modal.OpenModal's body: no popup, no onClose, no quit
            }
            catch (Exception e)
            {
                _log?.LogError($"GameUpdateModalSuppressPatch.Prefix failed (non-fatal, letting the vanilla modal through): {e}");
                return true;
            }
        }

        private static bool IsOutOfDateTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            // Resolved fresh each call (not cached) so this still matches correctly if the
            // player's language changes mid-session
            return title == LocalizedText.GetText("VERSIONOUTOFDATE")
                || title == LocalizedText.GetText("MODAL_OUTOFDATE_TITLE");
        }
    }
}
