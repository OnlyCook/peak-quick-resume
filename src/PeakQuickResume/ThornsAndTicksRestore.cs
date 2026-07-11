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
        /// </summary>
        public static void ApplyThorns(Character character, List<ushort> thornIndices, ManualLogSource log)
        {
            if (character?.refs?.afflictions == null || thornIndices == null) return;
            foreach (ushort index in thornIndices)
            {
                try { character.refs.afflictions.AddThorn(index); }
                catch (Exception e) { log?.LogWarning($"ThornsAndTicksRestore.ApplyThorns: failed for index {index}: {e.Message}"); }
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
