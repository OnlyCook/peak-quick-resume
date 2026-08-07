using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Gate for the mod's verbose step-by-step tracing (per-stage progress, per-item
    /// restore detail, coop handshake chatter, and the like). Off by default - see
    /// <see cref="PluginConfig.EnableDebugLogging"/> - so <c>LogOutput.log</c> stays
    /// readable for the vast majority of players who never touch that setting.
    ///
    /// <c>LogWarning</c>/<c>LogError</c> calls are never routed through this - real
    /// problems always land in the log regardless of this setting. Likewise a small,
    /// deliberate set of milestone lines (a save was written, a resume sequence
    /// started/completed, a restart was triggered, ...) stay as plain
    /// <c>LogInfo</c>/<c>LogWarning</c> calls at their call sites instead of going
    /// through here, so a report sent in with debug logging off still shows what
    /// actually happened, not just that something did.
    /// </summary>
    internal static class DebugLog
    {
        public static void Trace(this ManualLogSource log, string message)
        {
            if (log != null && Plugin.DebugLoggingEnabled) log.LogInfo(message);
        }
    }
}
