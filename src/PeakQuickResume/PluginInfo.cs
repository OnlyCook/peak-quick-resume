namespace PEAKQuickResume
{
    /// <summary>Static identity constants for this plugin</summary>
    public static class PluginInfo
    {
        public const string Guid = "OnlyCook.PEAKQuickResume";
        public const string Name = "PEAK Quick Resume";
        public const string Version = "1.0.0";

        /// <summary>
        /// BepInEx GUID of the mod that does the actual saving/loading
        /// We depend on it as a soft dependency and drive it via reflection
        /// (see <see cref="CheckpointInterop"/>). If dominik0207 ever renames
        /// the plugin GUID, update this one constant
        /// </summary>
        public const string CheckpointSaveGuid = "PEAK_Checkpoint_Save";

        /// <summary>Fully-qualified type name of the checkpoint mod's plugin class</summary>
        public const string CheckpointSaveTypeName = "PEAK_Checkpoint_Save.Plugin";
    }
}
