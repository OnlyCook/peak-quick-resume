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

        public static string Build(CheckpointInterop checkpoint, PluginConfig cfg, TeleportConfigOverride teleportOverride = null)
        {
            string loadKey = checkpoint?.TryGetLoadKeyText() ?? "F6";
            string resumeKey = Plugin.Instance?.ResumeKeyText ?? "F7";

            int altVal = cfg?.AltTeleportJumpLogic.Value ?? 2;
            int optimizedVal = cfg?.OptimizedCoopJumpLogic.Value ?? 1;
            bool optimizedEnabled = cfg?.EnableOptimizedCoopLoading.Value ?? true;
            int? baseVal = null;
            if (teleportOverride != null && teleportOverride.TryGetBaseJumpLogic(out int b)) baseVal = b;
            string baseValText = baseVal.HasValue ? baseVal.Value.ToString() : "unknown";
            string jumpLogicDesc = checkpoint?.TryGetTeleportJumpLogicDescription();

            var sb = new StringBuilder();
            sb.Append(HelpScreenLocalization.Get(HelpText.Intro1)).Append('\n');
            sb.Append(HelpScreenLocalization.Get(HelpText.NativeLoadFormat, Key(loadKey))).Append('\n');
            sb.Append(HelpScreenLocalization.Get(HelpText.QuickResumeFormat, Key(resumeKey))).Append("\n\n");

            sb.Append($"<color={Accent}>{HelpScreenLocalization.Get(HelpText.BugTitle)}</color> ")
                .Append(HelpScreenLocalization.Get(HelpText.BugSymptoms)).Append('\n');
            sb.Append(HelpScreenLocalization.Get(HelpText.BugExplain)).Append("\n\n");

            // The single most effective fix, and the cheapest to try - goes first,
            // before any of the teleportJumpLogic workarounds below
            sb.Append($"<color={Accent}>{HelpScreenLocalization.Get(HelpText.RestartFirstTitle)}</color> ")
                .Append(HelpScreenLocalization.Get(HelpText.RestartFirstNote)).Append("\n\n");

            if (optimizedEnabled)
            {
                sb.Append(HelpScreenLocalization.Get(HelpText.OptimizedIntroFormat, Key(loadKey), Key(resumeKey), optimizedVal, baseValText))
                    .Append(' ').Append(HelpScreenLocalization.Get(HelpText.OptimizedSoloNote)).Append('\n');
                sb.Append(HelpScreenLocalization.Get(HelpText.AskHostFormat, resumeKey)).Append("\n\n");
                sb.Append(HelpScreenLocalization.Get(HelpText.ShiftLineFormat, Key("Shift"), Key(resumeKey), baseValText)).Append('\n');
                sb.Append(HelpScreenLocalization.Get(HelpText.AltLineFormat, Key("Alt"), Key(resumeKey), altVal)).Append("\n\n");
                sb.Append(HelpScreenLocalization.Get(HelpText.OptimizedFooterNote)).Append("\n\n");
            }
            else
            {
                sb.Append(HelpScreenLocalization.Get(HelpText.AskHostFormat, resumeKey)).Append("\n\n");
                sb.Append(HelpScreenLocalization.Get(HelpText.ShiftLineFormat, Key("Shift"), Key(resumeKey), baseValText)).Append('\n');
                sb.Append(HelpScreenLocalization.Get(HelpText.AltLineFormat, Key("Alt"), Key(resumeKey), altVal)).Append("\n\n");
                sb.Append(HelpScreenLocalization.Get(HelpText.DisabledFooterNote)).Append("\n\n");
                sb.Append(HelpScreenLocalization.Get(HelpText.DisabledNoteFormat, optimizedVal)).Append("\n\n");
            }
            //if (!string.IsNullOrEmpty(jumpLogicDesc))
            //    sb.Append($"<size=85%>{jumpLogicDesc}</size>\n\n");

            sb.Append(HelpScreenLocalization.Get(HelpText.AchievementsNote));

            return sb.ToString();
        }
    }
}
