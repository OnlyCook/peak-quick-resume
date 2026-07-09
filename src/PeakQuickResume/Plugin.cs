using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// PEAK Quick Resume, press one key to start a fresh run of your saved
    /// difficulty and immediately load your "PEAK Checkpoint Save" checkpoint,
    /// instead of doing it all by hand
    ///
    /// This plugin contains NO save/load logic of its own; it orchestrates the
    /// vanilla "start run" flow (<see cref="RunLauncher"/>) and dominik0207's
    /// checkpoint mod (<see cref="CheckpointInterop"/>)
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInDependency(PluginInfo.CheckpointSaveGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }

        private PluginConfig _cfg;
        private CheckpointInterop _checkpoint;
        private ResumeOrchestrator _orchestrator;
        private RestartOrchestrator _restart;
        private SavePicker _picker;
        private HelpScreen _helpScreen;
        private TeleportWatchdog _watchdog;
        private TeleportConfigOverride _teleportOverride;
        private IslandToggleButton _islandToggle;

        // Phase 8 M1: our own PhotonView/RPC channel, standing up alongside the
        // checkpoint mod's (still installed) rather than replacing anything yet -
        // see OwnNetwork.cs / ROADMAP.md Phase 8
        private OwnNetwork _ownNetwork;

        // Phase 8 M2: our own PreStartSetSegment/LoadPlayerOffline/LoadPlayerCoop
        // guard chain, not wired into ResumeOrchestrator yet - see OwnLoadEntryPoints.cs
        private OwnLoadEntryPoints _ownLoadEntryPoints;

        /// <summary>Display string for the configured resume key (e.g. "F7"), for UI text</summary>
        internal string ResumeKeyText => _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";

        private void Awake()
        {
            Instance = this;
            _cfg = new PluginConfig(Config);

            _checkpoint = new CheckpointInterop(Logger);
            _checkpoint.Probe();

            // Override PEAK Checkpoint Save's own tutorial/help key to match ours (see
            // PluginConfig.HelpKey / CheckpointInterop.TrySetTutorialKey), applied now for
            // the default and again live whenever it's changed via ModConfig
            _checkpoint.TrySetTutorialKey(_cfg.HelpKey.Value);
            _cfg.HelpKey.SettingChanged += (_, __) => _checkpoint.TrySetTutorialKey(_cfg.HelpKey.Value);

            var harmony = new Harmony(PluginInfo.Guid);

            var go = new GameObject("PEAKQuickResume.Orchestrator");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _orchestrator = go.AddComponent<ResumeOrchestrator>();

            // Phase 6: detect the checkpoint mod's intermittent bad-teleport bug
            // (see ROADMAP.md); no-ops if its members can't be found. Created before
            // the Harmony patches below since several of them need a reference to it
            _watchdog = go.AddComponent<TeleportWatchdog>();
            _watchdog.Init(Logger, _cfg, _checkpoint);

            // Phase 6 step 2: temporary Shift/Alt teleport-config override, shared by
            // our own F7 flow (below) and the native-F6 path (LoadingScreenPatch)
            _teleportOverride = new TeleportConfigOverride(Logger, _cfg, _checkpoint, _orchestrator);

            _restart = go.AddComponent<RestartOrchestrator>();
            _restart.Init(Logger, _cfg, _checkpoint, _watchdog);

            _picker = go.AddComponent<SavePicker>();
            _picker.Init(Logger, _cfg, _checkpoint, _teleportOverride);

            // Phase 6 step 3: the F1 help screen, a small menu built from the same
            // visual primitives as the picker above. Created before TutorialPatch.Apply
            // below, which needs a reference to it
            _helpScreen = go.AddComponent<HelpScreen>();
            _helpScreen.Init(Logger, _cfg, _checkpoint, _teleportOverride);

            // Phase 7: big, clearly-labeled boarding-pass island-toggle button (mirrors
            // the checkpoint mod's own tiny, easy-to-miss checkbox, see IslandToggleButton)
            _islandToggle = go.AddComponent<IslandToggleButton>();
            _islandToggle.Init(Logger, _cfg, _checkpoint);

            // Phase 8 M1: stands up our own PhotonView/RPC channel (separate GameObject,
            // own ViewID) purely to prove it resolves/works; nothing reads from it yet
            _ownNetwork = go.AddComponent<OwnNetwork>();
            _ownNetwork.Init(Logger, _cfg);

            // Phase 8 M3: our own literal port of CustomJumpToSegment/TeleportToPosition/
            // TeleportClientsToHost/ReviveDeadPlayers (see OwnTeleportSequence.cs)
            var ownTeleportSequence = go.AddComponent<OwnTeleportSequence>();

            // Phase 8 M2/M3: our own load-entry-point guard chain. As of M3, its solo path
            // IS wired live via ResumeOrchestrator below
            _ownLoadEntryPoints = go.AddComponent<OwnLoadEntryPoints>();
            _ownLoadEntryPoints.Init(Logger, _cfg, _ownNetwork, ownTeleportSequence);
            ownTeleportSequence.Init(Logger, _cfg, _ownLoadEntryPoints);

            // Now that _ownLoadEntryPoints exists, wire the orchestrator (Phase 8 M3:
            // its SOLO path calls _ownLoadEntryPoints directly; coop is unchanged, still
            // via _checkpoint - see ResumeOrchestrator.cs)
            _orchestrator.Init(Logger, _cfg, _checkpoint, _ownLoadEntryPoints, _teleportOverride, _watchdog);

            // Phase 8 M3: our own copy of the checkpoint mod's fall/lava-damage
            // protection window, armed from OwnTeleportSequence (see OwnFallDamageProtection.cs)
            OwnFallDamageProtection.Apply(harmony, Logger);

            // Phase 8 M2: our own copy of the checkpoint mod's MapBaker.GetLevel prefix.
            // Safe alongside its own equivalent patch: OwnLoadEntryPoints.SelectedLevel
            // stays "null" (a pure no-op) until a later milestone wires a real resume
            // flow through it - see MapBakerLevelOverridePatch.cs
            MapBakerLevelOverridePatch.Apply(harmony, Logger);

            // Phase 8 M6: our own save-capture port, triggered additively alongside the
            // checkpoint mod's own still-active autosave patch. Writes to a NON-canonical
            // diagnostic path only (see OwnSaveCapture.cs) - does not touch the live
            // save file the checkpoint mod still writes and our own restore path still reads
            CampfireAutoSavePatch.Apply(harmony, _cfg, _ownLoadEntryPoints, Logger);

            // Harmony patches against the checkpoint mod (all non-fatal if it changed):
            //  - replace its F1 tutorial overlay with HelpScreen (Quick Resume + teleport-bug help)
            //  - archive every save it writes so the F7 picker can browse past checkpoints
            //  - localize its "Loading savegame..." caption (also arms the teleport watchdog's
            //    load window AND applies the Shift/Alt override for a native F6 load)
            //  - localize its "Save game loaded!" message (and arm the teleport watchdog's watch window)
            if (_checkpoint.CheckpointType != null)
            {
                TutorialPatch.Apply(harmony, _checkpoint.CheckpointType, Logger, _helpScreen);
                SavePatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
                CampfireCookSaveFixPatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
                BackpackSaveMitigation.Apply(harmony, _checkpoint, Logger);
                LoadingScreenPatch.Apply(harmony, _checkpoint.CheckpointType, Logger, _watchdog, _teleportOverride);
                SavegameLoadedMessagePatch.Apply(harmony, _checkpoint.CheckpointType, Logger, _watchdog, _teleportOverride, _checkpoint);
                MessageOverlayWrapPatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
            }

            // Vanilla Character.WarpPlayerRPC patch, records the local player's teleport
            // target for the watchdog above. No checkpoint-mod dependency (Character is
            // a vanilla type), but pointless without the checkpoint mod's own hooks above
            // ever arming a load window, so only applied alongside them
            if (_checkpoint.CheckpointType != null)
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
                + $"Resume key: {_cfg.ResumeKey.Value}. Checkpoint interop: "
                + (_checkpoint.IsAvailable ? "READY" : "UNAVAILABLE"));
        }

        private void Update()
        {
            if (_cfg == null) return;

            // While the picker is open, Enter is an alternative "load selected"; the
            // picker itself handles arrows / Delete / Escape
            if (_picker != null && _picker.IsOpen
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                ConfirmLoad();
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
                _checkpoint.TryShowMessage(MessagesLocalization.Get(MsgKey.OnlyHostResume),
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
                _checkpoint.TryShowMessage(
                    MessagesLocalization.Get(offline ? MsgKey.NoSavesSolo : MsgKey.NoSavesCoop),
                    new Color(1f, 0.5f, 0.5f, 1f), 3f);
            }
        }

        // Load the picker's highlighted save: close the picker and hand the choice to the
        // orchestrator (which restores it over the checkpoint mod's file, then resumes)
        private void ConfirmLoad()
        {
            var chosen = _picker.Selected;
            // Captured HERE, at confirm time - our own load doesn't happen synchronously
            // with this keypress (it waits for a fresh run to start first), so by the
            // time the checkpoint mod's teleport actually runs the user has likely
            // already released Shift/Alt. See TeleportConfigOverride / ROADMAP.md Phase 6 step 2
            int? teleportOverride = _teleportOverride?.ResolveOverride();
            _picker.Close();
            if (chosen == null) return;

            Logger.LogInfo($"Resume confirmed: loading {chosen.DifficultyLabel} save from {chosen.SortTime:u}.");
            _orchestrator.RequestResume(chosen, teleportOverride);
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
