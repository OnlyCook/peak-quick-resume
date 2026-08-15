using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Detects and reports (once per episode) the still-open co-op bug where another player's
    /// ragdoll convulses/spins for the rest of the run, visible only on the observing machine.
    /// Observational only, never touches the characters it watches. Always-on rather than
    /// debug-gated because the bug is intermittent and leaves no trace elsewhere.
    ///
    /// Detection: per remote character, compares accumulated path length (sum of per-step hip
    /// displacement) against net displacement over a 1s window. Thresholds (<see cref="MinPath"/>,
    /// <see cref="MaxNet"/>) were tuned from measured healthy (0-4m path) vs affected (65-138m
    /// path, 1.5-7m net) sessions; two consecutive bad windows required to avoid false positives
    /// right after a teleport.
    ///
    /// Status: root cause still open. See OwnTeleportSequence for related fixes that reduced
    /// correlated state but were never confirmed to prevent the thrash.
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
