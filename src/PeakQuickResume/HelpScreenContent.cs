using System.Text;

namespace PEAKQuickResume
{
    /// <summary>
    /// Builds the F1 help screen's body text (see HelpScreen). No bold anywhere - the
    /// game's font has no real bold face and TMP faking one came out illegible - so a
    /// single gold accent color (matching the F7 picker's key badges) does the emphasis
    /// work instead.
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

            // Two variants of the same line, matching whichever picker key layout is
            // configured: by default the resume key opens/closes the picker and only Enter
            // loads, or the old "press it twice to load" layout via resume-key-loads-instead-of-closing.
            bool keyLoads = cfg != null && cfg.ResumeKeyLoadsInsteadOfClosing.Value;
            sb.Append(HelpScreenLocalization.Get(
                keyLoads ? HelpText.QuickResumeKeyLoadsFormat : HelpText.QuickResumeFormat,
                Key(resumeKey))).Append("\n\n");

            // Accent-color just the tip's opening question, split on the opening paren
            // ('(' or the fullwidth '（' used by CJK translations) so the parenthetical
            // symptom list stays in the normal body color.
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
            // also shows (see Plugin.Update), reusing the same translated string.
            if (Plugin.Instance?.CheckpointModInstalled == true)
                sb.Append("\n\n")
                    .Append($"<color={Accent}>{MessagesLocalization.Get(MsgKey.CheckpointModStillInstalled)}</color>");

            return sb.ToString();
        }
    }
}
