using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Zorro.UI.Modal;

namespace PEAKQuickResume
{
    /// <summary>
    /// Suppresses vanilla's "game has been updated, close the game to continue" modal
    /// (single OK button whose only effect is <c>Application.Quit</c>) and replaces it
    /// with our own non-blocking heads-up
    ///
    /// The vanilla check isn't scoped to boot: <c>NextLevelService.NextLevelData.SecondsLeft</c>
    /// (decompiled Assembly-CSharp) re-queries <c>CloudAPI.CheckVersion</c> every time its
    /// cached countdown expires, from wherever that property happens to be read - the
    /// Airport kiosk's <c>Start()</c> among others - so this can fire hours into an
    /// already-running session, not just at the title screen. In vanilla that's fine: you
    /// only ever see the Airport after dying or winning a completed run, so being forced
    /// to quit there costs nothing. This mod breaks that assumption - <see cref="RunLauncher"/>
    /// sends players to the Airport as a mid-run pit stop purely to make loading an
    /// unfinished save possible, so the SAME forced-quit at the SAME screen would brick an
    /// otherwise-recoverable save. The Airport visit is our intermediate halt, not
    /// vanilla's "run is over" checkpoint
    ///
    /// The two known call sites both open the modal via <c>Zorro.UI.Modal.Modal.OpenModal</c>
    /// with a <see cref="DefaultHeaderModalOption"/> whose Title is the localized
    /// "VERSIONOUTOFDATE" (MainMenuPageHandler.Start) or "MODAL_OUTOFDATE_TITLE"
    /// (NextLevelService.NextLevelData.SecondsLeft) key. Rather than chase each call site
    /// individually (private nested types, anonymous delegates), we patch the single
    /// choke point both funnel through and match on that title - anything else opened
    /// through Modal.OpenModal (disconnect notices, kick messages, etc.) is untouched
    ///
    /// Understand what this trades away: the underlying game processes/servers may have
    /// actually moved on (e.g. a map-pool rotation), so a save loaded after this point can
    /// end up pointing at content that no longer matches what everyone else's client sees.
    /// We accept that risk deliberately - it's still strictly better than the alternative
    /// (unable to load the save at all) - and tell the player plainly so they can act on
    /// it (finish up, don't update anyone's game mid-save). Players without this mod
    /// installed still see the real modal and cannot get past it - nothing we can do about
    /// that from here
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
                    // Same duration as GameUpdatedSavesMayBeWrong (Plugin.cs) - both are
                    // "don't touch the game/update right now" notices of equal importance
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
