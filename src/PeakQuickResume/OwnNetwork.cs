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
    /// M1 scope only: the readiness-gate RPC (client -> host "I'm ready"). The
    /// remaining RPCs (save request, message relay, loading screen, hero title,
    /// endscreen close, afflictions) are ported in M7 alongside the rest of the coop
    /// path - see ROADMAP.md Phase 8. Not wired into <see cref="ResumeOrchestrator"/>
    /// yet; this milestone only proves the channel itself works
    ///
    /// Uses a distinct <c>ViewID</c> (69420) from the checkpoint mod's own hardcoded
    /// 19420 (decompile line 992) since both mods' PhotonViews coexist during the
    /// Phase 8 transition window (checkpoint mod stays installed for diffing, see
    /// ROADMAP.md decision 2). Safe from Photon's auto-allocated range: PEAK caps
    /// rooms at 4 players (<c>NetworkingUtilities.MAX_PLAYERS</c>, decompile line
    /// 89482), so with PUN's default 1000 auto-IDs per actor, nothing auto-assigned
    /// ever gets remotely close to 69420
    /// </summary>
    public class OwnNetwork : MonoBehaviour
    {
        private const int ViewId = 69420;

        private ManualLogSource _log;
        private PluginConfig _cfg;

        private GameObject _networkGo;
        private PhotonView _pv;

        // Mirrors the checkpoint mod's own playerReceivedReadyStatus (decompile line
        // 843): userId -> userName, populated only on the master client, reset on
        // scene transitions exactly like the original (decompile 1345-1413)
        private readonly Dictionary<string, string> _playerReceivedReadyStatus = new Dictionary<string, string>();
        private bool _clientSentReadyStatus;

        public void Init(ManualLogSource log, PluginConfig cfg)
        {
            _log = log;
            _cfg = cfg;
            CreatePhotonView();
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
                return;
            }

            if (RunLauncher.InTitle)
            {
                _clientSentReadyStatus = false;
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
    }
}
