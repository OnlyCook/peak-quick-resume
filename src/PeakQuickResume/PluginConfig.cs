using BepInEx.Configuration;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>All user-facing configuration for PEAK Quick Resume</summary>
    public class PluginConfig
    {
        public readonly ConfigEntry<KeyCode> ResumeKey;
        public readonly ConfigEntry<bool> ResumeKeyAlsoConfirmsLoad;
        public readonly ConfigEntry<bool> AllowMidGame;
        public readonly ConfigEntry<float> PanelOpacity;
        public readonly ConfigEntry<bool> EnableDebugLogging;

        // Miscellaneous QoL: pause menu buttons (mechanics 3 & 4). Each independently
        // hides its button from the pause menu entirely when disabled
        public readonly ConfigEntry<bool> ShowRestartButton;
        public readonly ConfigEntry<bool> ShowReturnToAirportButton;
        public readonly ConfigEntry<bool> ShowBoardFlightButton;
        public readonly ConfigEntry<bool> MoveRebindControlsToSettings;

        // Phase 7: a big, clearly-labeled toggle button on the boarding-pass screen,
        // standing in for the checkpoint mod's own tiny, unlabeled "use saved island /
        // new island" checkbox (easy to never notice it's even clickable)
        public readonly ConfigEntry<bool> ShowIslandToggleButton;
        public readonly ConfigEntry<float> IslandToggleOffsetX;
        public readonly ConfigEntry<float> IslandToggleOffsetY;

        // Timing knobs, tuned blindly for now, exposed so we can iterate from
        // in-game reports without recompiling. Times are in seconds
        public readonly ConfigEntry<float> SettleAfterAirport;
        public readonly ConfigEntry<float> SettleAfterLevel;
        public readonly ConfigEntry<float> StepTimeout;
        public readonly ConfigEntry<float> CoopAirportSettle;

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

        // Phase 6 step 2: temporary teleport-config override for the next load only
        // (native F6 or ours). Every number here is exposed as its own setting
        // specifically so it's easy to change without touching code
        //
        // Extensive maintainer testing (session 11, both directions of host/client,
        // every campfire on 3 islands, mixed inventories) found teleportJumpLogic=1
        // avoids nearly every case of the checkpoint mod's intermittent teleport bug in
        // COOP specifically (it unfogs/loads every intervening island's segment on the
        // way to the target, rather than jumping straight there). So coop's plain,
        // no-modifier load now defaults to OptimizedCoopJumpLogic (see below) instead
        // of the user's own base teleportJumpLogic - the exact opposite of the old
        // "0 is safest, override only if asked" stance, now that testing backs a
        // specific better default. Solo is never affected (no known solo-only benefit
        // was found, and there's no coop desync mechanism for it to fix there).
        // Holding Shift now means "use my own base config anyway" (no override applied
        // at all) rather than a distinct configured value - the escape hatch for
        // anyone who wants the old behavior for one load without changing settings
        public readonly ConfigEntry<bool> EnableOptimizedCoopLoading;
        public readonly ConfigEntry<int> OptimizedCoopJumpLogic;
        public readonly ConfigEntry<int> AltTeleportJumpLogic;
        public readonly ConfigEntry<int> OverrideFramesToWait;
        public readonly ConfigEntry<float> OverrideJumpLogicWaitTime;
        public readonly ConfigEntry<float> OverrideRestoreDelaySeconds;

        // Internal bookkeeping, not meant to be hand-edited: persists the pre-override
        // teleport config to disk for the ~35s window an override is active, so a
        // crash/quit in that window doesn't leave it stuck. See
        // TeleportConfigOverride.Apply/RestoreAfterDelay/ReconcileAfterRestart.
        public readonly ConfigEntry<bool> PendingOverrideResetOwed;
        public readonly ConfigEntry<int> PendingOverrideOriginalJumpLogic;
        public readonly ConfigEntry<int> PendingOverrideOriginalFramesToWait;
        public readonly ConfigEntry<float> PendingOverrideOriginalWaitTime;

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

            ShowIslandToggleButton = cfg.Bind("Boardingpass", "show-island-toggle-button", true,
                "Show a big, clearly-labeled toggle button in the bottom-right of the boarding pass screen for "
                + "switching between loading your saved island (with its biomes) and a new one, mirroring the "
                + "checkpoint mod's own tiny \"use saved island / new island\" checkbox next to its boarding-pass "
                + "text (top-left, easy to miss). Both control the exact same setting; this just makes it "
                + "obvious and easy to click.");

            // Explicit AcceptableValueRange on both (not just a bare default) so mod-config
            // UIs (e.g. PEAKLib.ModConfig) render a slider that actually SPANS negative
            // values instead of inferring some positive-only range from the default alone
            // and silently clamping island-toggle-offset-y's negative default back to 0
            IslandToggleOffsetX = cfg.Bind("Boardingpass", "island-toggle-offset-x", 400f,
                new ConfigDescription(
                    "Horizontal offset (pixels) of the island-toggle button from the checkpoint mod's own "
                    + "boarding-pass message anchor point (the same coordinate system its own message text and "
                    + "tiny checkbox use, so this scales/positions consistently with them at any resolution). "
                    + "Positive = further right. Raise this if the button overlaps the message text (e.g. a long "
                    + "campfire/level name makes a line wider than usual).",
                    new AcceptableValueRange<float>(-200f, 900f)));

            IslandToggleOffsetY = cfg.Bind("Boardingpass", "island-toggle-offset-y", -20f,
                new ConfigDescription(
                    "Vertical offset (pixels), added on top of the checkpoint mod's own checkbox height (so by "
                    + "default the button lines up with the top line of the message). Positive = further up, "
                    + "negative = further down.",
                    new AcceptableValueRange<float>(-300f, 300f)));

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

            EnableOptimizedCoopLoading = cfg.Bind("Teleport-Override", "enable-optimized-coop-loading", true,
                "In COOP, a plain load (native F6, or this mod's F7/Enter with no modifier held) uses "
                + "optimized-coop-jump-logic instead of your own base teleportJumpLogic, ONLY for that one load - "
                + "extensive maintainer testing found this avoids nearly every case of the checkpoint mod's "
                + "intermittent teleport bug. Solo is never affected by this setting (always uses your own base "
                + "value). Hold Shift while loading to use your own base value anyway for just that load, even "
                + "with this enabled. If you disable this, a plain load goes back to your own base value in coop "
                + "too, same as solo - set your own teleportJumpLogic to 1 directly in PEAK Checkpoint Save's own "
                + "config if you still want the optimized value as your default everywhere.");

            OptimizedCoopJumpLogic = cfg.Bind("Teleport-Override", "optimized-coop-jump-logic", 1,
                "Which of the checkpoint mod's own teleportJumpLogic values (0 = SetSegmentOnSpawn, "
                + "1 = JumpToSegment, 2 = GoToSegment) enable-optimized-coop-loading above uses (advanced - the "
                + "default of 1 is the one extensive testing actually validated, only change this if you're doing "
                + "your own testing).");

            AltTeleportJumpLogic = cfg.Bind("Teleport-Override", "alt-teleport-jump-logic", 2,
                "Which of the checkpoint mod's own teleportJumpLogic values to use, ONLY for the next load, when "
                + "holding Alt while loading (native F6, or this mod's F7/Enter) - in both solo and coop. Restored "
                + "to whatever you actually have configured afterward. Host-only, has no effect for non-hosts "
                + "(this setting only ever matters on whichever machine drives the actual teleport).");

            OverrideFramesToWait = cfg.Bind("Teleport-Override", "override-frames-to-wait", 40,
                "While a Shift/Alt override is active, the checkpoint mod's own teleportFramesToWait is raised to "
                + "at least this value (never lowered below whatever you already had configured) (advanced).");

            OverrideJumpLogicWaitTime = cfg.Bind("Teleport-Override", "override-jump-logic-wait-time", 2f,
                "Same as override-frames-to-wait above, but for the checkpoint mod's jumpLogicWaitTime "
                + "(advanced).");

            OverrideRestoreDelaySeconds = cfg.Bind("Teleport-Override", "override-restore-delay-seconds", 35f,
                "Seconds after a Shift/Alt-overridden load before the checkpoint mod's teleport settings are "
                + "restored to whatever you actually have configured. Comfortably longer than the ~30s window "
                + "its own teleport corrections can keep running in (advanced).");

            PendingOverrideResetOwed = cfg.Bind("Teleport-Override-Recovery", "pending-reset-owed", false,
                "Internal bookkeeping, do not edit by hand. True while a Shift/Alt override's restore is still "
                + "pending; used to detect and fix a teleport config left stuck if the game closed before it "
                + "could restore on its own.");

            PendingOverrideOriginalJumpLogic = cfg.Bind("Teleport-Override-Recovery", "pending-original-jump-logic", 0,
                "Internal bookkeeping, do not edit by hand. The teleportJumpLogic value to restore if "
                + "pending-reset-owed is stuck true.");

            PendingOverrideOriginalFramesToWait = cfg.Bind("Teleport-Override-Recovery", "pending-original-frames-to-wait", 0,
                "Internal bookkeeping, do not edit by hand. Same as pending-original-jump-logic, for "
                + "teleportFramesToWait.");

            PendingOverrideOriginalWaitTime = cfg.Bind("Teleport-Override-Recovery", "pending-original-wait-time", 0f,
                "Internal bookkeeping, do not edit by hand. Same as pending-original-jump-logic, for "
                + "jumpLogicWaitTime.");
        }
    }
}
