using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own port of the checkpoint mod's post-load world-state cleanup. None of
    /// this RESTORES anything from the save file - it is entirely a "clean slate"
    /// pass so a repeat load (loading again after already having loaded once this
    /// run) doesn't end up with duplicated or orphaned player-placed props/loot from
    /// a different timeline; segment loot is then deterministically respawned by the
    /// level's own <c>ISpawner</c>s (called separately from <see cref="OwnTeleportSequence"/>,
    /// matching decompile 2415-2434)
    ///
    /// Three pieces, ported field-for-field:
    ///  - <see cref="ResetWorldLoot"/> = <c>ResetLuggage</c> a.k.a. "ResetWorldLoot" in
    ///    the original's own log lines (decompile 3527-3627): resets luggage/chests via
    ///    reflective duck-typing, then destroys stray dropped-item PhotonViews
    ///  - <see cref="DestroyStaleWorldObjects"/> = the segment-object destroy-by-name-
    ///    substring pass + stray <c>MagicBeanVine</c> destroy, both inlined directly in
    ///    <c>CustomJumpToSegment</c> (decompile 2441-2504)
    ///  - <see cref="DestroyLeftoverHeldItems"/> = the "destroy items I'm holding that
    ///    aren't a Marshmallow/Glizzy" pass, also inlined in <c>CustomJumpToSegment</c>
    ///    (decompile 2372-2404)
    /// </summary>
    public static class OwnWorldLootReset
    {
        private static readonly string[] LuggageResetMethodNames =
            { "ResetRecievedData", "SetKinematicAndResetSyncData", "Reset", "Refresh", "Rebuild", "Invalidate" };
        private static readonly string[] ChestResetMethodNames = { "Reset", "Refresh", "Rebuild", "Invalidate" };

        private static readonly string[] StaleSegmentObjectNames =
        {
            "ChainShootable", "RopeAnchor", "RopeDynamic", "PortableStovetop_Placed", "ClimbingSpikeHammered",
            "CloudFungus", "Flag_Planted_Checkpoint", "ShelfShroom", "ScoutCannon_Placed", "BounceShroomSpawn",
            "MagicBean",
        };

        /// <summary>Mirrors ResetLuggage/"ResetWorldLoot" exactly (decompile 3527-3627)</summary>
        public static void ResetWorldLoot(ManualLogSource log)
        {
            try
            {
                int luggageCount = 0, chestCount = 0;
                foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (behaviour == null) continue;
                    string typeName = behaviour.GetType().Name;

                    if (typeName.IndexOf("Luggage", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        luggageCount++;
                        ResetLootObjectGeneric(behaviour, LuggageResetMethodNames);
                    }
                    else if (typeName.IndexOf("RespawnChest", StringComparison.OrdinalIgnoreCase) >= 0
                        || (typeName.IndexOf("Chest", StringComparison.OrdinalIgnoreCase) >= 0
                            && typeName.IndexOf("Respawn", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        chestCount++;
                        ResetLootObjectGeneric(behaviour, ChestResetMethodNames);
                    }
                }
                log?.LogInfo($"OwnWorldLootReset: ResetWorldLoot touched luggage={luggageCount}, chests={chestCount}.");
            }
            catch (Exception e)
            {
                log?.LogError($"OwnWorldLootReset.ResetWorldLoot (luggage/chests) failed: {e}");
            }

            try
            {
                int destroyed = 0, skipped = 0;
                foreach (PhotonView pv in UnityEngine.Object.FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
                {
                    if (pv == null) continue;

                    bool isRoomView;
                    try { isRoomView = pv.IsRoomView; }
                    catch { skipped++; continue; }
                    if (isRoomView) continue;

                    GameObject go = pv.gameObject;
                    if (go == null) continue;

                    if (go.GetComponentInParent<Player>(true) != null || go.GetComponentInParent<Character>(true) != null)
                    {
                        skipped++;
                        continue;
                    }

                    bool looksLikeDroppedItem = go.GetComponentInChildren<Rigidbody>(true) != null
                        && (go.GetComponent("Item") != null || go.GetComponent("ItemPickup") != null
                            || go.name.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0
                            || go.name.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
                            || go.name.IndexOf("Drop", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!looksLikeDroppedItem)
                    {
                        skipped++;
                        continue;
                    }

                    if (!PhotonNetwork.OfflineMode) PhotonNetwork.Destroy(pv);
                    else UnityEngine.Object.Destroy(go);
                    destroyed++;
                }
                log?.LogInfo($"OwnWorldLootReset: ResetDroppedRuntimeItems destroyed={destroyed}, skipped={skipped}.");
            }
            catch (Exception e)
            {
                log?.LogError($"OwnWorldLootReset.ResetWorldLoot (dropped items) failed: {e}");
            }
        }

        /// <summary>
        /// Mirrors the segment-object destroy-by-name-substring pass + stray
        /// MagicBeanVine destroy, both inlined in CustomJumpToSegment (decompile
        /// 2441-2504). Only called when <c>loadedSaveFileThisRound</c> (a repeat
        /// load), matching the original exactly
        /// </summary>
        public static void DestroyStaleWorldObjects(ManualLogSource log)
        {
            try
            {
                foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                {
                    try
                    {
                        if (go == null) continue;
                        if (go.transform.parent != null && go.GetComponentInParent<Player>() != null) continue;

                        PhotonView pv = go.GetComponent<PhotonView>();
                        if (pv == null) continue;

                        foreach (string needle in StaleSegmentObjectNames)
                        {
                            if (go.name.Contains(needle) && pv.CreatorActorNr > 0 && !pv.IsRoomView)
                            {
                                PhotonNetwork.Destroy(go);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // matches the original's own inner per-object try/catch swallow
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"OwnWorldLootReset.DestroyStaleWorldObjects (segment objects) failed: {e}");
            }

            try
            {
                foreach (MagicBeanVine vine in UnityEngine.Object.FindObjectsByType<MagicBeanVine>(FindObjectsSortMode.None))
                {
                    if (vine == null) continue;
                    if ((vine.transform.parent == null || vine.GetComponentInParent<Player>() == null)
                        && vine.name.Contains("MagicBeanVine"))
                    {
                        UnityEngine.Object.Destroy(vine.gameObject);
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"OwnWorldLootReset.DestroyStaleWorldObjects (MagicBeanVine) failed: {e}");
            }
        }

        /// <summary>
        /// Mirrors the "destroy items I'm holding that aren't a Marshmallow/Glizzy"
        /// pass inlined in CustomJumpToSegment (decompile 2372-2404). Master-client
        /// only, and only when <c>loadedSaveFileThisRound</c> (a repeat load),
        /// matching the original exactly. PEAKapalooza's branch (decompile 2384-2390)
        /// is deliberately not ported (see ROADMAP.md Phase 8)
        /// </summary>
        public static void DestroyLeftoverHeldItems(ManualLogSource log)
        {
            try
            {
                foreach (Item item in UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None))
                {
                    if (item == null) continue;
                    if (item.transform.parent != null && item.GetComponentInParent<Player>() != null) continue;
                    if (item.photonView == null || !item.photonView.IsMine) continue;

                    if (!item.name.Contains("Marshmallow") && !item.name.Contains("Glizzy"))
                        PhotonNetwork.Destroy(item.photonView);
                }
            }
            catch (Exception e)
            {
                log?.LogError($"OwnWorldLootReset.DestroyLeftoverHeldItems failed: {e}");
            }
        }

        // Mirrors ResetLootObjectGeneric/TrySetBoolFieldOrProp/TrySetEnumToDefault/
        // TryInvokeNoArg exactly (decompile 3629-3714)
        private static void ResetLootObjectGeneric(MonoBehaviour obj, string[] methodNames)
        {
            TrySetBoolFieldOrProp(obj, "opened", false);
            TrySetBoolFieldOrProp(obj, "isOpened", false);
            TrySetBoolFieldOrProp(obj, "hasOpened", false);
            TrySetBoolFieldOrProp(obj, "looted", false);
            TrySetBoolFieldOrProp(obj, "isLooted", false);
            TrySetEnumToDefault(obj, "state");
            TrySetEnumToDefault(obj, "luggageState");

            foreach (string methodName in methodNames)
                if (TryInvokeNoArg(obj, methodName))
                    break;

            TrySetBoolFieldOrProp(obj, "canInteract", true);
            TrySetBoolFieldOrProp(obj, "interactable", true);
        }

        private static bool TryInvokeNoArg(object inst, string methodName)
        {
            try
            {
                MethodInfo method = inst.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null || method.GetParameters().Length != 0) return false;
                method.Invoke(inst, null);
                return true;
            }
            catch { return false; }
        }

        private static void TrySetBoolFieldOrProp(object inst, string name, bool value)
        {
            try
            {
                Type type = inst.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool)) { field.SetValue(inst, value); return; }

                PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool)) prop.SetValue(inst, value);
            }
            catch { /* matches the original: best-effort, silently skip */ }
        }

        private static void TrySetEnumToDefault(object inst, string name)
        {
            try
            {
                Type type = inst.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType.IsEnum) { field.SetValue(inst, Enum.ToObject(field.FieldType, 0)); return; }

                PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum) prop.SetValue(inst, Enum.ToObject(prop.PropertyType, 0));
            }
            catch { /* matches the original: best-effort, silently skip */ }
        }
    }
}
