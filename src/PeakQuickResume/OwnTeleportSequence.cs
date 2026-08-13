using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;

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

        /// <summary>
        /// Character's revive <c>[PunRPC]</c>, named here rather than inline because
        /// PEAK 2.0.a renamed it from <c>RPCA_Revive</c> to <c>ReviveCharacter</c>. A
        /// string RPC name can't be checked by the compiler, so a rename like that fails
        /// silently at runtime (Photon just reports an unknown method and nobody gets
        /// revived) - see <see cref="ReviveDeadPlayers"/> for the full rationale
        /// </summary>
        private const string ReviveRpcName = "ReviveCharacter";

        /// <summary>
        /// Vanilla's atomic "revive this dead character AND place it here" <c>[PunRPC]</c>,
        /// <c>RPCA_ReviveAtPosition(Vector3 position, bool applyStatus, int statueSegment)</c>.
        /// This is the call the GAME itself makes for exactly our situation - see
        /// <c>CharacterSpawner.SpawnDeadAtBaseCamp</c>, which kills a late-joining player with
        /// <c>DeathOnArrival()</c> and then, one frame later, revives them with this when it is
        /// allowed to. See <see cref="ReviveDeadClientAtPosition"/> for why we now use it too
        /// </summary>
        private const string ReviveAtPositionRpcName = "RPCA_ReviveAtPosition";

        // Coop client-warp settle tracking (see TeleportClientsToHost's class remarks and
        // the wait block near the end of RunSequence): TeleportClientsToHost used to be
        // fired fully fire-and-forget, so the host's own "LOADING SAVE..." overlay could
        // fade out and vanish while clients were still being warped/confirmed in the
        // background - if a client then never arrived, the failure was only ever logged,
        // with nothing shown on screen (session-reported: overlay disappearing mid-connect
        // looked broken). _clientWarpSettled/_clientWarpAllArrived let RunSequence hold the
        // overlay up until that background work has actually finished (or given up), and
        // decide afterward whether to surface an error - see their use below
        private bool _clientWarpSettled = true;
        private bool _clientWarpAllArrived = true;

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

        /// <summary>
        /// Starts the sequence. <paramref name="data"/> is the level/world half, already
        /// read from <paramref name="selection"/>'s HOST file - everything this method
        /// reads out of it (segment, position, time of day, day count, ground items,
        /// luggage, statue, deployables) is level state and comes from that one file. The
        /// selection is carried alongside purely so the per-player restore steps at the
        /// tail can look up each player's OWN file, see <see cref="SaveSelection"/>
        /// </summary>
        public void Begin(OwnSaveData data, SaveSelection selection) => StartCoroutine(RunSequenceWrapper(data, selection));

        private IEnumerator RunSequenceWrapper(OwnSaveData data, SaveSelection selection)
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
            // Brackets the teleport itself, independently of ResumeOrchestrator's own
            // (wider) window - this sequence also runs for paths that never went through a
            // resume, and the try/finally guarantees the pause is lifted even on a throw.
            // Nested windows are reference counted, see HeightAchievementGuard
            HeightAchievementGuard.Suppress("teleport sequence");
            try
            {
                yield return RunSequence(data, selection);
            }
            finally
            {
                IsRunning = false;
                RestoreComplete = true;
                HeightAchievementGuard.Release("teleport sequence");
            }
        }

        private IEnumerator RunSequence(OwnSaveData data, SaveSelection selection)
        {
            bool offline = selection.Offline;

            // Reset per-run (this MonoBehaviour is reused across every Begin() call) -
            // see the fields' own remarks
            _clientWarpSettled = true;
            _clientWarpAllArrived = true;

            Segment finalSegment = data.segment;
            Vector3 savedPos = new Vector3(data.posX, data.posY, data.posZ);
            float waitTime = Mathf.Max(0f, _cfg.OwnJumpLogicWaitTime.Value);

            // Recovery path for a checkpoint whose world state was anchored in the off-map
            // death zone because the HOST was dead when it was written (see
            // OwnSaveCapture.ResolveWorldAnchor, which stops new saves being written that way).
            // Loading one as-is warped everyone to (0, 5000, -5000), 5km off the map with no
            // terrain anywhere near - the reported "the map won't load in". Existing saves
            // can't be un-written, so detect the marker position and retarget to the campfire
            // this checkpoint actually belongs to, which is where it was taken. Resolved after
            // the segment jump below (PreviousCampfire only means anything once the saved
            // segment is active), so only the flag is worked out here
            bool deathZoneSave = IsDeathZonePosition(savedPos);
            if (deathZoneSave)
                _log?.LogWarning($"OwnTeleportSequence: this checkpoint's saved position {savedPos} is the game's "
                    + "off-map death zone - it was written while the host was dead by a build that still anchored "
                    + "world state on them. Retargeting to the saved segment's own campfire after the jump.");

            // Must run BEFORE any segment/position warp below - see
            // AchievementProgressIO's class remarks for why the ordering matters
            // (RUNBASEDVALUETYPE.MaxHeightReached has to already reflect this run's real
            // prior progress before the character's altitude jumps, or the teleport
            // itself gets miscounted as climbed height towards the High Altitude Badge)
            if (_cfg.RestoreAchievements.Value)
                AchievementProgressIO.RestoreAllPlayers(selection, _entryPoints, _log);

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

            // Do NOT advance the segment while a client is still spawning into the level.
            //
            // This is the measured trigger for the co-op ragdoll-thrash bug. Advancing the
            // segment is what makes the run look "already in progress", and CharacterSpawner
            // kills any player who finishes spawning after that point (SpawnDeadAtBaseCamp ->
            // DeathOnArrival -> KillImmediately -> RPCA_Die), parking their body 7km away at
            // DeathPos - which is the state we then have to revive and warp back from. Across
            // 11 logged resumes the bug appeared in exactly the one attempt where the client
            // registered AFTER this jump and got killed, and in none of the ten where it
            // registered before. That is also why pressing the resume key earlier or later
            // never changed anything: the race is between the CLIENT's spawn and this line,
            // not between the player's keypress and anything
            //
            // Vanilla never creates this situation - a run reaches Tropics because players
            // walked there, minutes after everyone spawned. We manufacture it about a second
            // after the level loads, which is exactly when a client is most likely to still be
            // registering. Waiting for every connected player to actually have a Character
            // closes the window, and deliberately does so by observing PlayerHandler on the
            // HOST rather than via our own ready RPC - that RPC only exists on machines running
            // this mod, and the bug reproduces with a completely unmodded client
            if (!offline) yield return WaitForEveryPlayerToRegister();

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

            // Coop only: bring MapHandler.LastRevivedSegment in line with the segment we just
            // jumped to. The solo branch above already does this - it is the second argument to
            // SetSegmentOnSpawn - but JumpToSegment has no equivalent parameter, so coop was
            // left with whatever value a brand-new run initialised it to
            if (!offline) SyncLastRevivedSegment(finalSegment);

            // Solo-only relight fix, folded in here - see class remarks
            if (offline)
            {
                Campfire previousCampfire = MapHandler.PreviousCampfire;
                if (previousCampfire != null && !previousCampfire.Lit)
                    previousCampfire.LightWithoutReveal();
            }

            // Death-zone recovery, resolved now that the saved segment is active: PreviousCampfire
            // is the campfire you light to reach this segment, i.e. exactly the checkpoint this save
            // was taken at. Everything downstream (the world-item/luggage/statue restore below, the
            // teleport target, the watchdog's known target) reads savedPos/spawnPos, so correcting
            // both here is enough. See the flag's own remarks at the top of this method
            if (deathZoneSave)
            {
                Campfire checkpointCampfire = MapHandler.PreviousCampfire;
                if (checkpointCampfire != null)
                {
                    savedPos = checkpointCampfire.transform.position;
                    spawnPos = savedPos;
                    spawnPos.y += 5f;
                    _log?.LogWarning($"OwnTeleportSequence: death-zone checkpoint retargeted to the "
                        + $"{finalSegment} campfire at {savedPos}.");
                }
                else
                {
                    _log?.LogError("OwnTeleportSequence: this checkpoint's position is the off-map death zone and no "
                        + "campfire could be found for the saved segment - loading it anyway, but expect to land off "
                        + "the map. Re-save from a fresh checkpoint to replace it.");
                }
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

            // Decided HERE, before the per-player restore starts, not at the end of the
            // sequence: these players are being restored as a corpse, so there is nothing
            // worth putting back onto them (RestoreAll skips them outright) and the death
            // itself is applied further down, still behind the fully-opaque loading screen.
            // Empty set whenever there's nothing to do - offline, or the setting is off
            HashSet<string> restoringAsDead = _cfg.RestoreDeathState.Value
                ? DeathStateRestore.ResolveSavedDeadUserIds(selection, _log)
                : new HashSet<string>();

            bool inventoryRestoreDone = !RunLauncher.IsHost;
            if (RunLauncher.IsHost)
            {
                // Breadcrumb: pairs with "restore sequence complete". If this line appears
                // without that one, the restore itself is where a stalled load died
                _log?.LogInfo("OwnTeleportSequence: starting the inventory/world restore.");
                StartCoroutine(RunInventoryRestoreAndSignal());
            }

            IEnumerator RunInventoryRestoreAndSignal()
            {
                try
                {
                    yield return StartCoroutine(OwnInventoryRestore.RestoreAll(selection, _cfg, _entryPoints, _log, restoringAsDead));
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
                // TeleportClientsToHost bounds itself (30s hard timeout, on top of its own
                // resend budget), so this just has to comfortably outlast that - see
                // _clientWarpSettled's remarks. Session-reported bug this fixes: the overlay
                // used to fade out (and the sequence would finish) the instant
                // inventoryRestoreDone flipped, even while clients were STILL being warped/
                // confirmed in the background - so "waiting for clients to connect" visibly
                // hid our own "LOADING SAVE..." screen well before that background work was
                // actually done
                const float maxWaitForClientWarp = 32f;
                float elapsed = 0f;
                while ((!inventoryRestoreDone && elapsed < maxWaitForRestore)
                    || (!_clientWarpSettled && elapsed < maxWaitForClientWarp))
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

            // Everyone was revived near the top of this sequence (see ReviveDeadPlayers) so the
            // segment jump and the warps all ran on living characters. NOW - with the teleport
            // and the per-player restore settled, but while the loading screen is still fully
            // opaque on every machine (the fade-out below, and each client's own via
            // ClientPresentationOthers(false) just after, are both still ahead of us) - put
            // whoever the checkpoint recorded as dead back into that state. Session-reported:
            // applying it at the very END of the sequence instead meant a restored-dead player
            // watched themselves load in, stand up, and only then visibly drop dead. They are
            // still warped to the campfire first, so their corpse/spectate position is right.
            // A player with no file in this save event (a friend who joined after it was
            // written) is never in this set - see DeathStateRestore
            if (RunLauncher.IsHost)
            {
                // Alive first, then the wanted deaths - order matters, or a player revived
                // here would immediately be re-killed and vice versa. This catches anyone
                // who died in the window after their warp landed (a fall mid-restore, a late
                // joined-late arrival kill); the warp step revives on its own behalf too,
                // because a dead character cannot be warped at all
                DeathStateRestore.EnsureUnsavedPlayersAlive(restoringAsDead, _log);
                DeathStateRestore.ApplySavedDeaths(restoringAsDead, _entryPoints, _log);
            }

            // Items/backpacks/afflictions are now confirmed in place - ResumeOrchestrator polls
            // this to show "Save loaded. Welcome back!" right here: after the restore, but
            // before the fade-out/stand-up below (matches the requested step order: load state,
            // fade out, welcome message, wake up)
            RestoreComplete = true;

            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(false);

            // Resuming into a Roots campfire with Fairoots installed: that mod starts its own
            // per-level work as the biome finishes loading, and revealing the world mid-burst
            // would drop the player straight into a freeze. Held here, behind the still-opaque
            // screen and BEFORE the cosmetic beat below, so that beat stays the last thing
            // before the reveal. Costs one bool per frame without Fairoots, or in any other
            // biome - see FairootsCompat. Refreshes the wake-up hold every frame for the same
            // reason the two waits above do
            yield return FairootsCompat.WaitUntilReady(_log, () => _wakeUpEffect?.RefreshHold());

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

            // Only surfaced AFTER the overlay above is confirmed fully gone (FadeOut has
            // already finished by this point) - deliberately not shown any earlier, so there's
            // no chance of it appearing while "LOADING SAVE..." is still up or mid-fade (see
            // _clientWarpAllArrived's remarks)
            if (wakeUpEnabled && !_clientWarpAllArrived)
                _entryPoints.Network?.MessageOverlay?.Show(MessagesLocalization.Get(MsgKey.PlayersTimedOut),
                    new Color(1f, 0.5f, 0.5f, 1f), 4f);

            _entryPoints.MarkLoadedThisRound();
        }

        /// <summary>
        /// True for a position at (or right next to) <c>Character.DeathPos()</c> - the fixed
        /// <c>(0, 5000, -5000)</c> spot vanilla drags every dead character's ragdoll to. No
        /// real gameplay position is remotely near it (the tallest island tops out well under
        /// 2000m and nothing legitimate sits 5km out on -Z), so this is an unambiguous marker
        /// that a save's world state was anchored on a dead player - see the call site
        /// </summary>
        private static bool IsDeathZonePosition(Vector3 pos)
        {
            const float toleranceSq = 50f * 50f;
            return (pos - new Vector3(0f, 5000f, -5000f)).sqrMagnitude < toleranceSq;
        }

        /// <summary>
        /// Mirrors ReviveDeadPlayers (decompile 2760-2779), plus one deliberate co-op
        /// DEVIATION (documented, not silent): the original - and our own port until now -
        /// revives by writing <c>character.data.dead/passedOut/fullyPassedOut</c> directly.
        /// That works for the machine that owns the character (i.e. solo, and the host's own
        /// character in co-op) but is invisible to a CLIENT: those fields only ever travel
        /// over the <c>RPCA_Die</c>/<c>RPCA_Revive</c>/<c>RPCA_SetDead</c> RPC family, never
        /// through the continuous character-sync stream. So the host revived its own local
        /// copy of a dead client and that client stayed a spectating ghost on their own
        /// screen - the exact session-reported co-op bug where a friend who joined the run
        /// after the host (and was therefore killed on arrival by the game's own
        /// <c>DeathOnArrival</c>) was still dead and spectating after a checkpoint load
        ///
        /// In co-op we therefore broadcast the game's own revive RPC on that character's
        /// view instead, which runs these exact same field writes (plus the affliction/
        /// thorn clears below) on EVERY machine, including the owning client.
        /// <c>false</c> = don't apply the post-revive Curse/Hunger status, matching what the
        /// original's direct writes did (i.e. nothing). Solo keeps the literal direct-write
        /// path untouched - there is no second machine to reach, and the local writes have
        /// been proven there since M3. The direct writes also stay as the fallback if the
        /// RPC itself throws
        ///
        /// PEAK 2.0.a RENAMED that RPC from <c>RPCA_Revive</c> to <c>ReviveCharacter</c>
        /// (same <c>[PunRPC]</c>, same lone <c>bool applyStatus</c> parameter, same body) -
        /// see <see cref="ReviveRpcName"/>. Deliberately NOT the similarly named
        /// <c>RPCA_ReviveAtPosition</c> that also exists in 2.0.a: that one additionally
        /// does <c>DropAllItems(includeBackpack: true)</c> and warps the character, both
        /// of which would fight the inventory restore and the warps this sequence runs
        /// itself
        ///
        /// Everyone flagged is revived here regardless of what the save says, so the segment
        /// jump and the warps below all run on living characters; whoever the checkpoint
        /// recorded as dead is put back that way later in the sequence, once the restore has
        /// settled but while the loading screen is still opaque - see
        /// <see cref="DeathStateRestore"/> and that call site's own remarks
        /// </summary>
        private void ReviveDeadPlayers(Vector3 pos)
        {
            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character character = player.character;
                if (character == null) continue;
                if (!character.data.dead && !character.data.passedOut && !character.data.fullyPassedOut) continue;

                if (!PhotonNetwork.OfflineMode && character.photonView != null)
                {
                    try
                    {
                        character.photonView.RPC(ReviveRpcName, RpcTarget.All, false);
                        _log.Trace($"OwnTeleportSequence.ReviveDeadPlayers: revived {character.characterName} (networked).");
                        continue;
                    }
                    catch (Exception e)
                    {
                        _log?.LogWarning($"OwnTeleportSequence.ReviveDeadPlayers: {ReviveRpcName} failed for "
                            + $"{character.characterName} ({e.Message}); falling back to a local-only revive.");
                    }
                }

                // Mirrors the body of the game's own revive (Character.ReviveCharacter,
                // called RPCA_Revive before 2.0.a) for the machine that owns this
                // character. Two of these lines track additions 2.0.a made to it:
                // the ragdoll collision re-enable, and the Petrify reduction. Note
                // ClearAllStatus(true) still matches vanilla's own parameterless call
                // exactly - 2.0.a's new second parameter (excludePetrify) defaults to
                // true, which is why the -0.75 nudge below is a separate step there too
                // rather than petrify simply being cleared outright
                try { character.refs.ragdoll.ToggleCollision(enableCollision: true); }
                catch { /* pre-2.0.a shape, or no ragdoll refs - not worth failing over */ }

                character.data.dead = false;
                character.data.deathTimer = 0f;
                character.data.passedOut = false;
                character.data.fullyPassedOut = false;
                character.data.sinceGrounded = 0f;
                character.refs.afflictions.ClearAllStatus(true);
                character.refs.afflictions.AdjustStatus(CharacterAfflictions.STATUSTYPE.Petrify, -0.75f);
                ThornsAndTicksRestore.ClearThornsSilently(character, _log);
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

            // Same precondition as the client warps below: a DEAD character cannot be warped
            // at all, because Character.FixedUpdate re-parks the body at Character.DeathPos() -
            // (0, 5000, -5000) - EVERY PHYSICS STEP:
            //     if (data.dead) { ragdoll.MoveAllRigsInDirection(DeathPos() - Center);
            //                      ragdoll.HaltBodyVelocity(); }
            // (2.0.a moved that from Update/HandleDeath into FixedUpdate; it is emphatically
            // still per-step, and it also zeroes velocity, so it does not merely undo a warp,
            // it fights it.) The loop below would then burn its full 30-second budget warping
            // into a position undone each step, and because RunSequence yields on this
            // coroutine, the ENTIRE restore never even starts: no items, no afflictions, and
            // the "LOADING SAVE..." overlay stays up on every machine.
            //
            // ReviveDeadPlayers runs much earlier in the sequence, so a host that died
            // after that point (or whose revive did not take) reaches here still dead with
            // nothing left to catch it. Cheap to re-check, and a no-op in the normal case
            if (ReviveBeforeWarp(Character.localCharacter))
            {
                // Same settle as the client warps below - see ReviveSettleSteps
                for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();
                if (Character.localCharacter == null) yield break;
            }

            Vector3 warpPos = pos + new Vector3(0f, 0.5f, 0f);

            // Hand the watchdog the real target up front (see TeleportWatchdog.SetKnownTarget):
            // the host also forwards this to clients when it ends the load window, so a client
            // that never receives a warp can still recover here instead of only being warned.
            // This is the same warpPos every client is sent below via TeleportClientsToHost
            _entryPoints.Network?.Watchdog?.SetKnownTarget(warpPos);

            // RpcTarget.All, NOT MasterClient. This method only ever runs on the host, and the
            // host IS the master client, so RpcTarget.MasterClient meant "run this on me and
            // nobody else" - the host's own body was the ONLY character in the whole run whose
            // warp never reached the other machines. Every client warp below already uses All,
            // and so does every warp vanilla itself sends (MapHandler's segment teleport,
            // RPCA_Die, RPCA_ReviveAtPosition)
            //
            // On every other machine the host's body was therefore never warped at all: it just
            // saw the streamed hip position jump, and CharacterSyncer.InterpolateRigPositions
            // had to drag the ragdoll across the gap itself, with collision enabled the whole
            // way. Character.WarpPlayer exists precisely to avoid that - it disables the
            // ragdoll's colliders for the move, halts body velocity, and re-enables them on
            // arrival - so routing around it means dragging a live ragdoll through the world and
            // potentially leaving it embedded in terrain, where depenetration impulses and the
            // syncer's own correction fight each other indefinitely
            //
            // That matches every measurement taken of the reported bug: it is always the HOST's
            // character that convulses, only ever on OTHER machines, while the host's own body
            // is fine and its streamed position sits perfectly still; the syncer is the only
            // thing moving the rigs (moveRigs == interp exactly); and the hip carries 100-289
            // m/s of velocity going INTO each of those corrections, i.e. energy arriving from
            // contacts rather than from the syncer
            Character.localCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.All, warpPos, false);
            // Logged so a session log identifies WHICH build produced it: this used to be
            // RpcTarget.MasterClient (i.e. host-local only), and without a marker there is no way
            // to tell from the host's log alone whether a given run had the broadcast or not
            _log?.LogInfo($"OwnTeleportSequence: warping the host to {warpPos} (broadcast to ALL machines, "
                + "so every client warps its own copy of the host rather than having the position sync "
                + "drag that ragdoll across the map).");

            float startTime = Time.time;
            float lastSend = Time.time;
            int tried = 0;
            int framesToWait = Mathf.Max(1, _cfg.OwnTeleportFramesToWait.Value);
            int maxResends = Mathf.Max(0, _cfg.OwnMaxClientWarpResends.Value);
            float resendGrace = Mathf.Max(0f, _cfg.OwnClientWarpResendGraceSeconds.Value);
            bool arrived = false;

            // Let the warp we just sent ACTUALLY START before judging whether it worked.
            // Without this the loop's first iteration ran in the very same frame as the send:
            // Character.WarpPlayer only starts its IMove coroutine (which does not displace a
            // single rigidbody until after its first `yield return null`), so Head is still the
            // pre-warp position, the y-check below still reads as "not there yet", and we fired
            // an immediate second warp for the identical target. That second warp hit
            // Character.WarpPlayer while the first was mid-flight, taking its
            //   "WHOA! We started a new warp before the old one wrapped up[...] this is very
            //    likely to break something"
            // branch, which StopCoroutine()s the in-flight warp partway through. This was
            // structural, not a timing fluke - the resend is unconditional because the loop
            // never yielded first - and a session log caught it exactly: two identical
            // "Starting move N1K0 to (-12.78, 292.01, 98.49)" lines with the WHOA error between
            // them, and our own "warped [...] after 1 attempts" right after.
            // Aborting a warp mid-move leaves the ragdoll half-displaced for
            // a frame, and CharacterSyncer on the OTHER machines latches whatever hip position it
            // samples at that moment into its `lastPosition` interpolation anchor - from then on
            // InterpolateRigPositions lerps that character between a stale anchor and its real
            // streamed position every FixedUpdate, which is the reported "another player's
            // character jitters and spins for the entire run, only on their screen" bug (it
            // clears only when the Character object is rebuilt: load, airport, or restart).
            // TeleportClientsToHost below already paced its resends for the same class of
            // problem; this path never got the same treatment
            for (int i = 0; i < framesToWait; i++) yield return null;

            while (Time.time - startTime < 30f && Character.localCharacter != null)
            {
                if (Mathf.Abs(Character.localCharacter.Head.y - warpPos.y) > 3f)
                {
                    // Hold off until the grace interval has passed since our last send, so a
                    // warp still in flight is never overwritten by a redundant one (see above).
                    // Same bound + pacing TeleportClientsToHost uses, for the same reason
                    if (Time.time - lastSend < resendGrace)
                    {
                        for (int i = 0; i < framesToWait; i++) yield return null;
                        continue;
                    }

                    if (tried >= maxResends)
                    {
                        _log?.LogWarning($"OwnTeleportSequence: {Character.localCharacter.player.name} still isn't at "
                            + $"{warpPos} after {tried} re-warp(s); giving up on re-sending (the watchdog/position "
                            + "recovery takes it from here) rather than spamming warps that abort each other.");
                        break;
                    }

                    try
                    {
                        // All, for the same reason as the initial send above
                        Character.localCharacter.photonView?.RPC("WarpPlayerRPC", RpcTarget.All, warpPos, false);
                        _log.Trace($"OwnTeleportSequence: warped {Character.localCharacter.player.name} to {warpPos} "
                            + $"(previous position: {Character.localCharacter.Head}, resend {tried + 1}/{maxResends}).");
                    }
                    catch (Exception e)
                    {
                        _log?.LogWarning($"OwnTeleportSequence: TeleportToPosition warp failed: {e}");
                    }

                    lastSend = Time.time;
                    tried++;
                }
                else if (Mathf.Abs(Character.localCharacter.Head.x - warpPos.x) < 6f
                    && Mathf.Abs(Character.localCharacter.Head.z - warpPos.z) < 6f)
                {
                    _log.Trace($"OwnTeleportSequence: warped {Character.localCharacter.player.name} after {tried} attempts.");
                    arrived = true;
                    yield return new WaitForSeconds(0.5f);
                    if (!PhotonNetwork.OfflineMode)
                    {
                        _clientWarpSettled = false;
                        StartCoroutine(RunClientWarpAndSignal(warpPos));
                    }
                    break;
                }

                for (int i = 0; i < framesToWait; i++) yield return null;
            }

            // Say so LOUDLY when the host's own warp never landed. RunSequence yields on
            // this coroutine, so failing here silently stalls the entire load for the full
            // 30s budget and then continues into a restore that has nowhere to restore TO -
            // which surfaces to players as both machines sitting on "LOADING SAVE..."
            // forever, with nothing in the log between the loading screen and the timeout.
            // Never leave that window silent again
            if (!arrived)
            {
                Character local = Character.localCharacter;
                string where = local != null ? local.Head.ToString() : "(no character)";
                bool isDead = local != null && local.data.dead;
                _log?.LogError($"OwnTeleportSequence: the HOST's own warp to {warpPos} never landed after "
                    + $"{tried} attempt(s) / {Time.time - startTime:0.#}s - still at {where} (dead={isDead}). "
                    + "The restore runs against the wrong position from here, so the load will not complete "
                    + "properly. A character that is dead cannot be warped at all (see ReviveBeforeWarp).");
            }
        }

        /// <summary>
        /// Thin wrapper so RunSequence can hold the "LOADING SAVE..." overlay up until
        /// <see cref="TeleportClientsToHost"/> has actually finished (settled either way -
        /// see the wait block near the end of RunSequence), instead of that coroutine
        /// running fully fire-and-forget the way its only call site above used to start it
        /// </summary>
        private IEnumerator RunClientWarpAndSignal(Vector3 hostPos)
        {
            try
            {
                yield return StartCoroutine(TeleportClientsToHost(hostPos));
            }
            finally
            {
                _clientWarpSettled = true;
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
        /// <summary>
        /// Revives <paramref name="ch"/> if it is dead, so a warp aimed at it can actually
        /// stick - see the call site for why a dead character is un-warpable. Anyone the
        /// checkpoint recorded as dead is re-killed later by
        /// <see cref="DeathStateRestore.ApplySavedDeaths"/>, so reviving unconditionally
        /// here costs nothing and keeps this free of save-file knowledge
        /// </summary>
        /// <summary>
        /// Number of physics steps to let a just-revived body settle before warping it.
        ///
        /// This is the fix for the "another player's ragdoll convulses, and I get catapulted
        /// 30-40m, for the rest of the run" bug. Session logs correlate it EXACTLY with this
        /// path: every reproduction carried
        /// "<c>was dead before being warped [...] body was at (0, 5000, -5000)</c>" and warped
        /// the client from DeathPos; every clean run had no such line and warped the client
        /// from an ordinary beach position. It never once appeared without a dead-on-arrival
        /// client
        ///
        /// A body parked at DeathPos is 7,071m from the campfire. Warping it there asks
        /// <c>Character.WarpPlayer</c>'s IMove to close that gap with
        /// <c>MoveAllRigsInDirection</c> -&gt; <c>Rigidbody.MovePosition</c>, and MovePosition
        /// on a non-kinematic body IMPLIES a velocity of delta/fixedDeltaTime to reach its
        /// target - here on the order of hundreds of thousands of m/s. Measured directly: a
        /// mere 557m warp left the hip at 254 m/s (telemetry <c>velIn=254</c>), and during the
        /// bug individual bodyparts were seen at 766 m/s with the ragdoll racking up ~90m of
        /// path per second while going nowhere
        ///
        /// The window that makes it catastrophic is <c>data.dead</c> still being true while
        /// that is happening, because <c>Character.FixedUpdate</c> then runs its own
        /// <c>MoveAllRigsInDirection(DeathPos() - Center)</c> EVERY physics step in the
        /// opposite direction - a 7km tug-of-war. Our revive and our warp used to be sent in
        /// the same frame with nothing in between, so any late-arriving <c>RPCA_Die</c> from
        /// the client's own DeathOnArrival kill (the host log shows that kill firing four
        /// times in a burst, with three of vanilla's "WHOA! We started a new warp before the
        /// old one wrapped up" errors between them) landed inside the warp
        ///
        /// Waiting here closes that window: the revive is given real physics steps to take
        /// effect on every machine, and <see cref="TeleportClientsToHost"/> re-checks
        /// <c>data.dead</c> afterwards, so we never hand IMove a body that something else is
        /// still dragging to DeathPos
        /// </summary>
        private const int ReviveSettleSteps = 10;

        /// <summary>
        /// Sets <c>MapHandler.LastRevivedSegment</c> to the segment a restored run has just been
        /// placed in, so the run's map state is internally consistent.
        ///
        /// WHY THIS MATTERS
        /// That value normally tracks where a scout statue last revived somebody, and the ONLY
        /// logic that reads it is <c>MapHandler.BaseCampHasRevived</c>, which is in turn one of
        /// the three terms deciding whether a joining player gets auto-revived:
        /// <code>
        /// canRevive = GameHandler.IsOnIsland &amp;&amp; MapHandler.BaseCampHasRevived
        ///                                     &amp;&amp; MapHandler.LastSeenCampfireIsSafe;
        /// </code>
        /// <c>CharacterSpawner.SpawnDeadAtBaseCamp</c> ALWAYS kills a late joiner
        /// (<c>DeathOnArrival()</c>) and then, one frame later, revives and places them with
        /// <c>RPCA_ReviveAtPosition</c> if and only if <c>canRevive</c> holds. Walking to a biome
        /// normally leaves the value consistent, so that auto-revive fires and the joining player
        /// never even notices they died
        ///
        /// A checkpoint load jumps a BRAND NEW run straight to a mid-run segment, so the value
        /// still held whatever a fresh run started with - never equal to the current segment or
        /// one below it. <c>BaseCampHasRevived</c> was therefore false, the auto-revive never
        /// ran, and the client was left dead at DeathPos (0, 5000, -5000) for our own code to
        /// revive and drag 7km back. Session logs tie the reported ragdoll-thrash bug to exactly
        /// that state: across 11 logged resumes it appeared in the one attempt that went through
        /// it and in none of the ten that did not
        ///
        /// Deliberately narrow: the field has only one logic consumer (above), plus the two
        /// CharacterSpawner RPCs that forward it to a joining client, which applies it through
        /// <c>SetSegmentOnSpawn</c>. It does NOT feed <c>BaseCampScoutStatue</c> or
        /// <c>PreviousSegmentIsStillBaseCamp</c> - those are computed independently - so this
        /// cannot change which statue counts as base camp
        ///
        /// The setter is private, hence the reflection; the backing field is the fallback
        /// </summary>
        private void SyncLastRevivedSegment(Segment segment)
        {
            try
            {
                var handler = Singleton<MapHandler>.Instance;
                if (handler == null) return;

                var setter = HarmonyLib.AccessTools.PropertySetter(typeof(MapHandler), "LastRevivedSegment");
                if (setter != null) setter.Invoke(handler, new object[] { (int)segment });
                else
                {
                    var backing = HarmonyLib.AccessTools.Field(typeof(MapHandler), "_lastRevivedSegment");
                    if (backing == null)
                    {
                        _log?.LogWarning("OwnTeleportSequence: could not find MapHandler.LastRevivedSegment; a "
                            + "late-joining client may be left dead at DeathPos for us to revive by hand.");
                        return;
                    }
                    backing.SetValue(handler, (int)segment);
                }

                _log.Trace($"OwnTeleportSequence: LastRevivedSegment set to {(int)segment} ({segment}), matching the "
                    + "segment this load jumped to, so the game auto-revives a late-joining client itself instead of "
                    + "leaving them dead at DeathPos.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: could not sync LastRevivedSegment ({e.Message}); "
                    + "a late-joining client may be left dead at DeathPos for us to revive by hand.");
            }
        }

        /// <summary>
        /// Revives a DEAD client and places it at <paramref name="target"/> in a single atomic
        /// call, using vanilla's own <c>RPCA_ReviveAtPosition</c>. Returns false if the
        /// character was not dead (nothing to do) or the RPC could not be sent
        ///
        /// WHY THIS REPLACES OUR OWN REVIVE-THEN-WARP FOR THE DEAD CASE
        /// A dead body has been parked at <c>Character.DeathPos()</c> - (0, 5000, -5000) - which
        /// is 7,071m from the campfire, and <c>Character.FixedUpdate</c> drags it back there
        /// EVERY physics step for as long as <c>data.dead</c> holds. Our old sequence was two
        /// separate RPCs (<c>ReviveCharacter</c>, then <c>WarpPlayerRPC</c>) with a settle
        /// between them, which meant a 7km <c>WarpPlayer</c> on every machine and a window in
        /// which a late-arriving <c>RPCA_Die</c> could put the body back into the death state
        /// mid-warp. Session logs correlate the reported ragdoll-thrash bug EXACTLY with this
        /// path: across 11 logged resumes it appeared in the one attempt where the client was
        /// killed on arrival and warped from DeathPos, and in none of the other ten
        ///
        /// <c>RPCA_ReviveAtPosition</c> is what the game itself uses here (see
        /// <c>CharacterSpawner.SpawnDeadAtBaseCamp</c>, which calls <c>DeathOnArrival()</c> and
        /// then revives with this a frame later whenever it is permitted to). It clears the
        /// death state and places the body inside ONE RPC body, so the two can never interleave
        /// or be separated by a settle, and there is no separate 7km warp to fight
        ///
        /// Arguments chosen to match what our old path did, not to add behaviour:
        ///  - <c>applyStatus: false</c> - no post-revive Curse/Hunger, same as our
        ///    <c>ReviveCharacter(false)</c>
        ///  - <c>statueSegment: -1</c> - leaves <c>data.lastRevivedSegment</c> untouched, since
        ///    this is not an Ancient Statue revive
        /// Its <c>DropAllItems(includeBackpack: true)</c> is a no-op in this scenario:
        /// <c>RPCA_Die</c> already dropped everything when the game killed them
        /// </summary>
        private bool ReviveDeadClientAtPosition(Character ch, Vector3 target)
        {
            if (ch == null || ch.photonView == null || ch.data == null || !ch.data.dead) return false;

            try
            {
                ch.photonView.RPC(ReviveAtPositionRpcName, RpcTarget.All, target, false, -1);
                _log?.LogInfo($"OwnTeleportSequence: {ch.characterName} was dead before being warped "
                    + $"(joined-late arrival kill, most likely) - body was at {ch.Head}. Revived AND placed at "
                    + $"{target} in one atomic {ReviveAtPositionRpcName}, so there is no 7km warp to fight.");
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: atomic revive-at-position for {ch.characterName} failed "
                    + $"({e.Message}); falling back to the separate revive + warp path.");
                return false;
            }
        }

        /// <summary>
        /// True if <paramref name="ch"/> was dead and a revive was sent - i.e. the caller must
        /// let <see cref="ReviveSettleSteps"/> physics steps pass before warping it
        /// </summary>
        private bool ReviveBeforeWarp(Character ch)
        {
            if (ch == null || ch.photonView == null) return false;

            // ONLY data.dead, deliberately. Character.FixedUpdate routes just that state into
            // the per-step DeathPos re-park (the one that eats the warp and zeroes velocity);
            // passedOut/fullyPassedOut go to HandlePassedOut, which warps perfectly well.
            // Reviving a merely passed-out character here would be both unnecessary and
            // destructive - ReviveCharacter clears afflictions and thorns
            if (!ch.data.dead) return false;

            try
            {
                // Where the body actually sits is worth recording: a dead character has been
                // parked at DeathPos() by RPCA_Die, so a revive that fails to take shows up
                // here as a body still ~7km out rather than as a silent bad warp later
                ch.photonView.RPC(ReviveRpcName, RpcTarget.All, false);
                _log?.LogInfo($"OwnTeleportSequence: {ch.characterName} was dead before being warped "
                    + $"(joined-late arrival kill, most likely) - revived so the warp can land (body was at {ch.Head}). "
                    + $"Letting it settle {ReviveSettleSteps} physics steps before warping.");
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: could not revive {ch.characterName} before warping "
                    + $"({e.Message}); the warp will probably not stick.");
                return false;
            }
        }

        /// <summary>
        /// Where a given client should actually land, given the host's own arrival point.
        ///
        /// Every client used to be warped to the EXACT coordinate the host warped itself to, so
        /// all bodies arrived occupying the same space and had their colliders re-enabled at the
        /// same instant (see <c>Character.WarpPlayer</c>'s IMove, which disables them for the
        /// move and switches them back on at the end). Asking PhysX to resolve two fully
        /// interpenetrating ragdolls is asking for a very large separating impulse, and that
        /// matches the session reports of a player being flung 16m, 29m and 40m in different
        /// directions on different runs. Vanilla never does this to itself - CharacterSpawner
        /// scatters its arrivals with <c>RandomBaseCampOffset</c> for the same reason
        ///
        /// The offset is derived from the player's Photon ActorNumber via a golden-angle step
        /// (137.5 degrees), which spreads any small number of players evenly around the circle
        /// and, crucially, is STABLE: <see cref="TeleportClientsToHost"/> re-sends a safety warp
        /// to the same client several times, and a target that moved between re-sends would
        /// never satisfy the arrival check. Height is left alone - everyone still arrives at the
        /// host's own y, slightly above ground, and falls the same short distance
        ///
        /// NOTE (measured, so the scope of this is not overstated): this is NOT the trigger for
        /// the ragdoll-thrash bug. Per-step telemetry caught the host's body already thrashing
        /// on the client's machine while that client was still 6,943m away at DeathPos, so the
        /// two bodies were nowhere near each other when it started. This removes a genuine
        /// physics hazard we were creating - and is the most likely explanation for the CATAPULT
        /// specifically - but the thrash itself has a separate, still-unidentified cause
        /// </summary>
        private Vector3 SpreadTargetFor(Character ch, Vector3 hostPos)
        {
            float radius = _cfg != null ? Mathf.Max(0f, _cfg.OwnClientWarpSpreadRadius.Value) : 0f;
            if (radius <= 0f) return hostPos;

            int actor;
            try { actor = ch.photonView?.Owner?.ActorNumber ?? 0; }
            catch { actor = 0; }

            float angle = (actor * 137.5f) * Mathf.Deg2Rad;
            return hostPos + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        /// <summary>
        /// Blocks until every player in the room has a registered <c>Character</c>, so the
        /// segment advance that follows cannot catch one of them mid-spawn - see the call site
        /// for why that matters. Times out rather than stalling the load: a player who never
        /// registers would otherwise hold the whole restore hostage, and being killed on arrival
        /// is recoverable (we revive and warp them) whereas a hung load is not
        /// </summary>
        private IEnumerator WaitForEveryPlayerToRegister()
        {
            float timeout = Mathf.Max(1f, _cfg != null ? _cfg.CoopReadyTimeout.Value : 30f);
            float started = Time.time;
            int expected = 0;

            while (Time.time - started < timeout)
            {
                int registered = 0;
                expected = 0;
                try
                {
                    expected = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
                    foreach (var p in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                        if (p != null && p.character != null) registered++;
                }
                catch { /* mid-scene-load churn; just try again next frame */ }

                if (expected > 0 && registered >= expected)
                {
                    _log.Trace($"OwnTeleportSequence: all {registered}/{expected} player character(s) registered "
                        + $"after {Time.time - started:F1}s; safe to advance the segment.");
                    yield break;
                }

                yield return null;
            }

            _log?.LogWarning($"OwnTeleportSequence: not every player had registered a character after "
                + $"{timeout:F0}s (expected {expected}); advancing the segment anyway. Anyone still spawning may "
                + "be killed on arrival by the game and warped back from DeathPos.");
        }

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

                // A dead client CANNOT be warped: Character.FixedUpdate re-parks the body at
                // Character.DeathPos() - (0, 5000, -5000) - EVERY PHYSICS STEP, velocity zeroed
                // along with it. Confirmed in a session log ("was dead before being warped [...]
                // body was at (0.01, 5000.23, -5000.02)"). The warp below lands and is
                // immediately dragged back, so all the re-sends burn out against it and the
                // client is left 7km away, watching their own camera get yanked back and forth.
                //
                // This happens on a perfectly ordinary load: a client who joined the run
                // late is killed on arrival by the game itself (DeathOnArrival ->
                // KillImmediately), and "arrival" for them is when THEY finish loading into
                // the fresh run - which is well after ReviveDeadPlayers already ran at the
                // top of this sequence and found nothing to do. So revive here too, right
                // before the warp, where being alive is a precondition rather than a nicety.
                // Whoever the checkpoint genuinely recorded as dead is put back that way
                // afterwards by DeathStateRestore.ApplySavedDeaths, still behind the
                // opaque loading screen
                // Each client lands on its own point around the host rather than inside it -
                // see SpreadTargetFor. Computed ONCE per client and reused for every re-send
                // below, so the arrival check always tests against the point we actually aimed at
                Vector3 clientTarget = SpreadTargetFor(ch, hostPos);
                if (clientTarget != hostPos)
                    _log.Trace($"OwnTeleportSequence.TeleportClientsToHost: {ch.characterName} lands at "
                        + $"{clientTarget} ({(clientTarget - hostPos).magnitude:F1}m off the host's own arrival "
                        + "point, so the bodies do not arrive inside each other).");

                // A dead client is revived AND placed in one atomic call rather than being
                // revived and then warped 7km - see ReviveDeadClientAtPosition
                bool revivedAtPosition = ReviveDeadClientAtPosition(ch, clientTarget);
                if (revivedAtPosition)
                {
                    // Let it land, then re-check: the client's own DeathOnArrival kill fires in a
                    // burst and a late RPCA_Die can still arrive after ours, putting the body
                    // straight back at DeathPos with Character.FixedUpdate dragging it there every
                    // step. One more atomic revive-and-place is the right answer to that, never a
                    // plain warp into a body something else still owns
                    for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();

                    if (ch != null && ch.data != null && ch.data.dead)
                    {
                        _log?.LogWarning($"OwnTeleportSequence: {ch.characterName} died AGAIN right after being "
                            + "revived (a late DeathOnArrival kill, most likely); re-placing them once more.");
                        ReviveDeadClientAtPosition(ch, clientTarget);
                        for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();
                    }
                }
                else if (ReviveBeforeWarp(ch))
                {
                    // Fallback only: the atomic RPC could not be sent (see the catch there), so
                    // fall back to the old revive-then-warp with its settle
                    for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();
                }

                if (ch == null || ch.photonView == null) continue;

                // RPCA_ReviveAtPosition already placed them; sending a second warp on top would
                // be exactly the redundant back-to-back warp that makes Character.WarpPlayer abort
                // its own in-flight move ("WHOA! We started a new warp before the old one wrapped
                // up"). The arrival loop below still re-sends plain warps if the placement did not
                // actually stick
                if (!revivedAtPosition)
                    ch.photonView.RPC("WarpPlayerRPC", RpcTarget.All, clientTarget, false);
                float startTime = Time.time;
                float lastSend = Time.time;
                int tried = 0;
                bool arrived = false;

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
                    if (Mathf.Abs(ch.Head.x - clientTarget.x) < 6f && Mathf.Abs(ch.Head.z - clientTarget.z) < 6f)
                    {
                        _log.Trace($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} after {tried} attempts.");
                        arrived = true;
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
                                + $"near {clientTarget} after {tried} re-warp(s); giving up (client's own watchdog/position "
                                + "recovery will handle it) rather than spamming further warps.");
                            break;
                        }

                        try
                        {
                            ch.photonView?.RPC("WarpPlayerRPC", RpcTarget.All, clientTarget, false);
                            _log.Trace($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} to {clientTarget} "
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

                // Covers BOTH give-up paths: the explicit maxResends bail-out above (already
                // logged there) and this loop's own 30s hard timeout expiring with the client
                // never confirmed arrived (previously silent - see _clientWarpAllArrived's remarks)
                if (!arrived)
                {
                    _clientWarpAllArrived = false;
                    if (Time.time - startTime >= 30f)
                        _log?.LogWarning($"OwnTeleportSequence.TeleportClientsToHost: gave up waiting for {ch.player.name} "
                            + $"near {clientTarget} after a 30s timeout.");
                }
            }
        }
    }
}
