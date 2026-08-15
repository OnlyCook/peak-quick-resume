using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Puppets a "waking up" beat on the local player around a Quick Resume teleport: collapsed
    /// into the passed-out pose while <see cref="OwnLoadingScreen"/> covers the teleport work,
    /// then visibly stands back up once it clears. Reuses the game's own pass-out/revive system
    /// (<c>data.passedOut</c> drives the ragdoll limp/recover visual) by writing the field
    /// directly rather than through the networked RPCs, which keeps the pose local-only and
    /// skips side effects (dropped items, stat tracking, UI ducking).
    ///
    /// Deliberately never sets <c>data.fullyPassedOut</c> - that flips the local camera into
    /// spectate mode, which <c>passedOut</c> alone does not. Also pre-empts
    /// <c>CharacterCustomization.Update()</c>'s own eyes-closed RPC broadcast by applying/
    /// reverting that cosmetic directly first.
    ///
    /// LANDMINE: <c>Character.HandlePassedOut()</c> auto-reverts <c>passedOut</c> if
    /// <c>data.lastPassedOut</c> is more than 3s stale - since we bypass <c>RPCA_PassOut</c>,
    /// that field is never stamped for us, so the failsafe fired within a frame of collapsing.
    /// <see cref="Collapse"/> stamps it, and <see cref="RefreshHold"/> re-stamps it every frame
    /// the pose is held, so the failsafe's timer never accumulates past 3 seconds.
    /// </summary>
    public class OwnWakeUpEffect : MonoBehaviour
    {
        private ManualLogSource _log;

        public void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Collapses the local player into the passed-out pose. No-ops safely if there's no local character.</summary>
        public void Collapse()
        {
            Character character = ResolveCharacter("Collapse");
            if (character == null) return;

            _log.Trace("OwnWakeUpEffect: collapsing into the passed-out pose.");
            SnapPassOut(character, true);
        }

        /// <summary>Re-stamps <c>data.lastPassedOut</c> to now, defeating the vanilla auto-revive failsafe while the pose is held.</summary>
        public void RefreshHold()
        {
            Character character = null;
            try { character = Character.localCharacter; } catch { }
            if (character != null && character.data.passedOut)
                character.data.lastPassedOut = Time.time;
        }

        /// <summary>Clears the passed-out pose, starting the native stand-up recovery, and waits for it to play out.</summary>
        public IEnumerator Wake(float standTime)
        {
            Character character = ResolveCharacter("Wake");
            if (character != null)
            {
                _log.Trace("OwnWakeUpEffect: waking up (starting the native stand-up recovery).");
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
                // Pre-empt CharacterCustomization.Update()'s own auto-broadcast by applying/
                // reverting the eyes-closed cosmetic directly first.
                CharacterCustomization customization = character.refs?.customization;
                if (customization != null)
                {
                    if (value) customization.CharacterPassedOut();
                    else customization.OnRevive_RPC();
                }

                character.data.passedOut = value;
                // fullyPassedOut deliberately never set - see class remarks.

                // Defeats the vanilla auto-revive failsafe - without this the game force-clears
                // passedOut within a frame or two.
                if (value) character.data.lastPassedOut = Time.time;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnWakeUpEffect.SnapPassOut({value}) failed (non-fatal): {e.Message}");
            }
        }
    }
}
