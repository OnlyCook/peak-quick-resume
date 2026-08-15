using System;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Puts the Strange Gem back on the scout statue after a load into the final segment
    /// (<c>Segment.TheKiln</c>). The gem isn't placed by a level <c>ISpawner</c>; it's spawned
    /// once by <c>Peak.ScoutStatue.Update</c> the first frame the statue is active, then our
    /// own world-loot cleanup deletes it again (its Photon view isn't a room view, so it looks
    /// like ordinary dropped loot). <c>spawnedInitialGem</c> latches after that first spawn, so
    /// vanilla never retries and the statue stays empty for the rest of the run. Loading a
    /// Gloom checkpoint is unaffected since the statue only activates later, via the normal
    /// campfire transition.
    ///
    /// Uses reflection, not a direct <c>Peak.ScoutStatue</c> reference: the type is new in
    /// PEAK 2.x, so a hard reference would throw on older builds. Degrades to a log line instead.
    /// </summary>
    internal static class StrangeGemRestore
    {
        private const string ScoutStatueTypeName = "Peak.ScoutStatue, Assembly-CSharp";

        private static Type _statueType;
        private static bool _statueTypeResolved;

        /// <summary>
        /// Re-spawns the gem on every active scout statue currently missing one. Safe to call
        /// when there's nothing to do; never duplicates the gem. Host only, TheKiln only.
        /// </summary>
        public static void Restore(ManualLogSource log)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            Type statueType = ResolveStatueType(log);
            if (statueType == null) return;

            try
            {
                FieldInfo spawnedGemField = statueType.GetField("spawnedGem_master", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo gemPrefabField = statueType.GetField("gemPrefab", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo spawnGemMethod = statueType.GetMethod("SpawnGem_Master", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (spawnedGemField == null || spawnGemMethod == null)
                {
                    log?.LogWarning("StrangeGemRestore: Peak.ScoutStatue no longer exposes spawnedGem_master/SpawnGem_Master "
                        + "- the Strange Gem will be missing after a load into the final segment until this is re-ported.");
                    return;
                }

                UnityEngine.Object[] statues = UnityEngine.Object.FindObjectsByType(statueType, FindObjectsSortMode.None);
                log.Trace($"StrangeGemRestore: checking {statues.Length} scout statue(s).");

                foreach (UnityEngine.Object statueObject in statues)
                {
                    try
                    {
                        Component statue = statueObject as Component;
                        if (statue == null) continue;

                        // Only the statue in the segment we just loaded into is active/live.
                        if (!statue.gameObject.activeInHierarchy) continue;

                        if (!IsDestroyedOrMissing(spawnedGemField.GetValue(statue) as UnityEngine.Object))
                        {
                            log.Trace($"StrangeGemRestore: {statue.gameObject.name} still has its gem, nothing to do.");
                            continue;
                        }

                        // Second guard against duplicates: also check no prefab instance exists elsewhere.
                        GameObject gemPrefab = gemPrefabField?.GetValue(statue) as GameObject;
                        if (gemPrefab != null && GemInstanceExists(gemPrefab.name))
                        {
                            log.Trace($"StrangeGemRestore: a {gemPrefab.name} already exists in the scene, "
                                + "leaving it alone.");
                            continue;
                        }

                        // Exactly what vanilla's own RPC_InsertAmulet(99) path does on the host.
                        spawnGemMethod.Invoke(statue, null);

                        bool spawned = !IsDestroyedOrMissing(spawnedGemField.GetValue(statue) as UnityEngine.Object);
                        if (spawned) log?.LogInfo($"StrangeGemRestore: re-spawned the Strange Gem on {statue.gameObject.name}.");
                        else log?.LogWarning($"StrangeGemRestore: SpawnGem_Master on {statue.gameObject.name} did not "
                            + "produce a gem - the statue will be empty for this run.");
                    }
                    catch (Exception e)
                    {
                        log?.LogError($"StrangeGemRestore: re-spawning the gem on one statue failed: {e}");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"StrangeGemRestore.Restore failed: {e}");
            }
        }

        /// <summary>Unity's fake-null semantics: a gem destroyed by our cleanup leaves the field pointing at a destroyed object.</summary>
        private static bool IsDestroyedOrMissing(UnityEngine.Object obj) => obj == null;

        private static bool GemInstanceExists(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;

            // PUN's default pool instantiates through Object.Instantiate, so the live object is
            // named "<prefab>(Clone)"; match on the prefix to cover both spellings
            foreach (Item item in UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None))
            {
                if (item != null && item.name.StartsWith(prefabName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static Type ResolveStatueType(ManualLogSource log)
        {
            if (_statueTypeResolved) return _statueType;
            _statueTypeResolved = true;

            try
            {
                _statueType = Type.GetType(ScoutStatueTypeName, throwOnError: false);
            }
            catch (Exception e)
            {
                log?.LogError($"StrangeGemRestore: resolving {ScoutStatueTypeName} threw: {e}");
            }

            if (_statueType == null)
                log?.LogWarning($"StrangeGemRestore: {ScoutStatueTypeName} not found in this game build - skipping the "
                    + "Strange Gem restore (harmless on any build that doesn't have the amulet statue).");

            return _statueType;
        }
    }
}
