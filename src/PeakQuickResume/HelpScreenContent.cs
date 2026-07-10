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

            // The old "native load" (F6) line and the checkpoint-mod-specific teleport-bug
            // warning are gone (our own restore path has no native-key equivalent). The
            // restart tip below is kept regardless: quitting/rejoining still fixes most
            // load or teleport hiccups, whatever their source (possibly even vanilla ones)
            sb.Append(HelpScreenLocalization.Get(HelpText.QuickResumeFormat, Key(resumeKey))).Append("\n\n");

            // Accent-color just the tip's opening question, matching the gold key badges
            // (same treatment the old BugTitle line had). The question ends where the
            // parenthetical symptom list begins - split on the opening paren ('(' or the
            // fullwidth '（' the CJK translations use) rather than the \n (which comes only
            // after the parenthetical) so the symptoms stay in the normal body color.
            // Substring(question.Length) keeps whatever spacing was between them exactly as-is
            string tip = HelpScreenLocalization.Get(HelpText.RestartTip);
            int paren = tip.IndexOfAny(new[] { '(', '（' });
            if (paren > 0)
            {
                string question = tip.Substring(0, paren).TrimEnd();
                tip = $"<color={Accent}>{question}</color>{tip.Substring(question.Length)}";
            }
            sb.Append(tip).Append("\n\n");

            sb.Append(HelpScreenLocalization.Get(HelpText.AchievementsNote));

            // Persistent, re-viewable copy of the duplicate-mod warning the one-time popup
            // also shows (see Plugin.Update) - only when PEAK Checkpoint Save is actually
            // still installed. Reuses the same translated string (MessagesLocalization) so
            // there's only one place to maintain it. Accent-colored to stand out as a note
            if (Plugin.Instance?.CheckpointModInstalled == true)
                sb.Append("\n\n")
                    .Append($"<color={Accent}>{MessagesLocalization.Get(MsgKey.CheckpointModStillInstalled)}</color>");

            return sb.ToString();
        }
    }
}
