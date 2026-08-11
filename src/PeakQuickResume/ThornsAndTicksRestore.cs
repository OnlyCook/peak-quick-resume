using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Native save/restore for the two "physically attached to a player's body" hazards:
    /// thorns (<c>CharacterAfflictions.physicalThorns</c>, decompile ~2854-4327 - stuck
    /// via touching a Cactus/Tumbleweed) and ticks (<c>Bugfix</c>, decompile ~54795-54931
    /// - a rare 1% chance while walking through certain plants, <c>TickTrigger</c>
    /// decompile ~74208). Neither the checkpoint mod nor our own capture ever looked at
    /// either before - genuinely new, not a port.
    ///
    /// Thorns are a FIXED pool of pre-placed, initially-inactive body-mesh objects
    /// (<c>physicalThorns</c>, populated once via
    /// <c>GetComponentsInChildren&lt;ThornOnMe&gt;(includeInactive: true)</c> in
    /// <c>InitThorns</c>) - <c>AddThorn(index)</c>/<c>RemoveThorn</c> just toggle one on/
    /// off, so we only ever need to save WHICH indices are <c>stuckIn</c>, no position
    /// data of our own. Importantly: the "Thorns" STATUSTYPE affliction is entirely
    /// DERIVED from this every frame (<c>CharacterAfflictions.UpdateWeight</c> ->
    /// <c>GetTotalThornStatusIncrements</c>, sums <c>thornDamage</c> over every
    /// <c>stuckIn</c> physical thorn) - so it must NEVER be restored directly, it would
    /// just be overwritten within a frame anyway. Restoring the physical thorns is what
    /// brings the correct status level back on its own, for free.
    ///
    /// Ticks are the opposite shape: not a pool, a single dynamically
    /// <c>PhotonNetwork.Instantiate</c>'d "BugfixOnYou" prefab, attached via the
    /// <c>AttachBug</c> RPC which computes its own fixed knee-relative offset - no
    /// position data needed either. <c>TickTrigger.OnTriggerEnter</c> only ever allows
    /// ONE <c>Bugfix</c> per character at a time (checked via the static
    /// <c>Bugfix.AllAttachedBugs</c> dict), so a bool is enough per player
    /// </summary>
    public static class ThornsAndTicksRestore
    {
        /// <summary>Indices of every currently-stuckIn physicalThorns slot on this character</summary>
        public static List<ushort> CaptureThorns(Character character)
        {
            var result = new List<ushort>();
            List<ThornOnMe> thorns = character?.refs?.afflictions?.physicalThorns;
            if (thorns == null) return result;

            for (ushort i = 0; i < thorns.Count; i++)
            {
                if (thorns[i] != null && thorns[i].stuckIn) result.Add(i);
            }
            return result;
        }

        /// <summary>Whether a Bugfix (tick) is currently attached to this character</summary>
        public static bool CaptureTick(Character character)
        {
            if (character == null) return false;
            foreach (var kv in Bugfix.AllAttachedBugs)
            {
                if (kv.Value == character) return true;
            }
            return false;
        }

        /// <summary>
        /// Re-applies saved thorn indices via <c>CharacterAfflictions.AddThorn</c> - the
        /// same method the game itself calls when a Cactus/Tumbleweed hits a player.
        /// Must run on the OWNING client: <c>AddThorn</c> silently no-ops otherwise
        /// (decompile: <c>if (!base.photonView.IsMine ...) return;</c>). Callers are
        /// responsible for only invoking this on the right machine - see
        /// <see cref="OwnInventoryRestore.RestoreAll"/> and
        /// <see cref="OwnNetworkRpc.RPC_RestoreThorns"/>
        ///
        /// <c>thornIndex:</c> IS LOAD-BEARING, do not drop it. PEAK 2.0.a added a leading
        /// parameter to this method:
        /// <code>
        /// public void AddThorn(ushort thornIndex = 999)                                  // to 1.65.a
        /// public void AddThorn(int type = -1, ushort thornIndex = 999, bool update = true)  // 2.0.a
        /// </code>
        /// A positional <c>AddThorn(index)</c> still COMPILES against 2.0.a (ushort widens
        /// to int) but silently binds the saved index to <c>type</c> - a filter that picks
        /// a RANDOM thorn of that type - leaving <c>thornIndex</c> at its 999 default. That
        /// restores the wrong thorns entirely, with no error anywhere. Named, the call is
        /// an exact behavioural match for the old one (<c>type: -1</c> means "don't filter",
        /// and the old body ended in the same unconditional <c>UpdateWeight()</c>)
        /// </summary>
        public static void ApplyThorns(Character character, List<ushort> thornIndices, ManualLogSource log)
        {
            if (character?.refs?.afflictions == null || thornIndices == null) return;

            CharacterAfflictions afflictions = character.refs.afflictions;

            // Level the Thorns/Arrow statuses are at before we start. At this point in the
            // restore they already hold the saved values (the affliction copy runs earlier
            // in the same frame), which is also exactly what the single recompute at the
            // end will arrive at, since it derives them from this very set of thorns
            float thornsBefore = SafeGetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Thorns);
            float arrowBefore = SafeGetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Arrow);

            // Switching a thorn/arrow object back on makes it announce itself with its
            // impact sound (a playOnStart SFX_PlayOneShot on the object). This is THE
            // source of the arrow noise during a load - see ThornRestoreSilencer
            ThornRestoreSilencer.SilenceDuringRestore(afflictions, thornIndices);

            foreach (ushort index in thornIndices)
            {
                // updateWeight: false keeps the STATUS side quiet. UpdateWeight fires the
                // thorn "hurt" SFX (plus screen FX and particles) whenever it finds the
                // recomputed status HIGHER than the current one, and by default every
                // single AddThorn runs it - so restoring N thorns walked the status up one
                // notch at a time and re-triggered it each step. Recomputed exactly once
                // below instead. Note this is the THORN status effect only - the arrow
                // impact sound comes from the object itself, see ThornRestoreSilencer
                try { afflictions.AddThorn(thornIndex: index, updateWeight: false); }
                catch (Exception e) { log?.LogWarning($"ThornsAndTicksRestore.ApplyThorns: failed for index {index}: {e.Message}"); }
            }

            // Put the statuses back to where they started before the one real recompute:
            // that way it sees no INCREASE and stays silent, then overwrites both with the
            // properly derived values. Note this is also the safe ordering - if the
            // recompute below can't run at all, these saved levels are what remains, which
            // is already the correct answer rather than a stale or zeroed one
            SafeSetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Thorns, thornsBefore, log);
            SafeSetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Arrow, arrowBefore, log);
            TryUpdateWeight(afflictions, log);
        }

        /// <summary>
        /// <c>RemoveAllThorns()</c>, but without the game treating it as the player having
        /// yanked every thorn and arrow out of themselves.
        ///
        /// NOT the source of the arrow impact sound during a load - that turned out to be
        /// the objects themselves, see ThornRestoreSilencer. This is a separate (real, but
        /// narrower) problem found along the way: pulling an arrow out hurts you, and the
        /// game applies that on ANY removal, ours included:
        /// <code>
        /// RemoveAllThorns() -> RemoveThornRPC(i, removedByPlayer: false)
        ///   -> ThornOnMe.OnPulledOut(false)
        ///     -> if (addStatusOnRemove) AddStatus(statusToAddOnRemove, ...)   // Injury
        ///       -> StatusSFX(Injury) -> injurySmall.Play(...)                 // the sound
        /// </code>
        /// Note <c>OnPulledOut</c> does NOT check <c>removedByPlayer</c> before applying
        /// it. So loading a save while you happen to have thorns or arrows stuck in you
        /// clears them by "pulling them out", charging you real Injury (and its sound) once
        /// per thorn. In solo that damage is then masked by the affliction copy that
        /// overwrites your statuses moments later, which is why it went unnoticed - it is
        /// not necessarily masked with affliction restore off, or on a co-op client.
        ///
        /// Clearing <c>addStatusOnRemove</c> for the duration suppresses it. A checkpoint
        /// load is not the player pulling anything out, so it should not be charging them
        /// for it. Only the removal path is affected - re-attaching the saved thorns
        /// afterwards still applies their proper status
        /// </summary>
        public static void ClearThornsSilently(Character character, ManualLogSource log)
        {
            CharacterAfflictions afflictions = character?.refs?.afflictions;
            if (afflictions == null) return;

            List<ThornOnMe> thorns = afflictions.physicalThorns;
            var suppressed = new List<ThornOnMe>();

            try
            {
                if (thorns != null)
                {
                    foreach (ThornOnMe thorn in thorns)
                    {
                        if (thorn == null || !thorn.addStatusOnRemove) continue;
                        thorn.addStatusOnRemove = false;
                        suppressed.Add(thorn);
                    }
                }

                afflictions.RemoveAllThorns();
            }
            catch (Exception e)
            {
                log?.LogWarning($"ThornsAndTicksRestore.ClearThornsSilently: {e.Message}");
            }
            finally
            {
                // Always put the flag back, or thorns picked up AFTER the load would stop
                // hurting on removal for the rest of the run
                foreach (ThornOnMe thorn in suppressed)
                {
                    try { if (thorn != null) thorn.addStatusOnRemove = true; }
                    catch { /* nothing sensible left to do */ }
                }
            }
        }

        private static float SafeGetStatus(CharacterAfflictions afflictions, CharacterAfflictions.STATUSTYPE type)
        {
            try { return afflictions.GetCurrentStatus(type); }
            catch { return 0f; }
        }

        private static void SafeSetStatus(CharacterAfflictions afflictions, CharacterAfflictions.STATUSTYPE type, float value, ManualLogSource log)
        {
            try { afflictions.SetStatus(type, value); }
            catch (Exception e) { log?.LogWarning($"ThornsAndTicksRestore: could not pre-set {type} ({e.Message})."); }
        }

        /// <summary>
        /// Runs <c>CharacterAfflictions.UpdateWeight</c> once, so the Thorns/Arrow/Weight
        /// statuses end up derived from the thorns we just restored. It's <c>internal</c>
        /// to the game assembly, hence the reflection; a failure here is harmless because
        /// the caller has already left both statuses at their correct saved values, and
        /// the game recomputes weight on the next inventory or thorn change regardless
        /// </summary>
        private static void TryUpdateWeight(CharacterAfflictions afflictions, ManualLogSource log)
        {
            try
            {
                var method = typeof(CharacterAfflictions).GetMethod("UpdateWeight",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method == null)
                {
                    log?.LogWarning("ThornsAndTicksRestore: CharacterAfflictions.UpdateWeight not found; leaving the saved thorn/arrow levels in place.");
                    return;
                }
                method.Invoke(afflictions, null);
            }
            catch (Exception e)
            {
                log?.LogWarning($"ThornsAndTicksRestore: UpdateWeight failed ({e.Message}); leaving the saved thorn/arrow levels in place.");
            }
        }

        /// <summary>
        /// Removes any Bugfix currently attached to this character - defensive cleanup
        /// so a stale tick from before the reload can't survive underneath (or stack
        /// alongside) whatever gets restored below. Snapshotted into a list first since
        /// the RPC we send can eventually mutate the static <c>AllAttachedBugs</c> dict
        /// we're reading (Bugfix.OnDestroy removes its own entry)
        /// </summary>
        public static void RemoveExistingTick(Character character, ManualLogSource log)
        {
            if (character == null) return;
            List<Bugfix> toRemove = Bugfix.AllAttachedBugs
                .Where(kv => kv.Value == character)
                .Select(kv => kv.Key)
                .ToList();

            foreach (Bugfix bug in toRemove)
            {
                try { bug.GetComponent<PhotonView>()?.RPC("RPCA_Remove", RpcTarget.All); }
                catch (Exception e) { log?.LogWarning($"ThornsAndTicksRestore.RemoveExistingTick: failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// Spawns and attaches a fresh Bugfix, mirroring <c>TickTrigger.OnTriggerEnter</c>'s
        /// own spawn code exactly (decompile ~74233). Host-only - unlike thorns, any
        /// client can instantiate this room object regardless of the target character's
        /// ownership: <c>AttachBug</c> resolves its target purely via ViewID, and every
        /// receiving client (including the target's own) applies the attach to their own
        /// local copy of that character (see <c>Bugfix.AttachBug</c>/<c>LateUpdate</c>)
        /// </summary>
        public static void ApplyTick(Character character, ManualLogSource log)
        {
            if (character?.photonView == null) return;
            try
            {
                GameObject spawned = PhotonNetwork.Instantiate("BugfixOnYou", Vector3.zero, Quaternion.identity, 0);
                if (spawned == null)
                {
                    log?.LogWarning("ThornsAndTicksRestore.ApplyTick: PhotonNetwork.Instantiate returned null.");
                    return;
                }
                spawned.GetComponent<PhotonView>().RPC("AttachBug", RpcTarget.All, character.photonView.ViewID);
            }
            catch (Exception e)
            {
                log?.LogWarning($"ThornsAndTicksRestore.ApplyTick: failed: {e.Message}");
            }
        }
    }
}
