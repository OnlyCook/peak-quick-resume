using System;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Puts the Strange Gem back on the scout statue behind the peak after a load into
    /// the final segment (THE CITADEL, and the Volcano island's THE KILN variant, both
    /// <c>Segment.TheKiln</c> = 4).
    ///
    /// WHY THIS IS NEEDED: the gem is not placed by a level <c>ISpawner</c> like the rest
    /// of the world loot, so nothing in our restore or in the game's own segment logic
    /// ever re-creates it. It is spawned exactly once, by <c>Peak.ScoutStatue</c> itself,
    /// on the first frame the statue is active for the master client:
    /// <code>
    /// private void Update() {
    ///     if (PhotonNetwork.InRoom &amp;&amp; PhotonNetwork.IsMasterClient &amp;&amp; !spawnedInitialGem) {
    ///         SpawnGem_Master();
    ///         spawnedInitialGem = true;
    ///     }
    /// }
    /// private void SpawnGem_Master() {
    ///     if (PhotonNetwork.IsMasterClient &amp;&amp; spawnedGem_master == null) {
    ///         spawnedGem_master = PhotonNetwork.Instantiate("0_Items/" + gemPrefab.name, ...);
    ///         ...
    ///     }
    /// }
    /// </code>
    /// The statue lives inside the final segment's own parent (Gloom/Temple_Segment/Peak/
    /// AmuletScoutStatue, or Volcano/Volcano_Segment/"Peak_Kiln Variant"/AmuletScoutStatue),
    /// so it only starts ticking once that segment is activated. Loading a checkpoint taken
    /// in the Citadel activates the segment during the jump, the statue's <c>Update</c> spawns
    /// the gem a frame later, and then our own cleanup deletes it again:
    /// <c>ScoutStatue</c> uses <c>PhotonNetwork.Instantiate</c>, NOT <c>InstantiateItemRoom</c>,
    /// so the gem's view has <c>CreatorActorNr = host</c> and therefore <c>IsRoomView == false</c>
    /// (PUN2: <c>IsRoomView =&gt; CreatorActorNr == 0</c>). That is exactly the case
    /// <see cref="OwnWorldLootReset.ResetWorldLoot"/>'s dropped-item pass destroys, map-wide
    /// with no radius, and <see cref="OwnWorldLootReset.DestroyLeftoverHeldItems"/> would take
    /// it too on a repeat load (<c>IsMine</c> is true for the host on its own instantiate).
    /// <c>spawnedInitialGem</c> has already latched by then, so vanilla never tries again and
    /// the statue is left empty for the rest of the run.
    ///
    /// Loading a GLOOM checkpoint is unaffected, which is how this was reported: there the
    /// statue is still inactive while our cleanup runs, and the normal Gloom -> Citadel
    /// campfire transition activates it long afterwards, so the gem spawns and survives.
    /// Lighting the campfire normally is likewise unaffected, which is why this only runs on
    /// the load path (called from <see cref="OwnTeleportSequence"/>, host only).
    ///
    /// The statue's own state needs no save/restore alongside this: it can only be interacted
    /// with in the final segment, past the last campfire, so no checkpoint can ever exist with
    /// amulets already inserted.
    ///
    /// Reflection rather than a direct <c>Peak.ScoutStatue</c> reference: the type is new in
    /// PEAK 2.x and both the field and the spawn method are private, and a hard type reference
    /// would throw a <c>TypeLoadException</c> right in the middle of the restore coroutine on
    /// any game build that doesn't have it. Everything here degrades to a log line instead
    /// </summary>
    internal static class StrangeGemRestore
    {
        private const string ScoutStatueTypeName = "Peak.ScoutStatue, Assembly-CSharp";

        private static Type _statueType;
        private static bool _statueTypeResolved;

        /// <summary>
        /// Re-spawns the gem on every active scout statue that is currently missing one.
        /// Safe to call when there is nothing to do: a statue whose gem is still alive (or
        /// is being carried by someone) is skipped, and so is one whose gem prefab already
        /// exists somewhere in the scene, so this can never duplicate the gem. Host only,
        /// and only ever called for <c>Segment.TheKiln</c>
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

                        // The unused biome variant's statue is disabled along with its whole
                        // branch, and a statue in a segment we are not in has never ticked, so
                        // spawning its gem here would be putting it in place early. Only the
                        // one belonging to the segment we just loaded into is live
                        if (!statue.gameObject.activeInHierarchy) continue;

                        if (!IsDestroyedOrMissing(spawnedGemField.GetValue(statue) as UnityEngine.Object))
                        {
                            log.Trace($"StrangeGemRestore: {statue.gameObject.name} still has its gem, nothing to do.");
                            continue;
                        }

                        // Second, independent guard against ever ending up with two gems: the
                        // field above is only the statue's own bookkeeping, so also make sure no
                        // instance of the prefab is lying around (or being carried) elsewhere
                        GameObject gemPrefab = gemPrefabField?.GetValue(statue) as GameObject;
                        if (gemPrefab != null && GemInstanceExists(gemPrefab.name))
                        {
                            log.Trace($"StrangeGemRestore: a {gemPrefab.name} already exists in the scene, "
                                + "leaving it alone.");
                            continue;
                        }

                        // Exactly what vanilla's own RPC_InsertAmulet(99) path does on the host.
                        // Self-guarded on spawnedGem_master == null, and PhotonNetwork.Instantiate
                        // inside it networks the gem to every client the same as the first spawn
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

        /// <summary>
        /// Unity's fake-null semantics on purpose: a gem destroyed by our own cleanup leaves
        /// the statue's field pointing at a destroyed object, which is what we want to treat
        /// as "missing", exactly like <c>SpawnGem_Master</c>'s own <c>== null</c> check does
        /// </summary>
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
