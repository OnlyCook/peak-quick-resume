using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Soft compat with Snosz's TerrainRandomiser (tested against 1.1.7), which
    /// re-generates the level's biome layout on every <c>MapHandler.InitializeMap</c> call
    /// with no concept of "this is a Quick Resume, not a fresh Boarding Pass start" - so an
    /// F7 load ends up with terrain that no longer matches the restored save.
    /// <c>roomMapSettings.enableRandomiser</c> isn't reset between scene loads either, so a
    /// leftover "true" from an earlier run is what actually triggers it.
    ///
    /// Fix: a prefix (Priority.First, ahead of TerrainRandomiser's own) temporarily forces
    /// <c>enableRandomiser</c> false during our own resume loads; a postfix restores it after.
    ///
    /// Reflection-only, no dependency on TerrainRandomiser being installed. A future
    /// TerrainRandomiser update that renames these fields just disables this patch (logged).
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
