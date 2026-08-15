using System;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Guard chain for loading a save: <see cref="TryPreStartSetSegment"/> resolves which
    /// scene to load, <see cref="TryLoadPlayer"/> hands off to <see cref="OwnTeleportSequence"/>.
    /// The "one-time load in Hardmode" guard (<see cref="OneTimeLoadEnabled"/>) always
    /// passes since that config isn't ported.
    /// </summary>
    public class OwnLoadEntryPoints : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnNetwork _network;
        private OwnTeleportSequence _teleportSequence;

        /// <summary>
        /// The saved scene name for whichever save <see cref="TryPreStartSetSegment"/> last
        /// resolved, consumed by <see cref="MapBakerLevelOverridePatch"/>. "null" (the
        /// string) is the sentinel for "nothing selected". Cleared back to "null" the
        /// instant it's consumed (one-shot), so a later plain manual Boarding Pass start
        /// isn't silently redirected onto a stale saved island with none of our restore
        /// logic behind it.
        /// </summary>
        public static string SelectedLevel { get; private set; } = "null";

        /// <summary>One-shot consume: called by <see cref="MapBakerLevelOverridePatch"/> right after reading a real (non-"null") value, see <see cref="SelectedLevel"/>'s remarks</summary>
        internal static void ClearSelectedLevel() => SelectedLevel = "null";

        /// <summary>
        /// Used by <see cref="RestartOrchestrator"/> to force <see cref="SelectedLevel"/>
        /// to the current level's scene name directly (no save file involved). Without
        /// this, a restart's fresh <c>RunLauncher.StartRun</c> left <see cref="SelectedLevel"/>
        /// at "null" and fell through to vanilla's daily-rotation scene instead of the
        /// island the player was just on.
        /// </summary>
        internal static void ForceSelectedLevel(string sceneName) =>
            SelectedLevel = string.IsNullOrEmpty(sceneName) ? "null" : sceneName;

        /// <summary>
        /// Armed on every peer right before <c>RunLauncher.StartRun</c> for a quick-resume
        /// load. Consumed one-shot by <see cref="TerrainRandomiserCompat"/>'s
        /// <c>MapHandler.InitializeMap</c> prefix to suppress that mod's terrain
        /// regeneration for this one load. Survives longer than <see cref="SelectedLevel"/>
        /// deliberately, since that gets consumed earlier by <c>MapBaker.GetLevel</c>.
        /// </summary>
        private static bool _suppressExternalTerrainRandomizer;

        internal static void ArmSuppressExternalTerrainRandomizerOnce() => _suppressExternalTerrainRandomizer = true;

        internal static bool ConsumeSuppressExternalTerrainRandomizerOnce()
        {
            bool value = _suppressExternalTerrainRandomizer;
            _suppressExternalTerrainRandomizer = false;
            return value;
        }

        public bool CurrentlyLoading { get; private set; }

        /// <summary>
        /// True while a <see cref="OwnTeleportSequence"/> triggered by <see cref="TryLoadPlayer"/>
        /// is still running, including its wake-up + loading-screen presentation at the end.
        /// <see cref="TryLoadPlayer"/> itself is fire-and-forget (starts the sequence and returns
        /// immediately) - <see cref="ResumeOrchestrator"/> polls this to know when it's actually
        /// safe to show the "Save loaded" message
        /// </summary>
        public bool TeleportInProgress => _teleportSequence != null && _teleportSequence.IsRunning;

        /// <summary>
        /// True once the CURRENT (or most recent) <see cref="OwnTeleportSequence"/> has actually
        /// finished restoring inventory/backpacks/afflictions/etc., well before
        /// <see cref="TeleportInProgress"/> goes false (that also waits out the purely-cosmetic
        /// wake-up fade-out/stand-up beat). <see cref="ResumeOrchestrator"/> polls THIS to show
        /// "Save loaded. Welcome back!" right after the restore is actually done
        /// </summary>
        public bool RestoreComplete => _teleportSequence == null || _teleportSequence.RestoreComplete;

        /// <summary>Exposes the shared <see cref="OwnNetwork"/> channel (RPCs, watchdog/checkpoint refs attached to it) to <see cref="OwnTeleportSequence"/>/<see cref="OwnInventoryRestore"/></summary>
        internal OwnNetwork Network => _network;

        /// <summary>
        /// False for the first load after a fresh run start, true for any repeat load in
        /// the same run instance. Several restore steps only run on a repeat load. Reset
        /// on reaching the Airport.
        /// </summary>
        public bool LoadedSaveFileThisRound { get; private set; }

        private float _recentlyLoadedUntil = -1f;

        /// <summary>Armed at the end of a real restore so the campfire-autosave patch doesn't immediately re-save right after a load.</summary>
        public float RecentlyLitCampfireUntil { get; private set; } = -1f;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnNetwork network, OwnTeleportSequence teleportSequence)
        {
            _log = log;
            _cfg = cfg;
            _network = network;
            _teleportSequence = teleportSequence;
        }

        internal void MarkNotCurrentlyLoading() => CurrentlyLoading = false;
        internal void ArmRecentlyLoadedCooldown(float seconds) => _recentlyLoadedUntil = Time.time + seconds;
        internal void ArmRecentlyLitCampfireCooldown(float seconds) => RecentlyLitCampfireUntil = Time.time + seconds;

        /// <summary>Called by <see cref="OwnTeleportSequence"/> at the end of its sequence.</summary>
        internal void MarkLoadedThisRound() => LoadedSaveFileThisRound = true;

        private void Update()
        {
            if (RunLauncher.InAirport)
            {
                LoadedSaveFileThisRound = false;
                _recentlyLoadedUntil = -1f;
            }
        }

        /// <summary>
        /// Records the chosen save's <c>sceneName</c> into <see cref="SelectedLevel"/>.
        /// Returns true iff the selection's host file exists, deserializes, and names a
        /// scene. Reads <see cref="SaveSelection.HostFilePath"/> only, since a co-op
        /// client's file has no <c>sceneName</c>.
        /// </summary>
        public bool TryPreStartSetSegment(SaveSelection selection)
        {
            try
            {
                OwnSaveData data = ReadHostSave(selection);
                if (data == null || string.IsNullOrEmpty(data.sceneName))
                {
                    if (data != null)
                        _log?.LogError("OwnLoadEntryPoints: the chosen save has no scene recorded "
                            + "(is it a co-op client's file rather than the host's?).");
                    SelectedLevel = "null";
                    return false;
                }

                SelectedLevel = data.sceneName;
                return true;
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnLoadEntryPoints.TryPreStartSetSegment failed: {e}");
                SelectedLevel = "null";
                return false;
            }
        }

        /// <summary>
        /// Reads the level/world half of a selection - the host's file. Per-player state
        /// is read separately by <see cref="OwnInventoryRestore"/> and <see cref="AchievementProgressIO"/>.
        /// </summary>
        private OwnSaveData ReadHostSave(SaveSelection selection)
        {
            if (selection == null || string.IsNullOrEmpty(selection.HostFilePath))
            {
                _log?.LogWarning("OwnLoadEntryPoints: no save selection to load.");
                return null;
            }
            if (!File.Exists(selection.HostFilePath))
            {
                _log?.LogWarning($"OwnLoadEntryPoints: save file no longer exists: {selection.HostFilePath}");
                return null;
            }

            OwnSaveData data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(selection.HostFilePath));
            if (data == null)
                _log?.LogError($"OwnLoadEntryPoints: save file failed to deserialize: {selection.HostFilePath}");
            return data;
        }

        /// <summary>
        /// Guard chain (not at Airport, host-only, one-time-load, cooldown, coop
        /// readiness) before handing off to <see cref="OwnTeleportSequence"/>.
        /// </summary>
        public bool TryLoadPlayer(SaveSelection selection)
        {
            try
            {
                bool offline = selection?.Offline ?? true;

                if (RunLauncher.InAirport)
                {
                    _log?.LogError("OwnLoadEntryPoints: tried to load save at the Airport!");
                    return false;
                }
                if (!RunLauncher.IsHost)
                {
                    _log?.LogError("OwnLoadEntryPoints: tried to load as a non-host client!");
                    return false;
                }
                if (LoadedSaveFileThisRound && OneTimeLoadEnabled())
                {
                    _log?.LogError("OwnLoadEntryPoints: tried to load again in Hardmode (one-time-load)!");
                    return false;
                }
                if (_recentlyLoadedUntil > Time.time)
                {
                    _log.Trace($"OwnLoadEntryPoints: please wait {(_recentlyLoadedUntil - Time.time):F0}s before loading again.");
                    return false;
                }
                if (!offline && _network != null && !_network.CheckReadyStatusForPlayers() && !LoadedSaveFileThisRound)
                {
                    _log.Trace("OwnLoadEntryPoints: please wait until everybody is ready!");
                    return false;
                }

                CurrentlyLoading = true;
                OwnSaveData data = ReadHostSave(selection);
                if (data == null)
                {
                    CurrentlyLoading = false;
                    return false;
                }

                _teleportSequence.Begin(data, selection);
                return true;
            }
            catch (Exception e)
            {
                _recentlyLoadedUntil = Time.time - 1f;
                _log?.LogError($"OwnLoadEntryPoints.TryLoadPlayer failed: {e}");
                return false;
            }
        }

        private bool OneTimeLoadEnabled() => false;
    }
}
