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
    /// thorns (<c>CharacterAfflictions.physicalThorns</c>) and ticks (<c>Bugfix</c>).
    ///
    /// Thorns are a fixed pool of pre-placed, initially-inactive objects toggled on/off by
    /// index, so only the stuck-in indices need saving. The "Thorns" status is entirely
    /// derived from them each frame, so it must never be restored directly, or it would just
    /// be overwritten. Restoring the physical thorns brings the status back for free.
    ///
    /// Ticks are the opposite: a single dynamically instantiated "BugfixOnYou" prefab
    /// attached via an RPC that computes its own fixed offset, so no position data is needed
    /// either. Only one Bugfix per character is allowed, so a bool is enough per player.
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
        /// Re-applies saved thorn indices via <c>AddThorn</c>. Must run on the owning
        /// client; it silently no-ops otherwise. The <c>thornIndex:</c> named argument is
        /// load-bearing: PEAK 2.0.a added a leading <c>type</c> parameter, and a positional
        /// call would still compile but silently bind the saved index to <c>type</c>
        /// instead, restoring a random wrong thorn.
        /// </summary>
        public static void ApplyThorns(Character character, List<ushort> thornIndices, ManualLogSource log)
        {
            if (character?.refs?.afflictions == null || thornIndices == null) return;

            CharacterAfflictions afflictions = character.refs.afflictions;

            // Snapshot the saved Thorns/Arrow status levels (already correct from the earlier affliction copy).
            float thornsBefore = SafeGetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Thorns);
            float arrowBefore = SafeGetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Arrow);

            // Switching a thorn/arrow object back on replays its impact sound; see ThornRestoreSilencer.
            ThornRestoreSilencer.SilenceDuringRestore(afflictions, thornIndices);

            foreach (ushort index in thornIndices)
            {
                // updateWeight: false, or every AddThorn would fire the "hurt" SFX/FX as
                // the recomputed status walks up one notch at a time. Recomputed once below instead.
                try { afflictions.AddThorn(thornIndex: index, updateWeight: false); }
                catch (Exception e) { log?.LogWarning($"ThornsAndTicksRestore.ApplyThorns: failed for index {index}: {e.Message}"); }
            }

            // Restore pre-recompute levels first so UpdateWeight sees no increase and stays
            // silent; if the recompute can't run at all, these saved levels are still correct.
            SafeSetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Thorns, thornsBefore, log);
            SafeSetStatus(afflictions, CharacterAfflictions.STATUSTYPE.Arrow, arrowBefore, log);
            TryUpdateWeight(afflictions, log);
        }

        /// <summary>
        /// <c>RemoveAllThorns()</c> without the game treating it as the player yanking every
        /// thorn/arrow out of themselves. Vanilla's <c>OnPulledOut</c> applies Injury (and
        /// its sound) on any removal regardless of who caused it, so loading a save while
        /// thorns/arrows are stuck in you charged real damage per thorn (masked in solo by
        /// the affliction copy moments later, not necessarily elsewhere). Clearing
        /// <c>addStatusOnRemove</c> for the duration suppresses it; re-attaching afterwards
        /// still applies proper status.
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
                // Always restore the flag, or thorns picked up after the load would never hurt on removal again.
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
        /// Runs <c>UpdateWeight</c> once via reflection (it's internal to the game assembly).
        /// A failure is harmless: the caller already left both statuses at correct saved values.
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
        /// Defensive cleanup so a stale tick can't survive under whatever gets restored.
        /// Snapshotted into a list first since the RPC we send mutates the static dict we're reading.
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
        /// own spawn code. Unlike thorns, any client can instantiate this regardless of the
        /// target's ownership; <c>AttachBug</c> resolves its target purely via ViewID.
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
