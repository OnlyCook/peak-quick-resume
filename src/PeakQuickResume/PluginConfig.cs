using BepInEx.Configuration;

namespace PEAKQuickResume
{
    /// <summary>All user-facing configuration for PEAK Quick Resume</summary>
    public class PluginConfig
    {
        public readonly ConfigEntry<KeyboardShortcut> ResumeKey;
        public readonly ConfigEntry<bool> RequireDoublePress;
        public readonly ConfigEntry<float> DoublePressWindow;
        public readonly ConfigEntry<bool> AllowMidGame;
        public readonly ConfigEntry<bool> EnableDebugLogging;

        // Timing knobs, tuned blindly for now, exposed so we can iterate from
        // in-game reports without recompiling. Times are in seconds
        public readonly ConfigEntry<float> SettleAfterAirport;
        public readonly ConfigEntry<float> SettleAfterLevel;
        public readonly ConfigEntry<float> StepTimeout;
        public readonly ConfigEntry<float> CoopAirportSettle;

        public PluginConfig(ConfigFile cfg)
        {
            ResumeKey = cfg.Bind("General", "resumeKey",
                new KeyboardShortcut(UnityEngine.KeyCode.F7),
                "Opens the save picker (a menu of your checkpoints for the current solo/co-op category). "
                + "Use the arrow keys to choose, then press this key again (or Enter) to load. The newest "
                + "save is preselected, so pressing it twice loads your latest checkpoint. Delete removes "
                + "the highlighted save; Escape closes the menu.");

            RequireDoublePress = cfg.Bind("General", "requireDoublePress", true,
                "DEPRECATED / no longer used: the F7 save picker now provides the confirmation step "
                + "(open, then press again to load). Kept only so old config files don't error.");

            DoublePressWindow = cfg.Bind("General", "doublePressWindow", 5f,
                "DEPRECATED / no longer used (superseded by the F7 save picker). Kept for config compatibility.");

            AllowMidGame = cfg.Bind("General", "allowMidGame", true,
                "If enabled, the resume key also works while you are alive in a level (returns to the Airport, "
                + "starts a fresh run and loads the save). If disabled, it only works after death / at the Airport.");

            EnableDebugLogging = cfg.Bind("Debug", "enableDebugLogging", true,
                "Verbose logging of every step of the resume sequence. Very useful while the mod is young, "
                + "please keep this on when reporting issues.");

            SettleAfterAirport = cfg.Bind("Timing", "settleAfterAirport", 0.75f,
                "Seconds to wait after the Airport scene loads before starting the new run (advanced).");

            SettleAfterLevel = cfg.Bind("Timing", "settleAfterLevel", 1.5f,
                "Seconds to wait after the level scene loads (and the local character exists) before triggering "
                + "the checkpoint load (advanced).");

            StepTimeout = cfg.Bind("Timing", "stepTimeout", 30f,
                "Max seconds to wait for each stage (Airport load, kiosk, level load, character spawn) before "
                + "aborting the resume sequence (advanced).");

            CoopAirportSettle = cfg.Bind("Timing", "coopAirportSettle", 2f,
                "COOP ONLY: extra seconds to wait at the Airport before starting the fresh run, so other "
                + "players have finished loading the Airport and will receive the run-start (advanced). "
                + "Raise this if a client occasionally gets left behind on a slow connection.");
        }
    }
}
