using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>One per-item "extra stat" entry read via <see cref="CheckpointInterop.ReadItemStateValues"/></summary>
    public readonly struct ItemStateEntry
    {
        public readonly string TypeName;
        public readonly float Value;
        public ItemStateEntry(string typeName, float value) { TypeName = typeName; Value = value; }
    }

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
    ///     ConfigEntry&lt;bool&gt; configLoadLevelScene; (force-load the saved sceneName instead of today's daily island)
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

        // The checkpoint mod's own "enableLoadLevelScene" config (ConfigEntry<bool>):
        // whether MapBaker.GetLevel gets force-overridden to the saved sceneName instead
        // of today's live daily-rotation scene. Off, a resume silently loads today's
        // island instead of the saved one - different biome layout per segment (biome
        // assignment is fully baked per scene, no runtime randomness), while the save's
        // own biome_names metadata stays stale, exactly the "wrong biome" symptom this
        // toggle is responsible for. See ResumeOrchestrator, which forces this true for
        // every Quick Resume load regardless of the checkbox's current UI state
        private FieldInfo _useLevelSceneField;

        // Backpack-save mitigation (see BackpackSaveMitigation), all optional: reused
        // straight off the checkpoint mod's own item-state serialization so an injected
        // phantom backpack entry matches its save schema exactly rather than us
        // re-guessing which DataEntryKeys existing item types populate and how each one
        // coerces to a float. TryGetKey is private static; the other two are private
        // instance methods; ExcludedItemIds is a private instance field
        private MethodInfo _tryGetKeyMethod;
        private MethodInfo _tryGetEntryObjectMethod;
        private MethodInfo _tryReadEntryNumericMethod;
        private FieldInfo _excludedItemIdsField;

        // Same order the checkpoint mod's own SavePlayerOffline/Coop reads per item,
        // see its inlined per-key blocks - not derivable from any single enum/list on
        // its side, this list only exists as that repeated inline code
        private static readonly string[] ItemStateKeyNames =
        {
            "ItemUses", "PetterItemUses", "UseRemainingPercentage", "CookedAmount", "Fuel",
            "Color", "Scale", "value__", "Used", "SpawnedBees", "ScreamTime", "FlareActive", "InstanceID",
        };

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

                _useLevelSceneField = AccessTools.Field(_pluginType, "configLoadLevelScene");

                _tryGetKeyMethod = AccessTools.Method(_pluginType, "TryGetKey");
                _tryGetEntryObjectMethod = AccessTools.Method(_pluginType, "TryGetEntryObject");
                _tryReadEntryNumericMethod = AccessTools.Method(_pluginType, "TryReadEntryNumeric");
                _excludedItemIdsField = AccessTools.Field(_pluginType, "ExcludedItemIds");

                _log.LogInfo("Checkpoint interop probe:");
                _log.LogInfo($"  Instance field ....... {(_instanceField != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  selectedAscent ....... {(_selectedAscentField != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  currentlyLoading ..... {(_currentlyLoadingField != null ? "OK" : "MISSING (non-fatal)")}");
                _log.LogInfo($"  PreStartSetSegment ... {(_preStartSetSegment != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  LoadPlayerOffline .... {(_loadPlayerOffline != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  LoadPlayerCoop ....... {(_loadPlayerCoop != null ? "OK" : "MISSING")}");
                _log.LogInfo($"  CheckReadyStatus ..... {(_checkReadyStatus != null ? "OK" : "MISSING (non-fatal, coop readiness)")}");
                _log.LogInfo($"  readyStatusConfig .... {(_readyCheckConfigField != null ? "OK" : "MISSING (non-fatal, coop readiness)")}");
                _log.LogInfo($"  teleportJumpLogic .... {(_teleportJumpLogicField != null ? "OK" : "MISSING (non-fatal, campfire-relight fix unavailable)")}");
                _log.LogInfo($"  teleportFramesToWait . {(_teleportFramesToWaitField != null ? "OK" : "MISSING (non-fatal)")}");
                _log.LogInfo($"  jumpLogicWaitTime .... {(_teleportWaitTimeField != null ? "OK" : "MISSING (non-fatal)")}");
                _log.LogInfo($"  loadKey/tutorialKey .. {(_loadKeyField != null && _tutorialKeyField != null ? "OK" : "MISSING (non-fatal, F1 screen falls back to defaults)")}");
                _log.LogInfo($"  ShowTutorialMessage .. {(_showTutorialMessage != null ? "OK" : "MISSING (non-fatal, closing F1 with Escape may need a second F1 press to reopen)")}");
                _log.LogInfo($"  configLoadLevelScene . {(_useLevelSceneField != null ? "OK" : "MISSING (non-fatal, resumed saves may load today's island instead of the saved one)")}");
                _log.LogInfo($"  item-state helpers ... {(_tryGetKeyMethod != null && _tryGetEntryObjectMethod != null && _tryReadEntryNumericMethod != null ? "OK" : "MISSING (non-fatal, dropped-backpack save mitigation unavailable)")}");

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
        /// Reads the checkpoint mod's current teleport-config values (its own
        /// `teleportJumpLogic` / `teleportFramesToWait` / `jumpLogicWaitTime` settings),
        /// live off its ConfigEntry objects, not a cached copy. Used by
        /// <see cref="CampfireRelightFix"/> to check whether the native (F6) load path
        /// is using jumpLogic 0, the only value with the unlit-campfire gap it fixes
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

        /// <summary>The checkpoint mod's own native load key (default F6), as displayed text (e.g. "F6"), or null if unavailable</summary>
        public string TryGetLoadKeyText() => TryGetKeyboardShortcutText(_loadKeyField);

        /// <summary>The checkpoint mod's own F1-equivalent tutorial key, as displayed text, or null if unavailable</summary>
        public string TryGetTutorialKeyText() => TryGetKeyboardShortcutText(_tutorialKeyField);

        /// <summary>
        /// Overrides the checkpoint mod's own configTutorialKey (a ConfigEntry&lt;KeyboardShortcut&gt;)
        /// to a plain single key, no modifiers, keeping its own F1/tutorial-key detection
        /// (which HelpScreen/TutorialPatch ride on, see TutorialPatch) and footer prompt
        /// in sync with PluginConfig.HelpKey. Called once at startup and again whenever
        /// HelpKey changes (see Plugin.Awake)
        /// </summary>
        public bool TrySetTutorialKey(UnityEngine.KeyCode key)
        {
            try
            {
                if (_tutorialKeyField == null) return false;
                var inst = Instance;
                if (inst == null) return false;
                SetConfigEntryValue(_tutorialKeyField, inst, new BepInEx.Configuration.KeyboardShortcut(key));
                return true;
            }
            catch (Exception e) { _log.LogWarning($"TrySetTutorialKey failed (non-fatal): {e.Message}"); return false; }
        }

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

        /// <summary>
        /// Forces the checkpoint mod's own "use saved island" config
        /// (<c>configLoadLevelScene</c>, backing its boarding-pass checkbox) to
        /// <paramref name="value"/>, so <c>MapBaker.GetLevel</c> is (or isn't) overridden
        /// to the saved <c>sceneName</c> for the next run start. Quick Resume forces this
        /// true unconditionally before every load, see <see cref="ResumeOrchestrator"/>
        /// </summary>
        public bool TrySetUseSavedLevel(bool value)
        {
            try
            {
                if (_useLevelSceneField == null) return false;
                var inst = Instance;
                if (inst == null) return false;
                SetConfigEntryValue(_useLevelSceneField, inst, value);
                return true;
            }
            catch (Exception e) { _log.LogWarning($"TrySetUseSavedLevel failed (non-fatal): {e.Message}"); return false; }
        }

        /// <summary>
        /// Reads a live item's "extra stats" (CookedAmount, Fuel, Color, ...) in exactly
        /// the shape the checkpoint mod's own save schema uses: key name -> (runtime
        /// type name, numeric value). Used by BackpackSaveMitigation to build a phantom
        /// backpack save entry that round-trips through the checkpoint mod's own loader
        /// the same as a normally-saved one would. Empty (never null) if unavailable
        /// </summary>
        public Dictionary<string, ItemStateEntry> ReadItemStateValues(ItemInstanceData data, ushort itemId)
        {
            var result = new Dictionary<string, ItemStateEntry>();
            if (data == null || _tryGetKeyMethod == null || _tryGetEntryObjectMethod == null || _tryReadEntryNumericMethod == null)
                return result;
            var inst = Instance;
            if (inst == null) return result;

            bool excluded = IsExcludedItemId(itemId);
            foreach (string name in ItemStateKeyNames)
            {
                // Matches the checkpoint mod's own SavePlayerOffline/Coop: these two
                // keys are skipped for "excluded" item ids (consumables that shouldn't
                // remember partial-use state across a save)
                if (excluded && (name == "ItemUses" || name == "UseRemainingPercentage")) continue;

                object[] keyArgs = { name, null };
                if (!(bool)_tryGetKeyMethod.Invoke(null, keyArgs)) continue;

                object[] entryArgs = { data, keyArgs[1], null };
                if (!(bool)_tryGetEntryObjectMethod.Invoke(inst, entryArgs)) continue;
                object entryObj = entryArgs[2];
                if (entryObj == null) continue;

                object[] numArgs = { entryObj, 0f };
                if (!(bool)_tryReadEntryNumericMethod.Invoke(inst, numArgs)) continue;

                result[name] = new ItemStateEntry(entryObj.GetType().AssemblyQualifiedName, (float)numArgs[1]);
            }
            return result;
        }

        private bool IsExcludedItemId(ushort itemId)
        {
            try
            {
                if (_excludedItemIdsField == null) return false;
                var inst = Instance;
                if (inst == null) return false;
                if (_excludedItemIdsField.GetValue(inst) is IEnumerable list)
                    foreach (var v in list)
                        if (Convert.ToInt32(v) == itemId) return true;
                return false;
            }
            catch { return false; }
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
