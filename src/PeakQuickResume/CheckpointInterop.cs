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

        // Phase 6 step 2 (Shift/Alt temporary teleport-config override), all optional:
        // ConfigEntry<int>, ConfigEntry<int>, ConfigEntry<float> respectively
        private FieldInfo _teleportJumpLogicField;
        private FieldInfo _teleportFramesToWaitField;
        private FieldInfo _teleportWaitTimeField;

        // Phase 6 step 3 (F1 help screen rewrite), all optional:
        // ConfigEntry<KeyboardShortcut>, ConfigEntry<KeyboardShortcut>
        private FieldInfo _loadKeyField;
        private FieldInfo _tutorialKeyField;
        private MethodInfo _showTutorialMessage; // optional (ShowTutorialMessage(bool, string))

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

                _teleportJumpLogicField     = AccessTools.Field(_pluginType, "configTeleportJumpLogic");
                _teleportFramesToWaitField  = AccessTools.Field(_pluginType, "configAdvancedTeleportFramesToWait");
                _teleportWaitTimeField      = AccessTools.Field(_pluginType, "configAdvancedJumpLogicWaitTime");

                _loadKeyField     = AccessTools.Field(_pluginType, "configLoadKey");
                _tutorialKeyField = AccessTools.Field(_pluginType, "configTutorialKey");
                _showTutorialMessage = AccessTools.Method(_pluginType, "ShowTutorialMessage",
                    new[] { typeof(bool), typeof(string) });

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
                _log.LogInfo($"  teleportJumpLogic .... {(_teleportJumpLogicField != null ? "OK" : "MISSING (non-fatal, Shift/Alt override unavailable)")}");
                _log.LogInfo($"  teleportFramesToWait . {(_teleportFramesToWaitField != null ? "OK" : "MISSING (non-fatal, Shift/Alt override unavailable)")}");
                _log.LogInfo($"  jumpLogicWaitTime .... {(_teleportWaitTimeField != null ? "OK" : "MISSING (non-fatal, Shift/Alt override unavailable)")}");
                _log.LogInfo($"  loadKey/tutorialKey .. {(_loadKeyField != null && _tutorialKeyField != null ? "OK" : "MISSING (non-fatal, F1 screen falls back to defaults)")}");
                _log.LogInfo($"  ShowTutorialMessage .. {(_showTutorialMessage != null ? "OK" : "MISSING (non-fatal, closing F1 with Escape may need a second F1 press to reopen)")}");

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

        /// <summary>
        /// Reads the checkpoint mod's current teleport-config values (its own
        /// `teleportJumpLogic` / `teleportFramesToWait` / `jumpLogicWaitTime` settings),
        /// live off its ConfigEntry objects, not a cached copy - so this always reflects
        /// whatever's ACTUALLY in effect right now, including a still-active override
        /// from <see cref="TrySetTeleportConfig"/> or a change made via ModConfig.
        /// Used both by the Shift/Alt override (Phase 6 step 2) and the save picker's
        /// live footer indicator
        /// </summary>
        public bool TryGetTeleportConfig(out int jumpLogic, out int framesToWait, out float waitTime)
        {
            jumpLogic = 0; framesToWait = 0; waitTime = 0f;
            try
            {
                if (_teleportJumpLogicField == null || _teleportFramesToWaitField == null || _teleportWaitTimeField == null)
                    return false;
                var inst = Instance;
                if (inst == null) return false;

                jumpLogic = GetConfigEntryValue<int>(_teleportJumpLogicField, inst);
                framesToWait = GetConfigEntryValue<int>(_teleportFramesToWaitField, inst);
                waitTime = GetConfigEntryValue<float>(_teleportWaitTimeField, inst);
                return true;
            }
            catch (Exception e) { _log.LogWarning($"TryGetTeleportConfig failed (non-fatal): {e.Message}"); return false; }
        }

        /// <summary>Writes all three teleport-config values at once (see <see cref="TryGetTeleportConfig"/>)</summary>
        public bool TrySetTeleportConfig(int jumpLogic, int framesToWait, float waitTime)
        {
            try
            {
                if (_teleportJumpLogicField == null || _teleportFramesToWaitField == null || _teleportWaitTimeField == null)
                    return false;
                var inst = Instance;
                if (inst == null) return false;

                SetConfigEntryValue(_teleportJumpLogicField, inst, jumpLogic);
                SetConfigEntryValue(_teleportFramesToWaitField, inst, framesToWait);
                SetConfigEntryValue(_teleportWaitTimeField, inst, waitTime);
                return true;
            }
            catch (Exception e) { _log.LogWarning($"TrySetTeleportConfig failed (non-fatal): {e.Message}"); return false; }
        }

        /// <summary>The checkpoint mod's own native load key (default F6), as displayed text (e.g. "F6"), or null if unavailable</summary>
        public string TryGetLoadKeyText() => TryGetKeyboardShortcutText(_loadKeyField);

        /// <summary>The checkpoint mod's own F1-equivalent tutorial key, as displayed text, or null if unavailable</summary>
        public string TryGetTutorialKeyText() => TryGetKeyboardShortcutText(_tutorialKeyField);

        /// <summary>
        /// Tells the checkpoint mod its own tutorial is now closed (`ShowTutorialMessage(false,
        /// "")`), syncing its private `tutorialMessageEnabled` toggle without going through its
        /// own tutorial-key handling. Needed because <see cref="HelpScreen"/> can also be closed
        /// via Escape (see ROADMAP.md Phase 6 step 3) - without this, the checkpoint mod's own
        /// toggle would still think its tutorial is open, and the next tutorial-key press would
        /// just "close" it again (a no-op, since HelpScreen is already closed) instead of
        /// reopening, effectively requiring two presses to reopen after an Escape-close
        /// </summary>
        public void TryCloseTutorial()
        {
            try
            {
                var inst = Instance;
                if (inst == null || _showTutorialMessage == null) return;
                _showTutorialMessage.Invoke(inst, new object[] { false, "" });
            }
            catch (Exception e) { _log.LogWarning($"TryCloseTutorial failed (non-fatal): {e.Message}"); }
        }

        private string TryGetKeyboardShortcutText(FieldInfo field)
        {
            try
            {
                if (field == null) return null;
                var inst = Instance;
                if (inst == null) return null;
                object entry = field.GetValue(inst);
                object value = entry?.GetType().GetProperty("Value")?.GetValue(entry);
                return value?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// The checkpoint mod's own live description text for `teleportJumpLogic` (read
        /// straight off its BepInEx ConfigDescription, not a copy we maintain), so the F1
        /// screen quotes the real, currently-shipping wording instead of a paraphrase that
        /// could drift out of date. Null if unavailable
        /// </summary>
        public string TryGetTeleportJumpLogicDescription() => TryGetConfigDescription(_teleportJumpLogicField);

        private string TryGetConfigDescription(FieldInfo field)
        {
            try
            {
                if (field == null) return null;
                var inst = Instance;
                if (inst == null) return null;
                object entry = field.GetValue(inst);
                object desc = entry?.GetType().GetProperty("Description")?.GetValue(entry);
                return desc?.GetType().GetProperty("Description")?.GetValue(desc) as string;
            }
            catch { return null; }
        }

        // BepInEx ConfigEntry<T> isn't a type we reference directly (it IS available via
        // BepInEx.dll, but going through its untyped Value property by reflection here
        // keeps this symmetric with the rest of this file's reflection-only approach)
        private static T GetConfigEntryValue<T>(FieldInfo configEntryField, object instance)
        {
            object entry = configEntryField.GetValue(instance);
            return (T)entry.GetType().GetProperty("Value").GetValue(entry);
        }

        private static void SetConfigEntryValue<T>(FieldInfo configEntryField, object instance, T value)
        {
            object entry = configEntryField.GetValue(instance);
            entry.GetType().GetProperty("Value").SetValue(entry, value);
        }
    }
}
