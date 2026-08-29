using BepInEx;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// PEAK Quick Resume: press one key to start a fresh run of your saved difficulty
    /// and immediately load your latest checkpoint. Fully self-contained: orchestrates
    /// the vanilla "start run" flow (<see cref="RunLauncher"/>) and drives its own
    /// independent save/load/teleport. The save format descends from dominik0207's
    /// "PEAK Checkpoint Save", but that mod is no longer required or integrated with -
    /// saves live in our own folder (see <see cref="OwnSavePaths"/>).
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }

        /// <summary>Read by DebugLog.Trace so every file can gate its verbose tracing
        /// without needing a PluginConfig reference threaded through it.</summary>
        internal static bool DebugLoggingEnabled =>
            Instance != null && Instance._cfg != null && Instance._cfg.EnableDebugLogging.Value;

        private PluginConfig _cfg;
        private ResumeOrchestrator _orchestrator;
        private RestartOrchestrator _restart;
        private SavePicker _picker;
        private HelpScreen _helpScreen;
        private TeleportWatchdog _watchdog;

        private OwnMessageOverlay _messageOverlay;
        private OwnNetwork _ownNetwork;
        private OwnLoadEntryPoints _ownLoadEntryPoints;

        // BepInEx GUID of the old PEAK Checkpoint Save mod. No longer depended on or
        // integrated with, but if it's still installed alongside us both mods run their own
        // campfire-autosave + logging independently, producing duplicate messages/log lines.
        // Purely cosmetic - detected only to warn the player.
        private const string CheckpointSaveGuid = "PEAK_Checkpoint_Save";
        private bool _dupWarningShown;

        private bool _versionCheckDone;

        /// <summary>
        /// Whether PEAK Checkpoint Save is loaded alongside us. Queried lazily, not cached at
        /// Awake, since <c>Chainloader.PluginInfos</c> may not yet list it there.
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

            OrchestrationLock.Init(_orchestrator);

            _messageOverlay = go.AddComponent<OwnMessageOverlay>();
            _messageOverlay.Init(Logger);

            // Suppresses vanilla's forced-quit "game has been updated" modal, which can
            // otherwise fire at our mid-run Airport pit stop and brick an unfinished save.
            GameUpdateModalSuppressPatch.Apply(harmony, Logger, _messageOverlay);

            // Created before the Harmony patches below since TeleportWatchdogPatch needs a reference to it.
            _watchdog = go.AddComponent<TeleportWatchdog>();
            _watchdog.Init(Logger, _cfg, _messageOverlay);

            _restart = go.AddComponent<RestartOrchestrator>();
            _restart.Init(Logger, _cfg, _messageOverlay, _watchdog);

            _picker = go.AddComponent<SavePicker>();
            _picker.Init(Logger, _cfg);

            _helpScreen = go.AddComponent<HelpScreen>();
            _helpScreen.Init(Logger, _cfg);

            _ownNetwork = go.AddComponent<OwnNetwork>();
            _ownNetwork.Init(Logger, _cfg);

            var ownWakeUpEffect = go.AddComponent<OwnWakeUpEffect>();
            ownWakeUpEffect.Init(Logger);

            var ownLoadingScreen = go.AddComponent<OwnLoadingScreen>();
            ownLoadingScreen.Init(Logger);

            var ownTeleportSequence = go.AddComponent<OwnTeleportSequence>();

            _ownLoadEntryPoints = go.AddComponent<OwnLoadEntryPoints>();
            _ownLoadEntryPoints.Init(Logger, _cfg, _ownNetwork, ownTeleportSequence);
            ownTeleportSequence.Init(Logger, _cfg, _ownLoadEntryPoints, ownWakeUpEffect, ownLoadingScreen);

            _ownNetwork.AttachDependencies(_messageOverlay, _watchdog, _ownLoadEntryPoints, ownWakeUpEffect, ownLoadingScreen);

            _orchestrator.Init(Logger, _cfg, _messageOverlay, _ownLoadEntryPoints, _watchdog);

            OwnFallDamageProtection.Apply(harmony, Logger);
            MapBakerLevelOverridePatch.Apply(harmony, Logger);
            // fixes biome gated achievements never unlocking
            // after a segment jump lands in a biome shared with an earlier, skipped point
            BiomeSkipResumeFix.Apply(harmony, Logger);

            // Third-party mod compat, no-op if not installed: stops Snosz's TerrainRandomiser
            // from re-randomizing the terrain on an F7 load.
            TerrainRandomiserCompat.Apply(harmony, Logger);

            CampfireAutoSavePatch.Apply(harmony, _cfg, _ownLoadEntryPoints, _ownNetwork, Logger);
            // Nadir's equivalent of lighting a campfire - the Void biome has none.
            ScoutmasterSoulPillarAutoSavePatch.Apply(harmony, _cfg, _ownLoadEntryPoints, _ownNetwork, Logger);
            BackpackSaveMitigation.Apply(harmony, Logger);

            // Debug aid only, not patched in unless debug logging is on.
            if (_cfg.EnableDebugLogging.Value) AchievementDebugLogging.Apply(harmony, Logger);

            RespawnChestDoubleCreditFix.Apply(harmony, Logger);
            AchievementSubscriptionFix.Apply(harmony, Logger);
            TeleportWatchdogPatch.Apply(harmony, Logger, _watchdog);
            PauseMenuPatch.Apply(harmony, _cfg, Logger);
            RebindControlsRelocationPatch.Apply(harmony, _cfg, Logger);
            PauseSuppressPatch.Apply(harmony, Logger);
            ThornRestoreSilencer.Apply(harmony, Logger);
            HeightAchievementGuard.Apply(harmony, Logger);

            RemoteRagdollWatch.Init(Logger);
            go.AddComponent<RemoteRagdollWatchPump>();

            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded. "
                + $"Resume key: {_cfg.ResumeKey.Value}.");
        }

        private void Update()
        {
            if (_cfg == null) return;

            // Deferred until the player is in a game scene so the overlay is seen, and so
            // Chainloader.PluginInfos is fully populated by then.
            if (!_dupWarningShown && _messageOverlay != null
                && (RunLauncher.InAirport || RunLauncher.InLevel))
            {
                _dupWarningShown = true;
                if (CheckpointModInstalled)
                {
                    Logger.LogWarning("PEAK Checkpoint Save is still installed. Quick Resume no longer needs it and "
                        + "runs fully on its own; both mods will save/log independently, so expect duplicate log "
                        + "messages and saves appearing. This is harmless (no logic conflict), but uninstall PEAK "
                        + "Checkpoint Save to remove the duplicates.");
                    _messageOverlay.Show(
                        MessagesLocalization.Get(MsgKey.CheckpointModStillInstalledShort, _cfg.HelpKey.Value.ToString()),
                        new Color(1f, 0.8f, 0.4f, 1f), 7f);
                }
            }

            // Shown when the game version has moved past the last one recorded here. An empty
            // slot (fresh install, or an upgrade predating this feature) has no real baseline,
            // so it's treated as "nothing to warn about" rather than firing on first launch.
            if (!_versionCheckDone && _messageOverlay != null
                && (RunLauncher.InAirport || RunLauncher.InLevel))
            {
                _versionCheckDone = true;
                string current = GameVersionCompat.Current;
                string lastChecked = _cfg.LastCheckedGameVersion.Value;
                if (!string.IsNullOrEmpty(lastChecked) && GameVersionCompat.IsOlderThan(lastChecked, current))
                {
                    string msg = MessagesLocalization.Get(MsgKey.GameUpdatedSavesMayBeWrong, current);
                    Logger.LogWarning(msg);
                    _messageOverlay.Show(msg, new Color(1f, 0.8f, 0.4f, 1f), 12f);
                }

                // Rewritten regardless of outcome, so this isn't re-evaluated forever.
                _cfg.LastCheckedGameVersion.Value = current;
            }

            if (_picker != null && _picker.IsOpen
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                ConfirmLoad();
                return;
            }

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
            // Picker already open: the resume key closes it again by default (players kept
            // loading a save by accident while trying to close the picker). Opt-in via
            // config for the old behaviour where a second press loads instead.
            if (_picker != null && _picker.IsOpen)
            {
                if (_cfg.ResumeKeyLoadsInsteadOfClosing.Value) ConfirmLoad();
                else _picker.Close();
                return;
            }

            if (RunLauncher.InTitle)
            {
                Logger.LogWarning("Resume key ignored on the Title screen.");
                return;
            }

            // Only the host can drive save/load in coop.
            if (!RunLauncher.IsHost)
            {
                Logger.Trace("Resume key ignored: only the host can resume.");
                _messageOverlay.Show(MessagesLocalization.Get(MsgKey.OnlyHostResume),
                    new Color(1f, 0.5f, 0.5f, 1f), 3f);
                return;
            }

            if (_orchestrator.IsRunning)
            {
                Logger.Trace("Resume key ignored: a resume is already in progress.");
                return;
            }

            bool midGame = RunLauncher.InLevel && !PlayerIsDead();
            if (midGame && !_cfg.AllowMidGame.Value)
            {
                Logger.Trace("Mid-game resume is disabled (allowMidGame=false).");
                return;
            }

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
            // Routed through the shared cooldown/queue too: it triggers a full scene
            // transition just like Resume/Restart, so it needs the same safety.
            OrchestrationLock.RunOrQueue("return-to-airport", RequestReturnToAirportNow, Logger);
        }

        private void RequestReturnToAirportNow()
        {
            if (!RunLauncher.IsHost)
            {
                Logger.LogWarning("Return to Airport ignored: only the host can do this.");
                return;
            }

            if (OrchestrationLock.IsBusy)
            {
                Logger.Trace("Return to Airport ignored: a resume/restart is already in progress.");
                return;
            }

            _watchdog?.LiftWatch();
            RunLauncher.ClearVanillaQuicksaveResume(Logger);
            RunLauncher.ReturnToAirport(Logger);

            if (!PhotonNetwork.OfflineMode)
                OrchestrationLock.ArmCooldown(_cfg.PostOrchestrationCooldown.Value);
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
