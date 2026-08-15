using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our PhotonView/RPC channel for coop: save requests, campfire cooldown sync,
    /// fall-damage protection, messages, loading-screen/watchdog signaling, endscreen
    /// close, afflictions. <c>RPC_Loadingscreen</c> is repurposed - no caption, its
    /// load-begin/load-done moments drive <see cref="TeleportWatchdog"/>'s load window
    /// on every machine instead.
    ///
    /// Uses a fixed <c>ViewID</c> (69420) well clear of Photon's auto-allocated range
    /// (PEAK caps rooms at 4 players, so nothing auto-assigned gets remotely close).
    /// </summary>
    public class OwnNetwork : MonoBehaviour
    {
        private const int ViewId = 69420;

        private ManualLogSource _log;
        private PluginConfig _cfg;
        internal OwnMessageOverlay MessageOverlay { get; private set; }
        internal TeleportWatchdog Watchdog { get; private set; }
        internal OwnLoadEntryPoints EntryPoints { get; private set; }
        internal OwnWakeUpEffect WakeUpEffect { get; private set; }
        internal OwnLoadingScreen LoadingScreen { get; private set; }

        private GameObject _networkGo;
        private PhotonView _pv;

        // userId -> userName, populated only on the master client, reset on scene transitions.
        private readonly Dictionary<string, string> _playerReceivedReadyStatus = new Dictionary<string, string>();
        private bool _clientSentReadyStatus;

        // userId -> reported PluginInfo.Version. Diagnostic only (see ClientPresentationOthers
        // for why it doesn't gate anything); never populated in solo/offline.
        private readonly Dictionary<string, string> _playerModVersions = new Dictionary<string, string>();
        private bool _clientSentVersionReport;

        // userIds that have confirmed their own RunClientPresentationExit (wake-up/fade-out)
        // has finished for the current cycle. Master-client side only. Cleared every time a
        // new cycle starts so a stale confirmation from a prior load can't satisfy a new wait -
        // without this a Restart's scene teardown could race a client's still-running wake-up
        // animation and leave it on an infinite loading screen.
        private readonly Dictionary<string, bool> _playerPresentationDone = new Dictionary<string, bool>();

        public void Init(ManualLogSource log, PluginConfig cfg)
        {
            _log = log;
            _cfg = cfg;
            CreatePhotonView();
        }

        /// <summary>Wires dependencies set after construction, since <see cref="OwnLoadEntryPoints"/> is created after this object in <c>Plugin.Awake</c>.</summary>
        internal void AttachDependencies(OwnMessageOverlay messageOverlay, TeleportWatchdog watchdog, OwnLoadEntryPoints entryPoints,
            OwnWakeUpEffect wakeUpEffect = null, OwnLoadingScreen loadingScreen = null)
        {
            MessageOverlay = messageOverlay;
            Watchdog = watchdog;
            EntryPoints = entryPoints;
            WakeUpEffect = wakeUpEffect;
            LoadingScreen = loadingScreen;
        }

        private void CreatePhotonView()
        {
            try
            {
                if (_networkGo != null) return;
                _networkGo = new GameObject("PEAKQuickResume.OwnNetwork");
                DontDestroyOnLoad(_networkGo);
                _pv = _networkGo.AddComponent<PhotonView>();
                var rpc = _networkGo.AddComponent<OwnNetworkRpc>();
                rpc.Owner = this;
                _pv.ViewID = ViewId;
                _log.Trace($"OwnNetwork: PhotonView created (ViewID={ViewId}).");
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.CreatePhotonView failed: {e}");
            }
        }

        // Handle to the running SendReadyStatusToMaster retry loop, so it can be explicitly
        // cancelled on a scene transition - this component lives DontDestroyOnLoad, so an
        // uncancelled coroutine would keep retrying through subsequent loads, stacking a
        // new retry loop on top of the still-alive old one every time.
        private Coroutine _readyStatusCoroutine;

        // Unscaled time the current Level scene was entered, or < 0 if not in one - see
        // CheckReadyStatusForPlayers' mod-detection grace window.
        private float _levelEnteredAt = -1f;

        private void Update()
        {
            if (_cfg == null) return;

            if (RunLauncher.InAirport)
            {
                StopReadyStatusRetry();
                _clientSentReadyStatus = false;
                _clientSentVersionReport = false;
                _playerReceivedReadyStatus.Clear();
                _playerModVersions.Clear();
                _playerPresentationDone.Clear();
                _levelEnteredAt = -1f;
                return;
            }

            if (RunLauncher.InLevel)
            {
                if (_levelEnteredAt < 0f) _levelEnteredAt = Time.unscaledTime;

                if (!_clientSentReadyStatus && !RunLauncher.IsHost)
                {
                    _readyStatusCoroutine = StartCoroutine(SendReadyStatusToMaster());
                    _clientSentReadyStatus = true;
                }
                if (!_clientSentVersionReport && !RunLauncher.IsHost)
                {
                    StartCoroutine(ReportVersionToMaster());
                    _clientSentVersionReport = true;
                }
                return;
            }

            if (RunLauncher.InTitle)
            {
                StopReadyStatusRetry();
                _clientSentReadyStatus = false;
                _clientSentVersionReport = false;
                _playerReceivedReadyStatus.Clear();
                _playerModVersions.Clear();
                _playerPresentationDone.Clear();
                _levelEnteredAt = -1f;
            }
        }

        private void StopReadyStatusRetry()
        {
            if (_readyStatusCoroutine == null) return;
            StopCoroutine(_readyStatusCoroutine);
            _readyStatusCoroutine = null;
        }

        // Waits for the local character, a flat 5s settle, then RPCs the master client -
        // retrying every few seconds rather than firing once, since a single fire-and-forget
        // RPC raced the host's hard-timeout wait on a slower client machine. Retrying is
        // safe: OnClientReportedReady is idempotent.
        private IEnumerator SendReadyStatusToMaster()
        {
            while (Character.localCharacter == null) yield return null;
            yield return new WaitForSeconds(5f);

            const float retryInterval = 3f;
            const float giveUpAfter = 60f;
            float elapsed = 0f;
            while (elapsed < giveUpAfter && RunLauncher.InLevel)
            {
                try
                {
                    _pv.RPC(nameof(OwnNetworkRpc.RPC_SendReadyStatusToMaster), RpcTarget.MasterClient,
                        PhotonNetwork.LocalPlayer.UserId, PhotonNetwork.LocalPlayer.NickName);
                }
                catch (Exception e)
                {
                    _log?.LogError($"OwnNetwork.SendReadyStatusToMaster RPC failed: {e}");
                }
                yield return new WaitForSeconds(retryInterval);
                elapsed += retryInterval;
            }
        }

        // Unlike ready-status, has nothing to wait on - sent as soon as the local character
        // exists, so it reaches the host well before the 5s-delayed ready-status report.
        private IEnumerator ReportVersionToMaster()
        {
            while (Character.localCharacter == null) yield return null;

            try
            {
                _pv.RPC(nameof(OwnNetworkRpc.RPC_ReportModVersion), RpcTarget.MasterClient,
                    PhotonNetwork.LocalPlayer.UserId, PluginInfo.Version);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.ReportVersionToMaster failed: {e.Message}");
            }
        }

        internal void OnClientReportedVersion(string userId, string version)
        {
            try
            {
                _playerModVersions[userId] = version;
                _log.Trace($"OwnNetwork: client {userId} reports Quick Resume v{version}.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.OnClientReportedVersion failed: {e.Message}");
            }
        }

        /// <summary>
        /// True once this userId has reported running Quick Resume at all (any version).
        /// Master-client side only; always false offline. Gates the held-item restore in
        /// <c>OwnInventoryRestore</c>.
        /// </summary>
        public bool PlayerReportedMod(string userId) => _playerModVersions.ContainsKey(userId);

        // Mirrors the host's local presentation using this machine's own WakeUpEffect/LoadingScreen.
        internal void HandleClientPresentation(bool show)
        {
            StartCoroutine(show ? RunClientPresentationEnter() : RunClientPresentationExit());
        }

        private IEnumerator RunClientPresentationEnter()
        {
            // No guarantee this client's character has finished spawning into the fresh level
            // scene when the host fires this RPC, so wait for it, with a timeout.
            float waited = 0f;
            const float timeout = 15f;
            while (Character.localCharacter == null && waited < timeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (Character.localCharacter == null)
                _log?.LogWarning("OwnNetwork.RunClientPresentationEnter: local character still null after "
                    + $"{timeout:F0}s; proceeding anyway (wake-up beat will no-op).");

            // Just hides the screen here; collapse/reveal/stand-up happens on RunClientPresentationExit.
            bool showLoadingScreen = _cfg != null && !_cfg.DebugDisableLoadingScreen.Value;
            if (showLoadingScreen && LoadingScreen != null)
                yield return LoadingScreen.FadeIn(_cfg.OwnLoadingScreenFadeTime.Value);
        }

        private IEnumerator RunClientPresentationExit()
        {
            // Mirrors the host's ordering; each client manages its own local timing.
            bool showLoadingScreen = _cfg != null && !_cfg.DebugDisableLoadingScreen.Value;
            if (WakeUpEffect != null) WakeUpEffect.Collapse();

            // Repeated per client rather than covered by the host's: every machine runs its
            // own copy of Fairoots' per-level work on its own timing.
            yield return FairootsCompat.WaitUntilReady(_log, () => WakeUpEffect?.RefreshHold());

            // Per-frame loop (not a flat WaitForSeconds) to re-stamp data.lastPassedOut every
            // frame - otherwise vanilla's "not really hurt" auto-revive failsafe force-clears
            // passedOut back to false within a couple frames of Collapse().
            if (_cfg != null)
            {
                float delayElapsed = 0f;
                float fadeOutDelay = Mathf.Max(0f, _cfg.OwnLoadingScreenFadeOutDelay.Value);
                while (delayElapsed < fadeOutDelay)
                {
                    WakeUpEffect?.RefreshHold();
                    yield return null;
                    delayElapsed += Time.unscaledDeltaTime;
                }
            }

            if (showLoadingScreen && LoadingScreen != null)
                yield return LoadingScreen.FadeOut(_cfg.OwnLoadingScreenFadeTime.Value);
            if (WakeUpEffect != null && _cfg != null)
                yield return WakeUpEffect.Wake(_cfg.OwnWakeUpStandTime.Value);

            // Best-effort: if lost, the host's wait just falls back to its timeout.
            try
            {
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_ClientPresentationDone), RpcTarget.MasterClient,
                    PhotonNetwork.LocalPlayer.UserId);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.RunClientPresentationExit: RPC_ClientPresentationDone failed: {e.Message}");
            }
        }

        internal void OnClientReportedReady(string userId, string userName)
        {
            try
            {
                if (PhotonNetwork.OfflineMode || !PhotonNetwork.IsMasterClient
                    || _cfg == null || !_cfg.OwnEnableClientReadyStatusCheck.Value)
                    return;

                if (!_playerReceivedReadyStatus.ContainsKey(userId))
                    _playerReceivedReadyStatus.Add(userId, userName);

                _log.Trace($"OwnNetwork: RPC_SendReadyStatusToMaster userId={userId}, userName={userName}.");
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.OnClientReportedReady failed: {e}");
            }
        }

        /// <summary>
        /// Grace window (seconds since level entry) given to a player to report something
        /// before we give up waiting on their ready-RPC and assume they're not running
        /// Quick Resume. Short enough to only shortcut players who were never going to
        /// report, well short of CoopReadyTimeout's default 60s.
        /// </summary>
        private const float ModDetectionGraceSeconds = 10f;

        /// <summary>
        /// True once every connected non-host player has reported ready (or the
        /// ready-check setting is disabled). A player who has also never reported a mod
        /// version, past <see cref="ModDetectionGraceSeconds"/>, is treated as not running
        /// Quick Resume and exempted - otherwise a host-only install would hang the full
        /// CoopReadyTimeout on every coop resume.
        /// </summary>
        public bool CheckReadyStatusForPlayers()
        {
            if (_cfg == null || !_cfg.OwnEnableClientReadyStatusCheck.Value) return true;

            try
            {
                bool graceElapsed = _levelEnteredAt >= 0f
                    && (Time.unscaledTime - _levelEnteredAt) >= ModDetectionGraceSeconds;

                foreach (var player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (player == null) continue;
                    Character character = player.character;
                    if (character == null) continue;

                    string userId = NetworkingUtilities.GetUserId(character.player);
                    bool ownerIsMaster = character.photonView.Owner.IsMasterClient;
                    if (ownerIsMaster) continue;
                    if (_playerReceivedReadyStatus.ContainsKey(userId)) continue;
                    if (graceElapsed && !_playerModVersions.ContainsKey(userId)) continue; // no mod, never will report
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.CheckReadyStatusForPlayers failed (assuming ready): {e.Message}");
                return true;
            }
        }

        // --- Outbound RPC senders, host-called ---

        /// <summary>RpcTarget.All so the host arms its own window too.</summary>
        public void RequestFalldamageProtectionAll(int seconds)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RequestFalldamageProtection), RpcTarget.All, seconds); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RequestFalldamageProtectionAll failed: {e.Message}"); }
        }

        public void CloseEndscreenOthers()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_CloseEndscreen), RpcTarget.Others); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.CloseEndscreenOthers failed: {e.Message}"); }
        }

        public void SendMessageOthers(string message, string colorKey, float seconds)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_SendMessage), RpcTarget.Others, message, colorKey, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.SendMessageOthers failed: {e.Message}"); }
        }

        /// <summary>
        /// Repurposed to drive TeleportWatchdog on each client instead of showing a caption.
        /// <paramref name="target"/> carries the host's real teleport target on the "load done"
        /// call, so a client that never received a warp can still recover to it.
        /// </summary>
        public void LoadingScreenOthers(bool enable, Vector3? target = null)
        {
            try
            {
                string payload = target.HasValue ? FormatVector(target.Value) : "";
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_Loadingscreen), RpcTarget.Others, enable ? "true" : "false", payload);
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.LoadingScreenOthers failed: {e.Message}"); }
        }

        // Invariant-culture "x|y|z" so the target round-trips identically regardless of the
        // sender's or receiver's locale (a comma decimal separator would otherwise corrupt it)
        private static string FormatVector(Vector3 v)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return $"{v.x.ToString(c)}|{v.y.ToString(c)}|{v.z.ToString(c)}";
        }

        internal static Vector3? ParseVector(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split('|');
            if (parts.Length != 3) return null;
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out float x)
                && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out float y)
                && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, c, out float z))
                return new Vector3(x, y, z);
            return null;
        }

        /// <summary>
        /// Arms <see cref="OwnLoadEntryPoints.ArmSuppressExternalTerrainRandomizerOnce"/> on
        /// every peer right before a quick-resume <c>RunLauncher.StartRun</c> call.
        /// RpcTarget.All so the host arms itself too, since <c>MapHandler.InitializeMap</c>
        /// runs locally on every peer's own machine.
        /// </summary>
        public void ArmTerrainRandomizerSuppressionAll()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_ArmTerrainRandomizerSuppression), RpcTarget.All); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.ArmTerrainRandomizerSuppressionAll failed: {e.Message}"); }
        }

        /// <summary>
        /// Unlike timeOfDay, DayNightManager.dayCount has no vanilla RPC keeping it in sync
        /// across clients, so restoring it needs an explicit broadcast. The host applies the
        /// value locally before calling this, so RpcTarget.Others (not All) is correct.
        /// </summary>
        public void SyncDayCountAll(int dayCount)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_SyncDayCount), RpcTarget.Others, dayCount); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.SyncDayCountAll failed: {e.Message}"); }
        }

        /// <summary>
        /// Pushes a client's saved status effects using vanilla's own
        /// <c>CharacterAfflictions.RPC_ApplyStatusesFromFloatArray</c>, so it works on a client
        /// without this mod (statuses are owner-authoritative, so the host can't write them
        /// directly - only an RPC on the owner's machine reaches them, and
        /// <see cref="ApplyAfflictionsTo"/> uses our own channel, which doesn't exist there).
        /// Vanilla's RPC skips Weight/Thorns/Arrow (thorns are restored separately). Sent
        /// before our own RPC, so ours lands second and can't compound with it.
        /// </summary>
        public bool ApplyStatusesViaVanilla(Character character, float[] statuses)
        {
            if (character == null || statuses == null || statuses.Length == 0) return false;

            try
            {
                CharacterAfflictions afflictions = character.refs?.afflictions;
                if (afflictions == null) return false;

                PhotonView view = PhotonView.Get(afflictions);
                if (view == null) return false;

                view.RPC("RPC_ApplyStatusesFromFloatArray", character.photonView.Owner, statuses);
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.ApplyStatusesViaVanilla failed ({e.Message}); falling back to our own "
                    + "channel, which only reaches clients running this mod.");
                return false;
            }
        }

        public void ApplyAfflictionsTo(PhotonView playerView, string userId, float[] statuses, float extraStamina)
        {
            try
            {
                if (playerView == null || playerView.Owner == null) return;
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_ApplyAfflictions), playerView.Owner, userId, statuses, extraStamina);
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.ApplyAfflictionsTo failed: {e.Message}"); }
        }

        /// <summary>
        /// Tells the specific player who owns this PhotonView to equip their restored
        /// tempFullSlot (slot 250) item locally, since the host writing another client's
        /// Character state directly never becomes visible on that client's machine.
        /// </summary>
        public void EquipHeldItemFor(PhotonView playerView, string userId)
        {
            try
            {
                if (playerView == null || playerView.Owner == null) return;
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_EquipHeldItem), playerView.Owner, userId);
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.EquipHeldItemFor failed: {e.Message}"); }
        }

        /// <summary>
        /// Tells the specific player to re-apply their restored physical thorns locally
        /// (CharacterAfflictions.AddThorn no-ops unless called on the owning client). Takes
        /// <c>int[]</c>, not <c>ushort[]</c>: Photon's RPC serializer has no case for
        /// <c>ushort[]</c> and would silently fail to serialize it.
        /// </summary>
        public void RestoreThornsFor(PhotonView playerView, string userId, int[] thornIndices)
        {
            try
            {
                if (playerView == null || playerView.Owner == null) return;
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_RestoreThorns), playerView.Owner, userId, thornIndices);
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RestoreThornsFor failed: {e.Message}"); }
        }

        /// <summary>
        /// Tells the specific player to restore their achievement progress locally
        /// (AchievementManager is a client-local singleton). Sent as JSON rather than the
        /// raw <c>SerializableRunBasedValues</c> Photon type, since its <c>ConstructNew()</c>
        /// baseline must be primed from the receiving client's own Steam state - see
        /// AchievementProgressIO.ApplyLocal.
        /// </summary>
        public void RestoreAchievementProgressFor(PhotonView playerView, string userId, string achievementProgressJson)
        {
            try
            {
                if (playerView == null || playerView.Owner == null)
                {
                    _log.Trace($"[achievement-debug] RestoreAchievementProgressFor('{userId}'): no player view/owner, nothing sent.");
                    return;
                }
                _log.Trace($"[achievement-debug] RestoreAchievementProgressFor('{userId}'): sending "
                    + $"{(string.IsNullOrEmpty(achievementProgressJson) ? "EMPTY (no saved progress for them)" : achievementProgressJson.Length + " chars of saved progress")} "
                    + $"to actor #{playerView.Owner.ActorNumber}.");
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_ApplyAchievementProgress), playerView.Owner, userId, achievementProgressJson ?? "");
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RestoreAchievementProgressFor failed: {e.Message}"); }
        }

        /// <summary>
        /// Tells the specific player being restored as dead that the death is deliberate,
        /// so their <see cref="TeleportWatchdog"/> stops watching instead of reporting it
        /// as a bad-teleport symptom.
        /// </summary>
        public void SuppressWatchdogForRestoredDeath(PhotonView playerView, string userId)
        {
            try
            {
                if (playerView == null || playerView.Owner == null) return;
                _pv?.RPC(nameof(OwnNetworkRpc.RPC_SuppressWatchdogForRestoredDeath), playerView.Owner, userId);
            }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.SuppressWatchdogForRestoredDeath failed: {e.Message}"); }
        }

        public void RecentlyLitCampfireOthers()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RecentlyLitCampfire), RpcTarget.Others); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RecentlyLitCampfireOthers failed: {e.Message}"); }
        }

        /// <summary>
        /// Mirrors the host's wake-up + loading-screen presentation onto every other
        /// connected player, unconditionally rather than gating on the reported mod
        /// version: the version report arrives on its own timeline with no sync to this
        /// call, so a dict-based gate could miss a client that IS running this build. An
        /// older client without this RPC just logs a harmless "RPC method not found".
        /// </summary>
        public void ClientPresentationOthers(bool show)
        {
            // A `show` call starts a brand-new cycle - clear stale confirmations from the
            // previous one so AllClientsPresentationDone can't be satisfied by an old ack.
            if (show) _playerPresentationDone.Clear();
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_ClientPresentation), RpcTarget.Others, show); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.ClientPresentationOthers failed: {e.Message}"); }
        }

        internal void OnClientPresentationDone(string userId)
        {
            try
            {
                if (!_playerPresentationDone.ContainsKey(userId))
                    _playerPresentationDone[userId] = true;
                _log.Trace($"OwnNetwork: RPC_ClientPresentationDone userId={userId}.");
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.OnClientPresentationDone failed: {e}");
            }
        }

        /// <summary>
        /// True once every connected non-host player has confirmed their presentation
        /// cycle finished. Same mod-detection-grace exemption as <see cref="CheckReadyStatusForPlayers"/>.
        /// </summary>
        public bool AllClientsPresentationDone()
        {
            try
            {
                bool graceElapsed = _levelEnteredAt >= 0f
                    && (Time.unscaledTime - _levelEnteredAt) >= ModDetectionGraceSeconds;

                foreach (var player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (player == null) continue;
                    Character character = player.character;
                    if (character == null) continue;

                    string userId = NetworkingUtilities.GetUserId(character.player);
                    bool ownerIsMaster = character.photonView.Owner.IsMasterClient;
                    if (ownerIsMaster) continue;
                    if (_playerPresentationDone.ContainsKey(userId)) continue;
                    if (graceElapsed && !_playerModVersions.ContainsKey(userId)) continue; // no mod, never will report
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.AllClientsPresentationDone failed (assuming done): {e.Message}");
                return true;
            }
        }

        public void RequestSaveToMaster()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RequestSave), RpcTarget.MasterClient); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RequestSaveToMaster failed: {e.Message}"); }
        }

        internal void SavePlayerCoopFromRpc() => OwnSaveCapture.SavePlayerCoop(_cfg, _log, this);
        internal void LogError(string message) => _log?.LogError(message);
        internal ManualLogSource Log => _log;
    }

    /// <summary>
    /// PunRPC receiver for <see cref="OwnNetwork"/>'s channel. Kept as its own
    /// component (separate from <see cref="OwnNetwork"/>, a plain MonoBehaviour)
    /// since PUN RPCs must live on a <c>MonoBehaviourPun</c>
    /// </summary>
    public class OwnNetworkRpc : MonoBehaviourPun
    {
        internal OwnNetwork Owner;

        [PunRPC]
        public void RPC_SendReadyStatusToMaster(string userId, string userName)
        {
            Owner?.OnClientReportedReady(userId, userName);
        }

        [PunRPC]
        public void RPC_ReportModVersion(string userId, string version)
        {
            Owner?.OnClientReportedVersion(userId, version);
        }

        [PunRPC]
        public void RPC_ClientPresentation(bool show)
        {
            Owner?.HandleClientPresentation(show);
        }

        [PunRPC]
        public void RPC_ClientPresentationDone(string userId)
        {
            Owner?.OnClientPresentationDone(userId);
        }

        [PunRPC]
        public void RPC_RequestSave()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient) return;
                Owner?.SavePlayerCoopFromRpc();
                Owner?.EntryPoints?.ArmRecentlyLitCampfireCooldown(32f);
            }
            catch { }
        }

        [PunRPC]
        public void RPC_RecentlyLitCampfire()
        {
            if (PhotonNetwork.IsMasterClient) return;
            Owner?.EntryPoints?.ArmRecentlyLitCampfireCooldown(32f);
        }

        [PunRPC]
        public void RPC_RequestFalldamageProtection(int seconds)
        {
            OwnFallDamageProtection.Activate(seconds);
        }

        [PunRPC]
        public void RPC_ArmTerrainRandomizerSuppression()
        {
            OwnLoadEntryPoints.ArmSuppressExternalTerrainRandomizerOnce();
        }

        /// <summary>Colors reduced to a known set, since this is only ever sent by our own code.</summary>
        [PunRPC]
        public void RPC_SendMessage(string message, string colorKey, string seconds)
        {
            float duration = 4f;
            float.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);

            Color color = colorKey switch
            {
                "error" => new Color(1f, 0.5f, 0.5f, 1f),
                "success" => new Color(0.5f, 1f, 0.5f, 1f),
                "warning" => new Color(1f, 0.8f, 0.4f, 1f),
                _ => new Color(0.6f, 0.8f, 1f, 1f),
            };
            Owner?.MessageOverlay?.Show(message, color, duration);
        }

        /// <summary>Drives <see cref="TeleportWatchdog"/>'s load window on this client machine instead of showing a caption.</summary>
        [PunRPC]
        public void RPC_Loadingscreen(string enable, string targetPayload)
        {
            if (enable == "true")
            {
                Owner?.Watchdog?.BeginLoadWindow();
                HeightAchievementGuard.Suppress("client load window");
            }
            else
            {
                HeightAchievementGuard.Release("client load window");
                Owner?.Watchdog?.ArmPendingWatch(OwnNetwork.ParseVector(targetPayload));
            }
        }

        /// <summary>DayNightManager.dayCount has no owner/IsMine gating, so a direct write here is enough.</summary>
        [PunRPC]
        public void RPC_SyncDayCount(int dayCount)
        {
            try
            {
                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                if (dayNight != null) dayNight.dayCount = dayCount;
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_SyncDayCount error: {e}");
            }
        }

        [PunRPC]
        public void RPC_CloseEndscreen()
        {
            try
            {
                EndScreen endScreen = UnityEngine.Object.FindFirstObjectByType<EndScreen>();
                if (endScreen != null && endScreen.isOpen)
                    HarmonyLib.AccessTools.Method(typeof(MenuWindow), "Close")?.Invoke(endScreen, null);
            }
            catch { }
        }

        [PunRPC]
        public void RPC_ApplyAfflictions(string userId, float[] statuses, float extraStamina)
        {
            try
            {
                Character localCharacter = Character.localCharacter;
                if (localCharacter == null) return;
                if (NetworkingUtilities.GetUserId(localCharacter.player) != userId) return;

                // Length-tolerant, since this also has to survive a host/client mismatch mid-transition.
                CharacterAfflictions afflictions = localCharacter.refs.afflictions;
                AfflictionArrayCompat.CopyOverlap(statuses, afflictions.currentStatuses);

                try { localCharacter.SetExtraStamina(extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f); }
                catch { }
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_ApplyAfflictions error: {e}");
            }
        }

        /// <summary>
        /// Runs on the receiving client, where photonView.IsMine is true, so EquipSlot's
        /// network spawn + RPC broadcast work correctly. Requires tempFullSlot to already
        /// hold the restored item (checked defensively, not just trusted from the sender).
        /// </summary>
        [PunRPC]
        public void RPC_EquipHeldItem(string userId)
        {
            try
            {
                Character localCharacter = Character.localCharacter;
                if (localCharacter == null) return;
                if (NetworkingUtilities.GetUserId(localCharacter.player) != userId) return;
                if (localCharacter.player?.tempFullSlot == null || localCharacter.player.tempFullSlot.IsEmpty()) return;

                localCharacter.refs.items.EquipSlot(Zorro.Core.Optionable<byte>.Some((byte)250));
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_EquipHeldItem error: {e}");
            }
        }

        /// <summary>Runs on the receiving client, where photonView.IsMine is true, so AddThorn's IsMine guard passes.</summary>
        [PunRPC]
        public void RPC_RestoreThorns(string userId, int[] thornIndices)
        {
            try
            {
                Character localCharacter = Character.localCharacter;
                if (localCharacter == null) return;
                if (NetworkingUtilities.GetUserId(localCharacter.player) != userId) return;
                if (thornIndices == null) return;

                // Shared with the solo path, so co-op restore gets the same per-arrow hit-sound suppression.
                var indices = new List<ushort>(thornIndices.Length);
                foreach (int index in thornIndices) indices.Add((ushort)index);
                ThornsAndTicksRestore.ApplyThorns(localCharacter, indices, Owner?.Log);
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_RestoreThorns error: {e}");
            }
        }

        /// <summary>Runs on the machine whose character is about to be restored as dead, right before that death lands.</summary>
        [PunRPC]
        public void RPC_SuppressWatchdogForRestoredDeath(string userId)
        {
            try
            {
                Character localCharacter = Character.localCharacter;
                if (localCharacter == null) return;
                if (NetworkingUtilities.GetUserId(localCharacter.player) != userId) return;

                Owner?.Watchdog?.SuppressForRestoredDeath();
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_SuppressWatchdogForRestoredDeath error: {e}");
            }
        }

        /// <summary>Runs on the receiving client's own machine; an empty string means "no saved progress for this player".</summary>
        [PunRPC]
        public void RPC_ApplyAchievementProgress(string userId, string achievementProgressJson)
        {
            try
            {
                StartCoroutine(ApplyAchievementProgressWhenReady(userId, achievementProgressJson));
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_ApplyAchievementProgress error: {e}");
            }
        }

        /// <summary>
        /// Waits for the local character to exist (and be this player) before applying,
        /// rather than dropping the restore when <c>Character.localCharacter</c> is still
        /// null - the host fires this right as the client is still respawning its
        /// character, which previously silently discarded the achievement progress.
        /// Waiting also avoids being clobbered by vanilla's spawn-time InitRunBasedValues calls.
        /// </summary>
        private IEnumerator ApplyAchievementProgressWhenReady(string userId, string achievementProgressJson)
        {
            ManualLogSource log = Owner?.Log;
            float deadline = Time.realtimeSinceStartup + AchievementRestoreWaitSeconds;
            string localUserId = null;

            while (Time.realtimeSinceStartup < deadline)
            {
                Character local = Character.localCharacter;
                if (local != null && local.player != null)
                {
                    localUserId = null;
                    try { localUserId = NetworkingUtilities.GetUserId(local.player); }
                    catch { /* not registered yet - keep waiting */ }
                    if (!string.IsNullOrEmpty(localUserId)) break;
                }
                yield return null;
            }

            if (string.IsNullOrEmpty(localUserId))
            {
                log?.LogWarning($"[achievement-debug] RPC_ApplyAchievementProgress('{userId}'): DROPPED - no local "
                    + $"character after {AchievementRestoreWaitSeconds:F0}s, so this run's saved achievement progress "
                    + "was not restored on this machine.");
                yield break;
            }

            if (localUserId != userId)
            {
                log.Trace($"[achievement-debug] RPC_ApplyAchievementProgress('{userId}'): ignored, this machine is '{localUserId}'.");
                yield break;
            }

            log.Trace($"[achievement-debug] RPC_ApplyAchievementProgress('{userId}'): applying "
                + $"{(string.IsNullOrEmpty(achievementProgressJson) ? "EMPTY (no saved progress for this player)" : achievementProgressJson.Length + " chars")}.");

            OwnSavedAchievementProgress saved = AchievementProgressIO.FromJson(achievementProgressJson, log);
            AchievementProgressIO.ApplyLocal(saved, log);
        }

        // Generous: a slow machine's spawn can take a while; a timeout just falls back to counters restarting.
        private const float AchievementRestoreWaitSeconds = 30f;
    }
}
