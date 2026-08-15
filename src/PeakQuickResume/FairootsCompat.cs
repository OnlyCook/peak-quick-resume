using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Soft compat with Fairoots, which runs a blocking one-shot setup burst the moment
    /// the Roots biome finishes loading. Without this, our own loading screen could come
    /// down right as that burst starts, dropping the player into a world that
    /// immediately freezes. We hold the screen (on both the host and client paths) until
    /// Fairoots' static ShouldHoldLoadingScreen() reports done. Reflection-only, same
    /// shape as TerrainRandomiserCompat: no dependency on Fairoots being installed, and
    /// the wait is capped so a broken Fairoots can't strand the player.
    /// </summary>
    public static class FairootsCompat
    {
        /// <summary>Hard cap on how long we'll hold the loading screen for Fairoots.</summary>
        private const float MaxWaitSeconds = 25f;

        private static MethodInfo _shouldHold;
        private static bool _resolved;
        private static bool _loggedUnavailable;

        /// <summary>
        /// Blocks until Fairoots has finished (or isn't installed, or the cap is hit).
        /// Yield this immediately before taking the loading screen down. onFrame runs
        /// once per waited frame and is not optional on the host path: OwnWakeUpEffect's
        /// collapse must be re-stamped every frame or a vanilla failsafe cancels it.
        /// </summary>
        public static IEnumerator WaitUntilReady(ManualLogSource log, Action onFrame = null)
        {
            if (!Resolve(log))
            {
                yield break;
            }

            float waited = 0f;
            bool everWaited = false;

            while (waited < MaxWaitSeconds && ShouldHold(log))
            {
                if (!everWaited)
                {
                    everWaited = true;
                    log.Trace("FairootsCompat: Fairoots is preparing the Roots biome - holding the loading screen.");
                }

                onFrame?.Invoke();
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (waited >= MaxWaitSeconds)
            {
                log?.LogWarning($"FairootsCompat: Fairoots still busy after {MaxWaitSeconds:F0}s - taking the loading screen down anyway.");
            }
            else if (everWaited)
            {
                log.Trace($"FairootsCompat: Fairoots finished after {waited:F1}s - continuing.");
            }
        }

        private static bool ShouldHold(ManualLogSource log)
        {
            try
            {
                return _shouldHold.Invoke(null, null) is bool hold && hold;
            }
            catch (Exception e)
            {
                // Give up on the whole thing rather than log once per frame.
                log?.LogWarning($"FairootsCompat: Fairoots' hold check threw ({e.GetType().Name}); not waiting on it.");
                _shouldHold = null;
                return false;
            }
        }

        private static bool Resolve(ManualLogSource log)
        {
            if (_resolved)
            {
                return _shouldHold != null;
            }

            _resolved = true;

            try
            {
                Type interop = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Fairoots", StringComparison.OrdinalIgnoreCase))
                    ?.GetType("Fairoots.FairootsInterop");
                if (interop == null)
                {
                    return false; // Fairoots simply isn't installed - the normal case, not worth a log line.
                }

                _shouldHold = interop.GetMethod("ShouldHoldLoadingScreen", BindingFlags.Public | BindingFlags.Static);
                if (_shouldHold == null || _shouldHold.ReturnType != typeof(bool))
                {
                    _shouldHold = null;
                    if (!_loggedUnavailable)
                    {
                        _loggedUnavailable = true;
                        log?.LogWarning(
                            "FairootsCompat: Fairoots is installed but Fairoots.FairootsInterop.ShouldHoldLoadingScreen() "
                            + "was not found - the loading screen will not wait for it. Mod update?");
                    }

                    return false;
                }

                log.Trace("FairootsCompat: Fairoots detected - the loading screen will wait for its Roots setup.");
                return true;
            }
            catch (Exception e)
            {
                _shouldHold = null;
                log?.LogWarning($"FairootsCompat: could not resolve Fairoots (non-fatal): {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
