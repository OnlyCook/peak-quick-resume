using System;
using System.Globalization;
using System.IO;
using BepInEx;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Builds every on-disk save path this mod uses. One save store -
    /// <c>BepInEx/plugins/QuickResume/Archive</c> - with no separate "canonical"
    /// current-save file: <see cref="OwnSaveCapture"/> writes each event straight into
    /// the archive, and loading reads straight back out of it. The old shared
    /// Checkpoint_Save folder is never read, written, or migrated from.
    ///
    /// Layout:
    ///   offline: Archive\Offline\peak_save_{ascent|CustomRun}_offline__{stamp}.json
    ///   coop:    Archive\Coop\peak_save_{ascent|CustomRun}_{userId}__{stamp}.json
    ///
    /// <c>{stamp}</c> is the save event's identity: one value generated per autosave
    /// (see <see cref="NewEventStamp"/>) and written into every participating player's
    /// filename, so co-op siblings are matched by exact string equality, not file mtimes.
    /// </summary>
    public static class OwnSavePaths
    {
        /// <summary>Timestamp format used for the <c>__{stamp}</c> suffix on every save file</summary>
        public const string StampFormat = "yyyyMMdd_HHmmss_fff";

        /// <summary>Separator between a save's stem and its event stamp</summary>
        public const string StampSeparator = "__";

        /// <summary>Root of the one and only save store</summary>
        public static string ArchiveRoot => Path.Combine(Paths.PluginPath, "QuickResume", "Archive");

        /// <summary>Per-category store directory (<c>Archive/Offline</c> or <c>Archive/Coop</c>)</summary>
        public static string ArchiveDir(bool offline) => Path.Combine(ArchiveRoot, offline ? "Offline" : "Coop");

        /// <summary>
        /// A fresh save-event stamp. Call this ONCE per autosave on the host and pass the
        /// same value into every <see cref="For"/> call for that event - that shared value
        /// is what makes a co-op save event's files findable as one group later
        /// </summary>
        public static string NewEventStamp() =>
            DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// The stem (filename without the <c>__{stamp}</c> suffix or extension) for a run
        /// target, e.g. <c>peak_save_2_offline</c> or <c>peak_save_CustomRun_76561198...</c>
        /// </summary>
        public static string Stem(SaveTarget target, bool offline, string userId)
        {
            string token = target.IsCustom ? "CustomRun" : target.Ascent.ToString(CultureInfo.InvariantCulture);
            return offline ? $"peak_save_{token}_offline" : $"peak_save_{token}_{userId}";
        }

        /// <summary>Full path of one player's file within one save event</summary>
        public static string For(SaveTarget target, bool offline, string userId, string stamp) =>
            Path.Combine(ArchiveDir(offline), Stem(target, offline, userId) + StampSeparator + stamp + ".json");

        /// <summary>
        /// Splits an archive filename (with or without extension) into its stem and event
        /// stamp. Returns false for anything not matching the layout above - a stray file,
        /// or one of the flat legacy names earlier versions wrote (those are relocated by
        /// <see cref="SaveArchive"/>'s own migration, not parsed here)
        /// </summary>
        public static bool TrySplit(string fileName, out string stem, out string stamp)
        {
            stem = "";
            stamp = "";
            string name = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(name)) return false;

            int i = name.LastIndexOf(StampSeparator, StringComparison.Ordinal);
            if (i <= 0 || i >= name.Length - StampSeparator.Length) return false;

            stem = name.Substring(0, i);
            stamp = name.Substring(i + StampSeparator.Length);
            return stamp.Length > 0;
        }

        /// <summary>
        /// Pulls the userId out of a co-op stem like <c>peak_save_-1_7656...</c> or
        /// <c>peak_save_CustomRun_7656...</c> - always the segment after the last '_'
        /// (ascent tokens and "CustomRun" contain no underscore). Meaningless for offline
        /// stems, which end in the literal "_offline" instead
        /// </summary>
        public static bool TryGetCoopUserId(string stem, out string userId)
        {
            userId = "";
            if (string.IsNullOrEmpty(stem)) return false;
            int u = stem.LastIndexOf('_');
            if (u <= 0 || u >= stem.Length - 1) return false;
            userId = stem.Substring(u + 1);
            return userId.Length > 0 && userId != "offline";
        }

        /// <summary>Our own Photon user id (== SteamID64), used to name co-op save files</summary>
        public static string LocalUserId()
        {
            try { return PhotonNetwork.LocalPlayer?.UserId ?? ""; }
            catch { return ""; }
        }
    }
}
