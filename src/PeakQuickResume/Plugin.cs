using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// PEAK Quick Resume, press one key to start a fresh run of your saved
    /// difficulty and immediately load your latest checkpoint, instead of doing it
    /// all by hand
    ///
    /// Fully self-contained: orchestrates the vanilla "start run" flow
    /// (<see cref="RunLauncher"/>) and drives its own independent save/load/teleport
    /// (<see cref="OwnLoadEntryPoints"/> / <see cref="OwnTeleportSequence"/> / etc.).
    /// The save file format is still compatible with dominik0207's "PEAK Checkpoint
    /// Save", which this mod was originally built around, but that mod is no longer
    /// required, referenced, or integrated with in any way
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }

        private PluginConfig _cfg;
        private ResumeOrchestrator _orchestrator;
        private RestartOrchestrator _restart;
        private SavePicker _picker;
        private HelpScreen _helpScreen;
        private TeleportWatchdog _watchdog;

        // Our own on-screen message overlay (see OwnMessageOverlay.cs)
        private OwnMessageOverlay _messageOverlay;

        // Our own PhotonView/RPC channel (see OwnNetwork.cs)
        private OwnNetwork _ownNetwork;

        // Our own PreStartSetSegment/LoadPlayerOffline/LoadPlayerCoop guard chain,
        // driving OwnTeleportSequence (see OwnLoadEntryPoints.cs)
        private OwnLoadEntryPoints _ownLoadEntryPoints;

        // BepInEx GUID of the old PEAK Checkpoint Save mod. We no longer depend on,
        // reference, or integrate with it, but if it's STILL installed alongside us both
        // mods run their own campfire-autosave + logging independently, so the player sees
        // duplicate save messages and log lines. Purely cosmetic (both write the same file,
        // no logic conflict) - we just detect it to warn the player, see Update / HelpScreen
        private const string CheckpointSaveGuid = "PEAK_Checkpoint_Save";
        private bool _dupWarningShown;

        // One-time (per session) game-update notice - see Update() and GameVersionCompat.
        // Independent of _dupWarningShown above: unrelated condition, own popup, must not
        // be skipped just because that one already fired this session (or vice versa)
        private bool _versionCheckDone;

        /// <summary>
        /// Whether PEAK Checkpoint Save is loaded alongside us. Queried lazily (NOT cached at
        /// Awake): with the soft dependency gone there's no load-order guarantee, and
        /// <c>Chainloader.PluginInfos</c> only lists plugins loaded so far - at our Awake the
        /// checkpoint mod may not be in it yet. Every caller here runs well after all plugins
        /// have finished loading (in a game scene / on opening the help screen), when the list
        /// is complete, so the lookup is reliable there
        /// </summary>
        internal bool CheckpointModInstalled =>
            BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(CheckpointSaveGuid);

        /// <summary>Display string for the configured resume key (e.g. "F7"), for UI text</summary>
        internal string ResumeKeyText => _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";

        private void Awake()
        {
            Instance = this;
            _cfg = new PluginConfig(Config);

            var harmony = new Harmony(PluginInfo.Guid);

            var go = new GameObject("PEAKQuickResume.Orchestrator");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _orchestrator = go.AddComponent<ResumeOrchestrator>();

            // Phase 8 M9: stand up our own message overlay first - several components
            // below need it immediately
            _messageOverlay = go.AddComponent<OwnMessageOverlay>();
            _messageOverlay.Init(Logger);

            // Detect the intermittent bad-teleport symptoms after a resume (see
            // TeleportWatchdog / ROADMAP.md Phase 6). Created before the Harmony patches
            // below since TeleportWatchdogPatch needs a reference to it
            _watchdog = go.AddComponent<TeleportWatchdog>();
            _watchdog.Init(Logger, _cfg, _messageOverlay);

            _restart = go.AddComponent<RestartOrchestrator>();
            _restart.Init(Logger, _cfg, _messageOverlay, _watchdog);

            _picker = go.AddComponent<SavePicker>();
            _picker.Init(Logger, _cfg);

            // The help screen (help-key), a small menu built from the same visual
            // primitives as the picker above
            _helpScreen = go.AddComponent<HelpScreen>();
            _helpScreen.Init(Logger, _cfg);

            // Our own PhotonView/RPC channel (separate GameObject, own ViewID)
            _ownNetwork = go.AddComponent<OwnNetwork>();
            _ownNetwork.Init(Logger, _cfg);

            // Native-feeling wake-up + loading-screen crossfade around the teleport step
            // (see OwnWakeUpEffect.cs / OwnLoadingScreen.cs); wired into OwnTeleportSequence below
            var ownWakeUpEffect = go.AddComponent<OwnWakeUpEffect>();
            ownWakeUpEffect.Init(Logger);

            var ownLoadingScreen = go.AddComponent<OwnLoadingScreen>();
            ownLoadingScreen.Init(Logger);

            // Phase 8 M3: our own literal port of CustomJumpToSegment/TeleportToPosition/
            // TeleportClientsToHost/ReviveDeadPlayers (see OwnTeleportSequence.cs)
            var ownTeleportSequence = go.AddComponent<OwnTeleportSequence>();

            // Phase 8 M2/M3: our own load-entry-point guard chain. As of M3, its solo path
            // IS wired live via ResumeOrchestrator below
            _ownLoadEntryPoints = go.AddComponent<OwnLoadEntryPoints>();
            _ownLoadEntryPoints.Init(Logger, _cfg, _ownNetwork, ownTeleportSequence);
            ownTeleportSequence.Init(Logger, _cfg, _ownLoadEntryPoints, ownWakeUpEffect, ownLoadingScreen);

            // Phase 8 M7/M9: now that _watchdog/_messageOverlay/_ownLoadEntryPoints all
            // exist, wire them onto the channel so its RPC handlers (RPC_Loadingscreen ->
            // TeleportWatchdog, RPC_SendMessage -> our own overlay, RPC_RequestSave/
            // RPC_RecentlyLitCampfire -> OwnLoadEntryPoints' cooldowns) can reach them -
            // see OwnNetwork.AttachDependencies. Also wires ownWakeUpEffect/ownLoadingScreen so
            // RPC_ClientPresentation can mirror the host's own presentation on this machine too
            _ownNetwork.AttachDependencies(_messageOverlay, _watchdog, _ownLoadEntryPoints, ownWakeUpEffect, ownLoadingScreen);

            // Now that _ownLoadEntryPoints exists, wire the orchestrator - it drives the
            // whole resume through our own load path, solo and coop (see ResumeOrchestrator.cs)
            _orchestrator.Init(Logger, _cfg, _messageOverlay, _ownLoadEntryPoints, _watchdog);

            // Phase 8 M3: our own copy of the checkpoint mod's fall/lava-damage
            // protection window, armed from OwnTeleportSequence (see OwnFallDamageProtection.cs)
            OwnFallDamageProtection.Apply(harmony, Logger);

            // Our own MapBaker.GetLevel prefix (forces the saved island - see MapBakerLevelOverridePatch.cs)
            MapBakerLevelOverridePatch.Apply(harmony, Logger);

            // Third-party mod compat, no-op if not installed (see TerrainRandomiserCompat.cs):
            // stops Snosz's TerrainRandomiser from re-randomizing the terrain on an F7 load
            TerrainRandomiserCompat.Apply(harmony, Logger);

            // Our own save-capture port, the canonical save writer (see OwnSaveCapture.cs)
            CampfireAutoSavePatch.Apply(harmony, _cfg, _ownLoadEntryPoints, _ownNetwork, Logger);

            // Vanilla CharacterItems/Campfire hooks only; reads item state via OwnItemStateIO
            BackpackSaveMitigation.Apply(harmony, Logger);

            // Pure observability for achievement-progress restore (see AchievementProgressIO) -
            // logs run-based counters/steam-stat changes as "[achievement-debug]" lines, no
            // gameplay effect. TODO: remove once the achievement-progress restore is confirmed
            // solid across a few real sessions
            AchievementDebugLogging.Apply(harmony, Logger);

            // Vanilla Character.WarpPlayerRPC patch, records the local player's teleport
            // target for the watchdog above (Character is a vanilla type). Our own
            // OwnTeleportSequence/OwnInventoryRestore arm the watchdog's load window
            // directly for every resume through our own path (solo AND coop)
            TeleportWatchdogPatch.Apply(harmony, Logger, _watchdog);

            // Miscellaneous QoL, no dependency on the checkpoint mod: injects Restart /
            // Return to Airport / Board Flight buttons into the vanilla pause menu
            PauseMenuPatch.Apply(harmony, _cfg, Logger);

            // Optional QoL, off by default: relocates the vanilla Rebind Controls button
            // out of the pause menu (see RebindControlsRelocationPatch for why)
            RebindControlsRelocationPatch.Apply(harmony, _cfg, Logger);

            // Stops Escape from bleeding through and opening the vanilla pause menu right
            // behind the F7 save picker closing (see PauseSuppressPatch for why)
            PauseSuppressPatch.Apply(harmony, Logger);

            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded. "
                + $"Resume key: {_cfg.ResumeKey.Value}.");
        }

        private void Update()
        {
            if (_cfg == null) return;

            // One-time heads-up when PEAK Checkpoint Save is still installed (see
            // CheckpointModInstalled's remarks for why this is checked here, in-game, and
            // not at Awake). Deferred until the player is actually in a game scene (Airport
            // or a level) so the overlay is on-screen and seen, rather than firing over the
            // title - and so Chainloader.PluginInfos is fully populated by now
            if (!_dupWarningShown && _messageOverlay != null
                && (RunLauncher.InAirport || RunLauncher.InLevel))
            {
                _dupWarningShown = true;
                if (CheckpointModInstalled)
                {
                    // Full detail goes to the log (persists) and the help screen (re-viewable);
                    // the one-time popup stays brief and just points at the help screen
                    Logger.LogWarning("PEAK Checkpoint Save is still installed. Quick Resume no longer needs it and "
                        + "runs fully on its own; both mods will save/log independently, so expect duplicate log "
                        + "messages and saves appearing. This is harmless (no logic conflict), but uninstall PEAK "
                        + "Checkpoint Save to remove the duplicates.");
                    _messageOverlay.Show(
                        MessagesLocalization.Get(MsgKey.CheckpointModStillInstalledShort, _cfg.HelpKey.Value.ToString()),
                        new Color(1f, 0.8f, 0.4f, 1f), 7f);
                }
            }

            // One-time (per version bump) game-update notice: only actually shown if at
            // least one of our own archived saves predates the currently running game
            // version (see ArchivedSave.IsStaleVersion / GameVersionCompat) - a save made
            // right after this exact launch isn't affected, so there's nothing to warn
            // about for a player who never saved on an older version. Deferred to
            // Airport/Level for the same reasons as the check above (overlay on-screen,
            // not over the title)
            if (!_versionCheckDone && _messageOverlay != null
                && (RunLauncher.InAirport || RunLauncher.InLevel))
            {
                _versionCheckDone = true;
                string current = GameVersionCompat.Current;
                if (_cfg.LastCheckedGameVersion.Value != current)
                {
                    bool anyStale = false;
                    foreach (ArchivedSave save in SaveArchive.List(offline: true, Logger)) anyStale |= save.IsStaleVersion;
                    foreach (ArchivedSave save in SaveArchive.List(offline: false, Logger)) anyStale |= save.IsStaleVersion;

                    if (anyStale)
                    {
                        string msg = MessagesLocalization.Get(MsgKey.GameUpdatedSavesMayBeWrong, current);
                        Logger.LogWarning(msg);
                        // Longer than the "still installed" popup above (7s) - more
                        // important (loading the wrong island is an actual gameplay
                        // problem, not just a cosmetic duplicate-log heads-up)
                        _messageOverlay.Show(msg, new Color(1f, 0.8f, 0.4f, 1f), 12f);
                    }

                    // Rewritten regardless of anyStale, so a bump that affects nobody's
                    // saves still updates the baseline and isn't re-evaluated forever
                    _cfg.LastCheckedGameVersion.Value = current;
                }
            }

            // While the picker is open, Enter is an alternative "load selected"; the
            // picker itself handles arrows / Delete / Escape
            if (_picker != null && _picker.IsOpen
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                ConfirmLoad();
                return;
            }

            // Our own help-key listener, toggling the help screen (same shape as the
            // resume key below)
            if (_helpScreen != null && Input.GetKeyDown(_cfg.HelpKey.Value))
            {
                if (_helpScreen.IsOpen) _helpScreen.Close();
                else _helpScreen.Open();
                return;
            }

            if (!Input.GetKeyDown(_cfg.ResumeKey.Value)) return;
            OnResumeKey();
        }

        private void OnResumeKey()
        {
            // Picker already open → this second press loads the highlighted save
            // (Newest is preselected, so F7 then F7 still loads the latest checkpoint.)
            // Unless disabled via config, in which case only Enter confirms a load and
            // the resume key is a no-op while the picker is open (avoids accidental loads
            // from players trying to close the picker with the same key that opened it)
            if (_picker != null && _picker.IsOpen)
            {
                if (_cfg.ResumeKeyAlsoConfirmsLoad.Value) ConfirmLoad();
                return;
            }

            if (RunLauncher.InTitle)
            {
                Logger.LogWarning("Resume key ignored on the Title screen.");
                return;
            }

            // In coop only the host can drive save/load, tell clients immediately
            // instead of opening a picker that can't do anything
            if (!RunLauncher.IsHost)
            {
                Logger.LogInfo("Resume key ignored: only the host can resume.");
                _messageOverlay.Show(MessagesLocalization.Get(MsgKey.OnlyHostResume),
                    new Color(1f, 0.5f, 0.5f, 1f), 3f);
                return;
            }

            if (_orchestrator.IsRunning)
            {
                Logger.LogInfo("Resume key ignored: a resume is already in progress.");
                return;
            }

            // Block mid-game use unless allowed. "Mid-game" == alive in a level
            bool midGame = RunLauncher.InLevel && !PlayerIsDead();
            if (midGame && !_cfg.AllowMidGame.Value)
            {
                Logger.LogInfo("Mid-game resume is disabled (allowMidGame=false).");
                return;
            }

            // Open the save picker for the current category. Mid-run we prefer the current
            // run's difficulty as the default selection, so F7+F7 loads the current run's
            // latest checkpoint just like before
            bool offline;
            try { offline = Photon.Pun.PhotonNetwork.OfflineMode; } catch { offline = true; }

            SaveTarget? preferred = null;
            if (!RunLauncher.InAirport)
            {
                try
                {
                    preferred = RunLauncher.IsCustomRun
                        ? SaveTarget.Custom()
                        : SaveTarget.Normal(Ascents.currentAscent);
                }
                catch { preferred = null; }
            }

            if (!_picker.Open(offline, preferred))
            {
                _messageOverlay.Show(
                    MessagesLocalization.Get(offline ? MsgKey.NoSavesSolo : MsgKey.NoSavesCoop),
                    new Color(1f, 0.5f, 0.5f, 1f), 3f);
            }
        }

        // Load the picker's highlighted save: close the picker and hand the choice to the
        // orchestrator (which restores it over the checkpoint mod's file, then resumes)
        private void ConfirmLoad()
        {
            var chosen = _picker.Selected;
            _picker.Close();
            if (chosen == null) return;

            Logger.LogInfo($"Resume confirmed: loading {chosen.DifficultyLabel} save from {chosen.SortTime:u}.");
            _orchestrator.RequestResume(chosen);
        }

        // --- Miscellaneous QoL entry points, called from PauseMenuPatch's injected buttons ---

        /// <summary>Restart the current run: back to the Airport, then immediately start a fresh run of the same difficulty</summary>
        internal void RequestRestart() => _restart?.RequestRestart();

        /// <summary>Send everyone back to the Airport, no new run started</summary>
        internal void RequestReturnToAirport()
        {
            if (!RunLauncher.IsHost)
            {
                Logger.LogWarning("Return to Airport ignored: only the host can do this.");
                return;
            }
            // Us intentionally moving the player away, not a checkpoint-mod teleport -
            // see TeleportWatchdog.LiftWatch
            _watchdog?.LiftWatch();
            RunLauncher.ReturnToAirport(Logger);
        }

        /// <summary>Open the gate-kiosk UI directly, without walking up to it</summary>
        internal void RequestOpenGateKiosk() => RunLauncher.OpenGateKiosk(Logger);

        private static bool PlayerIsDead()
        {
            try
            {
                var c = Character.localCharacter;
                return c == null || c.data == null || c.data.dead || c.data.fullyPassedOut;
            }
            catch { return false; }
        }
    }
}
