using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Soft compat with OnlyCook's Fairoots (same maintainer). Fairoots rebalances the
    /// Roots biome, and to do it it runs a burst of one-shot work the moment that biome
    /// finishes loading - a seeded spore-bomb cull across 400+ candidates, plus several
    /// scene-wide passes behind it. That work has to complete before the player is
    /// allowed to move, so it can't be spread out; it is simply a stall.
    ///
    /// Which is a problem for Quick Resume specifically: resuming into a Roots campfire
    /// means our own "LOADING SAVE..." screen is coming down at almost exactly the
    /// moment Fairoots starts. Take it down first and the player is dropped into a
    /// world that immediately freezes. So before either fade-out
    /// (<see cref="OwnTeleportSequence"/> on the host, <see cref="OwnNetwork"/> for
    /// every client - each machine runs its own copy of Fairoots' work, so both paths
    /// need this), we hold the screen until Fairoots reports it's done.
    ///
    /// Fairoots publishes exactly one thing for this: a static
    /// <c>Fairoots.FairootsInterop.ShouldHoldLoadingScreen()</c>, which is true while
    /// its per-level work is running <em>or</em> still pending, so this can't lose the
    /// race by asking too early. It also answers false instantly outside Roots, so a
    /// resume into any other biome costs one bool per frame and nothing else.
    ///
    /// Reflection-only, in the same shape as <see cref="TerrainRandomiserCompat"/>: no
    /// compile-time or runtime dependency on Fairoots being installed, resolved once,
    /// and a Fairoots that ever renames this just disables the wait (logged once)
    /// rather than throwing. The wait is also capped - hanging our own loading screen
    /// forever on another mod would be a worse failure than the stutter this avoids.
    /// </summary>
    public static class FairootsCompat
    {
        /// <summary>
        /// Hard cap on how long we'll hold the loading screen for Fairoots. Its own
        /// work is a few seconds at the very worst; this only exists so a Fairoots
        /// that somehow never finishes can't strand the player on a black screen.
        /// </summary>
        private const float MaxWaitSeconds = 25f;

        private static MethodInfo _shouldHold;
        private static bool _resolved;
        private static bool _loggedUnavailable;

        /// <summary>
        /// Blocks until Fairoots has finished (or isn't installed, or the cap is hit).
        /// Yield this immediately before taking the loading screen down.
        ///
        /// <paramref name="onFrame"/> runs once per waited frame and is not optional
        /// on the host path: the wake-up beat's collapse has to be re-stamped every
        /// single frame or a vanilla failsafe cancels it (see
        /// <see cref="OwnWakeUpEffect"/>), and this wait sits inside that window.
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
                    log?.LogInfo("FairootsCompat: Fairoots is preparing the Roots biome - holding the loading screen.");
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
                log?.LogInfo($"FairootsCompat: Fairoots finished after {waited:F1}s - continuing.");
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

                log?.LogInfo("FairootsCompat: Fairoots detected - the loading screen will wait for its Roots setup.");
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
