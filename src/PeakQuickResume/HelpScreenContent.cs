using System.Text;

namespace PEAKQuickResume
{
    /// <summary>
    /// Builds the F1 help screen's body text (see <see cref="HelpScreen"/>). Split out
    /// from the screen itself since this is pure content/formatting, no UI code
    ///
    /// No bold anywhere: the game's own font has no real bold face, and TMP faking one
    /// (tried in an earlier version of this screen) came out illegible. A single gold
    /// accent color (matching the F7 picker's own key badges) does the emphasis work
    /// for section headers and key mentions instead, against the picker's proven
    /// pale-blue body text on its blue panel - not the plain-black-background
    /// "everything needs to be colorful to read" logic that made the earlier
    /// screen-wide plain-text version pick unreadable colors
    /// </summary>
    internal static class HelpScreenContent
    {
        private const string Accent = "#FFF2B8"; // matches SavePicker.KeyTextColor

        private static string Key(string k) => $"<color={Accent}>({k})</color>";

        public static string Build(PluginConfig cfg)
        {
            string resumeKey = Plugin.Instance?.ResumeKeyText ?? "F7";

            var sb = new StringBuilder();
            sb.Append(HelpScreenLocalization.Get(HelpText.Intro1)).Append('\n');

            // The old "native load" (F6) line and the teleport-bug warning were only ever
            // shown alongside the external checkpoint mod; our own restore path has no
            // native-key equivalent and no longer has the segment-sync bug that warning
            // described, so both are gone
            sb.Append(HelpScreenLocalization.Get(HelpText.QuickResumeFormat, Key(resumeKey))).Append("\n\n");

            sb.Append(HelpScreenLocalization.Get(HelpText.AchievementsNote));

            return sb.ToString();
        }
    }
}
