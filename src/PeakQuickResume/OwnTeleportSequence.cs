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
    /// As of M4, inventory/backpack restore (<see cref="OwnInventoryRestore"/>) is
    /// wired in as a fire-and-forget coroutine, mirroring the original starting
    /// <c>LoadInventoryDelayed</c> without yielding on it (decompile line 2553).
    /// Afflictions/skeleton/stamina/time-sync/message/hero-title/one-time-load-delete
    /// (the REST of <c>LoadInventoryDelayed</c>) are still not ported - that's M5
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
        private OwnWakeUpEffect _wakeUpEffect;
        private OwnLoadingScreen _loadingScreen;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnLoadEntryPoints entryPoints,
            OwnWakeUpEffect wakeUpEffect = null, OwnLoadingScreen loadingScreen = null)
        {
            _log = log;
            _cfg = cfg;
            _entryPoints = entryPoints;
            _wakeUpEffect = wakeUpEffect;
            _loadingScreen = loadingScreen;
        }

        /// <summary>
        /// True for the whole duration of a Begin()-triggered sequence, including the wake-up +
        /// loading-screen presentation at the end. <c>Begin</c>/<c>TryLoadPlayer</c> are
        /// fire-and-forget (they start the coroutine and return immediately), so
        /// <see cref="ResumeOrchestrator"/> polls this (via <c>OwnLoadEntryPoints.TeleportInProgress</c>)
        /// to know when it's actually safe to show the "Save loaded" message - showing it
        /// immediately after Begin() returns would print it well before the player has even seen
        /// the loading screen appear
        /// </summary>
        public bool IsRunning { get; private set; }

        public void Begin(OwnSaveData data, SaveTarget target, bool offline) => StartCoroutine(RunSequenceWrapper(data, target, offline));

        private IEnumerator RunSequenceWrapper(OwnSaveData data, SaveTarget target, bool offline)
        {
            IsRunning = true;
            yield return RunSequence(data, target, offline);
            IsRunning = false;
        }

        private IEnumerator RunSequence(OwnSaveData data, SaveTarget target, bool offline)
        {
            Segment finalSegment = data.segment;
            Vector3 savedPos = new Vector3(data.posX, data.posY, data.posZ);
            float waitTime = Mathf.Max(0f, _cfg.OwnJumpLogicWaitTime.Value);

            _log?.LogInfo($"OwnTeleportSequence: executing custom jump to: {finalSegment}");

            // Session-requested polish: crossfade into the game's own real "LOADING..." screen
            // before any of the teleport work below (which is otherwise unchanged) runs, so it's
            // all hidden behind the loading screen instead of happening in full view; once it's
            // all done, collapse the player into the passed-out pose, reveal them already lying
            // down as the loading screen clears, then let them visibly stand back up (see the
            // matching block at the end of this method). Config-gated and fully null-safe (either
            // component being unavailable just skips straight to today's plain instant-teleport
            // behaviour). Also mirrored onto every OTHER connected player (see
            // OwnNetwork.ClientPresentationOthers) - fire-and-forget, same style as the existing
            // LoadingScreenOthers(true) call just below
            //
            // IMPORTANT: the wake-up collapse must NOT happen up here, before ReviveDeadPlayers
            // (a few lines down) - ReviveDeadPlayers unconditionally clears passedOut/fullyPassedOut
            // (plus afflictions) for ANY character it finds flagged that way, including our own
            // fake collapse, undoing it within a couple of seconds and long before the intended
            // reveal. Collapsing here made the beat impossible to see during a real resume - moving
            // the collapse to after everything else runs (right before FadeOut) sidesteps this entirely
            bool wakeUpEnabled = _cfg.OwnWakeUpAnimationEnabled.Value;
            // Debug-only escape hatch: skips just the loading-screen overlay (FadeIn/FadeOut)
            // while leaving the wake-up beat and every other Wake-Up timing setting untouched -
            // useful for watching what's happening underneath without the screen hiding it
            bool showLoadingScreen = wakeUpEnabled && !_cfg.DebugDisableLoadingScreen.Value;

            // Small delay before starting the crossfade in: without it, our own loading screen
            // can start covering things up right as the game's own level-load screen is still
            // finishing its own clear, cutting it off a beat too early (session-reported)
            if (wakeUpEnabled)
                yield return new WaitForSeconds(Mathf.Max(0f, _cfg.OwnLoadingScreenFadeInDelay.Value));
            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(true);
            if (showLoadingScreen && _loadingScreen != null)
                yield return _loadingScreen.FadeIn(_cfg.OwnLoadingScreenFadeTime.Value);

            // Mirrors decompile 2271-2274 (LoadingScreen(true) + RPC_Loadingscreen to
            // Others): repurposed here (see OwnNetwork's class remarks) to arm
            // TeleportWatchdog's load window on every machine, not to show a caption -
            // BeginLoadWindow() is a direct local call since RpcTarget.Others never
            // reaches the sender itself
            _entryPoints.Network?.Watchdog?.BeginLoadWindow();
            _entryPoints.Network?.LoadingScreenOthers(true);

            yield return new WaitForSeconds(waitTime);
            // Mirrors decompile line 2280: RpcTarget.All, so this also arms fall-damage
            // protection on the host's own machine (no separate local call needed)
            _entryPoints.Network?.RequestFalldamageProtectionAll(30);

            yield return new WaitForSeconds(waitTime);
            ReviveDeadPlayers(savedPos + new Vector3(0f, 4f, 0f));

            yield return new WaitForSeconds(waitTime);
            TryCloseLingeringEndScreen();

            MapHandler mh = MapHandler.Instance;
            int index = (int)finalSegment;
            MapHandler.MapSegment targetSegment = mh != null && index >= 0 && index < mh.segments.Length ? mh.segments[index] : null;

            Vector3 spawnPos = savedPos;
            spawnPos.y += 5f;

            // Hardcoded by connection mode, NOT configurable (session 15 fix, first real
            // deviation from a literal port - see ROADMAP.md Phase 8 M7 follow-up):
            // MapHandler.SetSegmentOnSpawn (the checkpoint mod's own default, "jump logic
            // 0") hardcodes playersToTeleport to the CALLER'S OWN seat only and never
            // sends anything over the network (docs/RESEARCH.md), so it's correct for
            // solo (the only player) but leaves every coop CLIENT stuck in the old
            // segment - the host teleports fine, but clients never get told to activate
            // the new segment/biome at all. MapHandler.JumpToSegment ("jump logic 1") is
            // the one that actually RPCs every player's position AND syncs the segment/
            // biome activation to every client (docs/RESEARCH.md), so that's the one
            // coop needs. Solo keeps using the simpler SetSegmentOnSpawn path since it's
            // already proven solid across M3-M6 and has no client to leave behind
            if (offline) MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment);
            else MapHandler.JumpToSegment(finalSegment);

            // Solo-only relight fix, folded in here - see class remarks
            if (offline)
            {
                Campfire previousCampfire = MapHandler.PreviousCampfire;
                if (previousCampfire != null && !previousCampfire.Lit)
                    previousCampfire.LightWithoutReveal();
            }

            if (RunLauncher.IsHost && _entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyLeftoverHeldItems(_log);

            if ((int)finalSegment == 5) index--;

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

                // Mirrors the original starting LoadInventoryDelayed here (decompile line
                // 2553) - fire-and-forget, NOT yielded on, matching exactly. M4 only:
                // inventory/backpack restore. Afflictions/skeleton/stamina/time-sync/
                // message/hero-title/one-time-load-delete are M5, not ported yet
                StartCoroutine(OwnInventoryRestore.RestoreAll(target, offline, _cfg, _entryPoints, _log));
            }

            if (isFoggedSegment)
                StartCoroutine(OwnEnvironmentReset.ResetFogAfterLoad(index, finalSegment, _log, extendedTime: true));

            // Everything above is unchanged/hidden behind the loading screen. NOW collapse into
            // the passed-out pose (safe here - well after ReviveDeadPlayers, nothing left in the
            // sequence resets passedOut/fullyPassedOut). Hold BEHIND the still-fully-opaque
            // loading screen a bit longer before fading out (see OwnWakeUpSettleHoldTime) - the
            // real teleport's small landing drop and the collapse itself both need a moment to
            // physically settle, and without this hold the fade-out reveals that motion still in
            // progress (session-reported: visible collapsing + a mid-air fall/landing shake right
            // after the screen cleared). Only once fully settled do we reveal the player already
            // lying at the new position and let them visibly stand back up
            if (wakeUpEnabled && _wakeUpEffect != null)
                _wakeUpEffect.Collapse();
            if (wakeUpEnabled)
                yield return new WaitForSeconds(Mathf.Max(0f, _cfg.OwnWakeUpSettleHoldTime.Value));
            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(false);
            if (showLoadingScreen && _loadingScreen != null)
                yield return _loadingScreen.FadeOut(_cfg.OwnLoadingScreenFadeTime.Value);
            if (wakeUpEnabled && _wakeUpEffect != null)
                yield return _wakeUpEffect.Wake(_cfg.OwnWakeUpStandTime.Value);

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
                {
                    AccessTools.Method(typeof(MenuWindow), "Close")?.Invoke(endScreen, null);
                    // Mirrors decompile line 2292: RpcTarget.Others, only sent when we
                    // actually found and closed one locally
                    _entryPoints.Network?.CloseEndscreenOthers();
                }
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
