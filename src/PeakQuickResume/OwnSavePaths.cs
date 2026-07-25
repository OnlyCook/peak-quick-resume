using System;
using System.Globalization;
using System.IO;
using BepInEx;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Builds every on-disk save path this mod uses. There is exactly ONE
    /// save store - <c>BepInEx/plugins/QuickResume/Archive</c> - and no "canonical"
    /// current-save file anywhere: <see cref="OwnSaveCapture"/> writes each save event
    /// straight into the archive, and the load path reads straight back out of it
    ///
    /// The old <c>BepInEx/plugins/Checkpoint_Save</c> folder (dominik0207's "PEAK
    /// Checkpoint Save", whose layout we used to share) is NEVER read, written, or
    /// migrated from anymore. That shared folder was the root of two real problems:
    /// files written by that mod were indistinguishable from ours by name or path, so
    /// the old archive sync copied them in as if they were ours; and every load went
    /// through a copy-archive-over-canonical round trip that re-stamped file
    /// modification times, which is what made hand-editing a save produce duplicated
    /// picker rows and mismatched client state
    ///
    /// Layout (unchanged from the archive format earlier versions already wrote, so
    /// existing archived saves keep working):
    ///   offline: Archive\Offline\peak_save_{ascent|CustomRun}_offline__{stamp}.json
    ///   coop:    Archive\Coop\peak_save_{ascent|CustomRun}_{userId}__{stamp}.json
    ///
    /// <c>{stamp}</c> is <see cref="StampFormat"/> and is the SAVE EVENT's identity: one
    /// value generated per autosave (see <see cref="NewEventStamp"/>) and written into
    /// every participating player's filename, so the host's file and its co-op siblings
    /// are matched by exact string equality rather than by guessing from file
    /// modification times. Editing a file's contents can therefore never break the
    /// matching, and re-saving can never collide with an existing event
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
