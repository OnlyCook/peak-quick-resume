using System;
using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Phase 8 milestone M3: our own literal port of <c>CustomJumpToSegment</c>
    /// (decompile 2263-2561), <c>TeleportToPosition</c>/<c>TeleportClientsToHost</c>
    /// (2629-2758), and <c>ReviveDeadPlayers</c> (2760-2779). Deliberately a
    /// near-verbatim copy (same steps, same wait cadence, same three
    /// <c>teleportJumpLogic</c> branches) rather than the "cleaner" direct
    /// <c>MapHandler.JumpToSegment</c>-only design - see ROADMAP.md Phase 8,
    /// "fidelity first" decision. Optimization is an explicit later pass
    ///
    /// PEAKapalooza's branches are NOT ported (maintainer decision, see ROADMAP.md).
    /// Inventory/afflictions restore (the original's <c>LoadInventoryDelayed</c>) is
    /// NOT ported yet - that's M4/M5; this milestone stubs that hand-off with a log
    /// line only, exactly like <c>OwnLoadEntryPoints</c>'s M2 stub did for the whole
    /// sequence
    ///
    /// Known, deliberate differences from the original (documented, not silent):
    ///  - The checkpoint mod's own "Loading savegame..." UI caption
    ///    (<c>LoadingScreen(true)</c>) is not ported - purely cosmetic, and
    ///    Quick Resume's own <c>ResumeOrchestrator</c> already shows its own status
    ///    messages around the whole resume flow, so porting a second, redundant
    ///    caption would be dead weight
    ///  - The "else if (segment != 4 &amp;&amp; !configLoadLevelScene.Value) spawnPos.y += 8"
    ///    branch is not ported: that config gates whether the checkpoint mod's OWN
    ///    scene override is active, which has no equivalent toggle in our own flow
    ///    (see <see cref="MapBakerLevelOverridePatch"/> - we always force the saved-
    ///    island override on), so this branch can never trigger for us
    ///  - The solo unlit-campfire-after-jumpLogic-0 fix (previously
    ///    <see cref="CampfireRelightFix"/>, hooked off the checkpoint mod's own
    ///    "Save game loaded!" message) is folded in HERE directly, right after
    ///    segment activation - the actual root cause location - rather than at a
    ///    later message-timing hook we no longer have for our own path
    /// </summary>
    public class OwnTeleportSequence : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnLoadEntryPoints _entryPoints;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnLoadEntryPoints entryPoints)
        {
            _log = log;
            _cfg = cfg;
            _entryPoints = entryPoints;
        }

        public void Begin(OwnSaveData data) => StartCoroutine(RunSequence(data));

        private IEnumerator RunSequence(OwnSaveData data)
        {
            Segment finalSegment = data.segment;
            Vector3 savedPos = new Vector3(data.posX, data.posY, data.posZ);
            float waitTime = Mathf.Max(0f, _cfg.OwnJumpLogicWaitTime.Value);

            _log?.LogInfo($"OwnTeleportSequence: executing custom jump to: {finalSegment}");

            yield return new WaitForSeconds(waitTime);
            OwnFallDamageProtection.Activate(30f);

            yield return new WaitForSeconds(waitTime);
            ReviveDeadPlayers(savedPos + new Vector3(0f, 4f, 0f));

            yield return new WaitForSeconds(waitTime);
            TryCloseLingeringEndScreen();

            MapHandler mh = MapHandler.Instance;
            int index = (int)finalSegment;
            MapHandler.MapSegment targetSegment = mh != null && index >= 0 && index < mh.segments.Length ? mh.segments[index] : null;

            Vector3 spawnPos = savedPos;
            spawnPos.y += 5f;

            bool kilnWorkaround = _cfg.OwnTeleportTheKilnWorkaround.Value;
            if (kilnWorkaround && (int)finalSegment == 4)
            {
                spawnPos = new Vector3(-0.91186905f, 838.8689f, 1713.6833f);
                finalSegment = (Segment)3;
            }

            switch (_cfg.OwnTeleportJumpLogic.Value)
            {
                case 0: MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment); break;
                case 1: MapHandler.JumpToSegment(finalSegment); break;
                case 2:
                    if (mh != null) mh.GoToSegment(finalSegment);
                    else MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment);
                    break;
                default: MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment); break;
            }

            // Solo-only relight fix, folded in here - see class remarks
            if (PhotonNetwork.OfflineMode && _cfg.OwnTeleportJumpLogic.Value == 0)
            {
                Campfire previousCampfire = MapHandler.PreviousCampfire;
                if (previousCampfire != null && !previousCampfire.Lit)
                    previousCampfire.LightWithoutReveal();
            }

            if (RunLauncher.IsHost && _entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyLeftoverHeldItems(_log);

            if ((int)finalSegment == 5) index--;
            else if (kilnWorkaround && (int)finalSegment == 4) index--;

            yield return new WaitForSeconds(waitTime);
            OwnWorldLootReset.ResetWorldLoot(_log);

            if (RunLauncher.IsHost)
            {
                if (_entryPoints.LoadedSaveFileThisRound && targetSegment != null)
                {
                    try
                    {
                        foreach (ISpawner spawner in targetSegment.segmentParent.GetComponentsInChildren<ISpawner>())
                            spawner.TrySpawnItems();
                    }
                    catch (Exception e)
                    {
                        _log?.LogError($"OwnTeleportSequence: TrySpawnItems failed: {e}");
                    }
                }
                yield return new WaitForSeconds(waitTime);
            }

            if (_entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyStaleWorldObjects(_log);

            bool isFoggedSegment = (int)finalSegment >= 0 && (int)finalSegment <= 4;
            if (isFoggedSegment)
            {
                yield return new WaitForSeconds(waitTime);
                StartCoroutine(OwnEnvironmentReset.ResetFogAfterLoad(index, finalSegment, _log));
            }

            if ((int)finalSegment == 2)
            {
                foreach (Tornado tornado in UnityEngine.Object.FindObjectsByType<Tornado>(FindObjectsSortMode.None))
                {
                    if (tornado != null && tornado.name.Contains("Clone"))
                    {
                        try { UnityEngine.Object.Destroy(tornado.gameObject); }
                        catch { /* matches the original's own swallow */ }
                    }
                }
            }

            if ((int)finalSegment == 4 && _entryPoints.LoadedSaveFileThisRound)
            {
                OwnEnvironmentReset.ResetLavaAfterLoad(_log);
                yield return new WaitForSeconds(0.5f);
            }

            yield return new WaitForSeconds(waitTime);

            if (RunLauncher.IsHost)
            {
                yield return StartCoroutine(TeleportToPosition(spawnPos));
                if ((int)finalSegment == 4 && Ascents.currentAscent < 4)
                    StartCoroutine(OwnEnvironmentReset.SpawnFlaresAtPeak());
            }

            if (_entryPoints.LoadedSaveFileThisRound && _cfg.OwnCampfireReset.Value)
                yield return StartCoroutine(OwnEnvironmentReset.ResetCampfire());

            if (RunLauncher.IsHost && _cfg.OwnDaytime.Value)
            {
                if (data.timeOfDay != 0f)
                {
                    DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                    dayNight?.setTimeOfDay(data.timeOfDay);
                }

                // M4/M5 stub - real inventory/afflictions restore not ported yet
                _log?.LogInfo("OwnTeleportSequence (STUB, Phase 8 M3): would now restore "
                    + "inventory/afflictions here (see M4/M5 in ROADMAP.md Phase 8).");
            }

            if (isFoggedSegment)
                StartCoroutine(OwnEnvironmentReset.ResetFogAfterLoad(index, finalSegment, _log, extendedTime: true));

            _entryPoints.MarkLoadedThisRound();
        }

        /// <summary>Mirrors ReviveDeadPlayers exactly (decompile 2760-2779)</summary>
        private static void ReviveDeadPlayers(Vector3 pos)
        {
            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character character = player.character;
                if (character == null) continue;
                if (!character.data.dead && !character.data.passedOut && !character.data.fullyPassedOut) continue;

                character.data.dead = false;
                character.data.deathTimer = 0f;
                character.data.passedOut = false;
                character.data.fullyPassedOut = false;
                character.data.sinceGrounded = 0f;
                character.refs.afflictions.ClearAllStatus(true);
                character.refs.afflictions.RemoveAllThorns();
                character.refs.afflictions.ClearAllAfflictions();
                character.data.fallSeconds = 0f;
            }
        }

        // Mirrors the EndScreen-closing try/catch inlined in CustomJumpToSegment
        // (decompile 2285-2301). MenuWindow.Close is not public (MenuWindow.Open
        // isn't either - see RunLauncher's own reflection use for the same reason)
        private void TryCloseLingeringEndScreen()
        {
            try
            {
                EndScreen endScreen = UnityEngine.Object.FindFirstObjectByType<EndScreen>();
                if (endScreen != null && endScreen.isOpen)
                    AccessTools.Method(typeof(MenuWindow), "Close")?.Invoke(endScreen, null);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: closing a lingering EndScreen failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>Mirrors TeleportToPosition exactly (decompile 2629-2691)</summary>
        private IEnumerator TeleportToPosition(Vector3 pos)
        {
            if (Character.localCharacter == null) yield break;

            Vector3 warpPos = pos + new Vector3(0f, 0.5f, 0f);
            Character.localCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.MasterClient, warpPos, false);

            float startTime = Time.time;
            int tried = 0;
            int framesToWait = Mathf.Max(1, _cfg.OwnTeleportFramesToWait.Value);

            while (Time.time - startTime < 30f && Character.localCharacter != null)
            {
                if (Mathf.Abs(Character.localCharacter.Head.y - warpPos.y) > 3f)
                {
                    try
                    {
                        Character.localCharacter.photonView?.RPC("WarpPlayerRPC", RpcTarget.MasterClient, warpPos, false);
                        _log?.LogInfo($"OwnTeleportSequence: warped {Character.localCharacter.player.name} to {warpPos} "
                            + $"(previous position: {Character.localCharacter.Head}).");
                    }
                    catch (Exception e)
                    {
                        _log?.LogWarning($"OwnTeleportSequence: TeleportToPosition warp failed: {e}");
                    }

                    tried++;
                    if (tried > 150) break;
                }
                else if (Mathf.Abs(Character.localCharacter.Head.x - warpPos.x) < 6f
                    && Mathf.Abs(Character.localCharacter.Head.z - warpPos.z) < 6f)
                {
                    _log?.LogInfo($"OwnTeleportSequence: warped {Character.localCharacter.player.name} after {tried} attempts.");
                    yield return new WaitForSeconds(0.5f);
                    if (!PhotonNetwork.OfflineMode) StartCoroutine(TeleportClientsToHost(warpPos));
                    break;
                }

                for (int i = 0; i < framesToWait; i++) yield return null;
            }
        }

        /// <summary>
        /// Mirrors TeleportClientsToHost exactly (decompile 2693-2758). Coop-only in
        /// practice (guarded by <c>!PhotonNetwork.OfflineMode</c> at its only call
        /// site above) - ported now for fidelity/completeness even though M3 only
        /// wires the SOLO path live; M7 is what actually exercises this
        /// </summary>
        private IEnumerator TeleportClientsToHost(Vector3 hostPos)
        {
            int framesToWait = Mathf.Max(1, _cfg.OwnTeleportFramesToWait.Value);
            for (int i = 0; i < framesToWait; i++) yield return null;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character ch = player.character;
                if (ch == null || ch == Character.localCharacter) continue;

                ch.photonView.RPC("WarpPlayerRPC", RpcTarget.All, hostPos, false);
                float startTime = Time.time;
                int tried = 0;

                while (Time.time - startTime < 30f)
                {
                    if (Mathf.Abs(ch.Head.y - hostPos.y) > 2f)
                    {
                        try
                        {
                            ch.photonView?.RPC("WarpPlayerRPC", RpcTarget.All, hostPos, false);
                            _log?.LogInfo($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} to {hostPos} "
                                + $"(previous position: {ch.Head}).");
                        }
                        catch (Exception e)
                        {
                            _log?.LogWarning($"OwnTeleportSequence.TeleportClientsToHost failed: {e}");
                        }

                        tried++;
                        if (tried > 150) break;
                    }
                    else if (Mathf.Abs(ch.Head.x - hostPos.x) < 6f && Mathf.Abs(ch.Head.z - hostPos.z) < 6f)
                    {
                        _log?.LogInfo($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} after {tried} attempts.");
                        break;
                    }

                    for (int j = 0; j < framesToWait; j++) yield return null;
                }
            }
        }
    }
}
