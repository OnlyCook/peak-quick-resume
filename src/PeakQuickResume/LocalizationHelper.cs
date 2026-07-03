namespace PEAKQuickResume
{
    /// <summary>
    /// Shared lookup used by <see cref="SavePickerLocalization"/> and
    /// <see cref="MessagesLocalization"/> (and mirrors <see cref="PauseMenuLocalization"/>'s
    /// own copy of the same rule): array order matches LocalizedText.Language's declaration
    /// order, the current language selects the entry, empty/missing entries fall back to
    /// English (index 0) exactly like the game's own LocalizedText.GetText does
    /// </summary>
    internal static class LocalizationHelper
    {
        public static string Resolve(string[] arr)
        {
            int idx = (int)LocalizedText.CURRENT_LANGUAGE;
            if (idx >= 0 && idx < arr.Length && !string.IsNullOrEmpty(arr[idx]))
                return arr[idx];
            return arr[0]; // English fallback
        }
    }
}
