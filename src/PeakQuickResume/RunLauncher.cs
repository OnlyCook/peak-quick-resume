using System;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Thin wrappers around the vanilla game's "start a run" and "return to the
    /// Airport" flows, so the orchestrator can drive them without touching the UI
    ///
    /// Vanilla chain (PEAK 1.64.a), reproduced deliberately:
    ///   BoardingPass.StartGame() -> kiosk.StartGame(ascentIndex)
    ///     -> RPC LoadIslandMaster -> MapBaker.GetLevel (patched by checkpoint mod
    ///        to force the SAVED scene) -> RPC BeginIslandLoadRPC -> scene load
    ///   EndScreen.ReturnToAirport() -> loads the "Airport" scene networked
    /// </summary>
    public static class RunLauncher
    {
        // MenuWindow.Open() is `internal`, not accessible across assemblies
        private static readonly System.Reflection.MethodInfo _menuWindowOpen =
            HarmonyLib.AccessTools.Method(typeof(MenuWindow), "Open");

        public const string AirportScene = "Airport";
        public const string TitleScene = "Title";
        public const string LevelScenePrefix = "Level";

        public static string ActiveSceneName => SceneManager.GetActiveScene().name;
        public static bool InAirport => ActiveSceneName == AirportScene;
        public static bool InLevel => ActiveSceneName.StartsWith(LevelScenePrefix);
        public static bool InTitle => ActiveSceneName == TitleScene;

        /// <summary>
        /// Is a loading screen currently active? Vanilla <c>AirportCheckInKiosk.StartGame</c>
        /// and boarding-pass actions silently no-op while this is true, so the orchestrator
        /// must wait for it to clear before starting a run
        /// </summary>
        public static bool IsLoading
        {
            get
            {
                try { return LoadingScreenHandler.loading; }
                catch { return false; }
            }
        }

        /// <summary>Are we allowed to drive save/load? (host in coop, or offline).</summary>
        public static bool IsHost => PhotonNetwork.IsMasterClient || PhotonNetwork.OfflineMode;

        /// <summary>
        /// Whether the game currently considers this a custom run. The checkpoint mod
        /// picks its save-file name off this flag, so we read it to know which run the
        /// player is in (mid-run) and set it (below) before starting a resume
        /// </summary>
        public static bool IsCustomRun
        {
            get { try { return RunSettings.IsCustomRun; } catch { return false; } }
        }

        /// <summary>
        /// Forces <c>RunSettings.IsCustomRun</c> before starting a run: the game doesn't
        /// reliably reset it at the Airport, and a stale value picks the wrong save file.
        /// </summary>
        public static bool TrySetCustomRun(bool value, ManualLogSource log)
        {
            try
            {
                RunSettings.IsCustomRun = value;
                return true;
            }
            catch (Exception e)
            {
                log.LogError($"TrySetCustomRun({value}) failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Clears vanilla's "resuming the quicksave" latch (<c>Peak.Quicksave.ShouldUseSaveData</c>)
        /// before driving our own scene transition. Main-menu "Continue Run" sets it and nothing
        /// else clears it; left set, <c>CharacterSpawner.SpawnHostCharacter</c> takes the resume
        /// branch on the next scene load, which throws in the Airport (no MapHandler there) and
        /// hangs the loading screen for its full 200s timeout. Doesn't call
        /// <c>Quicksave.DestroySaveData()</c> (would delete quicksave.peak and remove "Continue Run").
        /// </summary>
        public static void ClearVanillaQuicksaveResume(ManualLogSource log)
        {
            try
            {
                if (!Peak.Quicksave.ShouldUseSaveData) return;
                Peak.Quicksave.ShouldUseSaveData = false;
                log.LogInfo("Cleared vanilla's Quicksave.ShouldUseSaveData latch (left over from a "
                    + "main-menu \"Continue Run\"); without this the Airport load never spawns the "
                    + "host and hangs on the vanilla loading screen.");
            }
            catch (Exception e)
            {
                log.LogWarning($"Could not clear Quicksave.ShouldUseSaveData ({e.Message}); "
                    + "an Airport load right after a \"Continue Run\" may hang.");
            }
        }

        /// <summary>
        /// Drops the room's buffered-RPC cache, same as vanilla does on an Airport transition.
        /// Needed because a leftover buffered <c>RPCA_InitGhost</c> pointing at a Character our
        /// load destroyed throws on every later joiner, leaving that player stuck as a ghost
        /// (matches reported "still being ghost" after a resume). Vanilla's own clear misses two
        /// paths we hit (resume started already at the Airport; an early-return in
        /// <c>LoadAirportMaster</c>), so this call closes both. Master-only, no-op offline.
        /// </summary>
        public static void ClearBufferedRpcs(ManualLogSource log)
        {
            try
            {
                if (PhotonNetwork.OfflineMode || !PhotonNetwork.IsMasterClient) return;
                PhotonNetwork.OpRemoveCompleteCache();
                log.Trace("Cleared the room's buffered-RPC cache before starting the run (stops a stale buffered "
                    + "RPCA_InitGhost from throwing on every later joiner and stranding an orphan ghost).");
            }
            catch (Exception e)
            {
                log?.LogWarning($"Could not clear the buffered-RPC cache ({e.Message}); a player who was a ghost "
                    + "before this load may stay one.");
            }
        }

        /// <summary>
        /// Sends everyone back to the Airport via <c>GameOverHandler.LoadAirport()</c> (RPC-to-all),
        /// unlike <c>EndScreen.ReturnToAirport()</c> which only loads locally and leaves clients
        /// behind. Fallbacks kept for safety.
        /// </summary>
        public static bool ReturnToAirport(ManualLogSource log)
        {
            try
            {
                var goh = Singleton<GameOverHandler>.Instance;
                if (goh != null)
                {
                    log.LogInfo("ReturnToAirport: GameOverHandler.LoadAirport() (synchronized RPC-to-all).");
                    goh.LoadAirport();
                    return true;
                }
                log.LogWarning("ReturnToAirport: GameOverHandler.Instance is null; using fallback "
                    + "(in coop this may not bring clients).");
            }
            catch (Exception e)
            {
                log.LogError($"ReturnToAirport via GameOverHandler failed ({e.Message}); using fallback.");
            }

            // Fallbacks (solo-safe; coop-incomplete):
            try
            {
                var endScreen = UnityEngine.Object.FindObjectOfType<EndScreen>();
                if (endScreen != null)
                {
                    log.LogInfo("ReturnToAirport: fallback EndScreen.ReturnToAirport().");
                    endScreen.ReturnToAirport();
                    return true;
                }
                log.LogInfo("ReturnToAirport: fallback direct networked Airport load.");
                return LoadAirportDirect(log);
            }
            catch (Exception e)
            {
                log.LogError($"ReturnToAirport fallback failed: {e}");
                return false;
            }
        }

        // Mirrors EndScreen.ReturnToAirport(): a networked Airport load, so it propagates to all clients.
        private static bool LoadAirportDirect(ManualLogSource log)
        {
            try
            {
                var handler = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                if (handler == null)
                {
                    log.LogError("LoadAirportDirect: LoadingScreenHandler.Instance is null; falling back to local scene load.");
                    SceneManager.LoadScene(AirportScene);
                    return true;
                }

                log.Trace("LoadAirportDirect: networked LoadingScreenHandler load of Airport.");
                handler.Load(
                    LoadingScreen.LoadingScreenType.Basic,
                    null,
                    handler.LoadSceneProcess(AirportScene, networked: true, yieldForCharacterSpawn: true));
                return true;
            }
            catch (Exception e)
            {
                log.LogError($"LoadAirportDirect failed ({e.Message}); falling back to local scene load.");
                SceneManager.LoadScene(AirportScene);
                return true;
            }
        }

        /// <summary>
        /// Start a fresh run at the Airport for the given ascent (difficulty),
        /// reproducing what clicking "Start" on the boarding pass does
        /// Assumes we are at the Airport and are the host
        /// </summary>
        public static bool StartRun(int ascent, ManualLogSource log)
        {
            try
            {
                if (!InAirport)
                {
                    log.LogError($"StartRun called while not in Airport (scene='{ActiveSceneName}').");
                    return false;
                }

                var kiosk = UnityEngine.Object.FindObjectOfType<AirportCheckInKiosk>();
                if (kiosk == null)
                {
                    log.LogError("StartRun: no AirportCheckInKiosk found in the Airport scene.");
                    return false;
                }

                if (IsLoading)
                {
                    // kiosk.StartGame() checks this internally and would silently do nothing
                    log.LogError("StartRun: a loading screen is still active; StartGame would no-op. Aborting.");
                    return false;
                }

                log.LogInfo($"StartRun: kiosk.StartGame(ascent={ascent}).");
                kiosk.StartGame(ascent);
                return true;
            }
            catch (Exception e)
            {
                log.LogError($"StartRun failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Miscellaneous QoL: open the gate-kiosk (boarding pass) UI directly, without
        /// walking up to it and interacting. Mirrors exactly what
        /// <c>AirportCheckInKiosk.Interact_CastFinished</c> does. Assumes we are at the Airport
        /// </summary>
        public static bool OpenGateKiosk(ManualLogSource log)
        {
            try
            {
                if (!InAirport)
                {
                    log.LogError($"OpenGateKiosk called while not in Airport (scene='{ActiveSceneName}').");
                    return false;
                }
                if (IsLoading)
                {
                    log.LogError("OpenGateKiosk: a loading screen is still active. Aborting.");
                    return false;
                }

                var kiosk = UnityEngine.Object.FindObjectOfType<AirportCheckInKiosk>();
                if (kiosk == null)
                {
                    log.LogError("OpenGateKiosk: no AirportCheckInKiosk found in the Airport scene.");
                    return false;
                }
                if (GUIManager.instance == null || GUIManager.instance.boardingPass == null)
                {
                    log.LogError("OpenGateKiosk: GUIManager.instance.boardingPass is null.");
                    return false;
                }

                var boardingPass = GUIManager.instance.boardingPass;
                _menuWindowOpen.Invoke(boardingPass, null);
                boardingPass.kiosk = kiosk;
                return true;
            }
            catch (Exception e)
            {
                log.LogError($"OpenGateKiosk failed: {e}");
                return false;
            }
        }
    }
}
