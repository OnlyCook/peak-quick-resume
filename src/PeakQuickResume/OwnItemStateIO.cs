using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>One per-item "extra stat" entry read via <see cref="OwnItemStateIO.ReadItemStateValues"/></summary>
    public readonly struct OwnItemStateEntry
    {
        public readonly string TypeName;
        public readonly float Value;
        public OwnItemStateEntry(string typeName, float value) { TypeName = typeName; Value = value; }
    }

    /// <summary>
    /// Per-item-type "extra stat" (key/value) IO helpers. Reflects directly into vanilla
    /// <c>ItemInstanceData</c>/<c>DataEntryKey</c>. Read side is used when capturing a
    /// save; write side is used by <c>OwnInventoryRestore</c> to restore saved values
    /// onto a freshly spawned item.
    /// </summary>
    public static class OwnItemStateIO
    {
        public static readonly string[] ItemStateKeyNames =
        {
            "ItemUses", "PetterItemUses", "UseRemainingPercentage", "CookedAmount", "Fuel",
            "Color", "Scale", "value__", "Used", "SpawnedBees", "ScreamTime", "FlareActive", "InstanceID",
        };

        // Item ids for which ItemUses/UseRemainingPercentage are skipped when capturing a
        // save (consumables that shouldn't remember partial-use state).
        public static readonly int[] ExcludedItemIds = { 100, 58, 66, 2, 24, 104, 115, 17, 63, 64 };

        private static readonly FieldInfo IidDataField =
            typeof(ItemInstanceData).GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Reads a live item's "extra stats" (CookedAmount, Fuel, Color, ...) as key name ->
        /// (runtime type name, numeric value). Used by <see cref="BackpackSaveMitigation"/>
        /// to build a phantom backpack save entry. Empty (never null) if unavailable.
        /// </summary>
        public static Dictionary<string, OwnItemStateEntry> ReadItemStateValues(ItemInstanceData data, ushort itemId)
        {
            var result = new Dictionary<string, OwnItemStateEntry>();
            if (data == null) return result;

            bool excluded = Array.IndexOf(ExcludedItemIds, itemId) >= 0;
            foreach (string name in ItemStateKeyNames)
            {
                if (excluded && (name == "ItemUses" || name == "UseRemainingPercentage")) continue;
                if (!TryGetKey(name, out DataEntryKey key)) continue;
                if (!TryGetEntryObject(data, key, out object entryObj)) continue;
                if (!TryReadEntryNumeric(entryObj, out float value)) continue;

                result[name] = new OwnItemStateEntry(entryObj.GetType().AssemblyQualifiedName, value);
            }
            return result;
        }

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
