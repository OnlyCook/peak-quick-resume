using BepInEx.Configuration;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>All user-facing configuration for PEAK Quick Resume</summary>
    public class PluginConfig
    {
        public readonly ConfigEntry<KeyCode> ResumeKey;
        public readonly ConfigEntry<bool> ResumeKeyAlsoConfirmsLoad;
        public readonly ConfigEntry<bool> RequireDoublePress;
        public readonly ConfigEntry<float> DoublePressWindow;
        public readonly ConfigEntry<bool> AllowMidGame;
        public readonly ConfigEntry<float> PanelOpacity;
        public readonly ConfigEntry<bool> EnableDebugLogging;

        // Miscellaneous QoL: pause menu buttons (mechanics 3 & 4). Each independently
        // hides its button from the pause menu entirely when disabled
        public readonly ConfigEntry<bool> ShowRestartButton;
        public readonly ConfigEntry<bool> ShowReturnToAirportButton;
        public readonly ConfigEntry<bool> ShowBoardFlightButton;
        public readonly ConfigEntry<bool> MoveRebindControlsToSettings;

        // Timing knobs, tuned blindly for now, exposed so we can iterate from
        // in-game reports without recompiling. Times are in seconds
        public readonly ConfigEntry<float> SettleAfterAirport;
        public readonly ConfigEntry<float> SettleAfterLevel;
        public readonly ConfigEntry<float> StepTimeout;
        public readonly ConfigEntry<float> CoopAirportSettle;

        public PluginConfig(ConfigFile cfg)
        {
            // Plain KeyCode, not KeyboardShortcut: PEAKLib.ModConfig (the in-game mod
            // settings menu, if installed) only recognizes a handful of concrete
            // ConfigEntry value types (bool/float/double/int/string/KeyCode/enum) and
            // renders a real "click, then press a key" rebind widget specifically for
            // KeyCode. KeyboardShortcut isn't one of the recognized types, so it fell
            // through to ModConfig's "unhandled setting type" case entirely (not shown
            // there at all, only editable by hand in the config file), which is the
            // exact problem this setting exists to avoid. The trade-off is losing
            // modifier-key combos (e.g. Ctrl+F7); ModConfig's own rebind capture only
            // ever assigns a single KeyCode anyway, so that ceiling already existed
            // for anyone using it to rebind
            ResumeKey = cfg.Bind("General", "resume-key", KeyCode.F7,
                "Opens the save picker (a menu of your checkpoints for the current solo/co-op category). "
                + "Use the arrow keys to choose, then press this key again (or Enter) to load. The newest "
                + "save is preselected, so pressing it twice loads your latest checkpoint. Delete removes "
                + "the highlighted save; Escape closes the menu.");

            ResumeKeyAlsoConfirmsLoad = cfg.Bind("General", "resume-key-also-confirms-load", true,
                "If enabled (default), pressing the resume key again while the save picker is open loads the "
                + "highlighted save (so pressing it twice loads the latest checkpoint). If disabled, only Enter "
                + "confirms a load while the picker is open, pressing the resume key does nothing, useful if "
                + "you keep accidentally loading a save while trying to close the picker with it.");

            // Renamed (not just re-described): Configuration Manager / PEAKLib.ModConfig
            // both display the raw key string as the row header, the description is
            // only a hover tooltip, easy to miss. Brackets/spaces in the KEY itself
            // are a bad idea though, a "[DEPRECATED] " key prefix silently broke the
            // whole plugin (PluginConfig's constructor threw, so Awake() never
            // finished, no logging, no F7, no pause menu buttons, nothing), so the
            // key stays a plain hyphenated identifier and only carries a harmless
            // "_DEPRECATED" suffix (underscore, not another hyphen, so it visibly
            // reads as a separate marker rather than part of the setting's own name).
            // Old "requireDoublePress"/"doublePressWindow" (and, as of the hyphenation
            // pass, the un-hyphenated versions of every key below too) entries in
            // existing config files are simply left orphaned (harmless)
            RequireDoublePress = cfg.Bind("General", "require-double-press_DEPRECATED", true,
                "[DEPRECATED] No longer used: the F7 save picker now provides the confirmation step "
                + "(open, then press again to load). Kept only so old config files don't error.");

            DoublePressWindow = cfg.Bind("General", "double-press-window_DEPRECATED", 5f,
                "[DEPRECATED] No longer used (superseded by the F7 save picker). Kept for config compatibility.");

            AllowMidGame = cfg.Bind("General", "allow-mid-game", true,
                "If enabled, the resume key also works while you are alive in a level (returns to the Airport, "
                + "starts a fresh run and loads the save). If disabled, it only works after death / at the Airport.");

            PanelOpacity = cfg.Bind("General", "panel-opacity", 1f,
                new ConfigDescription(
                    "Opacity of the F7 save picker's main background panel (0 = fully see-through, 1 = fully "
                    + "opaque). Lower this if you want to be able to see what's behind the menu while it's open.",
                    new AcceptableValueRange<float>(0f, 1f)));

            EnableDebugLogging = cfg.Bind("Debug", "enable-debug-logging", true,
                "Verbose logging of every step of the resume sequence. Very useful while the mod is young, "
                + "please keep this on when reporting issues.");

            ShowRestartButton = cfg.Bind("Pause-Menu", "show-restart-button", true,
                "Show the \"Restart\" button in the pause menu while mid-run (host only). Instantly returns "
                + "everyone to the Airport and starts a fresh run of the same difficulty, no checkpoint is loaded.");

            ShowReturnToAirportButton = cfg.Bind("Pause-Menu", "show-return-to-airport-button", true,
                "Show the \"Return to Airport\" button in the pause menu while mid-run (host only). Sends "
                + "everyone back to the Airport without starting a new run.");

            ShowBoardFlightButton = cfg.Bind("Pause-Menu", "show-board-flight-button", true,
                "Show the \"Board Flight\" button in the pause menu while at the Airport (any player). Opens "
                + "the gate-kiosk UI directly, skipping the walk over to it.");

            MoveRebindControlsToSettings = cfg.Bind("Pause-Menu", "move-rebind-controls-to-settings", false,
                "If enabled, moves the vanilla \"Rebind Controls\" button out of the pause menu's main page and "
                + "into the Settings page instead (placed just below its Back button), freeing up one row in the "
                + "main pause menu. Useful in coop with several pause-menu buttons active (this mod's own plus "
                + "other mods'), since only 9 fit on screen at once and a button you rarely use otherwise pushes "
                + "one you actually need off the bottom. Disabled by default since it relocates a vanilla button.");

            SettleAfterAirport = cfg.Bind("Timing", "settle-after-airport", 0.75f,
                "Seconds to wait after the Airport scene loads before starting the new run (advanced).");

            SettleAfterLevel = cfg.Bind("Timing", "settle-after-level", 1.5f,
                "Seconds to wait after the level scene loads (and the local character exists) before triggering "
                + "the checkpoint load (advanced).");

            StepTimeout = cfg.Bind("Timing", "step-timeout", 30f,
                "Max seconds to wait for each stage (Airport load, kiosk, level load, character spawn) before "
                + "aborting the resume sequence (advanced).");

            CoopAirportSettle = cfg.Bind("Timing", "coop-airport-settle", 2f,
                "COOP ONLY: extra seconds to wait at the Airport before starting the fresh run, so other "
                + "players have finished loading the Airport and will receive the run-start (advanced). "
                + "Raise this if a client occasionally gets left behind on a slow connection.");
        }
    }
}
