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
    /// Our own PhotonView/RPC channel (Phase 8 M1), replacing the checkpoint mod's own
    /// <c>CheckpointNetwork</c> (decompile 461-695) + <c>CreatePhotonView</c> (961-1003)
    /// + <c>CheckReadyStatusForPlayers</c>/<c>SendReadyStatusToMaster</c> (1005-1054)
    ///
    /// M7 adds the rest of the RPC surface needed for coop:
    /// <c>RPC_RequestSave</c>, <c>RPC_RecentlyLitCampfire</c>,
    /// <c>RPC_RequestFalldamageProtection</c>, <c>RPC_SendMessage</c>,
    /// <c>RPC_Loadingscreen</c>, <c>RPC_CloseEndscreen</c>, <c>RPC_ApplyAfflictions</c>
    /// (decompile 507-682). <c>RPC_SendModVersionToMaster</c>/<c>RPC_SetHeroTitle</c>/
    /// <c>RPC_SyncMapVisuals</c> are deliberately NOT ported (see ROADMAP.md Phase 8:
    /// mod-version check unused, hero-title banner is cosmetic, PEAKapalooza-only)
    ///
    /// <c>RPC_Loadingscreen</c> is named after the original's "Loading savegame..." UI
    /// caption (which we don't port - purely cosmetic), but is repurposed: the load-begin
    /// and load-done moments are the signal that arms/disarms
    /// <see cref="TeleportWatchdog"/>'s load window on every machine, so this RPC's
    /// receiver drives the watchdog directly instead of showing a caption, keeping that
    /// mitigation alive for our own path in coop (see ROADMAP.md Phase 6)
    ///
    /// Uses a fixed <c>ViewID</c> (69420) well clear of Photon's auto-allocated range:
    /// PEAK caps rooms at 4 players (<c>NetworkingUtilities.MAX_PLAYERS</c>, decompile
    /// line 89482), so with PUN's default 1000 auto-IDs per actor, nothing auto-assigned
    /// ever gets remotely close to 69420
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

        // Mirrors the checkpoint mod's own playerReceivedReadyStatus (decompile line
        // 843): userId -> userName, populated only on the master client, reset on
        // scene transitions exactly like the original (decompile 1345-1413)
        private readonly Dictionary<string, string> _playerReceivedReadyStatus = new Dictionary<string, string>();
        private bool _clientSentReadyStatus;

        // Own addition: userId -> reported PluginInfo.Version. Diagnostic only (see
        // ClientPresentationOthers' remarks for why it doesn't gate anything) - logged on
        // receipt so a session log shows exactly who's running what. Never populated in
        // solo/offline (no other players to hear from)
        private readonly Dictionary<string, string> _playerModVersions = new Dictionary<string, string>();
        private bool _clientSentVersionReport;

        public void Init(ManualLogSource log, PluginConfig cfg)
        {
            _log = log;
            _cfg = cfg;
            CreatePhotonView();
        }

        /// <summary>
        /// M7: wires the dependencies the rest of the RPC surface needs, set after
        /// construction since <see cref="OwnLoadEntryPoints"/> is created after this
        /// object in <c>Plugin.Awake</c> (mirrors <see cref="Init"/>'s own late-binding
        /// shape rather than restructuring construction order)
        /// </summary>
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
                _log?.LogInfo($"OwnNetwork: PhotonView created (ViewID={ViewId}).");
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.CreatePhotonView failed: {e}");
            }
        }

        // Mirrors the checkpoint mod's own Update() scene-based state machine
        // (decompile 1345-1413) for JUST the ready-status bookkeeping - the rest of
        // that state machine (mod-version check, campfire cooldowns, etc.) is ported
        // alongside the pieces that actually need it in later milestones
        private void Update()
        {
            if (_cfg == null) return;

            if (RunLauncher.InAirport)
            {
                _clientSentReadyStatus = false;
                _clientSentVersionReport = false;
                _playerReceivedReadyStatus.Clear();
                return;
            }

            if (RunLauncher.InLevel)
            {
                if (!_clientSentReadyStatus && !RunLauncher.IsHost)
                {
                    StartCoroutine(SendReadyStatusToMaster());
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
                _clientSentReadyStatus = false;
                _clientSentVersionReport = false;
                _playerReceivedReadyStatus.Clear();
            }
        }

        // Mirrors SendReadyStatusToMaster (decompile 1020-1032): waits for the local
        // character to exist, then a flat 5s settle, then RPCs the master client
        private IEnumerator SendReadyStatusToMaster()
        {
            while (Character.localCharacter == null) yield return null;
            yield return new WaitForSeconds(5f);

            try
            {
                _pv.RPC(nameof(OwnNetworkRpc.RPC_SendReadyStatusToMaster), RpcTarget.MasterClient,
                    PhotonNetwork.LocalPlayer.UserId, PhotonNetwork.LocalPlayer.NickName);
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.SendReadyStatusToMaster RPC failed: {e}");
            }
        }

        // Own addition: unlike ready-status (which deliberately waits 5s to settle), the version
        // report has nothing to wait on - send it as soon as the local character exists, so it's
        // guaranteed to reach the host well before the (5s-delayed) ready-status report does,
        // which is what the host's own coop wait actually gates on before ever loading a save
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

        // Called (master-client side only) by OwnNetworkRpc.RPC_ReportModVersion
        internal void OnClientReportedVersion(string userId, string version)
        {
            try
            {
                _playerModVersions[userId] = version;
                _log?.LogInfo($"OwnNetwork: client {userId} reports Quick Resume v{version}.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnNetwork.OnClientReportedVersion failed: {e.Message}");
            }
        }

        // Called on the RECEIVING client's machine by OwnNetworkRpc.RPC_ClientPresentation
        // (sent by the host, see ClientPresentationOthers) - mirrors the host's own local
        // presentation using this machine's own WakeUpEffect/LoadingScreen instances
        internal void HandleClientPresentation(bool show)
        {
            StartCoroutine(show ? RunClientPresentationEnter() : RunClientPresentationExit());
        }

        private IEnumerator RunClientPresentationEnter()
        {
            // The host fires this RPC as soon as ITS OWN character/teleport sequence is ready,
            // with no guarantee this client's own character has finished spawning into the fresh
            // level scene yet (confirmed in a real session log: PlayWakeUp saw
            // Character.localCharacter as null and silently skipped the whole beat). Wait for it
            // here, with a timeout so a genuinely stuck spawn can't hang this coroutine forever
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

            // Mirrors the host's own ordering exactly (see OwnTeleportSequence.cs remarks): just
            // hide the screen here, the collapse/reveal/stand-up happens on RunClientPresentationExit.
            // DebugDisableLoadingScreen skips just the overlay, same as on the host
            bool showLoadingScreen = _cfg != null && !_cfg.DebugDisableLoadingScreen.Value;
            if (showLoadingScreen && LoadingScreen != null)
                yield return LoadingScreen.FadeIn(_cfg.OwnLoadingScreenFadeTime.Value);
        }

        private IEnumerator RunClientPresentationExit()
        {
            // Mirrors the host's own ordering exactly, including the settle hold before fading
            // out (see OwnTeleportSequence.cs remarks) - each client manages its own local timing
            bool showLoadingScreen = _cfg != null && !_cfg.DebugDisableLoadingScreen.Value;
            if (WakeUpEffect != null) WakeUpEffect.Collapse();
            if (_cfg != null)
                yield return new WaitForSeconds(Mathf.Max(0f, _cfg.OwnWakeUpSettleHoldTime.Value));
            if (showLoadingScreen && LoadingScreen != null)
                yield return LoadingScreen.FadeOut(_cfg.OwnLoadingScreenFadeTime.Value);
            if (_cfg != null && WakeUpEffect != null)
                yield return WakeUpEffect.Wake(_cfg.OwnWakeUpStandTime.Value);
        }

        // Called (master-client side only) by OwnNetworkRpc.RPC_SendReadyStatusToMaster.
        // Mirrors the RPC_SendReadyStatusToMaster guard exactly (decompile 490-505)
        internal void OnClientReportedReady(string userId, string userName)
        {
            try
            {
                if (PhotonNetwork.OfflineMode || !PhotonNetwork.IsMasterClient
                    || _cfg == null || !_cfg.OwnEnableClientReadyStatusCheck.Value)
                    return;

                if (!_playerReceivedReadyStatus.ContainsKey(userId))
                    _playerReceivedReadyStatus.Add(userId, userName);

                _log?.LogInfo($"OwnNetwork: RPC_SendReadyStatusToMaster userId={userId}, userName={userName}.");
            }
            catch (Exception e)
            {
                _log?.LogError($"OwnNetwork.OnClientReportedReady failed: {e}");
            }
        }

        /// <summary>
        /// True once every connected non-host player has reported ready (or the
        /// ready-check setting is disabled). Mirrors <c>CheckReadyStatusForPlayers</c>
        /// (decompile 1034-1054) field-for-field: every live <c>Player</c>'s owning
        /// actor must either be the master client itself, or already be present in
        /// the ready-status dictionary above
        /// </summary>
        public bool CheckReadyStatusForPlayers()
        {
            if (_cfg == null || !_cfg.OwnEnableClientReadyStatusCheck.Value) return true;

            try
            {
                foreach (var player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (player == null) continue;
                    Character character = player.character;
                    if (character == null) continue;

                    string userId = NetworkingUtilities.GetUserId(character.player);
                    bool ownerIsMaster = character.photonView.Owner.IsMasterClient;
                    if (!_playerReceivedReadyStatus.ContainsKey(userId) && !ownerIsMaster)
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

        // --- M7: outbound RPC senders, host-called (mirrors each original call site's
        // own RpcTarget exactly - see decompile line references on each) ---

        /// <summary>Mirrors decompile line 2280: RpcTarget.All (0), so the host arms its own window too</summary>
        public void RequestFalldamageProtectionAll(int seconds)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RequestFalldamageProtection), RpcTarget.All, seconds); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RequestFalldamageProtectionAll failed: {e.Message}"); }
        }

        /// <summary>Mirrors decompile line 2292: RpcTarget.Others (1)</summary>
        public void CloseEndscreenOthers()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_CloseEndscreen), RpcTarget.Others); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.CloseEndscreenOthers failed: {e.Message}"); }
        }

        /// <summary>Mirrors decompile lines 2973/4586: RpcTarget.Others (1)</summary>
        public void SendMessageOthers(string message, string colorKey, float seconds)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_SendMessage), RpcTarget.Others, message, colorKey, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.SendMessageOthers failed: {e.Message}"); }
        }

        /// <summary>
        /// Mirrors decompile lines 2274/2964's RpcTarget.Others (1), repurposed to drive
        /// TeleportWatchdog on each client instead of showing a caption - see class remarks.
        /// The originally-unused second string param now carries the host's real teleport
        /// <paramref name="target"/> (on the enable=false "load done" call) so a client that
        /// never received a warp can still recover to it - see RPC_Loadingscreen
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

        /// <summary>Mirrors decompile line 2909: targeted at the specific player's owner</summary>
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
        /// Own addition (no decompile counterpart - see OwnSaveData.heldItemState
        /// remarks): tells the SPECIFIC player who owns this Player/PhotonView to equip
        /// their own restored tempFullSlot (slot 250) item locally - same targeted-RPC
        /// shape as <see cref="ApplyAfflictionsTo"/>, for the same reason (host writing
        /// another client's Character state directly never becomes visible on that
        /// client's own machine)
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

        /// <summary>Mirrors decompile line 162: RpcTarget.Others (1), sent by whichever machine actually saved</summary>
        public void RecentlyLitCampfireOthers()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RecentlyLitCampfire), RpcTarget.Others); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RecentlyLitCampfireOthers failed: {e.Message}"); }
        }

        /// <summary>
        /// Own addition: mirrors the host's own wake-up + loading-screen presentation (see
        /// OwnTeleportSequence.cs) onto every OTHER connected player, unconditionally
        /// (RpcTarget.Others, same shape as LoadingScreenOthers above) rather than gating on the
        /// per-player version reported above: an earlier version tried to only target players
        /// confirmed (via that dict) to be running Quick Resume v2.0.0+, but the version report is
        /// sent on its own timeline (as soon as each client's local character exists) with no
        /// synchronization to when the HOST happens to reach this call - confirmed in a real
        /// session log where the host's broadcast fired and the client's version report arrived
        /// only several log lines later, so the dict lookup missed a client that WAS actually
        /// running this exact build. A genuinely older client (pre-2.0.0, before this RPC existed
        /// at all) simply has no <c>RPC_ClientPresentation</c> method for Photon to find - it logs
        /// a harmless "RPC method not found" on ITS end and nothing happens, the same graceful
        /// degradation an explicit version gate would have provided, without the race. The version
        /// report above is kept (logged on receipt) as a diagnostic breadcrumb, not a gate
        /// </summary>
        public void ClientPresentationOthers(bool show)
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_ClientPresentation), RpcTarget.Others, show); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.ClientPresentationOthers failed: {e.Message}"); }
        }

        /// <summary>Mirrors decompile line 167: RpcTarget.MasterClient (2), client -> host</summary>
        public void RequestSaveToMaster()
        {
            try { _pv?.RPC(nameof(OwnNetworkRpc.RPC_RequestSave), RpcTarget.MasterClient); }
            catch (Exception e) { _log?.LogWarning($"OwnNetwork.RequestSaveToMaster failed: {e.Message}"); }
        }

        /// <summary>Called by <see cref="OwnNetworkRpc.RPC_RequestSave"/> on the master client</summary>
        internal void SavePlayerCoopFromRpc() => OwnSaveCapture.SavePlayerCoop(_cfg, _log, this);

        internal void LogError(string message) => _log?.LogError(message);
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

        /// <summary>Own addition: client -> host, see OwnNetwork.ReportVersionToMaster</summary>
        [PunRPC]
        public void RPC_ReportModVersion(string userId, string version)
        {
            Owner?.OnClientReportedVersion(userId, version);
        }

        /// <summary>
        /// Own addition: host -> a specific qualifying client, see OwnNetwork.ClientPresentationOthers.
        /// Mirrors the host's own wake-up + loading-screen presentation locally on this machine
        /// </summary>
        [PunRPC]
        public void RPC_ClientPresentation(bool show)
        {
            Owner?.HandleClientPresentation(show);
        }

        /// <summary>Mirrors RPC_RequestSave exactly (decompile 507-516): master-only</summary>
        [PunRPC]
        public void RPC_RequestSave()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient) return;
                Owner?.SavePlayerCoopFromRpc();
                Owner?.EntryPoints?.ArmRecentlyLitCampfireCooldown(32f);
            }
            catch { /* mirrors the original's own lack of a try/catch here being harmless - kept safe regardless */ }
        }

        /// <summary>Mirrors RPC_RecentlyLitCampfire exactly (decompile 518-525): non-master only</summary>
        [PunRPC]
        public void RPC_RecentlyLitCampfire()
        {
            if (PhotonNetwork.IsMasterClient) return;
            Owner?.EntryPoints?.ArmRecentlyLitCampfireCooldown(32f);
        }

        /// <summary>Mirrors RPC_RequestFalldamageProtection exactly (decompile 527-536)</summary>
        [PunRPC]
        public void RPC_RequestFalldamageProtection(int seconds)
        {
            OwnFallDamageProtection.Activate(seconds);
        }

        /// <summary>
        /// Mirrors RPC_SendMessage's dispatch shape (decompile 538-563), colors reduced to
        /// our own known set (only ever sent by our own code, not an open text channel) -
        /// shows through our own <see cref="OwnMessageOverlay"/> (Phase 8 M9), same as
        /// every other message this mod shows
        /// </summary>
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

        /// <summary>
        /// Repurposed - see <see cref="OwnNetwork"/>'s class remarks: no caption, drives
        /// <see cref="TeleportWatchdog"/>'s load window on this (client) machine instead
        /// </summary>
        [PunRPC]
        public void RPC_Loadingscreen(string enable, string targetPayload)
        {
            if (enable == "true")
            {
                Owner?.Watchdog?.BeginLoadWindow();
            }
            else
            {
                // End the window, passing the host-forwarded real target so a client that
                // never got warped can still recover to it - see OwnNetwork.LoadingScreenOthers
                Owner?.Watchdog?.ArmPendingWatch(OwnNetwork.ParseVector(targetPayload));
            }
        }

        /// <summary>Mirrors RPC_CloseEndscreen exactly (decompile 595-610)</summary>
        [PunRPC]
        public void RPC_CloseEndscreen()
        {
            try
            {
                EndScreen endScreen = UnityEngine.Object.FindFirstObjectByType<EndScreen>();
                if (endScreen != null && endScreen.isOpen)
                    HarmonyLib.AccessTools.Method(typeof(MenuWindow), "Close")?.Invoke(endScreen, null);
            }
            catch { /* matches the original's own swallow */ }
        }

        /// <summary>Mirrors RPC_ApplyAfflictions exactly (decompile 612-682)</summary>
        [PunRPC]
        public void RPC_ApplyAfflictions(string userId, float[] statuses, float extraStamina)
        {
            try
            {
                Character localCharacter = Character.localCharacter;
                if (localCharacter == null) return;
                if (NetworkingUtilities.GetUserId(localCharacter.player) != userId) return;

                CharacterAfflictions afflictions = localCharacter.refs.afflictions;
                if (statuses != null && afflictions.currentStatuses != null && afflictions.currentStatuses.Length == statuses.Length)
                    Array.Copy(statuses, afflictions.currentStatuses, afflictions.currentStatuses.Length);

                try { localCharacter.SetExtraStamina(extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f); }
                catch { /* matches the original's own swallow */ }
            }
            catch (Exception e)
            {
                Owner?.LogError($"RPC_ApplyAfflictions error: {e}");
            }
        }

        /// <summary>
        /// Own addition (no decompile counterpart), see <see cref="OwnNetwork.EquipHeldItemFor"/>.
        /// Runs on the receiving client's own machine, where photonView.IsMine is
        /// actually true for this character, so CharacterItems.EquipSlot's own network
        /// spawn + EquipSlotRpc broadcast work correctly - unlike calling it from the
        /// host for a Character it doesn't own. Requires the local tempFullSlot copy to
        /// already hold the restored item (the sender times this after that player's own
        /// SyncInventoryRPC), otherwise EquipSlot would just clear currentSelectedSlot
        /// instead - checked defensively here too, not just trusted from the sender
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
    }
}
