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
    /// Drives the actual segment jump, teleport, and revive sequence for a checkpoint
    /// load. PEAKapalooza's branches are not ported. Inventory/backpack/affliction
    /// restore (<see cref="OwnInventoryRestore"/>) is wired in as a fire-and-forget
    /// coroutine. No "Loading savegame..." caption - <c>ResumeOrchestrator</c> already
    /// shows its own status messages. The solo unlit-campfire-after-jump fix is folded
    /// in directly, right after segment activation.
    /// </summary>
    public class OwnTeleportSequence : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnLoadEntryPoints _entryPoints;
        private OwnWakeUpEffect _wakeUpEffect;
        private OwnLoadingScreen _loadingScreen;

        // PEAK 2.0.a renamed the revive RPC from RPCA_Revive to ReviveCharacter; a string RPC
        // name isn't compiler-checked, so a rename like that fails silently at runtime.
        private const string ReviveRpcName = "ReviveCharacter";

        // Vanilla's atomic "revive this dead character AND place it here" RPC - the same one
        // CharacterSpawner.SpawnDeadAtBaseCamp uses for a late-joining player.
        private const string ReviveAtPositionRpcName = "RPCA_ReviveAtPosition";

        // Lets RunSequence hold the "LOADING SAVE..." overlay up until client warps have
        // actually finished (or given up) instead of it vanishing while they're still in flight.
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
        /// loading-screen presentation at the end. Polled via <c>OwnLoadEntryPoints.TeleportInProgress</c>.
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// True once inventory/backpack/afflictions/etc. have finished restoring, well before
        /// <see cref="IsRunning"/> goes false. <see cref="ResumeOrchestrator"/> polls this to
        /// show "Save loaded. Welcome back!" without waiting for the wake-up animation too.
        /// </summary>
        public bool RestoreComplete { get; private set; }

        /// <summary>
        /// Starts the sequence. <paramref name="data"/> is the level/world half, read from
        /// <paramref name="selection"/>'s host file. The selection is carried alongside so
        /// per-player restore steps can look up each player's own file.
        /// </summary>
        public void Begin(OwnSaveData data, SaveSelection selection) => StartCoroutine(RunSequenceWrapper(data, selection));

        private IEnumerator RunSequenceWrapper(OwnSaveData data, SaveSelection selection)
        {
            IsRunning = true;
            RestoreComplete = false;
            // try/finally so IsRunning/RestoreComplete always reset even if RunSequence throws
            // partway through, or ResumeOrchestrator's wait hangs out its full timeout.
            HeightAchievementGuard.Suppress("teleport sequence");

            // Driven by hand rather than `yield return RunSequence(...)`: that form isn't
            // crash-safe, since Unity's coroutine runner swallows a thrown exception and never
            // resumes the outer coroutine, so the finally below never runs. Pumping MoveNext
            // ourselves puts the try/catch on our side, so a throw anywhere in RunSequence gets
            // logged and still runs the cleanup below.
            IEnumerator sequence = RunSequence(data, selection);
            while (true)
            {
                object current = null;
                bool moved;
                try
                {
                    moved = sequence.MoveNext();
                    if (moved) current = sequence.Current;
                }
                catch (Exception e)
                {
                    _log?.LogError("OwnTeleportSequence: the restore sequence threw and was aborted. The loading "
                        + $"screen is being torn down so the game is not left on \"LOADING SAVE...\" forever: {e}");
                    break;
                }

                if (!moved) break;
                yield return current;
            }

            // Always reached now, on the happy path AND on a throw
            IsRunning = false;
            RestoreComplete = true;
            HeightAchievementGuard.Release("teleport sequence");

            // The overlay is normally torn down at the tail of RunSequence; if that never ran, do it here.
            if (_loadingScreen != null)
            {
                bool wasFadedIn = false;
                try { wasFadedIn = _loadingScreen.isActiveAndEnabled; } catch { }
                if (wasFadedIn) yield return _loadingScreen.FadeOut(0.5f);
            }
        }

        private IEnumerator RunSequence(OwnSaveData data, SaveSelection selection)
        {
            bool offline = selection.Offline;

            // Reset per-run: this MonoBehaviour is reused across every Begin() call.
            _clientWarpSettled = true;
            _clientWarpAllArrived = true;

            Segment finalSegment = data.segment;
            Vector3 savedPos = new Vector3(data.posX, data.posY, data.posZ);
            float waitTime = Mathf.Max(0f, _cfg.OwnJumpLogicWaitTime.Value);

            // Recovery path for an existing checkpoint whose world state was anchored in the
            // off-map death zone (see OwnSaveCapture.ResolveWorldAnchor, which stops new saves
            // being written that way). Retargeted to the checkpoint's own campfire after the
            // segment jump below (PreviousCampfire only means anything once it's active).
            bool deathZoneSave = IsDeathZonePosition(savedPos);
            if (deathZoneSave)
                _log?.LogWarning($"OwnTeleportSequence: this checkpoint's saved position {savedPos} is the game's "
                    + "off-map death zone - it was written while the host was dead by a build that still anchored "
                    + "world state on them. Retargeting to the saved segment's own campfire after the jump.");

            // Must run before any segment/position warp: MaxHeightReached has to already
            // reflect prior progress before the character's altitude jumps, or the teleport
            // itself gets miscounted as climbed height toward the High Altitude Badge.
            if (_cfg.RestoreAchievements.Value)
            {
                // One pass here isn't enough in co-op: a client still respawning has no Player
                // object on the host yet, so it's missed. The routine below catches late spawners.
                var restored = new HashSet<string>();
                AchievementProgressIO.RestoreAllPlayers(selection, _entryPoints, _log, restored);
                if (!offline) StartCoroutine(RestoreAchievementsForLateSpawners(selection, restored));
            }

            // Solo has no networked clients to keep in sync, so collapse the inter-step wait to
            // a single frame (see PluginConfig.OwnFastSoloTeleport). Co-op keeps the full
            // cadence to give slower clients time to catch up before the host's precise teleport.
            float stepWait = (offline && _cfg.OwnFastSoloTeleport.Value) ? 0f : waitTime;

            // Do not advance the segment while a client is still spawning: this is the measured
            // trigger for the co-op ragdoll-thrash bug (CharacterSpawner kills a player who
            // finishes spawning after the segment advances, parking their body at DeathPos).
            // Waits by observing PlayerHandler on the host, not our own ready RPC, since the
            // bug reproduces with a completely unmodded client.
            if (!offline) yield return WaitForEveryPlayerToRegister();

            _log?.LogInfo($"OwnTeleportSequence: executing custom jump to: {finalSegment}"
                + (stepWait < waitTime ? " (fast solo cadence)" : ""));

            // Crossfades into the game's own "LOADING..." screen so the teleport work below
            // happens hidden; collapse/reveal/stand-up happens at the matching block near the
            // end. IMPORTANT: the wake-up collapse must not happen here, before
            // ReviveDeadPlayers - that call unconditionally clears passedOut/fullyPassedOut on
            // any character it finds flagged, undoing our fake collapse before the intended reveal.
            bool wakeUpEnabled = _cfg.OwnWakeUpAnimationEnabled.Value;
            // Debug-only: skips just the loading-screen overlay, leaving wake-up timing untouched.
            bool showLoadingScreen = wakeUpEnabled && !_cfg.DebugDisableLoadingScreen.Value;

            // Without this delay, our loading screen can start covering things up while the
            // game's own level-load screen is still finishing its clear.
            if (wakeUpEnabled)
                yield return new WaitForSeconds(Mathf.Max(0f, _cfg.OwnLoadingScreenFadeInDelay.Value));
            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(true);
            if (showLoadingScreen && _loadingScreen != null)
                yield return _loadingScreen.FadeIn(_cfg.OwnLoadingScreenFadeTime.Value);

            // Arms TeleportWatchdog's load window on every machine (repurposed, no caption).
            // BeginLoadWindow() is a direct local call since RpcTarget.Others never reaches the sender.
            _entryPoints.Network?.Watchdog?.BeginLoadWindow();
            _entryPoints.Network?.LoadingScreenOthers(true);

            yield return new WaitForSeconds(waitTime);
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

            // Nadir is the one segment that can be missing from an otherwise valid level scene:
            // vanilla's jump does SetUpVoidSegment() (which only logs when VoidBiome.instance is
            // null) and then immediately dereferences that same instance, so a scene without it
            // leaves the segment un-activated while the warps below still fire. Landing everyone
            // on the saved Nadir coordinates inside a biome that was never instantiated means
            // falling through the world with nothing to stand on, so retarget to where the run
            // actually spawned instead. Should be unreachable on stock PEAK 2.0.a+ - the Void
            // biome is baked into every level scene - but the failure mode is bad enough to guard.
            if (finalSegment == Segment.Void && Peak.VoidBiome.instance == null)
            {
                Vector3 fallback = Character.localCharacter != null ? Character.localCharacter.Center : savedPos;
                _log?.LogError("OwnTeleportSequence: this checkpoint is in Nadir, but this level scene has no "
                    + $"VoidBiome to jump into. Skipping the segment jump and restoring at {fallback} instead, so "
                    + "nobody is dropped into an area that was never instantiated.");
                savedPos = fallback;
                spawnPos = fallback;
                spawnPos.y += 5f;
                finalSegment = mh != null ? mh.GetCurrentSegment() : Segment.Beach;
                index = (int)finalSegment;
            }

            // MapHandler.SetSegmentOnSpawn only teleports the caller's own seat and never syncs
            // segment/biome activation over the network, so it's correct for solo but leaves
            // coop clients stuck in the old segment. JumpToSegment RPCs every player and syncs
            // activation, so coop needs that instead. Guarded: JumpToSegment throws if a stale
            // (destroyed) Character sits in PlayerHandler, e.g. a client reconnecting mid-jump.
            try
            {
                if (offline) MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment);
                else MapHandler.JumpToSegment(finalSegment);
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnTeleportSequence: the segment jump to {finalSegment} threw ({e.Message}) - most "
                    + "likely a player was mid-respawn and left a destroyed Character in PlayerHandler. Continuing "
                    + "with the restore; the warps below place everyone regardless.");
            }

            // JumpToSegment has no equivalent parameter to SetSegmentOnSpawn's own segment sync.
            if (!offline) SyncLastRevivedSegment(finalSegment);

            // Nadir's checkpoint is taken by communing with the scoutmaster's soul, so a save
            // can only exist in a world where that already happened - re-run it here to put the
            // world back the way the save found it. Fired this early, right after the jump, so
            // the ~6s break cutscene plays out behind the loading screen rather than in the
            // player's face after the reveal. See PreCommuneWithScoutmasterSoul.
            if (finalSegment == Segment.Void && RunLauncher.IsHost)
                PreCommuneWithScoutmasterSoul(data, savedPos);

            // Solo-only relight fix, folded in here - see class remarks
            if (offline)
            {
                Campfire previousCampfire = MapHandler.PreviousCampfire;
                if (previousCampfire != null && !previousCampfire.Lit)
                    previousCampfire.LightWithoutReveal();
            }

            // Death-zone recovery, resolved now that the saved segment is active: PreviousCampfire
            // is exactly the checkpoint this save was taken at. Everything downstream reads
            // savedPos/spawnPos, so correcting both here is enough.
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

            // Snap the saved time-of-day here, well before the loading screen fades out.
            // setTimeOfDay is an instant snap, so applying it later (right before reveal) made
            // a night save visibly flicker from bright morning to night just after the screen cleared.
            if (RunLauncher.IsHost && (_cfg.RestoreDaytime.Value || _cfg.RestoreDay.Value))
            {
                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                if (dayNight != null)
                {
                    if (_cfg.RestoreDaytime.Value && data.timeOfDay != 0f)
                        dayNight.setTimeOfDay(data.timeOfDay);

                    // dayCount has no vanilla RPC keeping it in sync, so broadcast ourselves.
                    if (_cfg.RestoreDay.Value && data.dayCount != 0)
                    {
                        dayNight.dayCount = data.dayCount;
                        _entryPoints.Network?.SyncDayCountAll(data.dayCount);
                    }
                }
            }

            if (RunLauncher.IsHost && _entryPoints.LoadedSaveFileThisRound)
                OwnWorldLootReset.DestroyLeftoverHeldItems(_log);

            // MapHandler stores Peak and Nadir one slot below their raw enum ordinal - its own
            // JumpToSegmentLogic applies the same "index-- when >= 5" fixup. Peak (5) shares the
            // Kiln's segment (4); Nadir (6) is appended by SetUpVoidSegment as the 6th element (5).
            if ((int)finalSegment >= 5) index--;

            // Nadir only. Its slot does not exist until the jump above ran SetUpVoidSegment, so
            // the resolve near the top of this method always came back null on a cold load and
            // silently skipped Nadir's own item spawners below. Re-resolved here, where the
            // array has actually grown. Deliberately not extended to Peak: that resolves to null
            // today too, and pointing it at the Kiln's segment would start respawning Kiln loot
            // on a Peak load - a behaviour change with nothing to do with this.
            if (finalSegment == Segment.Void && mh != null
                && index >= 0 && mh.segments != null && index < mh.segments.Length)
            {
                targetSegment = mh.segments[index];
            }

            // Coop reaches Nadir through MapHandler.JumpToSegment, which moves the fog origin
            // itself (past the last one, so vanilla just switches the fog sphere off). The solo
            // branch above uses SetSegmentOnSpawn, which never touches fog, and would otherwise
            // leave the sphere growing from whichever biome the run started in. Not folded into
            // the ResetFogAfterLoad calls further down: those are for the fogged biomes (0-4)
            // and do considerably more than this.
            if (offline && finalSegment == Segment.Void)
            {
                try { OrbFogHandler.Instance?.SetFogOrigin(index); }
                catch (Exception e)
                {
                    _log?.LogWarning($"OwnTeleportSequence: switching the fog sphere off for Nadir failed "
                        + $"(cosmetic only, continuing): {e.Message}");
                }
            }

            yield return new WaitForSeconds(stepWait);
            OwnWorldLootReset.ResetWorldLoot(_log);

            // Right after ResetWorldLoot so it can't be wiped out again. Not gated on
            // LoadedSaveFileThisRound like the steps below: the statue needs restoring on
            // every load, including the first, since ResetWorldLoot just closed it regardless.
            // WorldItemRestore's delete pass must run before AncientStatueRestore/LuggageRestore
            // place anything, or it would immediately destroy what they just restored.
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

            // Must run after DestroyStaleWorldObjects, not alongside the AncientStatue/Luggage/
            // WorldItem restore block earlier: that destroy pass matches these prefab names on
            // every repeat load and would immediately delete whatever was restored here.
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
                        catch { }
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

                // Deliberately this late: the statue's gem spawns a frame after the segment jump
                // activates it, and ResetWorldLoot/DestroyStaleWorldObjects/WorldItemRestore above
                // would delete it again (see StrangeGemRestore). Nothing below this point destroys
                // world items, so this is the first point it survives.
                if ((int)finalSegment == 4) StrangeGemRestore.Restore(_log);
            }

            // RestoreAll always runs on the host regardless of the daytime setting, so disabling
            // it can never leave the watchdog's load window stuck open (see time-of-day restore
            // above). Started fire-and-forget so it runs concurrently with the collapse below;
            // the hold just below gates on inventoryRestoreDone so items are always in place
            // before the fade-out reveals the player.
            HashSet<string> restoringAsDead = _cfg.RestoreDeathState.Value
                ? DeathStateRestore.ResolveSavedDeadUserIds(selection, _log)
                : new HashSet<string>();

            bool inventoryRestoreDone = !RunLauncher.IsHost;
            if (RunLauncher.IsHost)
            {
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

            // Collapse into the passed-out pose (safe here - well after ReviveDeadPlayers) and
            // hold behind the opaque loading screen until inventoryRestoreDone, capped by a
            // safety timeout, so items/backpacks/afflictions are in place before the reveal.
            if (wakeUpEnabled && _wakeUpEffect != null)
                _wakeUpEffect.Collapse();
            if (wakeUpEnabled)
            {
                const float maxWaitForRestore = 10f;
                // Must comfortably outlast TeleportClientsToHost's own 30s hard timeout, so the
                // overlay doesn't fade out while clients are still being warped in the background.
                const float maxWaitForClientWarp = 32f;
                float elapsed = 0f;
                while ((!inventoryRestoreDone && elapsed < maxWaitForRestore)
                    || (!_clientWarpSettled && elapsed < maxWaitForClientWarp))
                {
                    // Without this, vanilla's "not really hurt" auto-revive failsafe force-clears
                    // passedOut back to false within a couple frames of Collapse().
                    _wakeUpEffect?.RefreshHold();
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
            }

            // Puts whoever the checkpoint recorded as dead back into that state, while the
            // loading screen is still fully opaque - applying it at the very end instead meant a
            // restored-dead player watched themselves load in and stand up before visibly dropping dead.
            if (RunLauncher.IsHost)
            {
                // Alive first, then the wanted deaths - order matters, or a player revived here
                // would immediately be re-killed and vice versa.
                DeathStateRestore.EnsureUnsavedPlayersAlive(restoringAsDead, _log);
                DeathStateRestore.ApplySavedDeaths(restoringAsDead, _entryPoints, _log);
            }

            // Items/backpacks/afflictions are now confirmed in place, so this is when
            // ResumeOrchestrator shows "Save loaded. Welcome back!" - before the fade-out/stand-up below.
            RestoreComplete = true;

            if (wakeUpEnabled) _entryPoints.Network?.ClientPresentationOthers(false);

            // Resuming into a Roots campfire with Fairoots installed: that mod starts its own
            // per-level work as the biome finishes loading, and revealing the world mid-burst
            // would drop the player into a freeze. See FairootsCompat.
            yield return FairootsCompat.WaitUntilReady(_log, () => _wakeUpEffect?.RefreshHold());

            // Cosmetic breathing room before the fade-out starts.
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

            // Fade the loading screen out first, fully revealing the player still collapsed,
            // then start the stand-up recovery so it plays out in full view rather than racing the fade.
            if (showLoadingScreen && _loadingScreen != null)
                yield return _loadingScreen.FadeOut(_cfg.OwnLoadingScreenFadeTime.Value);
            if (wakeUpEnabled && _wakeUpEffect != null)
                yield return _wakeUpEffect.Wake(_cfg.OwnWakeUpStandTime.Value);

            // Only surfaced after the overlay is confirmed gone, not while it's still up or mid-fade.
            if (wakeUpEnabled && !_clientWarpAllArrived)
                _entryPoints.Network?.MessageOverlay?.Show(MessagesLocalization.Get(MsgKey.PlayersTimedOut),
                    new Color(1f, 0.5f, 0.5f, 1f), 4f);

            _entryPoints.MarkLoadedThisRound();
        }

        /// <summary>
        /// Keeps re-running the achievement-progress restore until every player in the room
        /// has been handled, since a co-op client's <c>Player</c> object is created some
        /// seconds after the host starts restoring. The "already restored" set prevents
        /// double-restoring. A timeout just leaves the missing player on vanilla's baseline.
        /// </summary>
        private IEnumerator RestoreAchievementsForLateSpawners(SaveSelection selection, HashSet<string> restored)
        {
            float deadline = Time.realtimeSinceStartup + LateAchievementRestoreSeconds;

            while (Time.realtimeSinceStartup < deadline)
            {
                int expected = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
                if (expected > 0 && restored.Count >= expected)
                {
                    _log.Trace($"[achievement-debug] RestoreAllPlayers: all {expected} player(s) in the room handled.");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
                AchievementProgressIO.RestoreAllPlayers(selection, _entryPoints, _log, restored);
            }

            int stillExpected = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
            if (stillExpected > restored.Count)
                _log?.LogWarning($"[achievement-debug] RestoreAllPlayers: gave up after {LateAchievementRestoreSeconds:F0}s "
                    + $"with {restored.Count}/{stillExpected} player(s) restored - whoever is missing never got a Player "
                    + "object in time, so their per-run badge progress restarts from zero this run.");
        }

        private const float LateAchievementRestoreSeconds = 45f;

        /// <summary>
        /// True for a position at (or near) <c>Character.DeathPos()</c> - the fixed
        /// (0, 5000, -5000) spot vanilla drags every dead character's ragdoll to. No real
        /// gameplay position is remotely near it, so this unambiguously marks a save whose
        /// world state was anchored on a dead player.
        /// </summary>
        private static bool IsDeathZonePosition(Vector3 pos)
        {
            const float toleranceSq = 50f * 50f;
            return (pos - new Vector3(0f, 5000f, -5000f)).sqrMagnitude < toleranceSq;
        }

        /// <summary>
        /// Revives everyone, regardless of what the save says, so the segment jump and warps
        /// below run on living characters (whoever the checkpoint recorded as dead is put back
        /// later, see <see cref="DeathStateRestore"/>). In co-op, broadcasts the game's own
        /// revive RPC on that character's view rather than writing
        /// <c>character.data.dead/passedOut/fullyPassedOut</c> directly, since those fields
        /// only travel over RPCs and a direct write is invisible to the owning client - a
        /// client who was killed on arrival stayed a spectating ghost on their own screen
        /// otherwise. Solo keeps the direct-write path (no second machine to reach).
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

                // Mirrors the body of the game's own revive (Character.ReviveCharacter) for the
                // machine that owns this character.
                try { character.refs.ragdoll.ToggleCollision(enableCollision: true); }
                catch { }

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

        /// <summary>
        /// Replays the commune on load: breaks the soul pillar the checkpoint was taken at, via
        /// the same RPC a player's own 2s hold sends, so the restored world matches the saved
        /// one - invisible walls down, ghost scoutmaster orbiting, rising souls under way.
        ///
        /// It also closes an exploit. Leaving the pillar intact would let anyone hand out
        /// Nadir's story beat to a friend brought along on a loaded save, over and over, with
        /// none of the climb. Breaking it up front means there is nothing left to re-commune
        /// with. (Nothing on this path throws an achievement - checked: the break only fires
        /// GlobalEvents.TriggerSoulFreed, whose only listeners are the invisible walls,
        /// VoidBiome's soul-state field and the rising-souls hazard. Nadir's two badges come
        /// from arriving in the biome and from winning the run, neither of which this touches -
        /// so there is no unlock to skip here.)
        ///
        /// Host-only, but sent as the saved interactor rather than as the host: vanilla hands
        /// that view to ScoutmasterGhostOrbiter.SetOrbitCharacter, so it decides which player
        /// the ghost circles. Everything else about the break is identical whoever sends it.
        /// </summary>
        private void PreCommuneWithScoutmasterSoul(OwnSaveData data, Vector3 anchorPos)
        {
            try
            {
                Character interactor = NadirCommuner.ResolveSavedCommuner(data, _log);
                if (interactor == null || interactor.photonView == null)
                {
                    _log?.LogWarning("OwnTeleportSequence: no character available to stand in for the interactor, so "
                        + "Nadir's soul pillar is left unbroken. The invisible walls stay up until somebody communes.");
                    return;
                }

                // Nearest to the checkpoint's own position, which is where the save was taken and
                // therefore which pillar was communed with. Breaking exactly one matches vanilla:
                // the walls listen to a global event, so one is enough to drop all of them, and
                // breaking several would spawn several ghosts.
                Peak.ScoutmasterSoulPillar pillar = null;
                float best = float.MaxValue;
                foreach (Peak.ScoutmasterSoulPillar p in UnityEngine.Object.FindObjectsByType<Peak.ScoutmasterSoulPillar>(FindObjectsSortMode.None))
                {
                    if (p == null || p.photonView == null) continue;
                    float d = Vector3.Distance(p.transform.position, anchorPos);
                    if (d < best) { best = d; pillar = p; }
                }

                if (pillar == null)
                {
                    _log?.LogWarning("OwnTeleportSequence: this is a Nadir checkpoint but no active "
                        + "ScoutmasterSoulPillar was found to pre-commune with. Loading anyway; the invisible walls "
                        + "will stay up until somebody communes in-game.");
                    return;
                }

                ScoutmasterSoulPillarAutoSavePatch.SuppressNextBreak(10f);
                pillar.photonView.RPC("RPC_Break", RpcTarget.All, 0, interactor.photonView);
                _log?.LogInfo($"OwnTeleportSequence: pre-communed with the soul pillar {best:F1}m from the "
                    + $"checkpoint as {interactor.characterName}, restoring Nadir to its post-commune state.");

                StartCoroutine(EnsureSoulFreed());

                // A second load can start while the previous hold is still inside its ceiling,
                // and the older one would then release the field in the middle of this restore.
                if (_risingFieldHold != null) StopCoroutine(_risingFieldHold);
                _risingFieldHold = StartCoroutine(HoldRisingFieldUntilEveryoneHasControl());
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnTeleportSequence: pre-communing with Nadir's soul pillar failed (non-fatal, "
                    + $"the walls will stay up until somebody communes in-game): {e}");
            }
        }

        /// <summary>
        /// Failsafe for the one step of vanilla's break routine that can die on us. Only the
        /// master client runs the ghost-spawn block, and it dereferences the interactor's
        /// Character several seconds after the fact - if that reference went stale in between,
        /// the coroutine throws there and never reaches TriggerSoulFreed, leaving the host's
        /// invisible walls up while every client's have dropped. Nadir's walls are what gate the
        /// way out, so that would strand the host. Fires the event locally if it never arrived.
        ///
        /// Both listeners it re-fires are idempotent (the wall setter no-ops when unchanged,
        /// LavaRising.StartWaiting guards on its own timer), so a false positive costs nothing.
        /// SoulFreedStatus is a static the game only resets in VoidBiome.Deactivate - i.e. on
        /// winning via Nadir - so on a second Nadir load in the same session it can still read 1
        /// from the previous one and this check quietly passes. That only ever means the
        /// failsafe doesn't run; the real break above is what actually does the work.
        /// </summary>
        private IEnumerator EnsureSoulFreed()
        {
            // Comfortably past the routine's own ~6s of staged waits.
            yield return new WaitForSeconds(12f);

            if (Peak.VoidBiome.SoulFreedStatus >= 0) yield break;

            try
            {
                _log?.LogWarning("OwnTeleportSequence: the pre-commune never reported the soul as freed, so the "
                    + "game's own break routine must have been cut short. Firing the soul-freed event directly to "
                    + "make sure Nadir's invisible walls come down.");
                GlobalEvents.TriggerSoulFreed(0);
                GlobalEvents.TriggerSoulFreed(1);
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnTeleportSequence: the soul-freed failsafe threw (non-fatal): {e}");
            }
        }

        /// <summary>
        /// Absolute ceiling on the rising-field hold, measured from the pre-commune, so a stuck
        /// or disconnected client can't cost Nadir its one mechanic. Comfortably past the host's
        /// own worst case (a 32s client-warp hold plus fades) and past ResumeOrchestrator's 60s
        /// tail timeout for the same client-presentation wait.
        /// </summary>
        private const float RisingFieldHoldCeilingSeconds = 90f;

        private Coroutine _risingFieldHold;

        /// <summary>
        /// Keeps Nadir's rising field parked from the pre-commune until every player can
        /// actually run from it, then starts its clock. The field deals roughly half a status
        /// point per second, so a player revealed into an already-risen one is dead in seconds
        /// with no way to react.
        ///
        /// The release condition is "everyone has control", not "everyone is connected":
        /// <c>IsRunning</c> covers the host (false at the end of the sequence, and on a throw
        /// too, since RunSequenceWrapper resets it either way), and
        /// <c>AllClientsPresentationDone</c> covers the clients, each of which RPCs that after
        /// its own wake-up animation finishes. Backstopped by
        /// <see cref="RisingFieldHoldCeilingSeconds"/>.
        /// </summary>
        private IEnumerator HoldRisingFieldUntilEveryoneHasControl()
        {
            LavaRising field = NadirRisingField.Find(_log);
            if (field == null)
            {
                _log?.LogWarning("OwnTeleportSequence: no Void-biome rising field found to hold, so Nadir's hazard "
                    + "clock starts from the pre-commune. Anyone still loading may be revealed into it.");
                yield break;
            }

            NadirRisingField.Park(field);
            NadirRisingField.BroadcastParked(field, _log);
            _log?.LogInfo("OwnTeleportSequence: Nadir's rising field parked until every player has control.");

            // Clients only report their presentation exit when the wake-up presentation is the
            // thing they were running; with it off they never send it, so waiting on it would
            // just burn the ceiling. Solo has nobody to wait for either way.
            bool waitForClients = !PhotonNetwork.OfflineMode && _cfg.OwnWakeUpAnimationEnabled.Value;
            OwnNetwork network = _entryPoints?.Network;

            float deadline = Time.realtimeSinceStartup + RisingFieldHoldCeilingSeconds;
            bool timedOut = false;

            while (true)
            {
                if (Time.realtimeSinceStartup >= deadline) { timedOut = true; break; }

                bool hostBusy = IsRunning;
                bool clientsBusy = false;
                if (!hostBusy && waitForClients && network != null)
                {
                    try { clientsBusy = !network.AllClientsPresentationDone(); }
                    catch { clientsBusy = false; }
                }
                if (!hostBusy && !clientsBusy) break;

                // Re-applied every frame rather than set once: the break routine's own
                // TriggerSoulFreed(1) lands several seconds after we sent the RPC, and would
                // otherwise start the clock right through the hold.
                NadirRisingField.Park(field);
                yield return null;
            }

            // One last park, so the frame we release from is always a clean zero.
            NadirRisingField.Park(field);
            NadirRisingField.Release(field, _log);

            if (timedOut)
                _log?.LogWarning($"OwnTeleportSequence: gave up holding Nadir's rising field after "
                    + $"{RisingFieldHoldCeilingSeconds:F0}s - somebody never finished loading, or the restore was "
                    + "cut short. Starting the hazard anyway rather than leaving the biome without its one mechanic.");
            else
                _log?.LogInfo("OwnTeleportSequence: everyone has control, Nadir's rising field released.");
        }

        private void TryCloseLingeringEndScreen()
        {
            try
            {
                EndScreen endScreen = UnityEngine.Object.FindFirstObjectByType<EndScreen>();
                if (endScreen != null && endScreen.isOpen)
                {
                    AccessTools.Method(typeof(MenuWindow), "Close")?.Invoke(endScreen, null);
                    _entryPoints.Network?.CloseEndscreenOthers();
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: closing a lingering EndScreen failed (non-fatal): {e.Message}");
            }
        }

        private IEnumerator TeleportToPosition(Vector3 pos)
        {
            if (Character.localCharacter == null) yield break;

            // A dead character cannot be warped at all: Character.FixedUpdate re-parks the body
            // at DeathPos() every physics step and zeroes velocity, fighting the warp. If the
            // host reaches here still dead (ReviveDeadPlayers ran earlier but didn't take), the
            // whole restore never even starts. Cheap to re-check, and a no-op in the normal case.
            if (ReviveBeforeWarp(Character.localCharacter))
            {
                for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();
                if (Character.localCharacter == null) yield break;
            }

            Vector3 warpPos = pos + new Vector3(0f, 0.5f, 0f);

            // Forwarded to clients when the host ends the load window, so a client that never
            // receives a warp can still recover here instead of only being warned.
            _entryPoints.Network?.Watchdog?.SetKnownTarget(warpPos);

            // RpcTarget.All, not MasterClient: this runs on the host, so MasterClient would have
            // meant "run this on me and nobody else" - the host's body was the only character
            // whose warp never reached other machines. Without it, other machines only saw the
            // streamed hip position jump and had to drag the ragdoll across the gap themselves
            // with collision enabled, instead of using Character.WarpPlayer's own collision-safe
            // move - this was the root cause of the host's character convulsing on other machines.
            Character.localCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.All, warpPos, false);
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

            // Let the warp we just sent actually start before judging whether it worked.
            // Character.WarpPlayer's IMove doesn't displace anything until its first
            // `yield return null`, so judging on the same frame as the send would read as "not
            // there yet" and immediately fire a redundant second warp - which aborts the
            // in-flight one mid-move (Character.WarpPlayer's own "WHOA" guard), leaving the
            // ragdoll half-displaced and causing other machines to latch a stale interpolation
            // anchor (the "character jitters and spins for the entire run" bug).
            for (int i = 0; i < framesToWait; i++) yield return null;

            while (Time.time - startTime < 30f && Character.localCharacter != null)
            {
                if (Mathf.Abs(Character.localCharacter.Head.y - warpPos.y) > 3f)
                {
                    // Hold off until the grace interval has passed, so a warp still in flight is
                    // never overwritten by a redundant one.
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

            // Log loudly when the host's own warp never lands: RunSequence yields on this
            // coroutine, so a silent failure here stalls the whole load with nothing in the log.
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

        /// <summary>Wraps <see cref="TeleportClientsToHost"/> so RunSequence can hold the overlay up until it settles.</summary>
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
        /// Coop-only (guarded by <c>!PhotonNetwork.OfflineMode</c> at its call site). Fixes a
        /// slow client being re-warped over and over, visibly glitching on the host's screen
        /// only (the host's local copy of the client keeps getting yanked back by the client's
        /// authoritative position stream): judges arrival by horizontal proximity, not
        /// vertical, since <c>hostPos</c> is a few metres up and a warped client legitimately
        /// falls below it. Also bounds and paces re-sends instead of firing endlessly.
        /// </summary>
        /// <summary>Revives <paramref name="ch"/> if dead, so a warp aimed at it can stick.</summary>
        /// <summary>
        /// Physics steps to let a just-revived body settle before warping it. A body parked at
        /// DeathPos is 7km from the campfire; warping it there implies enormous velocity, and if
        /// <c>data.dead</c> is still true while that happens, Character.FixedUpdate fights the
        /// warp every step. Waiting here closes the window where a late-arriving death RPC lands
        /// inside the warp. Not a confirmed fix for the separate "ragdoll convulses and I get
        /// catapulted" bug, which remains open.
        /// </summary>
        private const int ReviveSettleSteps = 10;

        /// <summary>
        /// Sets <c>MapHandler.LastRevivedSegment</c> to the segment a restored run has just
        /// been placed in. A checkpoint load jumps a brand-new run straight to a mid-run
        /// segment, leaving this value stale from the fresh-run start; that made
        /// <c>BaseCampHasRevived</c> false, so a late-joining client's auto-revive never fired
        /// and it was left dead at DeathPos for our own code to drag back - the root cause of
        /// the ragdoll-thrash bug. The setter is private, hence the reflection.
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

                _log.Trace($"OwnTeleportSequence: LastRevivedSegment set to {(int)segment} ({segment}).");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnTeleportSequence: could not sync LastRevivedSegment ({e.Message}); "
                    + "a late-joining client may be left dead at DeathPos for us to revive by hand.");
            }
        }

        /// <summary>
        /// Revives a dead client and places it at <paramref name="target"/> in a single atomic
        /// call, using vanilla's own <c>RPCA_ReviveAtPosition</c>, so the death state and
        /// position write can never interleave or be separated by a settle - avoiding the 7km
        /// warp our old revive-then-warp path needed (and the window for a late death RPC to
        /// land mid-warp) since a dead body sits at DeathPos, 7km from the campfire. Returns
        /// false if the character wasn't dead or the RPC couldn't be sent.
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

            // Only data.dead: passedOut/fullyPassedOut go through HandlePassedOut, which warps
            // fine, and reviving a merely passed-out character would clear afflictions/thorns unnecessarily.
            if (!ch.data.dead) return false;

            try
            {
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
        /// Clients used to all warp to the exact same coordinate, so bodies arrived fully
        /// interpenetrating and PhysX resolved it with a large separating impulse (players
        /// flung 16-40m). Spread via a golden-angle offset off the player's ActorNumber, stable
        /// across re-sends so the arrival check keeps testing the same target. This is likely
        /// the explanation for the catapult specifically, but not for the separate,
        /// still-unidentified ragdoll-thrash bug.
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
        /// Blocks until every player has a registered <c>Character</c>, so the segment advance
        /// that follows can't catch one mid-spawn. Times out rather than stalling the load.
        /// </summary>
        private const int RequiredStableFrames = 5;

        private IEnumerator WaitForEveryPlayerToRegister()
        {
            float timeout = Mathf.Max(1f, _cfg != null ? _cfg.CoopReadyTimeout.Value : 30f);
            float started = Time.time;
            int expected = 0;
            int stableFrames = 0;

            while (Time.time - started < timeout)
            {
                // Shared with the Restart/Resume StartRun gate - see PlayerRegistration
                bool all = PlayerRegistration.AllRegistered(out int registered, out expected);

                // Require the full count to HOLD for a few consecutive frames. A single good
                // frame proves nothing when a respawn is in flight: the old body can still be
                // alive on the frame we look and destroyed on the next
                if (all) stableFrames++;
                else stableFrames = 0;

                if (stableFrames >= RequiredStableFrames)
                {
                    _log.Trace($"OwnTeleportSequence: all {registered}/{expected} player character(s) registered and "
                        + $"stable for {RequiredStableFrames} frames after {Time.time - started:F1}s; safe to advance "
                        + "the segment.");
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

            // Bounds total re-sends and requires a real grace interval since the last send, so
            // the warp gets a full round trip to land before we decide it "didn't work" (see
            // PluginConfig) - otherwise a slow client gets hammered with redundant safety warps.
            int maxResends = Mathf.Max(0, _cfg.OwnMaxClientWarpResends.Value);
            float resendGrace = Mathf.Max(0f, _cfg.OwnClientWarpResendGraceSeconds.Value);

            for (int i = 0; i < framesToWait; i++) yield return null;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character ch = player.character;
                if (ch == null || ch == Character.localCharacter) continue;

                // A dead client cannot be warped (Character.FixedUpdate re-parks the body at
                // DeathPos every physics step), and a client joining late is killed on arrival
                // well after ReviveDeadPlayers already ran, so revive here too, right before the warp.
                // Each client lands on its own point around the host - see SpreadTargetFor.
                // Computed once per client and reused for every re-send below.
                Vector3 clientTarget = SpreadTargetFor(ch, hostPos);
                if (clientTarget != hostPos)
                    _log.Trace($"OwnTeleportSequence.TeleportClientsToHost: {ch.characterName} lands at "
                        + $"{clientTarget} ({(clientTarget - hostPos).magnitude:F1}m off the host's own arrival "
                        + "point, so the bodies do not arrive inside each other).");

                bool revivedAtPosition = ReviveDeadClientAtPosition(ch, clientTarget);
                if (revivedAtPosition)
                {
                    // Let it land, then re-check: a late DeathOnArrival kill can still arrive
                    // after ours, putting the body back at DeathPos. One more atomic
                    // revive-and-place handles that, never a plain warp into a body something else still owns.
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
                    // Fallback only: the atomic RPC couldn't be sent.
                    for (int i = 0; i < ReviveSettleSteps; i++) yield return new WaitForFixedUpdate();
                }

                if (ch == null || ch.photonView == null) continue;

                // RPCA_ReviveAtPosition already placed them; a second warp on top would be the
                // redundant back-to-back warp that makes Character.WarpPlayer abort its own move.
                if (!revivedAtPosition)
                    ch.photonView.RPC("WarpPlayerRPC", RpcTarget.All, clientTarget, false);
                float startTime = Time.time;
                float lastSend = Time.time;
                int tried = 0;
                bool arrived = false;

                while (Time.time - startTime < 30f)
                {
                    // Judge arrival horizontally, not by height: hostPos is a few metres up in the
                    // air, so a warped client falls and legitimately rests below hostPos.y and
                    // stays there - a y-proximity check would never read as arrived and re-warp forever.
                    if (Mathf.Abs(ch.Head.x - clientTarget.x) < 6f && Mathf.Abs(ch.Head.z - clientTarget.z) < 6f)
                    {
                        _log.Trace($"OwnTeleportSequence.TeleportClientsToHost: warped {ch.player.name} after {tried} attempts.");
                        arrived = true;
                        break;
                    }

                    // Re-send, but hold off until the grace interval has elapsed since the last
                    // warp, so a client whose teleport is still in flight isn't hammered with warps.
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

                // Covers both give-up paths: the maxResends bail-out above, and this loop's own 30s timeout.
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
