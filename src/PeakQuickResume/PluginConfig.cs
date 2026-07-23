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
        public readonly ConfigEntry<bool> MinimalPickerUi;

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

        // Solo-only speed-up: collapse the per-step waits between the map/campfire warp
        // and the final precise teleport (see OwnTeleportSequence). Off = the original,
        // slower, more conservative cadence
        public readonly ConfigEntry<bool> OwnFastSoloTeleport;

        // Coop client-warp anti-spam (see OwnTeleportSequence.TeleportClientsToHost): the
        // host re-warps a client off its own network-lagged view of that client's position,
        // so on a slow connection it can fire dozens of redundant safety warps before the
        // client's teleport ever round-trips back. These bound and pace those re-sends
        public readonly ConfigEntry<int> OwnMaxClientWarpResends;
        public readonly ConfigEntry<float> OwnClientWarpResendGraceSeconds;

        // Phase 8 M4: our own copies of configInventory/configItemStats (decompile
        // 1082-1083), used by OwnInventoryRestore.cs instead of reflecting into the
        // checkpoint mod's instance
        public readonly ConfigEntry<bool> RestoreInventory;
        public readonly ConfigEntry<bool> RestoreItemStats;

        // Phase 8 M5: our own copy of configAfflictions (decompile line 1081)
        public readonly ConfigEntry<bool> RestoreAfflictions;

        // v2.0.0: per-mechanic restore toggles for everything else OwnTeleportSequence
        // restores around/alongside the campfire - each independently gates just its
        // own mechanic's RESTORE step (capture always still runs, matching every
        // existing toggle above - see PluginConfig ctor for why)
        public readonly ConfigEntry<bool> RestoreGroundedItems;
        public readonly ConfigEntry<bool> RestoreGroundedBackpacks;
        public readonly ConfigEntry<bool> RestoreDeployables;
        public readonly ConfigEntry<bool> RestoreLuggage;
        public readonly ConfigEntry<bool> RestoreAncientStatue;
        public readonly ConfigEntry<bool> RestoreDay;
        public readonly ConfigEntry<bool> RestoreDaytime;
        public readonly ConfigEntry<bool> RestorePlayerEntities;
        public readonly ConfigEntry<bool> RestorePlayerTempSlot;
        public readonly ConfigEntry<bool> RestoreAchievements;

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

        // Native-feeling wake-up + loading-screen crossfade around the teleport step of
        // our own restore path (OwnWakeUpEffect.cs / OwnLoadingScreen.cs), replacing the
        // checkpoint mod's abrupt instant on/off overlay
        public readonly ConfigEntry<bool> OwnWakeUpAnimationEnabled;
        public readonly ConfigEntry<float> OwnLoadingScreenFadeInDelay;
        public readonly ConfigEntry<float> OwnLoadingScreenFadeOutDelay;
        public readonly ConfigEntry<float> OwnWakeUpStandTime;
        public readonly ConfigEntry<float> OwnLoadingScreenFadeTime;

        public readonly ConfigEntry<bool> EnableDebugLogging;
        public readonly ConfigEntry<bool> DebugDisableLoadingScreen;

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
            // rebind widget for KeyCode, not KeyboardShortcut)
            HelpKey = cfg.Bind("General", "help-key", KeyCode.F4,
                "Opens the help screen (Quick Resume controls).");

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

            MinimalPickerUi = cfg.Bind("General", "minimal-picker-ui", false,
                "If enabled, the F7 save picker and F4 help screen use a plain, minimal panel: no procedural "
                + "background grain texture, no hand-torn jagged edges, and no edge animation. Disabled by default.");

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
                new ConfigDescription(
                    "Seconds to wait after the Airport scene loads before starting the new run (advanced).",
                    new AcceptableValueRange<float>(0f, 10f)));

            SettleAfterLevel = cfg.Bind("Timing", "settle-after-level", 1.5f,
                new ConfigDescription(
                    "Seconds to wait after the level scene loads (and the local character exists) before triggering "
                    + "the checkpoint load (advanced).",
                    new AcceptableValueRange<float>(0f, 10f)));

            StepTimeout = cfg.Bind("Timing", "step-timeout", 30f,
                new ConfigDescription(
                    "Max seconds to wait for each stage (Airport load, kiosk, level load, character spawn) before "
                    + "aborting the resume sequence (advanced).",
                    new AcceptableValueRange<float>(1f, 120f)));

            CoopAirportSettle = cfg.Bind("Timing", "coop-airport-settle", 2f,
                new ConfigDescription(
                    "COOP ONLY: extra seconds to wait at the Airport before starting the fresh run, so other "
                    + "players have finished loading the Airport and will receive the run-start (advanced). "
                    + "Raise this if a client occasionally gets left behind on a slow connection.",
                    new AcceptableValueRange<float>(0f, 15f)));

            OwnTeleportFramesToWait = cfg.Bind("Teleport", "teleport-frames-to-wait", 30,
                new ConfigDescription(
                    "Frames to wait between teleport-correction tries in our own restore path (advanced).",
                    new AcceptableValueRange<int>(0, 300)));

            OwnJumpLogicWaitTime = cfg.Bind("Teleport", "jump-logic-wait-time", 1f,
                new ConfigDescription(
                    "Seconds to wait between steps of our own restore path's teleport sequence (advanced).",
                    new AcceptableValueRange<float>(0f, 10f)));

            OwnFastSoloTeleport = cfg.Bind("Teleport", "fast-solo-teleport", true,
                "SOLO ONLY: once the map has loaded and you've been warped to the campfire, skip the extra "
                + "per-step waits before the final precise teleport. Those waits only matter in co-op, where "
                + "they give other players time to catch the segment/biome activation and warps over the network "
                + "before the host teleports - in solo there is nobody to keep in sync, so they are just dead time "
                + "you watch after the map has already visibly loaded. Cuts a few seconds off a solo resume. "
                + "Disable to restore the original slower, more conservative cadence. Has no effect in co-op.");

            OwnMaxClientWarpResends = cfg.Bind("Teleport", "max-client-warp-resends", 10,
                new ConfigDescription(
                    "COOP ONLY: the maximum number of EXTRA 'safety' teleport RPCs the host will send a single "
                    + "client after the first one, if the host still doesn't see that client near the target. The "
                    + "host judges this from its own network-lagged view of the client, so a client on a slow "
                    + "connection (whose teleport hasn't reported back yet) could otherwise be spammed with warps it "
                    + "has already acted on. Once this cap is hit the host stops re-warping that client and leaves the "
                    + "rest to the client's own teleport watchdog / position recovery. Set to 0 to send only the "
                    + "initial warp and never re-send (advanced).",
                    new AcceptableValueRange<int>(0, 50)));

            OwnClientWarpResendGraceSeconds = cfg.Bind("Teleport", "client-warp-resend-grace-seconds", 1.5f,
                new ConfigDescription(
                    "COOP ONLY: minimum seconds the host waits after warping a client before it may re-send that "
                    + "client another safety warp. Gives the client's teleport time to actually take effect AND "
                    + "propagate back over the network before the host decides it 'didn't work' and fires again - the "
                    + "direct fix for a slow-connection client being re-warped faster than a round trip can complete "
                    + "(advanced).",
                    new AcceptableValueRange<float>(0f, 10f)));

            RestoreInventory = cfg.Bind("Teleport", "restore-inventory", true,
                "If enabled, restores saved inventory and backpack items.");

            RestoreItemStats = cfg.Bind("Teleport", "restore-item-stats", true,
                "If enabled, restores saved item stats (cooking amount, fuel, rope length...).");

            RestoreAfflictions = cfg.Bind("Teleport", "restore-afflictions", true,
                "If enabled, restores your saved afflictions (hunger, poison, cold, sleep, skeleton...).");

            RestoreGroundedItems = cfg.Bind("Teleport", "restore-grounded-items", true,
                "If enabled, restores loose items (berries, coconuts, campfire food, and anything else "
                + "dropped or lying free) found within range of the loaded campfire.");

            RestoreGroundedBackpacks = cfg.Bind("Teleport", "restore-grounded-backpacks", true,
                "If enabled, restores backpacks (natural spawns or player drops) found lying on the ground "
                + "within range of the loaded campfire, contents included.");

            RestoreDeployables = cfg.Bind("Teleport", "restore-deployables", true,
                "If enabled, restores player-placed Portable Stoves and Scout Cannons found near the loaded campfire.");

            RestoreLuggage = cfg.Bind("Teleport", "restore-luggage", true,
                "If enabled, restores Luggage boxes near the loaded campfire (open/closed state and any items "
                + "already taken out of them).");

            RestoreAncientStatue = cfg.Bind("Teleport", "restore-ancient-statue", true,
                "If enabled, restores the Ancient Statue near the loaded campfire (broken/unbroken state and "
                + "any unclaimed item it dropped).");

            RestoreDay = cfg.Bind("Teleport", "restore-day", true,
                "If enabled, restores the saved in-game day count (the number shown alongside a new biome's "
                + "title card).");

            RestoreDaytime = cfg.Bind("Teleport", "restore-daytime", true,
                "If enabled, restores the saved in-game time of day.");

            RestorePlayerEntities = cfg.Bind("Teleport", "restore-player-entities", true,
                "If enabled, restores physical thorns (Cactus/Tumbleweed) and ticks (Bugfix) attached to your body.");

            RestorePlayerTempSlot = cfg.Bind("Teleport", "restore-player-temp-slot", true,
                "If enabled, restores the 4th item held in your hands (the temporary slot used when all 3 "
                + "regular item slots are already full).");

            RestoreAchievements = cfg.Bind("Teleport", "restore-achievements", true,
                "If enabled, restores this run's in-progress achievement/Steam-stat tracking (Plunderer, "
                + "Foraging, Mycology, First Aid, Clutch, Knot Tying, and the \"without ever X\" family).");

            OwnEnableClientReadyStatusCheck = cfg.Bind("Network", "enable-client-ready-status-check", true,
                "COOP ONLY: if enabled, our own save/load restore waits until every connected client has "
                + "reported itself ready (in a Level scene) before proceeding.");

            EnableTeleportWatchdog = cfg.Bind("Teleport-Mitigation", "enable-teleport-watchdog", true,
                "Watches every resume teleport for the known intermittent bad-teleport symptoms: warp-loop "
                + "glitching, falling through the world, or never being moved at all. When detected, shows an "
                + "on-screen hint pointing at the help screen (help-key) and (see the auto-fix settings below) "
                + "can refund the resulting fall damage and force you to the correct spot. Disable to turn off "
                + "all teleport-bug detection.");

            WatchdogWindowSeconds = cfg.Bind("Teleport-Mitigation", "watchdog-window-seconds", 30f,
                new ConfigDescription(
                    "How long after a teleport to keep watching for the bad-teleport symptoms below (advanced).",
                    new AcceptableValueRange<float>(1f, 120f)));

            FallDistanceThreshold = cfg.Bind("Teleport-Mitigation", "fall-distance-threshold", 150f,
                new ConfigDescription(
                    "How far below the actual teleport target (in meters) counts as \"falling through the world\". "
                    + "Measured against the fixed teleport target, not a rolling peak, since a re-teleport correction "
                    + "would otherwise keep resetting a rolling peak upward (advanced).",
                    new AcceptableValueRange<float>(10f, 500f)));

            GlitchOscillationCount = cfg.Bind("Teleport-Mitigation", "glitch-oscillation-count", 4,
                new ConfigDescription(
                    "How many times you get re-warped within a few seconds AFTER a load already reported itself "
                    + "done counts as the up/down warp-loop glitch (advanced).",
                    new AcceptableValueRange<int>(1, 20)));

            NeverTeleportedDistanceThreshold = cfg.Bind("Teleport-Mitigation", "never-teleported-distance-threshold", 200f,
                new ConfigDescription(
                    "Checked once right after a load finishes: if you're still this many meters (or more) from the "
                    + "save's teleport target, you were never actually moved there. The nearest campfire to spawn is "
                    + "~500m out and you can't light one (which is what creates a save) unless everyone is within 30m "
                    + "of it, so this threshold has a wide safety buffer under a real miss (advanced).",
                    new AcceptableValueRange<float>(10f, 1000f)));

            EnableFallDamageRevert = cfg.Bind("Teleport-Mitigation", "enable-fall-damage-revert", true,
                "When the watchdog flags a bad teleport, snapshots your Injury status and, after "
                + "damage-revert-delay-seconds, refunds any Injury gained since (once). Only ever engages after a "
                + "teleport was already flagged as broken, never on ordinary damage. Note: any OTHER damage taken "
                + "in that same short window (e.g. a mob hit) gets refunded too, since only the net change is "
                + "visible - an accepted trade-off given how rarely a fight breaks out right after loading in.");

            DamageRevertDelaySeconds = cfg.Bind("Teleport-Mitigation", "damage-revert-delay-seconds", 20f,
                new ConfigDescription(
                    "Seconds to wait after a flagged bad teleport before comparing Injury status and refunding any "
                    + "increase (advanced).",
                    new AcceptableValueRange<float>(0f, 120f)));

            EnablePositionRecovery = cfg.Bind("Teleport-Mitigation", "enable-position-recovery", true,
                "When the watchdog flags a bad teleport, checks again after position-recovery-delay-seconds and, "
                + "if you're still more than position-recovery-distance-threshold meters from where the save "
                + "actually intended to put you, forces you there directly - the last-resort backstop if the "
                + "resume teleport somehow didn't land you at the checkpoint.");

            PositionRecoveryDelaySeconds = cfg.Bind("Teleport-Mitigation", "position-recovery-delay-seconds", 5f,
                new ConfigDescription(
                    "Seconds after a flagged bad teleport before checking whether a forced position recovery is "
                    + "needed (advanced).",
                    new AcceptableValueRange<float>(0f, 60f)));

            PositionRecoveryDistanceThreshold = cfg.Bind("Teleport-Mitigation", "position-recovery-distance-threshold", 5f,
                new ConfigDescription(
                    "How far (in meters) from the intended target still counts as \"not settled\" when the position "
                    + "recovery check above runs (advanced).",
                    new AcceptableValueRange<float>(0f, 100f)));

            OwnWakeUpAnimationEnabled = cfg.Bind("Wake-Up", "wake-up-animation-enabled", true,
                "If enabled, the teleport step of our own restore path plays a native-feeling "
                + "\"waking up at your last checkpoint\" beat (reusing the game's own pass-out/revive "
                + "pose, client-side only - nobody else sees it) and crossfades into the game's real "
                + "\"LOADING...\" screen while the teleport happens, instead of an instant cut. "
                + "Disable to go back to the plain instant teleport.");

            OwnLoadingScreenFadeInDelay = cfg.Bind("Wake-Up", "loading-screen-fade-in-delay", 0.5f,
                new ConfigDescription(
                    "Seconds to wait before starting the crossfade into our own loading screen "
                    + "(advanced). Without this, our screen can start covering things up slightly "
                    + "before the game's own level-load screen has actually finished clearing, "
                    + "cutting it off a beat too early.",
                    new AcceptableValueRange<float>(0f, 5f)));

            OwnLoadingScreenFadeOutDelay = cfg.Bind("Wake-Up", "loading-screen-fade-out-delay", 0.4f,
                new ConfigDescription(
                    "Seconds to keep the loading screen fully opaque AFTER everything (items, "
                    + "backpacks, afflictions, world state) has finished restoring, before it starts "
                    + "fading out (advanced). A small pause here avoids the screen clearing to reveal "
                    + "the player completely static for a beat right before the stand-up recovery "
                    + "kicks in - raise it if you still catch that; lower it if the loading screen "
                    + "feels like it lingers too long after everything is actually ready.",
                    new AcceptableValueRange<float>(0f, 3f)));

            OwnWakeUpStandTime = cfg.Bind("Wake-Up", "wake-up-stand-time", 1.8f,
                new ConfigDescription(
                    "Seconds to let the native stand-up recovery play out (after the loading screen "
                    + "clears, revealing the player already lying at the new position) before the "
                    + "resume sequence considers itself fully done (advanced). The recovery itself "
                    + "ramps over roughly 1.5-2 seconds.",
                    new AcceptableValueRange<float>(0f, 10f)));

            OwnLoadingScreenFadeTime = cfg.Bind("Wake-Up", "loading-screen-fade-time", 0.4f,
                new ConfigDescription(
                    "Seconds for each crossfade into/out of the loading screen (advanced).",
                    new AcceptableValueRange<float>(0f, 5f)));

            EnableDebugLogging = cfg.Bind("Debug", "enable-debug-logging", true,
                "Verbose logging of every step of the resume sequence. Very useful while the mod is young, "
                + "please keep this on when reporting issues.");

            DebugDisableLoadingScreen = cfg.Bind("Debug", "disable-loading-screen", false,
                "If enabled, skips showing the custom loading screen during Quick Resume's wake-up "
                + "sequence - the wake-up animation itself, and every other Wake-Up setting above, "
                + "still apply exactly as configured. Useful for debugging without the screen "
                + "hiding what's happening underneath.");
        }
    }
}
