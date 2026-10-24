using Fusion;
using UnityEngine;

namespace GemmaQuiz.Network
{
    /// <summary>
    /// ResultScene に配置される NetworkBehaviour。
    /// 「もう一度」を誰が押しても全員をロビーに戻すための RPC を提供する。
    /// スコア/選択ジャンルのリセットは LobbySync.Spawned で全員分行われる。
    /// </summary>
    public class ResultSync : NetworkBehaviour
    {
        public static ResultSync Instance { get; private set; }

        public override void Spawned()
        {
            Instance = this;
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 任意プレイヤーから呼び出し可能。ホストが LobbyScene をネットワーク経由でロードする。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcRequestPlayAgain()
        {
            Debug.Log("[ResultSync] RpcRequestPlayAgain received; host loads LobbyScene");
            NetworkManager.Instance?.LoadScene("LobbyScene");
        }
    }
}
