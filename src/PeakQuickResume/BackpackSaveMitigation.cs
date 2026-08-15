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
    /// Mitigates a player footgun: since saves only capture a player's EQUIPPED
    /// backpack, dropping it on the ground and then lighting the campfire before
    /// picking it back up would silently lose it and its contents.
    ///
    /// The host watches the single nearest unlit campfire for any backpack dropped
    /// within 50m by any player. If that campfire is then lit while a tracked drop is
    /// still on the ground and unclaimed, its contents are injected into the owner's
    /// just-written save as a phantom "equipped" backpack before the archive step
    /// copies the file. Tracking resets whenever a different campfire becomes nearest,
    /// or the watched one is lit. Host-only; no-op on clients.
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

        // Keyed by owner userId, not backpack ViewID: a save only has room for one
        // equipped backpack per player, so if a player drops a second before the fire
        // lights, the newer drop should replace the older in tracking (letting the
        // superseded one fall through to WorldItemRestore's normal ground-item save)
        // rather than both being queued and the JSON write clobbering one silently.
        private static Campfire _watchedCampfire;
        private static readonly Dictionary<string, TrackedDrop> _tracked = new Dictionary<string, TrackedDrop>();

        // Decided the instant a watched campfire lights, but the save file doesn't exist
        // yet at that point; ApplyPendingRestores (called from OwnSaveCapture once it does) applies these.
        private class PendingRestore
        {
            public string UserId;
            public SaveTarget Target;
            public JArray BackpackItemStates;
            public int BackpackViewId;

            // Which backpack variant was dropped, since 2.0.a it may be a Fannypack/Jetpack/Rocketpack too.
            public int BackpackType;

            public JObject BackpackOwnValues;
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

        // DropItemRpc runs on every machine; IsHostMachine() makes this a no-op elsewhere.
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

                if (_watchedCampfire != nearestUnlit)
                {
                    _tracked.Clear();
                    _watchedCampfire = nearestUnlit;
                }

                string userId = SafeUserId(___character);
                if (_tracked.ContainsKey(userId))
                    _log.Trace($"[backpack-mitigation] Replacing an earlier tracked drop for userId '{userId}' with this newer one (the earlier one now falls through to WorldItemRestore's normal ground-item save).");
                _tracked[userId] = new TrackedDrop { UserId = userId, Backpack = backpack };
                _log.Trace($"[backpack-mitigation] Tracking a dropped backpack near the unlit campfire (owner userId '{userId}').");
            }
            catch (Exception e)
            {
                _log?.LogError($"[backpack-mitigation] OnDropItemRpc failed (non-fatal): {e}");
            }
        }

        // updateSegment is only true for a real ignition; state-sync/reveal calls pass false.
        private static void OnLightRpc(Campfire __instance, bool updateSegment)
        {
            try
            {
                if (!updateSegment || !IsHostMachine()) return;

                if (_watchedCampfire != __instance)
                {
                    _tracked.Clear();
                    _watchedCampfire = null;
                    return;
                }

                foreach (var drop in _tracked.Values)
                {
                    if (drop.Backpack == null) continue;
                    if (drop.Backpack.itemState != ItemState.Ground) continue;
                    if (Vector3.Distance(__instance.transform.position, drop.Backpack.transform.position) > WatchRadius) continue;
                    if (PlayerAlreadyHasBackpack(drop.UserId)) continue;

                    if (!TryBuildBackpackItemStates(drop.Backpack, out var states) || states.Count == 0) continue;

                    // Must match the run our own autosave is about to write to, or this
                    // silently patches an unrelated save file (see SaveArchive.PatchSaveFile).
                    SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);

                    _pending.Add(new PendingRestore
                    {
                        UserId = drop.UserId,
                        Target = target,
                        BackpackItemStates = states,
                        BackpackViewId = drop.Backpack.photonView.ViewID,
                        BackpackType = (int)drop.Backpack.backpackType,
                        BackpackOwnValues = BuildBackpackOwnValues(drop.Backpack),
                    });
                    _log.Trace($"[backpack-mitigation] Queued a backpack restore for userId '{drop.UserId}' ({target}, {states.Count} item(s)).");
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
        /// Applies pending backpack restorations queued by OnLightRpc to the save file(s)
        /// OwnSaveCapture just wrote for save event stamp. The file is addressed by its
        /// exact path, never searched for, so a restore can't land in the wrong save.
        /// </summary>
        public static void ApplyPendingRestores(bool offline, string stamp, ManualLogSource log)
        {
            if (_pending.Count == 0) return;
            foreach (var restore in _pending)
            {
                string path = OwnSavePaths.For(restore.Target, offline, restore.UserId, stamp);
                bool applied = SaveArchive.PatchSaveFile(path, json =>
                {
                    json["hasBackpack"] = true;
                    // Must be written alongside hasBackpack since 2.0.a (see BackpackTypeCompat.FromSave).
                    json["backpackType"] = restore.BackpackType;
                    json["backpackItemStates"] = restore.BackpackItemStates;
                    if (restore.BackpackOwnValues != null)
                        json["backpackOwnValues"] = restore.BackpackOwnValues;
                }, log);

                if (applied)
                    log.Trace($"[backpack-mitigation] Restored a dropped backpack into the save for userId '{restore.UserId}'.");
                else
                    log?.LogWarning($"[backpack-mitigation] Could not find a save file for userId '{restore.UserId}' to restore the dropped backpack into.");
            }
            _pending.Clear();
        }

        /// <summary>
        /// PhotonView IDs of every dropped Backpack queued for a phantom-equip restore.
        /// Called from WorldItemRestore's capture (before ApplyPendingRestores clears
        /// _pending) so its ground-item sweep doesn't also save the same backpack, which
        /// would restore it twice.
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

        /// <summary>The dropped backpack's own stats (fuel, for a Jetpack/Rocketpack), in OwnSaveData.backpackOwnValues' JSON shape.</summary>
        private static JObject BuildBackpackOwnValues(Backpack backpack)
        {
            try
            {
                if (backpack?.data == null) return null;

                var values = new JObject();
                foreach (var kv in OwnItemStateIO.ReadItemStateValues(backpack.data, backpack.itemID))
                    values[kv.Key] = new JObject { ["type"] = kv.Value.TypeName, ["value"] = kv.Value.Value };

                return values.Count > 0 ? values : null;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[backpack-mitigation] Could not read the dropped backpack's own stats: {e.Message}");
                return null;
            }
        }

        private static bool PlayerAlreadyHasBackpack(string userId)
        {
            try
            {
                foreach (var ch in PlayerHandler.GetAllPlayerCharacters())
                {
                    if (SafeUserId(ch) != userId) continue;
                    return BackpackTypeCompat.HasAny(ch.player);
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
