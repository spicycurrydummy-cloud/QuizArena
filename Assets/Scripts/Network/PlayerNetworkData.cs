using Fusion;
using UnityEngine;

namespace GemmaQuiz.Network
{
    /// <summary>
    /// 各プレイヤーのネットワーク同期データ。
    /// プレイヤーがセッションに参加した際にスポーンされるNetworkObject上に配置する。
    /// </summary>
    public class PlayerNetworkData : NetworkBehaviour
    {
        [Networked] public NetworkString<_32> PlayerName { get; set; }
        [Networked] public int TotalScore { get; set; }
        [Networked] public NetworkBool IsReady { get; set; }
        [Networked] public int SelectedGenreIndex { get; set; } = -1; // -1 = 未選択

        public PlayerRef PlayerRef => Object.InputAuthority;

        public override void Spawned()
        {
            if (HasInputAuthority)
            {
                RpcSetPlayerName(NetworkManager.Instance.LocalPlayerName);
            }
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        private void RpcSetPlayerName(NetworkString<_32> name)
        {
            PlayerName = name;
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void RpcSetGenre(int genreIndex)
        {
            SelectedGenreIndex = genreIndex;
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
        public void RpcSetReady(NetworkBool ready)
        {
            IsReady = ready;
        }
    }
}
