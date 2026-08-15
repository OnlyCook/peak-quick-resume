using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Gate for the mod's verbose step-by-step tracing, off by default (see
    /// PluginConfig.EnableDebugLogging). Warnings/errors and milestone lines stay as
    /// plain LogInfo/LogWarning calls instead of going through here, so a report sent
    /// with debug logging off still shows what happened.
    /// </summary>
    internal static class DebugLog
    {
        public static void Trace(this ManualLogSource log, string message)
        {
            if (log != null && Plugin.DebugLoggingEnabled) log.LogInfo(message);
        }
    }
}
