using System;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Augments the checkpoint mod's F1 tutorial overlay so it also advertises Quick
    /// Resume (F7) and shows both mod versions. We do this by Harmony-postfixing the
    /// checkpoint mod's <c>ShowTutorialMessage(bool active, string message)</c> and
    /// rewriting the text it just placed on its TextMeshPro label, we never touch its DLL
    ///
    /// All string anchoring is defensive: if a checkpoint-mod update changes the wording,
    /// each rewrite simply no-ops (logged) and the original tutorial shows unchanged
    /// </summary>
    public static class TutorialPatch
    {
        private static ManualLogSource _log;
        private static PropertyInfo _textProp; // TMP_Text.text
        private static FieldInfo _tutorialTmpField; // Plugin._tutorialTMP

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            _log = log;
            try
            {
                var target = AccessTools.Method(checkpointType, "ShowTutorialMessage");
                if (target == null)
                {
                    log.LogWarning("TutorialPatch: ShowTutorialMessage not found; F1 tutorial will not mention F7.");
                    return;
                }
                _tutorialTmpField = AccessTools.Field(checkpointType, "_tutorialTMP");
                var postfix = new HarmonyMethod(typeof(TutorialPatch).GetMethod(nameof(Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(target, postfix: postfix);
                log.LogInfo("TutorialPatch: patched ShowTutorialMessage (F1 tutorial will mention F7).");
            }
            catch (Exception e)
            {
                log.LogError($"TutorialPatch.Apply failed (non-fatal): {e}");
            }
        }

        // Runs after the checkpoint mod sets its default tutorial text
        private static void Postfix(bool active, string message, object __instance)
        {
            try
            {
                // Only touch the built-in tutorial (message == ""), not custom popups
                if (!active || !string.IsNullOrEmpty(message)) return;
                if (_tutorialTmpField == null) return;

                object tmp = _tutorialTmpField.GetValue(__instance);
                if (tmp == null) return;

                if (_textProp == null)
                    _textProp = tmp.GetType().GetProperty("text");
                if (_textProp == null) return;

                string text = _textProp.GetValue(tmp) as string;
                if (string.IsNullOrEmpty(text)) return;

                // Reclaim vertical space first (so our two extra lines don't push the
                // block off the top/bottom), then add our content
                string updated = CompactPadding(text);
                updated = InsertF7Line(updated);
                updated = RewriteVersionLine(updated);

                if (!string.Equals(updated, text, StringComparison.Ordinal))
                    _textProp.SetValue(tmp, updated);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"TutorialPatch.Postfix failed (non-fatal): {e.Message}");
            }
        }

        // Collapse the checkpoint mod's oversized blank-line gaps so adding our two
        // lines keeps the overall block no taller than the original (which fit fine)
        // Runs of 3+ newlines -> a single blank line (2 newlines). Our own inserted
        // separators are exactly 2 newlines, so they are untouched
        private static string CompactPadding(string text)
        {
            try { return Regex.Replace(text, "\n{3,}", "\n\n"); }
            catch { return text; }
        }

        // Add a Quick Resume line right after the checkpoint mod's F6 line. Wording
        // emphasises what F6 does NOT: it works from anywhere and in a single step
        private static string InsertF7Line(string text)
        {
            const string anchor = "just start a level and press (";
            int ai = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (ai < 0) { _log?.LogWarning("TutorialPatch: F6 anchor not found; skipping F7 line."); return text; }
            int close = text.IndexOf(')', ai);
            if (close < 0) return text;

            string key = Plugin.Instance?.ResumeKeyText ?? "F7";
            string f7 = "\n\nQuick Resume: press (" + key + ") ANYWHERE to open the save picker; arrow-keys\n"
                      + "to choose a checkpoint, (" + key + ")/Enter to load (or press (" + key + ") twice for the latest).";
            return text.Insert(close + 1, f7);
        }

        // "Mod version: 0.4.7" -> "PCS Mod Version: 0.4.7 / Quick Resume Mod Version: X"
        private static string RewriteVersionLine(string text)
        {
            const string marker = "Mod version:";
            int mi = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (mi < 0) { _log?.LogWarning("TutorialPatch: version marker not found; leaving version line."); return text; }

            string pcsVer = text.Substring(mi + marker.Length).Trim();
            string ourVer = PluginInfo.Version;
            string combined = "PCS Mod Version: " + pcsVer + " / Quick Resume Mod Version: " + ourVer;
            return text.Substring(0, mi) + combined;
        }
    }
}
