using System;
using System.IO;
using BepInEx;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's <c>GetPlayerSaveFile</c> (decompile
    /// lines 2177-2216) - builds the exact same on-disk paths so we keep writing
    /// into the SAME <c>Checkpoint_Save</c> folder structure the checkpoint mod
    /// uses. This is deliberate (see ROADMAP.md Phase 8): <see cref="SaveArchive"/>/
    /// <see cref="SaveDiscovery"/>/the F7 picker need zero changes, and the
    /// checkpoint mod (kept installed during the Phase 8 transition window for
    /// diffing) keeps seeing/writing the same files we do
    ///
    /// The legacy single-file mode (<c>configLegacySaveFile</c>) is intentionally
    /// NOT ported here yet - see ROADMAP.md's open question about whether to carry
    /// it forward. <see cref="ForAscent"/>/<see cref="ForCustomRun"/> only cover the
    /// modern per-ascent / per-custom-run layout
    /// </summary>
    public static class OwnSavePaths
    {
        private static string BaseDir => Path.Combine(Paths.PluginPath, "Checkpoint_Save");
        private static string CoopDir => Path.Combine(BaseDir, "Coop");

        /// <summary>
        /// The canonical save-file path for a normal (non-custom) run at the given
        /// ascent, mirroring <c>GetPlayerSaveFile(userId, ascent)</c>'s non-legacy,
        /// non-custom-run branch exactly:
        ///   offline: Checkpoint_Save\peak_save_{ascent}_offline.json
        ///   coop:    Checkpoint_Save\Coop\peak_save_{ascent}_{userId}.json
        /// </summary>
        public static string ForAscent(bool offline, int ascent, string userId = null)
        {
            return offline
                ? Path.Combine(BaseDir, $"peak_save_{ascent}_offline.json")
                : Path.Combine(CoopDir, $"peak_save_{ascent}_{userId}.json");
        }

        /// <summary>
        /// The canonical save-file path for a custom run, mirroring
        /// <c>GetPlayerSaveFile</c>'s custom-run branch exactly:
        ///   offline: Checkpoint_Save\peak_save_CustomRun_offline.json
        ///   coop:    Checkpoint_Save\Coop\peak_save_CustomRun_{userId}.json
        /// </summary>
        public static string ForCustomRun(bool offline, string userId = null)
        {
            return offline
                ? Path.Combine(BaseDir, "peak_save_CustomRun_offline.json")
                : Path.Combine(CoopDir, $"peak_save_CustomRun_{userId}.json");
        }

        /// <summary>
        /// Resolves the canonical path for <paramref name="target"/> exactly like the
        /// checkpoint mod's own <c>GetPlayerSaveFile(userId, ascent)</c> does at its call
        /// sites (which pass either an explicit ascent, or default to
        /// <c>Ascents.currentAscent</c> when none is given - see decompile line 2180).
        /// <paramref name="offline"/> should read <c>PhotonNetwork.OfflineMode</c> at the
        /// call site, exactly like the original
        /// </summary>
        public static string For(SaveTarget target, bool offline, string userId = null)
            => target.IsCustom ? ForCustomRun(offline, userId) : ForAscent(offline, target.Ascent, userId);

        /// <summary>Our own Photon user id (== SteamID64), same source SaveArchive already uses</summary>
        public static string LocalUserId()
        {
            try { return PhotonNetwork.LocalPlayer?.UserId ?? ""; }
            catch { return ""; }
        }
    }
}
