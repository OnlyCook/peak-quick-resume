using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// Post-load environment resets (fog, lava, Peak flare spawn) called from
    /// <see cref="OwnTeleportSequence"/> as coroutines. <c>ResetCampfire</c> was dropped:
    /// our resume flow always starts a fresh session, so there's never a stale lit
    /// campfire to extinguish.
    public static class OwnEnvironmentReset
    {
        public static IEnumerator ResetFogAfterLoad(int index, Segment segment, ManualLogSource log, bool extendedTime = false)
        {
            OrbFogHandler fog = OrbFogHandler.Instance;
            if (fog == null) yield break;

            if (Ascents.currentAscent > -1)
            {
                fog.SetFogOrigin(index);
                yield return new WaitForSeconds(extendedTime ? 8f : 1.5f);
                fog.isMoving = false;
                fog.currentWaitTime = 0f;
            }
            else
            {
                GameObject sphere = GameObject.Find("FogSphereSystem");
                if (sphere != null && sphere.activeSelf) sphere.SetActive(false);
            }

            if ((int)segment == 3 || (int)segment == 4)
            {
                fog.currentSize = 10000f;
                fog.speed = 0f;
                if (!extendedTime) yield return new WaitForSeconds(0.5f);
            }
        }

        public static void ResetLavaAfterLoad(ManualLogSource log)
        {
            LavaRising lava = UnityEngine.Object.FindFirstObjectByType<LavaRising>();
            if (lava == null)
            {
                log?.LogWarning("OwnEnvironmentReset: LavaRising not found.");
                return;
            }

            lava.started = false;
            lava.ended = false;
            lava.secondsWaitedToStart = 0f;
            lava.timeTraveled = 0f;

            if (lava.lava != null)
            {
                object startHeightValue = typeof(LavaRising)
                    .GetField("startHeight", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(lava);
                if (startHeightValue is float startHeight)
                {
                    Vector3 position = lava.lava.position;
                    position.y = startHeight;
                    lava.lava.position = position;
                }
                else
                {
                    log?.LogWarning("OwnEnvironmentReset: could not read LavaRising's own startHeight; "
                        + "leaving its position untouched rather than guessing.");
                }
            }

            typeof(LavaRising).GetMethod("EndRising", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(lava, null);
            typeof(LavaRising).GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(lava, null);
            lava.ended = false;
            typeof(LavaRising).GetField("shownLavaRisingMessage", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lava, false);

            log.Trace("OwnEnvironmentReset: lava fully reset.");
        }
    }
}
