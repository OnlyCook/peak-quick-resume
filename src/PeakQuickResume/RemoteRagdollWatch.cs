using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Detects, and reports once, the still-open co-op bug where ANOTHER player's ragdoll
    /// convulses and spins for the rest of the run on this machine only. Purely observational -
    /// it never touches the characters it watches
    ///
    /// WHY THIS IS PERMANENT AND NOT GATED ON DEBUG LOGGING
    /// The bug is intermittent, only visible to the observer, and leaves no trace in the host's
    /// log - so it can only be caught on the machine that sees it, at the moment it happens.
    /// Two lines a session is a fair price for that, and it follows the same rule as the rest of
    /// the mod: verbose tracing is opt-in, fundamentals are always logged
    ///
    /// HOW IT DECIDES
    /// Per remote character it accumulates PATH LENGTH (the sum of per-step hip displacements)
    /// against NET displacement over a one-second window. Normal movement has path ~ net; a body
    /// being thrown back and forth racks up a huge path while going nowhere. Thresholds come
    /// from measured sessions rather than guesswork - healthy windows ran 0-4m of path, affected
    /// ones 65-138m with a net of 1.5-7m - so <see cref="MinPath"/>/<see cref="MaxNet"/> sit in
    /// the empty gap between them, and two consecutive windows are required so the brief
    /// settling flurry right after a teleport is never mistaken for it
    ///
    /// WHAT IS ALREADY RULED OUT (so a future session does not re-tread it)
    /// Leaked hand FixedJoints; CharacterSyncer skipping its interpolation guard (it runs every
    /// single physics step); a second MoveAllRigsInDirection caller; the two bodies colliding on
    /// arrival (the thrash was measured starting while the other player was still 6,943m away at
    /// DeathPos); and the dead-flag tug-of-war during the warp. What IS established is the shape
    /// of the runaway once started: InterpolateRigPositions repeatedly takes its &gt;10f
    /// hard-snap branch, each a 12-15m MoveAllRigsInDirection, and MovePosition with a delta that
    /// size implies hundreds of m/s - while OnDataReceived's velocity damping is blind to it,
    /// because it works off the AVERAGE part velocity and a symmetric thrash averages to zero
    /// </summary>
    internal static class RemoteRagdollWatch
    {
        private const int WindowSteps = 50; // ~1s at 50Hz physics
        private const float MinPath = 15f;  // metres travelled within one window
        private const float MaxNet = 8f;    // ...while ending up essentially in place
        private const int WindowsToConfirm = 2;

        private static ManualLogSource _log;

        private class State
        {
            public int Steps;
            public Vector3 WindowStart;
            public Vector3 LastPos;
            public bool HaveLast;
            public float Path;
            public int BadWindows;
            public bool Reported;
            public float Started;
        }

        private static readonly Dictionary<Character, State> _state = new Dictionary<Character, State>();

        internal static void Init(ManualLogSource log)
        {
            _log = log;
            // Logged so a session log confirms the watch is actually present - it is otherwise
            // silent until it fires, which makes "no warning" ambiguous between "no bug" and
            // "old build without the watch"
            log.LogInfo("RemoteRagdollWatch: watching for the co-op ragdoll-thrash bug on this machine.");
        }

        /// <summary>Pumped from <see cref="RemoteRagdollWatchPump"/> on the physics clock</summary>
        internal static void FixedTick()
        {
            if (_log == null || !RunLauncher.InLevel) return;

            try
            {
                foreach (var ch in Character.AllCharacters)
                {
                    if (ch == null || ch.IsLocal || ch.refs == null || ch.refs.ragdoll == null) continue;
                    Accumulate(ch);
                }
            }
            catch { /* observational only - never disturb the frame it is measuring */ }
        }

        private static void Accumulate(Character ch)
        {
            if (!_state.TryGetValue(ch, out var s)) { s = new State(); _state[ch] = s; }

            Vector3 pos = ch.refs.ragdoll.partDict[BodypartType.Hip].Rig.position;

            // A warp legitimately displaces the body; never count those steps
            if (ch.warping || ch.data.carrier != null) { s.HaveLast = false; return; }

            if (!s.HaveLast) { s.LastPos = pos; s.WindowStart = pos; s.HaveLast = true; s.Steps = 0; s.Path = 0f; return; }

            s.Path += (pos - s.LastPos).magnitude;
            s.LastPos = pos;
            if (++s.Steps < WindowSteps) return;

            float net = (pos - s.WindowStart).magnitude;
            if (s.Path > MinPath && net < MaxNet)
            {
                if (s.BadWindows == 0) s.Started = Time.time;
                s.BadWindows++;

                if (s.BadWindows == WindowsToConfirm && !s.Reported)
                {
                    s.Reported = true;
                    _log.LogWarning($"{ch.characterName}'s ragdoll is thrashing on this machine "
                        + $"({s.Path:F0}m of movement in the last second while staying within {net:F1}m). "
                        + "This is the known co-op physics bug, it is local to this machine (nobody else sees it) "
                        + "and it usually lasts the rest of the run - loading a save, returning to the airport or "
                        + "restarting clears it. " + Detail(ch));
                }
            }
            else
            {
                if (s.Reported)
                    _log.LogWarning($"{ch.characterName}'s ragdoll settled down after {Time.time - s.Started:F0}s.");
                s.Reported = false;
                s.BadWindows = 0;
            }

            s.Steps = 0;
            s.Path = 0f;
            s.WindowStart = pos;
        }

        private static string Detail(Character ch)
        {
            float maxPartV = 0f;
            try
            {
                foreach (var part in ch.refs.ragdoll.partList)
                    if (part != null && part.Rig != null)
                        maxPartV = Mathf.Max(maxPartV, part.Rig.linearVelocity.magnitude);
            }
            catch { }

            float distLocal = -1f;
            try
            {
                var me = Character.localCharacter;
                if (me != null && me.refs != null && me.refs.ragdoll != null)
                    distLocal = (me.refs.ragdoll.partDict[BodypartType.Hip].Rig.position
                        - ch.refs.ragdoll.partDict[BodypartType.Hip].Rig.position).magnitude;
            }
            catch { }

            return $"[fastest bodypart {maxPartV:F0}m/s, {distLocal:F1}m from you]";
        }
    }

    /// <summary>
    /// FixedUpdate pump for <see cref="RemoteRagdollWatch"/> - it has to sample on the physics
    /// clock, since that is the clock the thrashing happens on
    /// </summary>
    internal class RemoteRagdollWatchPump : MonoBehaviour
    {
        private void FixedUpdate() => RemoteRagdollWatch.FixedTick();
    }
}
