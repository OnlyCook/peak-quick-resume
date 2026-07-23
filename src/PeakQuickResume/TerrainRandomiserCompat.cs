using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Soft compat with Snosz's TerrainRandomiser (tested against 1.1.7). That mod
    /// re-generates the level's biome layout from scratch on EVERY
    /// <c>MapHandler.InitializeMap</c> call, gated only on its own
    /// <c>Plugin.Instance.roomMapSettings.enableRandomiser</c> flag - it has no concept
    /// of "this scene load is a Quick Resume of a saved level, not a fresh Boarding
    /// Pass start". Left alone, that regenerates a different terrain layout than
    /// whatever was actually saved, so an F7 load into a level ends up with props and
    /// geometry that no longer match the restored save (session-reported: loading a
    /// save via F7 visibly re-randomizes the terrain).
    ///
    /// <c>roomMapSettings.enableRandomiser</c> also isn't reset between scene loads -
    /// it only gets set by TerrainRandomiser's own <c>BoardingPass.StartGame</c> patch
    /// (which our own resume flow deliberately bypasses, see <c>RunLauncher.StartRun</c>
    /// - it calls <c>kiosk.StartGame</c> directly) or by its Photon room-property sync,
    /// so a leftover "true" from any earlier normal run in the same session is what
    /// actually triggers the re-randomization on a later F7 load, not anything about
    /// the load itself.
    ///
    /// Fix: patch <c>MapHandler.InitializeMap</c> ourselves with a prefix that runs
    /// BEFORE TerrainRandomiser's own (Priority.First vs its default Priority.Normal),
    /// and when <see cref="OwnLoadEntryPoints"/> tells us THIS load is our own resume,
    /// temporarily force <c>enableRandomiser</c> false so TerrainRandomiser's own prefix
    /// no-ops for this one call. A postfix restores the original value right after, so
    /// the player's actual TerrainRandomiser setting is untouched for their next real
    /// Boarding Pass start (which resets it fresh anyway, this is just defense in depth).
    ///
    /// Reflection-only throughout: no compile-time or runtime dependency on
    /// TerrainRandomiser being installed at all. The patch is only applied if its
    /// assembly is actually loaded, and every field lookup is verified up front, so a
    /// future TerrainRandomiser update that renames/removes these fields just disables
    /// this compat patch (logged) instead of throwing at load-order-sensitive times
    /// </summary>
    public static class TerrainRandomiserCompat
    {
        private static FieldInfo _instanceField;
        private static FieldInfo _roomMapSettingsField;
        private static FieldInfo _enableRandomiserField;

        // Not thread-safe by design: Unity/Harmony patches for a single-player-driven
        // scene load are never re-entrant on the same peer
        private static bool _restorePending;
        private static bool _restoreValue;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            try
            {
                Type pluginType = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "TerrainRandomiser", StringComparison.OrdinalIgnoreCase))
                    ?.GetType("TerrainRandomiser.Plugin");
                if (pluginType == null)
                {
                    log.LogInfo("TerrainRandomiserCompat: TerrainRandomiser not detected, skipping.");
                    return;
                }

                _instanceField = pluginType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                Type mapSettingsType = pluginType.Assembly.GetType("TerrainRandomiser.MapSettings");
                _roomMapSettingsField = pluginType.GetField("roomMapSettings", BindingFlags.Public | BindingFlags.Instance);
                _enableRandomiserField = mapSettingsType?.GetField("enableRandomiser", BindingFlags.Public | BindingFlags.Instance);

                if (_instanceField == null || _roomMapSettingsField == null || _enableRandomiserField == null)
                {
                    log.LogWarning("TerrainRandomiserCompat: TerrainRandomiser detected but its expected "
                        + "fields were not found (version mismatch?); compat patch NOT applied.");
                    return;
                }

                var target = AccessTools.Method(typeof(MapHandler), "InitializeMap");
                if (target == null)
                {
                    log.LogWarning("TerrainRandomiserCompat: MapHandler.InitializeMap not found; compat patch NOT applied.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(TerrainRandomiserCompat), nameof(Prefix)) { priority = Priority.First };
                var postfix = new HarmonyMethod(typeof(TerrainRandomiserCompat), nameof(Postfix)) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                log.LogInfo("TerrainRandomiserCompat: TerrainRandomiser detected, compat patch applied.");
            }
            catch (Exception e)
            {
                log.LogError($"TerrainRandomiserCompat.Apply failed (non-fatal): {e}");
            }
        }

        private static void Prefix()
        {
            _restorePending = false;
            try
            {
                if (!OwnLoadEntryPoints.ConsumeSuppressExternalTerrainRandomizerOnce()) return;

                object instance = _instanceField.GetValue(null);
                object roomMapSettings = instance != null ? _roomMapSettingsField.GetValue(instance) : null;
                if (roomMapSettings == null) return;

                _restoreValue = (bool)_enableRandomiserField.GetValue(roomMapSettings);
                if (!_restoreValue) return; // already off; nothing to suppress or restore

                _enableRandomiserField.SetValue(roomMapSettings, false);
                _restorePending = true;
            }
            catch
            {
                // Best-effort compat shim; never block the actual level load over this
            }
        }

        private static void Postfix()
        {
            if (!_restorePending) return;
            _restorePending = false;
            try
            {
                object instance = _instanceField.GetValue(null);
                object roomMapSettings = instance != null ? _roomMapSettingsField.GetValue(instance) : null;
                if (roomMapSettings == null) return;
                _enableRandomiserField.SetValue(roomMapSettings, _restoreValue);
            }
            catch
            {
                // Best-effort; worst case TerrainRandomiser stays off until the player's
                // next real Boarding Pass start resets it
            }
        }
    }
}
