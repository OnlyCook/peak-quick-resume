using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Mitigates a player-facing (not a bug, but an easy trap) footgun: dropping your
    /// backpack on the ground to rearrange items, then someone lights the campfire
    /// before it's picked back up. "PEAK Checkpoint Save" only ever saves a player's
    /// EQUIPPED backpack, so a dropped one on the ground is silently left out of the
    /// save entirely - reloading that checkpoint later, it and everything in it is
    /// just gone (the level itself is regenerated fresh, the physical dropped prop
    /// doesn't exist to find again even if you knew where to look)
    ///
    /// Approach: the host watches the single nearest-to-itself unlit campfire (there's
    /// only ever one "next" unlit campfire in play at a time - if a buggy load somehow
    /// produces two, the farther one can't be the real one anyway, so it's ignored) for
    /// any backpack dropped within 50m of it, by ANY player. If that same campfire is
    /// then lit while a tracked drop is still sitting on the ground, un-picked-up, and
    /// its owner hasn't since equipped a different backpack, we inject it (contents and
    /// all) into that owner's just-written save file as a phantom "equipped" backpack -
    /// even though by then it no longer really is one - before our own archive step
    /// copies that file. Tracking resets the moment a DIFFERENT campfire becomes the
    /// nearest unlit one, or the watched one gets lit (whether or not anything needed
    /// restoring), matching a fresh "next campfire" every time
    ///
    /// Host-only throughout (only the host ever writes save files); a no-op on clients
    /// </summary>
    public static class BackpackSaveMitigation
    {
        private const byte BackpackSlotId = 3; // Player.backpackSlot's own fixed slot index (see Player's ctor)
        private const float WatchRadius = 50f;

        private static ManualLogSource _log;

        private class TrackedDrop
        {
            public string UserId;
            public Backpack Backpack;
        }

        // The single unlit campfire currently being watched, and every backpack drop
        // seen near it since we started watching it (cleared whenever a DIFFERENT
        // campfire becomes the nearest unlit one, or this one gets lit - see remarks)
        private static Campfire _watchedCampfire;
        private static readonly Dictionary<int, TrackedDrop> _tracked = new Dictionary<int, TrackedDrop>();

        // Restorations decided the instant a watched campfire lights, but not yet
        // written to disk: our own autosave only runs AFTER Interact_CastFinished (and
        // the Light_Rpc call it makes) fully returns, so there's no save file to patch
        // yet at the point we make this decision. SaveArchive.PatchCanonicalFileForUser
        // (called from OwnSaveCapture right after the file actually exists) is what
        // applies these
        private class PendingRestore
        {
            public string UserId;
            public SaveTarget Target;
            public JArray BackpackItemStates;
            public int BackpackViewId;
        }
        private static readonly List<PendingRestore> _pending = new List<PendingRestore>();

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var dropTarget = AccessTools.Method(typeof(CharacterItems), "DropItemRpc");
                harmony.Patch(dropTarget, postfix: new HarmonyMethod(typeof(BackpackSaveMitigation), nameof(OnDropItemRpc)));

                var lightTarget = AccessTools.Method(typeof(Campfire), "Light_Rpc");
                harmony.Patch(lightTarget, postfix: new HarmonyMethod(typeof(BackpackSaveMitigation), nameof(OnLightRpc)));

                log.LogInfo("BackpackSaveMitigation: patched DropItemRpc/Light_Rpc (dropped-backpack save mitigation).");
            }
            catch (Exception e)
            {
                log.LogError($"BackpackSaveMitigation.Apply failed (non-fatal): {e}");
            }
        }

        // DropItemRpc runs on every machine (RpcTarget.All), but only the host needs to
        // track any of this - IsHostMachine() makes this a no-op everywhere else
        private static void OnDropItemRpc(byte slotID, Vector3 spawnPos, Character ___character)
        {
            try
            {
                if (slotID != BackpackSlotId || !IsHostMachine() || ___character == null) return;

                var nearestUnlit = FindNearestUnlitCampfire();
                if (nearestUnlit == null || Vector3.Distance(nearestUnlit.transform.position, spawnPos) > WatchRadius)
                    return;

                var backpack = FindNearestGroundBackpack(spawnPos);
                if (backpack == null) return;

                // A different nearest-unlit-campfire than whatever we were watching
                // invalidates the old tracking (see class remarks)
                if (_watchedCampfire != nearestUnlit)
                {
                    _tracked.Clear();
                    _watchedCampfire = nearestUnlit;
                }

                string userId = SafeUserId(___character);
                _tracked[backpack.photonView.ViewID] = new TrackedDrop { UserId = userId, Backpack = backpack };
                _log?.LogInfo($"[backpack-mitigation] Tracking a dropped backpack near the unlit campfire (owner userId '{userId}').");
            }
            catch (Exception e)
            {
                _log?.LogError($"[backpack-mitigation] OnDropItemRpc failed (non-fatal): {e}");
            }
        }

        // updateSegment is only true for a REAL ignition (Interact_CastFinished's light
        // branch, or DebugLight); the late-joiner state-sync call (CheckIfSyncNeeded)
        // and LightWithoutReveal both pass false, and aren't campfires actually being lit
        private static void OnLightRpc(Campfire __instance, bool updateSegment)
        {
            try
            {
                if (!updateSegment || !IsHostMachine()) return;

                if (_watchedCampfire != __instance)
                {
                    // Not the fire we were tracking drops around - nothing to restore,
                    // but still reset so the next unlit campfire starts clean
                    _tracked.Clear();
                    _watchedCampfire = null;
                    return;
                }

                foreach (var drop in _tracked.Values)
                {
                    if (drop.Backpack == null) continue; // destroyed/despawned since
                    if (drop.Backpack.itemState != ItemState.Ground) continue; // picked up again
                    if (Vector3.Distance(__instance.transform.position, drop.Backpack.transform.position) > WatchRadius) continue;
                    if (PlayerAlreadyHasBackpack(drop.UserId)) continue; // equipped a different one by now

                    if (!TryBuildBackpackItemStates(drop.Backpack, out var states) || states.Count == 0) continue;

                    // Lighting a campfire only ever happens mid-run, so the currently
                    // active run (whatever RunLauncher/Ascents report right now) IS the
                    // one the checkpoint mod's own autosave is about to write to - this
                    // has to match at the exact moment of the save, see
                    // SaveArchive.PatchCanonicalFileForUser for why guessing wrong here
                    // silently patches an unrelated save file instead
                    SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);

                    _pending.Add(new PendingRestore
                    {
                        UserId = drop.UserId,
                        Target = target,
                        BackpackItemStates = states,
                        BackpackViewId = drop.Backpack.photonView.ViewID,
                    });
                    _log?.LogInfo($"[backpack-mitigation] Queued a backpack restore for userId '{drop.UserId}' ({target}, {states.Count} item(s)).");
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[backpack-mitigation] OnLightRpc failed (non-fatal): {e}");
            }
            finally
            {
                _tracked.Clear();
                _watchedCampfire = null;
            }
        }

        /// <summary>
        /// Applies any pending backpack restorations queued by OnLightRpc to the
        /// canonical save file(s) our own save just wrote for this category, BEFORE
        /// SaveArchive.Sync copies them into the archive. Called from OwnSaveCapture right
        /// after the save is written; a no-op when nothing is pending
        /// </summary>
        public static void ApplyPendingRestores(bool offline, ManualLogSource log)
        {
            if (_pending.Count == 0) return;
            foreach (var restore in _pending)
            {
                bool applied = SaveArchive.PatchCanonicalFileForUser(offline, restore.Target, restore.UserId, json =>
                {
                    json["hasBackpack"] = true;
                    json["backpackItemStates"] = restore.BackpackItemStates;
                }, log);

                if (applied)
                    log?.LogInfo($"[backpack-mitigation] Restored a dropped backpack into the save for userId '{restore.UserId}'.");
                else
                    log?.LogWarning($"[backpack-mitigation] Could not find a save file for userId '{restore.UserId}' to restore the dropped backpack into.");
            }
            _pending.Clear();
        }

        /// <summary>
        /// PhotonView IDs of every dropped Backpack currently queued for a phantom-
        /// equip restore (see <see cref="ApplyPendingRestores"/>) - called from
        /// <see cref="WorldItemRestore"/>'s capture, BEFORE this class's own
        /// <see cref="ApplyPendingRestores"/> clears <c>_pending</c> (both run from
        /// within the same OwnSaveCapture call, this one first), so WorldItemRestore's
        /// generic ground-item sweep doesn't ALSO save the same physical backpack as a
        /// plain world item - that would restore it twice: once equipped on the owner
        /// (this class's job) and once dropped on the ground again (WorldItemRestore's)
        /// </summary>
        public static HashSet<int> GetPendingBackpackViewIds()
        {
            var ids = new HashSet<int>();
            foreach (var restore in _pending) ids.Add(restore.BackpackViewId);
            return ids;
        }

        private static bool TryBuildBackpackItemStates(Backpack backpack, out JArray states)
        {
            states = new JArray();
            try
            {
                if (backpack.data == null
                    || !backpack.data.TryGetDataEntry<BackpackData>(DataEntryKey.BackpackData, out var bpData)
                    || bpData?.itemSlots == null)
                    return false;

                for (byte slot = 0; slot < bpData.itemSlots.Length; slot++)
                {
                    ItemSlot itemSlot = bpData.itemSlots[slot];
                    if (itemSlot == null || itemSlot.IsEmpty() || itemSlot.prefab == null || itemSlot.data == null) continue;

                    var values = new JObject();
                    foreach (var kv in OwnItemStateIO.ReadItemStateValues(itemSlot.data, itemSlot.prefab.itemID))
                        values[kv.Key] = new JObject { ["type"] = kv.Value.TypeName, ["value"] = kv.Value.Value };

                    states.Add(new JObject
                    {
                        ["slotIndex"] = slot,
                        ["itemId"] = itemSlot.prefab.itemID,
                        ["values"] = values,
                    });
                }
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[backpack-mitigation] Could not read dropped backpack contents: {e.Message}");
                return false;
            }
        }

        private static bool PlayerAlreadyHasBackpack(string userId)
        {
            try
            {
                foreach (var ch in PlayerHandler.GetAllPlayerCharacters())
                {
                    if (SafeUserId(ch) != userId) continue;
                    return ch.player?.backpackSlot != null && ch.player.backpackSlot.hasBackpack;
                }
            }
            catch { /* fall through: couldn't tell, default to allowing the restore */ }
            return false;
        }

        private static Campfire FindNearestUnlitCampfire()
        {
            try
            {
                var host = Character.localCharacter;
                if (host == null) return null;
                Vector3 pos = host.Center;

                Campfire nearest = null;
                float best = float.MaxValue;
                foreach (var c in UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None))
                {
                    if (c.Lit) continue;
                    float d = Vector3.Distance(c.transform.position, pos);
                    if (d < best) { best = d; nearest = c; }
                }
                return nearest;
            }
            catch { return null; }
        }

        private static Backpack FindNearestGroundBackpack(Vector3 near)
        {
            Backpack nearest = null;
            float best = float.MaxValue;
            foreach (var b in UnityEngine.Object.FindObjectsByType<Backpack>(FindObjectsSortMode.None))
            {
                if (b.itemState != ItemState.Ground) continue;
                float d = Vector3.Distance(b.transform.position, near);
                if (d < best) { best = d; nearest = b; }
            }
            return nearest;
        }

        private static string SafeUserId(Character c)
        {
            try { return c.photonView.Owner?.UserId ?? ""; }
            catch { return ""; }
        }

        private static bool IsHostMachine() => PhotonNetwork.OfflineMode || PhotonNetwork.IsMasterClient;
    }
}
