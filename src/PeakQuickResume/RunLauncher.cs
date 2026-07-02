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
        /// Force <c>RunSettings.IsCustomRun</c> to <paramref name="value"/> before we
        /// start a run. Required in BOTH directions: the checkpoint mod's
        /// <c>GetPlayerSaveFile</c> chooses the CustomRun file iff this is true, and the
        /// game does not reliably reset it at the Airport, so a stale value would make
        /// us resume the wrong save (custom vs normal). <c>kiosk.StartGame</c> serializes
        /// the current run settings (this flag included), so setting it here is enough
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
        /// Send EVERYONE back to the Airport.
        ///
        /// Uses <c>GameOverHandler.LoadAirport()</c>, the game's canonical synchronized
        /// return: it RPCs the master, which RPCs <c>BeginAirportLoadRPC</c> to *All*,
        /// the same reliable RPC-to-all pattern as the run-start. This is essential in
        /// coop: <c>EndScreen.ReturnToAirport()</c> (which we used before) only loads the
        /// Airport locally for the caller, so a client sitting on the endscreen got left
        /// behind. GameOverHandler brings every client along, and works offline too
        /// (Photon routes the RPCs locally). Fallbacks kept for safety
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

        // Mirrors EndScreen.ReturnToAirport() exactly: a *networked* Airport load via
        // LoadingScreenHandler. Networked (PhotonNetwork.LoadLevel under the hood) is
        // essential in coop, the host's load propagates to all clients. In offline
        // mode Photon runs it locally, so the same call works solo too
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

                log.LogInfo("LoadAirportDirect: networked LoadingScreenHandler load of Airport.");
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
    }
}
