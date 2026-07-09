using System;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own <c>PreStartSetSegment</c>/<c>LoadPlayerOffline</c>/<c>LoadPlayerCoop</c>
    /// guard chain, ported field-for-field from the decompile (914-954, 4605-4763).
    /// As of Phase 8 M3, <see cref="TryLoadPlayer"/> hands off to the real
    /// <see cref="OwnTeleportSequence"/> port (solo path only is wired live via
    /// <see cref="ResumeOrchestrator"/> - see ROADMAP.md Phase 8)
    ///
    /// Known, deliberate gaps versus the original (documented here rather than silently
    /// diverging, revisit when their real logic is ported):
    ///  - The "one-time load in Hardmode" guard (<c>configOnetimeLoad</c>) always passes
    ///    (never blocks) since that config isn't ported yet - <see cref="OneTimeLoadEnabled"/>.
    ///  - <c>RecentlyLoaded</c>'s cooldown is only ever RESET here (on reaching the
    ///    Airport); nothing sets it into the future yet, since the two real call sites
    ///    that do (the campfire autosave patch, decompile line 148; the end of inventory
    ///    restore, decompile line 2968) are both ported in later milestones (M6, M4/M5)
    ///  - <see cref="CurrentlyLoading"/> is set true here but never reset to false -
    ///    the original resets it (<c>currentlyLoading = false;</c>) at decompile line
    ///    2966, inside <c>LoadInventoryDelayed</c> (M4/M5). Harmless for now since
    ///    nothing reads this property yet; will be fixed the moment M4/M5 ports that
    ///    method's actual completion point
    /// </summary>
    public class OwnLoadEntryPoints : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnNetwork _network;
        private OwnTeleportSequence _teleportSequence;

        /// <summary>
        /// The saved scene name for whichever save <see cref="TryPreStartSetSegment"/>
        /// last resolved, consumed by <see cref="MapBakerLevelOverridePatch"/> exactly
        /// like the checkpoint mod's own <c>selectedLevel</c> field. "null" (the string,
        /// not a null reference) mirrors the original's own sentinel for "nothing selected"
        /// </summary>
        public static string SelectedLevel { get; private set; } = "null";

        public bool CurrentlyLoading { get; private set; }

        /// <summary>
        /// Mirrors the checkpoint mod's own <c>loadedSaveFileThisRound</c> (decompile
        /// field ~833): false for the FIRST load after a fresh run start, true for any
        /// repeat load in the same run instance - several restore steps (item respawn,
        /// stale-object cleanup, campfire reset) only run on a repeat load, matching
        /// the original exactly. Reset on reaching the Airport (decompile 1345-1353)
        /// </summary>
        public bool LoadedSaveFileThisRound { get; private set; }

        private float _recentlyLoadedUntil = -1f;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnNetwork network, OwnTeleportSequence teleportSequence)
        {
            _log = log;
            _cfg = cfg;
            _network = network;
            _teleportSequence = teleportSequence;
        }

        /// <summary>Called by <see cref="OwnTeleportSequence"/> at the end of its own sequence, mirroring the original's <c>loadedSaveFileThisRound = true;</c> (decompile line 2560)</summary>
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
        /// <c>CustomJumpToSegment</c> coroutine, this hands off to the real
        /// <see cref="OwnTeleportSequence"/> port (Phase 8 M3)
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
                if (LoadedSaveFileThisRound && OneTimeLoadEnabled())
                {
                    _log?.LogError("OwnLoadEntryPoints: tried to load again in Hardmode (one-time-load)!");
                    return false;
                }
                if (_recentlyLoadedUntil > Time.time)
                {
                    _log?.LogInfo($"OwnLoadEntryPoints: please wait {(_recentlyLoadedUntil - Time.time):F0}s before loading again.");
                    return false;
                }
                if (!offline && _network != null && !_network.CheckReadyStatusForPlayers() && !LoadedSaveFileThisRound)
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

                _teleportSequence.Begin(data, target, offline);
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
