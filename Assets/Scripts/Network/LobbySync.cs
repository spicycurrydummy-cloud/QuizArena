using Fusion;
using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.Network
{
    /// <summary>
    /// ロビー画面でプレイヤー間の状態を同期するNetworkBehaviour。
    /// LobbyScene上にシーン配置されたNetworkObjectにアタッチする。
    /// </summary>
    public class LobbySync : NetworkBehaviour
    {
        public static LobbySync Instance { get; private set; }

        public static event System.Action OnSyncChanged;

        // 空スロットマーカー (PlayerIdは>=0なのでint.MinValueは衝突しない)
        private const int EMPTY_SLOT = int.MinValue;

        // プレイヤースロット8人分
        [Networked, Capacity(8)] public NetworkArray<int> SelectedGenres { get; }
        [Networked, Capacity(8)] public NetworkArray<NetworkString<_32>> PlayerNames { get; }
        [Networked, Capacity(8)] public NetworkArray<int> PlayerSlotIds { get; }
        [Networked, Capacity(8)] public NetworkArray<NetworkString<_64>> CustomGenreTexts { get; }
        [Networked] public int HostPlayerId { get; set; } = -1;

        private ChangeDetector changeDetector;

        public override void Render()
        {
            if (changeDetector == null) return;
            foreach (var changed in changeDetector.DetectChanges(this))
            {
                OnSyncChanged?.Invoke();
                break;
            }
        }

        public override void Spawned()
        {
            Instance = this;
            changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);

            if (HasStateAuthority)
            {
                for (int i = 0; i < 8; i++)
                {
                    SelectedGenres.Set(i, -1);
                    PlayerNames.Set(i, "");
                    PlayerSlotIds.Set(i, EMPTY_SLOT);
                    CustomGenreTexts.Set(i, "");
                }
                HostPlayerId = Runner.LocalPlayer.PlayerId;
            }

            Debug.Log($"[LobbySync] Spawned (HasStateAuthority={HasStateAuthority}, LocalPlayer={Runner.LocalPlayer.PlayerId})");

            // 即座に名前登録（シーン遷移前に確実に送信）
            RegisterMyName();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        private void RegisterMyName()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;
            RpcRegisterPlayer(nm.LocalPlayerName ?? "");
        }

        /// <summary>
        /// info.Sourceが空ならLocalPlayerを返すヘルパー
        /// </summary>
        private int ResolveCallerPlayerId(PlayerRef source)
        {
            if (source.IsRealPlayer) return source.PlayerId;
            return Runner.LocalPlayer.PlayerId; // ホストがローカル呼び出しした場合
        }

        /// <summary>
        /// プレイヤー名のみを登録（ジャンル選択前に呼ぶ）。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcRegisterPlayer(NetworkString<_32> playerName, RpcInfo info = default)
        {
            int playerId = ResolveCallerPlayerId(info.Source);
            int slot = GetOrAssignSlot(playerId);
            if (slot < 0) return;
            PlayerNames.Set(slot, playerName);
            Debug.Log($"[LobbySync] RpcRegisterPlayer: slot={slot}, name={playerName}, playerId={playerId}");
        }

        /// <summary>
        /// プレイヤーがジャンルを選択した際にホストに送信。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcSelectGenre(int genreIndex, NetworkString<_32> playerName, RpcInfo info = default)
        {
            int playerId = ResolveCallerPlayerId(info.Source);
            int slot = GetOrAssignSlot(playerId);
            if (slot < 0) return;

            SelectedGenres.Set(slot, genreIndex);
            PlayerNames.Set(slot, playerName);
            Debug.Log($"[LobbySync] Player(id={playerId}) (slot {slot}) selected genre {genreIndex}");
        }

        /// <summary>
        /// プレイヤーがカスタムジャンルを入力した際にホストに送信。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcSelectCustomGenre(NetworkString<_64> customText, NetworkString<_32> playerName, RpcInfo info = default)
        {
            int playerId = ResolveCallerPlayerId(info.Source);
            int slot = GetOrAssignSlot(playerId);
            if (slot < 0) return;

            SelectedGenres.Set(slot, GenreEncoding.CUSTOM_GENRE_CODE * 100);
            CustomGenreTexts.Set(slot, customText);
            PlayerNames.Set(slot, playerName);
            Debug.Log($"[LobbySync] Player(id={playerId}) (slot {slot}) selected custom genre: {customText}");
        }

        /// <summary>
        /// プレイヤーが切断した時にスロットを解放（ホストのみ呼ぶ）。
        /// </summary>
        public void ReleaseSlot(PlayerRef player)
        {
            if (!HasStateAuthority) return;
            int slot = FindSlot(player.PlayerId);
            if (slot >= 0)
            {
                SelectedGenres.Set(slot, -1);
                PlayerNames.Set(slot, "");
                PlayerSlotIds.Set(slot, EMPTY_SLOT);
                CustomGenreTexts.Set(slot, "");
            }
        }

        private int GetOrAssignSlot(int playerId)
        {
            int existing = FindSlot(playerId);
            if (existing >= 0) return existing;

            for (int i = 0; i < 8; i++)
            {
                if (PlayerSlotIds[i] == EMPTY_SLOT)
                {
                    PlayerSlotIds.Set(i, playerId);
                    return i;
                }
            }
            return -1;
        }

        private int FindSlot(int playerId)
        {
            for (int i = 0; i < 8; i++)
            {
                if (PlayerSlotIds[i] == playerId) return i;
            }
            return -1;
        }

        public int GetActiveSlotCount()
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                if (PlayerSlotIds[i] != EMPTY_SLOT) count++;
            }
            return count;
        }

        public bool AreAllReady(int requiredCount)
        {
            int ready = 0;
            for (int i = 0; i < 8; i++)
            {
                if (PlayerSlotIds[i] != EMPTY_SLOT && SelectedGenres[i] >= 0) ready++;
            }
            return ready >= requiredCount && requiredCount > 0;
        }
    }
}
