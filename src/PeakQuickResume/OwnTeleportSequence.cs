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
    ///  - The solo unlit-campfire-after-jumpLogic-0 fix is folded in HERE directly,
    ///    right after segment activation - the actual root cause location
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
        /// fire-and-forget (they start the coroutine and return immediately) - other code that
        /// needs to know the WHOLE sequence (including the cosmetic wake-up beat) has finished,
        /// not just the restore, polls this via <c>OwnLoadEntryPoints.TeleportInProgress</c>.
        /// See <see cref="RestoreComplete"/> for the one <see cref="ResumeOrchestrator"/> actually
        /// polls for its "Save loaded" message
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// True once inventory/backpack/afflictions/etc. have actually finished restoring for
        /// this Begin()-triggered sequence - set well before <see cref="IsRunning"/> goes false
        /// (that also waits out the purely-cosmetic fade-out/stand-up beat below). Session
        /// request: <see cref="ResumeOrchestrator"/> polls THIS (not <c>IsRunning</c>) to show
        /// the "Save loaded. Welcome back!" message right after the restore is actually done
        /// instead of waiting for the wake-up animation to finish playing too
        /// </summary>
        public bool RestoreComplete { get; private set; }

        public void Begin(OwnSaveData data, SaveTarget target, bool offline) => StartCoroutine(RunSequenceWrapper(data, target, offline));

        private IEnumerator RunSequenceWrapper(OwnSaveData data, SaveTarget target, bool offline)
        {
            IsRunning = true;
            RestoreComplete = false;
            // try/finally so IsRunning always resets even if RunSequence throws partway
            // through: ResumeOrchestrator polls TeleportInProgress (backed by IsRunning)
            // to know when to show its completion message, and a stuck-true flag would
            // leave it waiting out its whole StepTimeout on every subsequent resume.
            // RestoreComplete is force-set true here too (not just on the happy path further
            // down) for the same reason - a throw partway through must never leave it stuck
            // false, or ResumeOrchestrator's own wait on it would hang out its full timeout
            try
            {
                yield return RunSequence(data, target, offline);
            }
            finally
            {
                IsRunning = false;
                RestoreComplete = true;
            }
        }

        private IEnumerator RunSequence(OwnSaveData data, SaveTarget target, bool offline)
        {
            Segment finalSegment = data.segment;
            Vector3 savedPos = new Vector3(data.posX, data.posY, data.posZ);
            float waitTime = Mathf.Max(0f, _cfg.OwnJumpLogicWaitTime.Value);

            // Must run BEFORE any segment/position warp below - see
            // AchievementProgressIO's class remarks for why the ordering matters
            // (RUNBASEDVALUETYPE.MaxHeightReached has to already reflect this run's real
            // prior progress before the character's altitude jumps, or the teleport
            // itself gets miscounted as climbed height towards the High Altitude Badge)
            if (_cfg.RestoreAchievements.Value)
                AchievementProgressIO.RestoreAllPlayers(target, offline, _entryPoints, _log);

            // Inter-step wait between the map/campfire warp (JumpToSegment/SetSegmentOnSpawn,
            // below) and the final precise teleport. In solo there are no networked clients
            // to keep in sync across these steps, so the original's full waitTime-per-step
            // cadence is pure dead time the player watches AFTER the map has already visibly
            // loaded and warped them to the campfire - collapse it to a single frame (heavy
            // ops still don't all land in one frame), keeping exactly one real waitTime settle
            // right before the safety teleport. Co-op keeps the full cadence: there the segment
            // activation + warps are RPC'd to every client and the spacing gives slower clients
            // time to catch up before the host's precise teleport (see PluginConfig.OwnFastSoloTeleport)
            float stepWait = (offline && _cfg.OwnFastSoloTeleport.Value) ? 0f : waitTime;

            _log?.LogInfo($"OwnTeleportSequence: executing custom jump to: {finalSegment}"
                + (stepWait < waitTime ? " (fast solo cadence)" : ""));

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

            // Snap the saved time-of-day in RIGHT HERE, at the same moment the segment's own
            // day/night profile blend begins (JumpToSegmentLogic above calls BlendProfiles),
            // and long before the loading screen fades out. setTimeOfDay is an INSTANT snap
            // (it just assigns DayNightManager.timeOfDay; the sky/lighting is recomputed from
            // that value every frame), so the original's placement - applying it several
            // seconds later, right before the reveal - is exactly what made a night save
            // visibly flicker from the level's default bright morning to night just after the
            // screen cleared. Applied here it settles fully behind the still-opaque screen,
            // in step with the segment's own profile blend, so nothing changes on reveal.
            // DayNightManager is guaranteed present (BlendProfiles above just used it) and
            // this is a single field write, so there's no cost/overload concern moving it up.
            // Host-only, same guard as the original's own time restore (the inventory/
            // affliction restore + post-load cleanup stays later in RestoreAll, untouched)
            if (RunLauncher.IsHost && (_cfg.RestoreDaytime.Value || _cfg.RestoreDay.Value))
            {
                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                if (dayNight != null)
                {
                    if (_cfg.RestoreDaytime.Value && data.timeOfDay != 0f)
                        dayNight.setTimeOfDay(data.timeOfDay);

                    // dayCount has no vanilla RPC keeping it in sync (unlike timeOfDay's
                    // own periodic heartbeat) - apply locally then broadcast to clients
                    // ourselves, see OwnNetwork.SyncDayCountAll's remarks
                    if (_cfg.RestoreDay.Value && data.dayCount != 0)
                    {
                        dayNight.dayCount = data.dayCount;
                        _entryPoints.Network?.SyncDayCountAll(data.dayCount);
                    }
                }
            }

            if (RunLauncher.IsHost && _entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyLeftoverHeldItems(_log);

            if ((int)finalSegment == 5) index--;

            yield return new WaitForSeconds(stepWait);
            OwnWorldLootReset.ResetWorldLoot(_log);

            // World state (not per-player) - only restore it once, host-only. Right after
            // ResetWorldLoot so it can't be wiped out again - see AncientStatueRestore
            // for why this has to run here specifically.
            //
            // Deliberately NOT gated on _entryPoints.LoadedSaveFileThisRound like the
            // steps below (tried first, reverted - session-confirmed broken): that flag
            // means "this is a REPEAT load within the same run" (false on the very
            // first load after a fresh run start, see its own doc comment), which is
            // right for THOSE steps (they clean up duplicate state left over from an
            // earlier load THIS session) but wrong here - the statue needs restoring
            // from the save data on EVERY load, including the first one, since
            // ResetWorldLoot just unconditionally closed it a moment ago regardless of
            // which load this is
            // WorldItemRestore's own delete pass unconditionally clears every loose item
            // within range, so it has to run BEFORE AncientStatueRestore/LuggageRestore
            // place anything, or it would immediately destroy what they just restored -
            // see its own class remarks. Known limitation, not a correctness risk: on a
            // REPEAT load specifically, the spawner-retrigger loop further down (guarded
            // on LoadedSaveFileThisRound) can spawn a little more natural clutter in this
            // same area AFTER this cleanup already ran - accepted rather than risk moving
            // the statue/luggage restore call site, which is proven working as placed
            if (RunLauncher.IsHost)
            {
                WorldItemRestore.Restore(data, savedPos, _cfg, _log);
                if (_cfg.RestoreAncientStatue.Value) AncientStatueRestore.Restore(data, savedPos, _log);
                if (_cfg.RestoreLuggage.Value) LuggageRestore.Restore(data, savedPos, _log);
            }

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
                yield return new WaitForSeconds(stepWait);
            }

            if (_entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyStaleWorldObjects(_log);

            // Deployable restore (Portable Stove / Scout Cannon) - own addition, no
            // decompile counterpart (see DeployableRestore class remarks). Checkpoint
            // Flag was tried here too and reverted (session-confirmed broken in solo -
            // see OwnSaveData.portableStoves' remarks for why). MUST run after
            // DestroyStaleWorldObjects just above, not alongside the
            // AncientStatue/Luggage/WorldItem restore block earlier in this method:
            // that destroy pass matches these exact prefab names on every REPEAT load
            // and would immediately delete whatever was just restored here if this ran
            // any earlier. Same "every load, not just repeat loads" reasoning as the
            // earlier restore block otherwise applies - ResetWorldLoot doesn't touch
            // these objects at all (only DestroyStaleWorldObjects, repeat-load-only,
            // does), so there's no earlier "run every load" hazard to guard against here
            if (RunLauncher.IsHost && _cfg.RestoreDeployables.Value)
            {
                DeployableRestore.RestoreStoves(data, savedPos, _log);
                DeployableRestore.RestoreCannons(data, savedPos, _log);
            }

            bool isFoggedSegment = (int)finalSegment >= 0 && (int)finalSegment <= 4;
            if (isFoggedSegment)
            {
                yield return new WaitForSeconds(stepWait);
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

            // Deliberate DEVIATION from the original (decompile 2546-2553), not a port gap:
            // the original nests both the time-of-day restore AND LoadInventoryDelayed inside
            // `configDaytime.Value`, so turning the "restore time of day" setting off ALSO
            // silently disabled inventory/backpack/affliction restore AND skipped the post-load
            // cleanup at the tail of RestoreAll - which is the one and only thing that ends
            // TeleportWatchdog's load window (on every machine), arms the reload cooldown, and
            // clears CurrentlyLoading. Left coupled, disabling daytime would leave the watchdog's
            // load window stuck open forever (mitigation silently dead) and the load flag never
            // cleared. Split so each restore honours only its OWN toggle: time-of-day is applied
            // much earlier now (right after the segment jump above, gated on RestoreDaytime), and
            // RestoreAll always runs on the host regardless of the daytime setting
            //
            // Still started fire-and-forget (not yielded on directly) so it runs CONCURRENTLY
            // with the collapse below rather than blocking it outright - the hold just below
            // gates on inventoryRestoreDone directly, so the fade-out can never reveal the player
            // before their items have actually finished restoring
            bool inventoryRestoreDone = !RunLauncher.IsHost;
            if (RunLauncher.IsHost)
                StartCoroutine(RunInventoryRestoreAndSignal());

            IEnumerator RunInventoryRestoreAndSignal()
            {
                try
                {
                    yield return StartCoroutine(OwnInventoryRestore.RestoreAll(target, offline, _cfg, _entryPoints, _log));
                }
                finally
                {
                    inventoryRestoreDone = true;
                }
            }

            if (isFoggedSegment)
                StartCoroutine(OwnEnvironmentReset.ResetFogAfterLoad(index, finalSegment, _log, extendedTime: true));

            // Everything above is unchanged/hidden behind the loading screen. NOW collapse into
            // the passed-out pose (safe here - well after ReviveDeadPlayers, nothing left in the
            // sequence resets passedOut/fullyPassedOut) and hold BEHIND the still-fully-opaque
            // loading screen until inventoryRestoreDone, capped by a generous safety timeout so a
            // stuck/failed restore can never hang the sequence forever. Nothing else happens
            // during this hold - the player is already collapsed behind an opaque screen - so
            // items/backpacks/afflictions always end up fully in place BEFORE the fade-out ever
            // reveals the player, instead of racing it
            if (wakeUpEnabled && _wakeUpEffect != null)
                _wakeUpEffect.Collapse();
            if (wakeUpEnabled)
            {
                const float maxWaitForRestore = 10f;
                float elapsed = 0f;
                while (!inventoryRestoreDone && elapsed < maxWaitForRestore)
                {
                    // Re-stamps data.lastPassedOut every frame (see OwnWakeUpEffect's class
                    // remarks): without this, the vanilla "not really hurt" auto-revive failsafe
                    // force-clears passedOut back to false within a couple of frames of Collapse()
                    // - session-confirmed via logging that this was silently defeating the whole
                    // wake-up beat, independent of any of the timing below
                    _wakeUpEffect?.RefreshHold();
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
            }

            // Items/backpacks/afflictions are now confirmed in place - ResumeOrchestrator polls
            // this to show "Save loaded. Welcome back!" right here: after the restore, but
            // before the fade-out/stand-up below (matches the requested step order: load state,
            // fade out, welcome message, wake up)
            RestoreComplete = true;

            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(false);

            // A short extra pause BEHIND the still-opaque screen (loading-screen-fade-out-delay)
            // before the fade-out itself starts - purely cosmetic breathing room, requested so the
            // reveal doesn't feel like it's cutting straight from "everything just finished
            // loading" into the fade with zero beat in between. Still refreshes the hold each
            // frame, same as the restore-wait above
            if (wakeUpEnabled)
            {
                float delayElapsed = 0f;
                float fadeOutDelay = Mathf.Max(0f, _cfg.OwnLoadingScreenFadeOutDelay.Value);
                while (delayElapsed < fadeOutDelay)
                {
                    _wakeUpEffect?.RefreshHold();
                    yield return null;
                    delayElapsed += Time.unscaledDeltaTime;
                }
            }

            // Fade the loading screen out FIRST, fully revealing the player still collapsed at
            // the new position, THEN start the stand-up recovery - so the recovery plays out
            // entirely in full view once the screen is already gone, instead of racing (or being
            // raced by) the fade. See OwnWakeUpEffect's class remarks for why the recovery itself
            // is reliable now (the real bug was never this ordering - it was a vanilla failsafe
            // silently cancelling the collapse before any of this ever ran)
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

            // Hand the watchdog the real target up front (see TeleportWatchdog.SetKnownTarget):
            // the host also forwards this to clients when it ends the load window, so a client
            // that never receives a warp can still recover here instead of only being warned.
            // This is the same warpPos every client is sent below via TeleportClientsToHost
            _entryPoints.Network?.Watchdog?.SetKnownTarget(warpPos);

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
        /// Ports TeleportClientsToHost (decompile 2693-2758). Coop-only in practice
        /// (guarded by <c>!PhotonNetwork.OfflineMode</c> at its only call site above).
        ///
        /// Deliberate DEVIATIONS from the original (documented, not silent), both fixing the
        /// same real co-op bug: a slow client being re-warped over and over, visibly glitching
        /// up/down ON THE HOST'S SCREEN ONLY (the client owns its own position and never
        /// actually moved - the glitch is the host's own local copy of the client being yanked
        /// to the target by <c>WarpPlayerRPC(RpcTarget.All)</c> and then corrected back down by
        /// the client's authoritative position stream):
        ///  1. ROOT CAUSE - the original decides "has the client arrived?" by VERTICAL proximity
        ///     (<c>|Δy| &gt; 2</c> -&gt; re-warp). But <c>hostPos</c> is a few metres up in the
        ///     air (saved pos + spawn lift), so a warped client immediately falls to the ground
        ///     and legitimately rests several metres BELOW it - the y-check therefore never reads
        ///     as arrived and re-warps forever. We judge arrival HORIZONTALLY instead (the client
        ///     lands at the right x/z and stays there); depth is left to the client's own watchdog
        ///  2. The original re-warps up to 150 times spaced only <c>framesToWait</c> frames apart,
        ///     far shorter than a round trip. We bound the re-sends
        ///     (<see cref="PluginConfig.OwnMaxClientWarpResends"/>) and pace them by a real grace
        ///     interval (<see cref="PluginConfig.OwnClientWarpResendGraceSeconds"/>), then hand off
        ///     to the client's own teleport watchdog / position recovery rather than firing endlessly
        /// </summary>
        private IEnumerator TeleportClientsToHost(Vector3 hostPos)
        {
            int framesToWait = Mathf.Max(1, _cfg.OwnTeleportFramesToWait.Value);

            // Anti-spam bounds (see PluginConfig): the ORIGINAL re-warps a client up to 150
            // times, gated only on our own network-lagged view of that client's position and
            // spaced just framesToWait frames (~0.5s) apart - far shorter than a round trip.
            // On a slow client the teleport we already sent can't report back before the next
            // check, so the host keeps firing redundant safety warps at it (the known client
            // warp-spam bug). We cap the total re-sends AND require a real grace interval to
            // pass since our last send, so the first warp gets a full round trip to land and
            // propagate back before we ever decide it "didn't work" and send another
            int maxResends = Mathf.Max(0, _cfg.OwnMaxClientWarpResends.Value);
            float resendGrace = Mathf.Max(0f, _cfg.OwnClientWarpResendGraceSeconds.Value);

            for (int i = 0; i < framesToWait; i++) yield return null;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character ch = player.character;
                if (ch == null || ch == Character.localCharacter) continue;

                ch.photonView.RPC("WarpPlayerRPC", RpcTarget.All, hostPos, false);
                float startTime = Time.time;
                float lastSend = Time.time;
                int tried = 0;

                while (Time.time - startTime < 30f)
                {
                    // Judge arrival HORIZONTALLY, not by height. hostPos is a few metres up in
                    // the AIR (the caller's saved pos + a spawn lift), so every warped client
                    // immediately falls to the ground and legitimately rests several metres
                    // BELOW hostPos.y and stays there. The original judged arrival by y-proximity
                    // (|Δy| > 2 -> "not there yet, re-warp"), which therefore NEVER reads as
                    // arrived for a client - it re-warps forever, and because the host also runs
                    // that WarpPlayerRPC on its OWN local copy of the client, the client's body
                    // visibly glitches up/down on the HOST's screen (only there - the client owns
                    // its position and never actually moved). Root cause of the exact bug seen in
                    // co-op on a slow client (see class remarks). Horizontal distance is the
                    // reliable "did the warp land" signal; depth (a real fall-through) is left to
                    // the client's own teleport watchdog / position recovery, not fought from here
                    if (Mathf.Abs(ch.Head.x - hostPos.x) < 6f && Mathf.Abs(ch.Head.z - hostPos.z) < 6f)
                    {
                        _log?.LogInfo($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} after {tried} attempts.");
                        break;
                    }

                    // Still horizontally off target -> the warp hasn't landed (client not there
                    // yet, or genuinely never teleported). Re-send, but hold off until the grace
                    // interval has elapsed since our last warp: without this, a slow client whose
                    // teleport is still in flight (or whose resulting move hasn't propagated back
                    // to us yet) gets hammered with warps it has already acted on
                    if (Time.time - lastSend >= resendGrace)
                    {
                        if (tried >= maxResends)
                        {
                            _log?.LogWarning($"OwnTeleportSequence.TeleportClientsToHost: still don't see {ch.player.name} "
                                + $"near {hostPos} after {tried} re-warp(s); giving up (client's own watchdog/position "
                                + "recovery will handle it) rather than spamming further warps.");
                            break;
                        }

                        try
                        {
                            ch.photonView?.RPC("WarpPlayerRPC", RpcTarget.All, hostPos, false);
                            _log?.LogInfo($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} to {hostPos} "
                                + $"(previous position: {ch.Head}, resend {tried + 1}/{maxResends}).");
                        }
                        catch (Exception e)
                        {
                            _log?.LogWarning($"OwnTeleportSequence.TeleportClientsToHost failed: {e}");
                        }

                        lastSend = Time.time;
                        tried++;
                    }

                    for (int j = 0; j < framesToWait; j++) yield return null;
                }
            }
        }
    }
}
