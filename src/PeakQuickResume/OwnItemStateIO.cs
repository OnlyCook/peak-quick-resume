using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's item "extra stat" (per-item-type
    /// key/value) IO helpers, ported field-for-field from the decompile
    /// (3258-3480, 3417-3442, plus the static fields at 869-893). All reflect into
    /// vanilla <c>ItemInstanceData</c>/<c>DataEntryKey</c> directly - zero dependency
    /// on the checkpoint mod's own instance or types, unlike the wrapper-method
    /// reflection <see cref="CheckpointInterop.ReadItemStateValues"/> currently uses
    ///
    /// Split by direction:
    ///  - Read side (<see cref="TryGetEntryObject"/>/<see cref="TryReadEntryNumeric"/>/
    ///    <see cref="TryConvertToFloat"/>) is only used when CAPTURING a save
    ///    (M6, <c>SavePlayerOffline</c>/<c>SavePlayerCoop</c>) - unused until then
    ///  - Write side (<see cref="TryGetKey"/>/<see cref="TrySetOrCreateEntry"/>/
    ///    <see cref="TryWriteEntryNumeric"/>) is what M4's <c>OwnInventoryRestore</c>
    ///    actually needs today, to restore saved per-item values back onto a freshly
    ///    spawned item's <c>ItemInstanceData</c>
    /// </summary>
    public static class OwnItemStateIO
    {
        // Same fixed 13 key names the checkpoint mod's own SavePlayerOffline/Coop read
        // (decompile: repeated inline per-key blocks, not derivable from any single
        // enum/list on their side). Only consumed by the read side (M6)
        public static readonly string[] ItemStateKeyNames =
        {
            "ItemUses", "PetterItemUses", "UseRemainingPercentage", "CookedAmount", "Fuel",
            "Color", "Scale", "value__", "Used", "SpawnedBees", "ScreamTime", "FlareActive", "InstanceID",
        };

        // Decompile line 891, verbatim - item ids for which ItemUses/UseRemainingPercentage
        // are skipped when capturing a save (consumables that shouldn't remember partial-use
        // state). Item names behind these ids aren't labeled in the decompile; treated as an
        // opaque literal to copy verbatim, not a list to second-guess. Only consumed by the
        // read side (M6)
        public static readonly int[] ExcludedItemIds = { 100, 58, 66, 2, 24, 104, 115, 17, 63, 64 };

        // ItemInstanceData's own private dictionary field (decompile line 893)
        private static readonly FieldInfo IidDataField =
            typeof(ItemInstanceData).GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>Mirrors TryGetEntryObject exactly (decompile 3258-3278)</summary>
        public static bool TryGetEntryObject(ItemInstanceData inst, DataEntryKey key, out object entryObj)
        {
            entryObj = null;
            if (inst == null) return false;

            object dict = IidDataField?.GetValue(inst);
            if (dict is not IDictionary dictionary) return false;
            if (!dictionary.Contains(key)) return false;

            entryObj = dictionary[key];
            return entryObj != null;
        }

        /// <summary>Mirrors TryReadEntryNumeric exactly (decompile 3280-3317)</summary>
        public static bool TryReadEntryNumeric(object entryObj, out float value)
        {
            value = 0f;
            if (entryObj == null) return false;

            Type type = entryObj.GetType();
            PropertyInfo property = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                object propValue;
                try { propValue = property.GetValue(entryObj); }
                catch { return false; }
                return TryConvertToFloat(propValue, out value);
            }

            FieldInfo field = type.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                object fieldValue;
                try { fieldValue = field.GetValue(entryObj); }
                catch { return false; }
                return TryConvertToFloat(fieldValue, out value);
            }

            return false;
        }

        /// <summary>Mirrors TrySetOrCreateEntry exactly (decompile 3319-3367)</summary>
        public static bool TrySetOrCreateEntry(ItemInstanceData inst, DataEntryKey key, string entryTypeName, float value, ManualLogSource log = null)
        {
            if (inst == null) return false;

            object dict = IidDataField?.GetValue(inst);
            if (dict is not IDictionary dictionary) return false;

            object entryObj = dictionary.Contains(key) ? dictionary[key] : null;
            if (entryObj == null)
            {
                Type type = !string.IsNullOrEmpty(entryTypeName) ? Type.GetType(entryTypeName, throwOnError: false) : null;
                if (type == null)
                {
                    log?.LogWarning($"OwnItemStateIO: cannot resolve entry type '{entryTypeName}' for key {key}.");
                    return false;
                }

                try { entryObj = dictionary[key] = Activator.CreateInstance(type); }
                catch (Exception e)
                {
                    log?.LogWarning($"OwnItemStateIO: failed to create entry type '{type.FullName}': {e.Message}");
                    return false;
                }
            }

            return TryWriteEntryNumeric(entryObj, value);
        }

        /// <summary>Mirrors TryConvertToFloat exactly (decompile 3369-3415)</summary>
        public static bool TryConvertToFloat(object v, out float value)
        {
            value = 0f;
            if (v == null) return false;

            try
            {
                switch (v)
                {
                    case float f: value = f; return true;
                    case double d: value = (float)d; return true;
                    case int i: value = i; return true;
                    case long l: value = l; return true;
                    case short s: value = s; return true;
                    case byte b: value = b; return true;
                    default: value = Convert.ToSingle(v); return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Mirrors TryGetKey exactly (decompile 3417-3442)</summary>
        public static bool TryGetKey(string name, out DataEntryKey key)
        {
            try
            {
                key = (DataEntryKey)Enum.Parse(typeof(DataEntryKey), name, ignoreCase: true);
                return true;
            }
            catch
            {
                foreach (string candidate in Enum.GetNames(typeof(DataEntryKey)))
                {
                    if (candidate.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        key = (DataEntryKey)Enum.Parse(typeof(DataEntryKey), candidate);
                        return true;
                    }
                }
                key = default;
                return false;
            }
        }

        /// <summary>Mirrors TryWriteEntryNumeric exactly (decompile 3444-3480)</summary>
        public static bool TryWriteEntryNumeric(object entryObj, float value)
        {
            if (entryObj == null) return false;
            Type type = entryObj.GetType();

            PropertyInfo property = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(entryObj, Convert.ChangeType(value, property.PropertyType)); return true; }
                catch { /* matches the original: fall through to the field attempt below */ }
            }

            FieldInfo field = type.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { field.SetValue(entryObj, Convert.ChangeType(value, field.FieldType)); return true; }
                catch { /* matches the original: give up silently */ }
            }

            return false;
        }
    }
}
