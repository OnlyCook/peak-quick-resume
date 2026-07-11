using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Puppets a "waking up" beat on the LOCAL player around a Quick Resume teleport: collapsed
    /// into the passed-out pose while <see cref="OwnLoadingScreen"/> covers the actual teleport
    /// work, then revealed already lying down and visibly stands back up once the loading screen
    /// clears - so the whole sequence reads as "you're waking up at your last checkpoint" instead
    /// of "a mod is doing something to you". The caller (OwnTeleportSequence) controls exactly
    /// when <see cref="Collapse"/>/<see cref="Wake"/> fire relative to the loading-screen fade
    ///
    /// Deliberately reuses the game's own pass-out/revive systems instead of building a custom
    /// animation: <c>CharacterData.GetTargetRagdollControll()</c> reads <c>data.passedOut</c> every
    /// physics tick and drives the ragdoll limp (lying down, instant) / recovering (standing back
    /// up, ~1.5-2s ramp) purely off that one bool - the same visual used when you're knocked out
    /// and a teammate revives you. We set it DIRECTLY on <see cref="Character.localCharacter"/>'s
    /// data, deliberately bypassing the networked <c>RPCA_PassOut</c>/<c>RPCA_UnPassOut</c> RPCs:
    /// it's only ever synced to other clients through that RPC pair (and RPC_SyncOnJoin), never
    /// through the continuous character-sync stream, so a direct field write here is guaranteed
    /// local-only for the RAGDOLL pose itself (no teammate sees you physically collapse). Bypassing
    /// the RPCs also skips side effects we don't want: dropping held items, scout-report/stats
    /// tracking, and the passed-out UI/audio ducking triggers
    ///
    /// CRITICAL: we deliberately NEVER set <c>data.fullyPassedOut</c> (only <c>data.passedOut</c>).
    /// <c>MainCameraMovement.LateUpdate()</c> switches the LOCAL PLAYER'S OWN CAMERA into spectate
    /// mode the instant <c>Character.localCharacter.data.fullyPassedOut</c> is true (hunting for
    /// someone else to spectate) - gated specifically on <c>fullyPassedOut</c>, not <c>passedOut</c>.
    /// An earlier version set both, which yanked the player's camera away to spectate instead of
    /// showing them their own collapse/stand-up. `passedOut` alone already forces ragdoll control
    /// to 0 (<c>GetTargetRagdollControll()</c> checks it before <c>fullyPassedOut</c>), so we get
    /// the full limp/recover visual without ever tripping the spectate switch
    ///
    /// <c>CharacterCustomization.Update()</c> separately (and automatically, every frame) watches
    /// this same <c>data.passedOut</c> bool on whichever character it's mine (<c>view.IsMine</c>)
    /// and swaps in the "eyes closed" texture via its OWN buffered RPC broadcast the moment it goes
    /// true - a real network call we don't otherwise trigger, purely cosmetic (an eye-texture swap,
    /// not the ragdoll pose) but still a minor leak beyond "purely local". We pre-empt it: applying
    /// (and, on wake-up, reverting) that same cosmetic ourselves via direct method calls BEFORE
    /// flipping the data bool, so its own guard (<c>!isPassedOut</c>) is already false by the time
    /// its Update() runs and it never fires its own broadcast
    ///
    /// SESSION-DISCOVERED LANDMINE: <c>Character.HandlePassedOut()</c> (only reached while
    /// <c>data.passedOut</c> is true) runs an "you're not actually hurt enough to be passed out"
    /// failsafe every frame: <c>if (refs.afflictions.statusSum &lt; 1f &amp;&amp; Time.time -
    /// data.lastPassedOut &gt; 3f) view.RPC("RPCA_UnPassOut", ...)</c>. Since we set
    /// <c>data.passedOut</c> directly (bypassing <c>RPCA_PassOut</c>, see above), <c>data
    /// .lastPassedOut</c> is never stamped with the current time by us, so it's whatever stale (or
    /// default 0) value was already there - meaning that failsafe's 3-second grace period is
    /// already long since expired the INSTANT we collapse, and the game auto-reverts our fake
    /// collapse within a frame or two, entirely independent of when <see cref="Wake"/> is later
    /// called. Confirmed via targeted logging: <c>currentRagdollControll</c> never visibly left
    /// 1.0 across an entire artificially-extended hold, and <c>passedOut</c> read back false well
    /// before our own <see cref="Wake"/> ever ran. <see cref="Collapse"/> now stamps
    /// <c>data.lastPassedOut</c> itself, and <see cref="RefreshHold"/> re-stamps it every frame
    /// the caller is holding the pose, so the failsafe's timer never accumulates past 3 seconds
    /// while we're deliberately in control
    /// </summary>
    public class OwnWakeUpEffect : MonoBehaviour
    {
        private ManualLogSource _log;

        public void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Instant, synchronous: collapses the local player into the passed-out pose right now.
        /// Meant to be called immediately before <see cref="OwnLoadingScreen.FadeIn"/> starts, so
        /// the collapse itself is only ever briefly (if at all) visible before the black screen
        /// covers it - the player should be revealed ALREADY lying down once the loading screen
        /// clears, then visibly stand up via <see cref="Wake"/>. No-ops safely if there's no local
        /// character (falls through to plain instant teleport behaviour for the rest of the sequence)
        /// </summary>
        public void Collapse()
        {
            Character character = ResolveCharacter("Collapse");
            if (character == null) return;

            _log?.LogInfo("OwnWakeUpEffect: collapsing into the passed-out pose.");
            SnapPassOut(character, true);
        }

        /// <summary>
        /// Re-stamps <c>data.lastPassedOut</c> to now, so the vanilla "not really hurt" auto-revive
        /// failsafe (see class remarks) never sees more than a frame's worth of elapsed time and
        /// can't fire while the caller is still deliberately holding the collapsed pose. Safe to
        /// call every frame (e.g. from the caller's own hold-wait loop); no-ops if there's no local
        /// character or it's not currently passed out via this effect
        /// </summary>
        public void RefreshHold()
        {
            Character character = null;
            try { character = Character.localCharacter; } catch { /* best-effort, see ResolveCharacter */ }
            if (character != null && character.data.passedOut)
                character.data.lastPassedOut = Time.time;
        }

        /// <summary>
        /// Clears the passed-out pose, starting the native ~1.5-2s stand-up recovery, and waits
        /// for it to play out. Meant to be called right after <see cref="OwnLoadingScreen.FadeOut"/>
        /// reveals the player already lying at the new position, so the stand-up itself is what
        /// the player sees first
        /// </summary>
        public IEnumerator Wake(float standTime)
        {
            Character character = ResolveCharacter("Wake");
            if (character != null)
            {
                _log?.LogInfo("OwnWakeUpEffect: waking up (starting the native stand-up recovery).");
                SnapPassOut(character, false);
            }

            yield return new WaitForSeconds(Mathf.Max(0f, standTime));
        }

        private Character ResolveCharacter(string caller)
        {
            Character character = null;
            try { character = Character.localCharacter; }
            catch (Exception e) { _log?.LogWarning($"OwnWakeUpEffect.{caller}: could not read Character.localCharacter: {e.Message}"); }

            if (character == null)
                _log?.LogWarning($"OwnWakeUpEffect.{caller}: Character.localCharacter is null; skipping.");

            return character;
        }

        private void SnapPassOut(Character character, bool value)
        {
            try
            {
                // Pre-empt CharacterCustomization.Update()'s own auto-broadcast (see class
                // remarks): apply/revert the eyes-closed cosmetic ourselves, directly (no RPC,
                // no network call), so its guard already matches by the time its Update() runs
                CharacterCustomization customization = character.refs?.customization;
                if (customization != null)
                {
                    if (value) customization.CharacterPassedOut();
                    else customization.OnRevive_RPC();
                }

                character.data.passedOut = value;
                // Deliberately never set: MainCameraMovement switches the local player's own
                // camera into spectate mode while fullyPassedOut is true (see class remarks)

                // Defeats the vanilla auto-revive failsafe (see class remarks) - without this,
                // the game force-clears passedOut back to false within a frame or two of us
                // setting it true, regardless of anything RefreshHold does afterward
                if (value) character.data.lastPassedOut = Time.time;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnWakeUpEffect.SnapPassOut({value}) failed (non-fatal): {e.Message}");
            }
        }
    }
}
