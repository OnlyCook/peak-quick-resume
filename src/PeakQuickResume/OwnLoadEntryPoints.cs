using System;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Phase 8 milestone M2: our own <c>PreStartSetSegment</c>/<c>LoadPlayerOffline</c>/
    /// <c>LoadPlayerCoop</c> guard chain, ported field-for-field from the decompile
    /// (914-954, 4605-4763). NOT wired into <see cref="ResumeOrchestrator"/> yet -
    /// <see cref="TryLoadPlayer"/> hands off to a stub (<see cref="OwnTeleportSequence"/>)
    /// that only logs, so this milestone proves the guard chain resolves correctly
    /// without touching the live F7 flow at all. See ROADMAP.md Phase 8
    ///
    /// Known, deliberate gaps versus the original (documented here rather than silently
    /// diverging, revisit when their real logic is ported):
    ///  - The "one-time load in Hardmode" guard (<c>configOnetimeLoad</c>) always passes
    ///    (never blocks) since that config isn't ported yet - <see cref="OneTimeLoadEnabled"/>.
    ///  - <c>RecentlyLoaded</c>'s cooldown is only ever RESET here (on reaching the
    ///    Airport); nothing sets it into the future yet, since the two real call sites
    ///    that do (the campfire autosave patch, decompile line 148; the end of inventory
    ///    restore, decompile line 2968) are both ported in later milestones (M6, M4/M5)
    /// </summary>
    public class OwnLoadEntryPoints : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnNetwork _network;

        /// <summary>
        /// The saved scene name for whichever save <see cref="TryPreStartSetSegment"/>
        /// last resolved, consumed by <see cref="MapBakerLevelOverridePatch"/> exactly
        /// like the checkpoint mod's own <c>selectedLevel</c> field. "null" (the string,
        /// not a null reference) mirrors the original's own sentinel for "nothing selected"
        /// </summary>
        public static string SelectedLevel { get; private set; } = "null";

        public bool CurrentlyLoading { get; private set; }

        // Mirrors loadedSaveFileThisRound / RecentlyLoaded (decompile fields ~827-833),
        // reset on reaching the Airport exactly like the original's own Update()
        // (decompile 1345-1353)
        private bool _loadedSaveFileThisRound;
        private float _recentlyLoadedUntil = -1f;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnNetwork network)
        {
            _log = log;
            _cfg = cfg;
            _network = network;
        }

        private void Update()
        {
            if (RunLauncher.InAirport)
            {
                _loadedSaveFileThisRound = false;
                _recentlyLoadedUntil = -1f;
            }
        }

        /// <summary>
        /// Mirrors <c>PreStartSetSegment</c> (decompile 914-954): resolves the save file
        /// for <paramref name="target"/>/<paramref name="offline"/>/<paramref name="userId"/>
        /// and records its <c>sceneName</c> into <see cref="SelectedLevel"/>. Returns true
        /// iff a save file exists and deserialized successfully
        /// </summary>
        public bool TryPreStartSetSegment(SaveTarget target, bool offline, string userId)
        {
            try
            {
                string path = OwnSavePaths.For(target, offline, userId);
                if (!File.Exists(path))
                {
                    SelectedLevel = "null";
                    return false;
                }

                OwnSaveData data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                if (data == null)
                {
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
        /// Mirrors <c>LoadPlayerOffline</c>/<c>LoadPlayerCoop</c>'s shared guard chain
        /// (decompile 4605-4763) exactly: not at the Airport, host-only, the one-time-
        /// hardmode-load guard (see class remarks), the post-load cooldown, and (coop
        /// only) the readiness gate - THEN, where the original starts its
        /// <c>CustomJumpToSegment</c> coroutine, this milestone instead hands off to a
        /// stub that only logs (<see cref="OwnTeleportSequence"/>). Not called from
        /// anywhere live yet
        /// </summary>
        public bool TryLoadPlayer(SaveTarget target, bool offline, string userId)
        {
            try
            {
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
                if (_loadedSaveFileThisRound && OneTimeLoadEnabled())
                {
                    _log?.LogError("OwnLoadEntryPoints: tried to load again in Hardmode (one-time-load)!");
                    return false;
                }
                if (_recentlyLoadedUntil > Time.time)
                {
                    _log?.LogInfo($"OwnLoadEntryPoints: please wait {(_recentlyLoadedUntil - Time.time):F0}s before loading again.");
                    return false;
                }
                if (!offline && _network != null && !_network.CheckReadyStatusForPlayers() && !_loadedSaveFileThisRound)
                {
                    _log?.LogInfo("OwnLoadEntryPoints: please wait until everybody is ready!");
                    return false;
                }

                string path = OwnSavePaths.For(target, offline, userId);
                if (!File.Exists(path))
                {
                    _log?.LogWarning("OwnLoadEntryPoints: no save file found.");
                    return false;
                }

                CurrentlyLoading = true;
                OwnSaveData data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                if (data == null)
                {
                    CurrentlyLoading = false;
                    _log?.LogError("OwnLoadEntryPoints: save file failed to deserialize.");
                    return false;
                }

                OwnTeleportSequence.Begin(data, _log);
                return true;
            }
            catch (Exception e)
            {
                _recentlyLoadedUntil = Time.time - 1f;
                _log?.LogError($"OwnLoadEntryPoints.TryLoadPlayer failed: {e}");
                return false;
            }
        }

        // Not ported yet (see class remarks) - always disabled until configOnetimeLoad's
        // real Hardmode behavior is ported alongside the rest of SavePlayerOffline/Coop
        private bool OneTimeLoadEnabled() => false;
    }
}
