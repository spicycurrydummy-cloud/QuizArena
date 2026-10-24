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

        public const int MaxSlots = 8;

        // 空スロットマーカー (PlayerIdは>=0なのでint.MinValueは衝突しない)
        private const int EMPTY_SLOT = int.MinValue;

        // プレイヤースロット8人分
        [Networked, Capacity(MaxSlots)] public NetworkArray<int> SelectedGenres { get; }
        [Networked, Capacity(MaxSlots)] public NetworkArray<NetworkString<_32>> PlayerNames { get; }
        [Networked, Capacity(MaxSlots)] public NetworkArray<int> PlayerSlotIds { get; }
        [Networked, Capacity(MaxSlots)] public NetworkArray<NetworkString<_64>> CustomGenreTexts { get; }
        [Networked] public int HostPlayerId { get; set; } = -1;

        // ホストが設定した「1ジャンルあたりの問題数」。クライアントの表示用。
        [Networked] public int QuestionsPerGenre { get; set; }

        private ChangeDetector changeDetector;

        public override void Render()
        {
            if (changeDetector == null) return;
            foreach (var _ in changeDetector.DetectChanges(this))
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
                for (int i = 0; i < MaxSlots; i++)
                {
                    SelectedGenres.Set(i, -1);
                    PlayerNames.Set(i, "");
                    PlayerSlotIds.Set(i, EMPTY_SLOT);
                    CustomGenreTexts.Set(i, "");
                }
                HostPlayerId = Runner.LocalPlayer.PlayerId;

                var gen = AI.QuizGenerator.Instance;
                QuestionsPerGenre = gen != null ? gen.QuestionsPerGenre : 5;
            }

            // ロビーに入った時点で前回ゲームのスコア/選択をリセット (ホスト・クライアント共通)
            SessionManager.Instance?.ResetRoundState();

            Debug.Log($"[LobbySync] Spawned (HasStateAuthority={HasStateAuthority}, LocalPlayer={Runner.LocalPlayer.PlayerId})");

            // 即座に名前登録
            var nm = NetworkManager.Instance;
            if (nm != null)
                RpcRegisterPlayer(nm.LocalPlayerName ?? "");
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        public bool IsSlotEmpty(int slot) => PlayerSlotIds[slot] == EMPTY_SLOT;

        public int FindSlot(int playerId)
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                if (PlayerSlotIds[i] == playerId) return i;
            }
            return -1;
        }

        /// <summary>
        /// ホストのみ: 問題数を変更して全員に同期する。
        /// </summary>
        public void SetQuestionsPerGenre(int count)
        {
            if (!HasStateAuthority) return;
            QuestionsPerGenre = count;
        }

        /// <summary>
        /// info.Sourceが空(ホストのローカル呼び出し)ならLocalPlayerを返す
        /// </summary>
        private int ResolveCallerPlayerId(PlayerRef source)
        {
            return source.IsRealPlayer ? source.PlayerId : Runner.LocalPlayer.PlayerId;
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
        /// プレイヤーがジャンルを選択した際にホストに送信。genreIndex は GenreEncoding 済みの値。
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RpcSelectGenre(int encodedGenre, NetworkString<_32> playerName, RpcInfo info = default)
        {
            int playerId = ResolveCallerPlayerId(info.Source);
            int slot = GetOrAssignSlot(playerId);
            if (slot < 0) return;

            SelectedGenres.Set(slot, encodedGenre);
            CustomGenreTexts.Set(slot, "");
            PlayerNames.Set(slot, playerName);
            Debug.Log($"[LobbySync] Player(id={playerId}) (slot {slot}) selected genre {encodedGenre}");
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
        /// プレイヤーが切断した時にスロットを解放（ホストのみ）。
        /// </summary>
        public void ReleaseSlot(PlayerRef player)
        {
            if (!HasStateAuthority) return;
            int slot = FindSlot(player.PlayerId);
            if (slot < 0) return;
            SelectedGenres.Set(slot, -1);
            PlayerNames.Set(slot, "");
            PlayerSlotIds.Set(slot, EMPTY_SLOT);
            CustomGenreTexts.Set(slot, "");
        }

        private int GetOrAssignSlot(int playerId)
        {
            int existing = FindSlot(playerId);
            if (existing >= 0) return existing;

            for (int i = 0; i < MaxSlots; i++)
            {
                if (PlayerSlotIds[i] == EMPTY_SLOT)
                {
                    PlayerSlotIds.Set(i, playerId);
                    return i;
                }
            }
            return -1;
        }
    }
}
