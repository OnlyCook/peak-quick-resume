using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// The single seam between us and dominik0207's "PEAK Checkpoint Save" mod
    ///
    /// Everything we need from that mod is reached through reflection here, so a
    /// future update to the checkpoint mod can only ever break THIS file. Each
    /// lookup is cached, guarded and logged; call <see cref="Probe"/> once at
    /// startup to print exactly which members resolved, making a post-update
    /// breakage obvious at a glance in the log
    ///
    /// Members we rely on (checkpoint save 0.4.7):
    ///   PEAK_Checkpoint_Save.Plugin
    ///     public static Plugin Instance;
    ///     private int    selectedAscent; (field)
    ///     private bool   currentlyLoading; (field)
    ///     private bool   PreStartSetSegment(); (loads save meta, sets selectedLevel, returns true if a save exists)
    ///     public  void   LoadPlayerOffline();
    ///     public  void   LoadPlayerCoop();
    /// </summary>
    public class CheckpointInterop
    {
        private readonly ManualLogSource _log;

        private Type _pluginType;
        private FieldInfo _instanceField;
        private FieldInfo _selectedAscentField;
        private FieldInfo _currentlyLoadingField;
        private MethodInfo _preStartSetSegment;
        private MethodInfo _loadPlayerOffline;
        private MethodInfo _loadPlayerCoop;
        private MethodInfo _showMessage; // optional (on-screen feedback only)
        private MethodInfo _checkReadyStatus; // optional (coop readiness gate)
        private FieldInfo _readyCheckConfigField; // optional (ConfigEntry<bool>)

        private bool _resolved;

        public CheckpointInterop(ManualLogSource log) => _log = log;

        /// <summary>True only if every member we depend on was found</summary>
        public bool IsAvailable => _resolved;

        /// <summary>The resolved checkpoint mod plugin type (null if not found). For Harmony targeting</summary>
        public Type CheckpointType => _pluginType;

        /// <summary>Resolve and cache all reflection targets. Safe to call more than once</summary>
        public bool Probe()
        {
            try
            {
                _pluginType = AccessTools.TypeByName(PluginInfo.CheckpointSaveTypeName);
                if (_pluginType == null)
                {
                    _log.LogWarning($"Checkpoint mod type '{PluginInfo.CheckpointSaveTypeName}' not found. "
                        + "Is 'PEAK Checkpoint Save' installed and loaded? Quick Resume will be inert.");
                    _resolved = false;
                    return false;
                }

                _instanceField        = AccessTools.Field(_pluginType, "Instance");
                _selectedAscentField  = AccessTools.Field(_pluginType, "selectedAscent");
                _currentlyLoadingField = AccessTools.Field(_pluginType, "currentlyLoading");
                _preStartSetSegment   = AccessTools.Method(_pluginType, "PreStartSetSegment");
                _loadPlayerOffline    = AccessTools.Method(_pluginType, "LoadPlayerOffline");
                _loadPlayerCoop       = AccessTools.Method(_pluginType, "LoadPlayerCoop");
                // ShowMessage(string text, Color color, float duration, bool disableMessage)
                _showMessage          = AccessTools.Method(_pluginType, "ShowMessage",
                                            new[] { typeof(string), typeof(UnityEngine.Color), typeof(float), typeof(bool) });
                // Coop readiness gate, LoadPlayerCoop refuses until this is true
                _checkReadyStatus     = AccessTools.Method(_pluginType, "CheckReadyStatusForPlayers");
                _readyCheckConfigField = AccessTools.Field(_pluginType, "configAdvancedEnableClientReadyStatusCheck");

                _log.LogInfo("Checkpoint interop probe:");
                _log.LogInfo($"  Instance field ....... {(_instanceField != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  selectedAscent ....... {(_selectedAscentField != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  currentlyLoading ..... {(_currentlyLoadingField != null ? "OK" : "MISSING (non-fatal)")}");
                _log.LogInfo($"  PreStartSetSegment ... {(_preStartSetSegment != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  LoadPlayerOffline .... {(_loadPlayerOffline != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  LoadPlayerCoop ....... {(_loadPlayerCoop != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  ShowMessage .......... {(_showMessage != null ? "OK" : "MISSING (non-fatal, on-screen text only)")}");
                _log.LogInfo($"  CheckReadyStatus ..... {(_checkReadyStatus != null ? "OK" : "MISSING (non-fatal, coop readiness)")}");
                _log.LogInfo($"  readyStatusConfig .... {(_readyCheckConfigField != null ? "OK" : "MISSING (non-fatal, coop readiness)")}");

                // currentlyLoading is only a nicety (prevents double loads); not required
                _resolved = _instanceField != null
                            && _selectedAscentField != null
                            && _preStartSetSegment != null
                            && _loadPlayerOffline != null
                            && _loadPlayerCoop != null;

                if (!_resolved)
                    _log.LogError("One or more required checkpoint members are missing, the checkpoint mod likely "
                        + "changed. See docs/RESEARCH.md and update CheckpointInterop.cs.");

                return _resolved;
            }
            catch (Exception e)
            {
                _log.LogError($"Checkpoint interop probe threw: {e}");
                _resolved = false;
                return false;
            }
        }

        private object Instance => _instanceField?.GetValue(null);

        public bool IsCurrentlyLoading
        {
            get
            {
                try { return _currentlyLoadingField != null && (bool)_currentlyLoadingField.GetValue(Instance); }
                catch { return false; }
            }
        }

        public bool TrySetSelectedAscent(int ascent)
        {
            try
            {
                var inst = Instance;
                if (inst == null) { _log.LogError("Checkpoint Instance is null (mod not initialized yet?)."); return false; }
                _selectedAscentField.SetValue(inst, ascent);
                return true;
            }
            catch (Exception e) { _log.LogError($"TrySetSelectedAscent failed: {e}"); return false; }
        }

        /// <summary>
        /// Runs the checkpoint mod's PreStartSetSegment(): reads the save file for the
        /// currently selected ascent and sets its internal selectedLevel/metadata
        /// Returns true when a save file exists for that difficulty
        /// </summary>
        public bool TryPreStartSetSegment()
        {
            try
            {
                var inst = Instance;
                if (inst == null) { _log.LogError("Checkpoint Instance is null."); return false; }
                object result = _preStartSetSegment.Invoke(inst, null);
                return result is bool b && b;
            }
            catch (Exception e) { _log.LogError($"TryPreStartSetSegment failed: {e}"); return false; }
        }

        /// <summary>
        /// Triggers the checkpoint restore for the current level, choosing the
        /// offline or coop path exactly like the checkpoint mod's own load key does
        /// </summary>
        public bool TryLoadPlayer()
        {
            try
            {
                var inst = Instance;
                if (inst == null) { _log.LogError("Checkpoint Instance is null."); return false; }

                if (PhotonNetwork.OfflineMode)
                    _loadPlayerOffline.Invoke(inst, null);
                else
                    _loadPlayerCoop.Invoke(inst, null);
                return true;
            }
            catch (Exception e) { _log.LogError($"TryLoadPlayer failed: {e}"); return false; }
        }

        /// <summary>
        /// Whether the checkpoint mod's "all clients must be ready" gate is enabled
        /// If we can't tell, assume enabled (safer, we'll wait rather than load early)
        /// </summary>
        public bool ReadyCheckEnabled()
        {
            try
            {
                if (_readyCheckConfigField == null) return true;
                object cfg = _readyCheckConfigField.GetValue(Instance);
                if (cfg == null) return true;
                object val = cfg.GetType().GetProperty("Value")?.GetValue(cfg);
                return val is bool b ? b : true;
            }
            catch { return true; }
        }

        /// <summary>
        /// True when every connected client has reported "ready" to the host, per the
        /// checkpoint mod's own logic. If we can't call it, report true so we don't
        /// block forever, LoadPlayerCoop will still refuse if genuinely not ready
        /// </summary>
        public bool AllClientsReady()
        {
            try
            {
                if (_checkReadyStatus == null) return true;
                object result = _checkReadyStatus.Invoke(Instance, null);
                return result is bool b ? b : true;
            }
            catch (Exception e) { _log.LogWarning($"AllClientsReady failed (assuming ready): {e.Message}"); return true; }
        }

        /// <summary>
        /// Best-effort on-screen message using the checkpoint mod's own overlay so it
        /// looks/behaves like its F6 prompts. Never throws; a failure just means no text
        /// </summary>
        public void TryShowMessage(string text, UnityEngine.Color color, float duration = 2.5f)
        {
            try
            {
                var inst = Instance;
                if (inst == null || _showMessage == null) return;
                _showMessage.Invoke(inst, new object[] { text, color, duration, false });
            }
            catch (Exception e) { _log.LogWarning($"TryShowMessage failed (non-fatal): {e.Message}"); }
        }
    }
}
