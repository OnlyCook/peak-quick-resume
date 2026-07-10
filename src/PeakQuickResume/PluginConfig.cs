using BepInEx.Configuration;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>All user-facing configuration for PEAK Quick Resume</summary>
    public class PluginConfig
    {
        public readonly ConfigEntry<KeyCode> ResumeKey;
        public readonly ConfigEntry<bool> ResumeKeyAlsoConfirmsLoad;
        public readonly ConfigEntry<KeyCode> HelpKey;
        public readonly ConfigEntry<KeyCode> StarKey;
        public readonly ConfigEntry<bool> AllowMidGame;
        public readonly ConfigEntry<float> PanelOpacity;

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

        // Phase 8 M3: our own copies of the checkpoint mod's teleport-sequence
        // config entries (same names/defaults/meaning as configAdvancedTeleportFramesToWait,
        // configAdvancedJumpLogicWaitTime, configCampfireReset, configDaytime - decompile
        // lines 1081-1116), used by OwnTeleportSequence.cs instead of reflecting into the
        // checkpoint mod's instance
        public readonly ConfigEntry<int> OwnTeleportFramesToWait;
        public readonly ConfigEntry<float> OwnJumpLogicWaitTime;
        public readonly ConfigEntry<bool> OwnCampfireReset;
        public readonly ConfigEntry<bool> OwnDaytime;

        // Phase 8 M4: our own copies of configInventory/configItemStats (decompile
        // 1082-1083), used by OwnInventoryRestore.cs instead of reflecting into the
        // checkpoint mod's instance
        public readonly ConfigEntry<bool> OwnInventory;
        public readonly ConfigEntry<bool> OwnItemStats;

        // Phase 8 M5: our own copy of configAfflictions (decompile line 1081)
        public readonly ConfigEntry<bool> OwnAfflictions;

        // Phase 8 M1: our own PhotonView/RPC channel (OwnNetwork.cs), replacing the
        // checkpoint mod's configAdvancedEnableClientReadyStatusCheck (same default,
        // same meaning) once we stop reflecting into its instance for this
        public readonly ConfigEntry<bool> OwnEnableClientReadyStatusCheck;

        // Phase 6: helps recover from the checkpoint mod's own intermittent teleport
        // bug (up/down warp-loop glitching, occasionally falling through the world).
        // We never touch its teleport logic, only detect + soften the aftermath
        public readonly ConfigEntry<bool> EnableTeleportWatchdog;
        public readonly ConfigEntry<float> WatchdogWindowSeconds;
        public readonly ConfigEntry<float> FallDistanceThreshold;
        public readonly ConfigEntry<int> GlitchOscillationCount;
        public readonly ConfigEntry<float> NeverTeleportedDistanceThreshold;

        // Phase 6 steps 4-5: auto-fixes that only ever act on a teleport the watchdog
        // above already flagged as bad, never unconditionally on every teleport
        public readonly ConfigEntry<bool> EnableFallDamageRevert;
        public readonly ConfigEntry<float> DamageRevertDelaySeconds;
        public readonly ConfigEntry<bool> EnablePositionRecovery;
        public readonly ConfigEntry<float> PositionRecoveryDelaySeconds;
        public readonly ConfigEntry<float> PositionRecoveryDistanceThreshold;
        public readonly ConfigEntry<bool> EnableWarpSuppression;
        public readonly ConfigEntry<float> WarpSuppressionExtraSeconds;

        public readonly ConfigEntry<bool> EnableDebugLogging;

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

            // Same plain-KeyCode reasoning as resume-key above (ModConfig only renders a
            // rebind widget for KeyCode, not KeyboardShortcut). This isn't just OUR key:
            // it's also pushed onto PEAK Checkpoint Save's own configTutorialKey (see
            // CheckpointInterop.TrySetTutorialKey / Plugin.Awake), which is otherwise
            // stuck as a KeyboardShortcut with no ModConfig rebind widget of its own.
            // Overriding it keeps the checkpoint mod's own F1 detection (which
            // HelpScreen/TutorialPatch ride on) and its footer prompt in sync with
            // whatever key is actually configured here
            HelpKey = cfg.Bind("General", "help-key", KeyCode.F1,
                "Opens the help screen (Quick Resume controls + the teleport-bug workaround). Also overrides "
                + "PEAK Checkpoint Save's own tutorial/help key to match, so its own F1 detection and footer "
                + "prompt stay in sync with whatever you set here.");

            // Default of B (not the more obvious S): while the F7 picker is open, key
            // input still reaches the character underneath it (it's an overlay, not a
            // real pause), so WASD-adjacent keys double as movement input - some players
            // even rely on that bleed-through deliberately. B has no vanilla listener
            StarKey = cfg.Bind("General", "star-key", KeyCode.B,
                "While the F7 save picker is open, stars/unstars the highlighted save. Starred saves are pinned "
                + "to the top of the list (newest first) and can't be deleted until unstarred again.");

            AllowMidGame = cfg.Bind("General", "allow-mid-game", true,
                "If enabled, the resume key also works while you are alive in a level (returns to the Airport, "
                + "starts a fresh run and loads the save). If disabled, it only works after death / at the Airport.");

            PanelOpacity = cfg.Bind("General", "panel-opacity", 1f,
                new ConfigDescription(
                    "Opacity of the F7 save picker's main background panel (0 = fully see-through, 1 = fully "
                    + "opaque). Lower this if you want to be able to see what's behind the menu while it's open.",
                    new AcceptableValueRange<float>(0f, 1f)));

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

            OwnTeleportFramesToWait = cfg.Bind("Teleport", "teleport-frames-to-wait", 30,
                "Frames to wait between teleport-correction tries in our own restore path (advanced).");

            OwnJumpLogicWaitTime = cfg.Bind("Teleport", "jump-logic-wait-time", 1f,
                "Seconds to wait between steps of our own restore path's teleport sequence (advanced).");

            OwnCampfireReset = cfg.Bind("Teleport", "campfire-reset", true,
                "If enabled, the campfire resets after loading more than once in the current run.");

            OwnDaytime = cfg.Bind("Teleport", "daytime", true,
                "If enabled, restores the saved in-game time of day.");

            OwnInventory = cfg.Bind("Teleport", "inventory", true,
                "If enabled, restores saved inventory and backpack items.");

            OwnItemStats = cfg.Bind("Teleport", "item-stats", true,
                "If enabled, restores saved item stats (cooking amount, fuel, rope length...).");

            OwnAfflictions = cfg.Bind("Teleport", "afflictions", true,
                "If enabled, restores your saved afflictions (hunger, poison, cold, sleep, skeleton...).");

            OwnEnableClientReadyStatusCheck = cfg.Bind("Network", "enable-client-ready-status-check", true,
                "COOP ONLY: if enabled, our own save/load restore waits until every connected client has "
                + "reported itself ready (in a Level scene) before proceeding.");

            EnableTeleportWatchdog = cfg.Bind("Teleport-Mitigation", "enable-teleport-watchdog", true,
                "Watches every checkpoint-mod teleport (its own F6 load, or ours) for the known intermittent "
                + "upstream bug: warp-loop glitching or falling through the world. When detected, shows an "
                + "on-screen hint pointing at the F1 help screen. Disable to turn off all Phase 6 detection.");

            WatchdogWindowSeconds = cfg.Bind("Teleport-Mitigation", "watchdog-window-seconds", 30f,
                "How long after a teleport to keep watching for the bad-teleport symptoms below (advanced).");

            FallDistanceThreshold = cfg.Bind("Teleport-Mitigation", "fall-distance-threshold", 150f,
                "How far below the actual teleport target (in meters) counts as \"falling through the world\". "
                + "Measured against the fixed teleport target, not a rolling peak, since the checkpoint mod's "
                + "own re-teleport corrections would otherwise keep resetting a rolling peak upward (advanced).");

            GlitchOscillationCount = cfg.Bind("Teleport-Mitigation", "glitch-oscillation-count", 4,
                "How many times the checkpoint mod re-warps you within a few seconds AFTER a load already "
                + "reported itself done counts as the up/down warp-loop glitch (advanced).");

            NeverTeleportedDistanceThreshold = cfg.Bind("Teleport-Mitigation", "never-teleported-distance-threshold", 200f,
                "Checked once right after a load finishes: if you're still this many meters (or more) from the "
                + "save's teleport target, you were never actually moved there. The nearest campfire to spawn is "
                + "~500m out and you can't light one (which is what creates a save) unless everyone is within 30m "
                + "of it, so this threshold has a wide safety buffer under a real miss (advanced).");

            EnableWarpSuppression = cfg.Bind("Teleport-Mitigation", "enable-warp-suppression", true,
                "Once the checkpoint mod reports the load done (\"Save game loaded!\"), cancels every further "
                + "WarpPlayerRPC it sends you for the rest of watchdog-window-seconds. Root-caused from real "
                + "session logs: the checkpoint mod's own TeleportClientsToHost keeps re-warping a client whenever "
                + "ITS view of your position looks too far off, but checks so infrequently (teleportFramesToWait "
                + "frames apart) that ordinary gravity pulls you back out of tolerance between checks nearly every "
                + "time - so it can loop 30+ times fighting its own retry cadence instead of converging, each snap "
                + "briefly hoisting you back into the air and re-accumulating fall damage on the way back down. "
                + "Cancelling everything after the load already reported itself done leaves you wherever the "
                + "first, legitimate warp already put you; position-recovery (below) is the fallback if that "
                + "wasn't actually close enough.");

            WarpSuppressionExtraSeconds = cfg.Bind("Teleport-Mitigation", "warp-suppression-extra-seconds", 2f,
                "Extra seconds added on top of watchdog-window-seconds before warp suppression above lifts. The "
                + "checkpoint mod's own retry loop times out on its own ~30s clock, started at a slightly "
                + "different moment than ours - this buffer avoids a straggler correction sneaking through right "
                + "at the boundary right as suppression lifts (advanced).");

            EnableFallDamageRevert = cfg.Bind("Teleport-Mitigation", "enable-fall-damage-revert", true,
                "When the watchdog flags a bad teleport, snapshots your Injury status and, after "
                + "damage-revert-delay-seconds, refunds any Injury gained since (once). Only ever engages after a "
                + "teleport was already flagged as broken, never on ordinary damage. Note: any OTHER damage taken "
                + "in that same short window (e.g. a mob hit) gets refunded too, since only the net change is "
                + "visible - an accepted trade-off given how rarely a fight breaks out right after loading in.");

            DamageRevertDelaySeconds = cfg.Bind("Teleport-Mitigation", "damage-revert-delay-seconds", 20f,
                "Seconds to wait after a flagged bad teleport before comparing Injury status and refunding any "
                + "increase (advanced).");

            EnablePositionRecovery = cfg.Bind("Teleport-Mitigation", "enable-position-recovery", true,
                "When the watchdog flags a bad teleport, checks again after position-recovery-delay-seconds and, "
                + "if you're still more than position-recovery-distance-threshold meters from where the save "
                + "actually intended to put you, forces you there directly - cuts a warp-loop glitch short instead "
                + "of waiting for the checkpoint mod's own correction loop to eventually sort itself out.");

            PositionRecoveryDelaySeconds = cfg.Bind("Teleport-Mitigation", "position-recovery-delay-seconds", 5f,
                "Seconds after a flagged bad teleport before checking whether a forced position recovery is "
                + "needed (advanced).");

            PositionRecoveryDistanceThreshold = cfg.Bind("Teleport-Mitigation", "position-recovery-distance-threshold", 5f,
                "How far (in meters) from the intended target still counts as \"not settled\" when the position "
                + "recovery check above runs (advanced).");

            EnableDebugLogging = cfg.Bind("Debug", "enable-debug-logging", true,
                "Verbose logging of every step of the resume sequence. Very useful while the mod is young, "
                + "please keep this on when reporting issues.");
        }
    }
}
