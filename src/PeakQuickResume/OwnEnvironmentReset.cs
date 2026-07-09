using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own port of the checkpoint mod's post-load environment resets: fog
    /// (<c>ResetFogAfterLoad</c>, decompile 2563-2599), lava
    /// (<c>ResetLavaAfterLoad</c>, 2601-2627), campfire extinguishing
    /// (<c>ResetCampfire</c>, 2239-2261), and the Peak flare spawn
    /// (<c>SpawnFlaresAtPeak</c>, 2218-2237). All ported field-for-field; called from
    /// <see cref="OwnTeleportSequence"/> as coroutines on its own MonoBehaviour
    /// </summary>
    public static class OwnEnvironmentReset
    {
        /// <summary>Mirrors ResetFogAfterLoad exactly (decompile 2563-2599)</summary>
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

        /// <summary>Mirrors ResetLavaAfterLoad exactly (decompile 2601-2627)</summary>
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
                Vector3 position = lava.lava.position;
                position.y = 847.8f;
                lava.lava.position = position;
            }

            typeof(LavaRising).GetMethod("EndRising", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(lava, null);
            typeof(LavaRising).GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(lava, null);
            lava.ended = false;
            typeof(LavaRising).GetField("shownLavaRisingMessage", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lava, false);

            log?.LogInfo("OwnEnvironmentReset: lava fully reset.");
        }

        /// <summary>Mirrors ResetCampfire exactly (decompile 2239-2261)</summary>
        public static IEnumerator ResetCampfire()
        {
            foreach (Campfire campfire in UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None))
            {
                if (campfire.EveryoneInRange() && !campfire.name.Contains("PortableStovetop_Placed"))
                {
                    MethodInfo method = typeof(Campfire).GetMethod("Extinguish_Rpc", BindingFlags.Instance | BindingFlags.NonPublic);
                    method?.Invoke(campfire, null);

                    yield return new WaitForSeconds(0.3f);
                    campfire.state = Campfire.FireState.Off;
                    Transform logRoot = campfire.logRoot;
                    for (int j = 0; j < logRoot.childCount; j++)
                        logRoot.GetChild(j).gameObject.SetActive(true);
                }
                yield return new WaitForSeconds(0.05f);
            }
        }

        /// <summary>Mirrors SpawnFlaresAtPeak exactly (decompile 2218-2237)</summary>
        public static IEnumerator SpawnFlaresAtPeak()
        {
            Vector3 basePos = new Vector3(19f, 1228.1f, 2240f);
            for (int i = 0; i < 10; i++)
            {
                Vector3 offset = UnityEngine.Random.insideUnitSphere * 0.1f;
                offset.y = 0f;
                GameObject flare = PhotonNetwork.InstantiateItem("flare", basePos + offset, Quaternion.identity);
                if (flare != null)
                {
                    Rigidbody rb = flare.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.useGravity = true;
                    }
                }
                yield return new WaitForSeconds(0.1f);
            }
        }
    }
}
