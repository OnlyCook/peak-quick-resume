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

        /// <summary>Display string for the configured resume key (e.g. "F7"), for UI text</summary>
        internal string ResumeKeyText => _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";

        private void Awake()
        {
            Instance = this;
            _cfg = new PluginConfig(Config);

            _checkpoint = new CheckpointInterop(Logger);
            _checkpoint.Probe();

            var harmony = new Harmony(PluginInfo.Guid);

            // Harmony patches against the checkpoint mod (all non-fatal if it changed):
            //  - augment its F1 tutorial to mention F7 + show both versions,
            //  - archive every save it writes so the F7 picker can browse past checkpoints
            //  - localize its "Loading savegame..." caption
            if (_checkpoint.CheckpointType != null)
            {
                TutorialPatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
                SavePatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
                LoadingScreenPatch.Apply(harmony, _checkpoint.CheckpointType, Logger);
            }

            // Miscellaneous QoL, no dependency on the checkpoint mod: injects Restart /
            // Return to Airport / Board Flight buttons into the vanilla pause menu
            PauseMenuPatch.Apply(harmony, _cfg, Logger);

            // Stops Escape from bleeding through and opening the vanilla pause menu right
            // behind the F7 save picker closing (see PauseSuppressPatch for why)
            PauseSuppressPatch.Apply(harmony, Logger);

            var go = new GameObject("PEAKQuickResume.Orchestrator");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _orchestrator = go.AddComponent<ResumeOrchestrator>();
            _orchestrator.Init(Logger, _cfg, _checkpoint);

            _restart = go.AddComponent<RestartOrchestrator>();
            _restart.Init(Logger, _cfg, _checkpoint);

            _picker = go.AddComponent<SavePicker>();
            _picker.Init(Logger, _cfg);

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

            if (!_cfg.ResumeKey.Value.IsDown()) return;
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
