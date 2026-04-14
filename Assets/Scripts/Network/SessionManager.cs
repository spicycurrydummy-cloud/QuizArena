using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEngine;

namespace GemmaQuiz.Network
{
    /// <summary>
    /// セッション内のプレイヤー管理を担当。
    /// NetworkObjectのスポーンではなく、ローカルなDictionary + RPCで管理する。
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        public static SessionManager Instance { get; private set; }

        private readonly Dictionary<PlayerRef, PlayerInfo> players = new();

        public event Action OnPlayerListChanged;

        public IReadOnlyDictionary<PlayerRef, PlayerInfo> Players => players;
        public int PlayerCount => players.Count;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private bool subscribed;

        private void OnEnable()
        {
            EnsureSubscribed();
        }

        private void OnDisable()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnPlayerJoinedSession -= HandlePlayerJoined;
            nm.OnPlayerLeftSession -= HandlePlayerLeft;
            subscribed = false;
        }

        /// <summary>
        /// NetworkManagerのイベント購読を確実に実行する。
        /// Awake/OnEnable順序問題を避けるため何度呼んでも安全。
        /// </summary>
        public void EnsureSubscribed()
        {
            if (subscribed) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnPlayerJoinedSession -= HandlePlayerJoined;
            nm.OnPlayerLeftSession -= HandlePlayerLeft;
            nm.OnPlayerJoinedSession += HandlePlayerJoined;
            nm.OnPlayerLeftSession += HandlePlayerLeft;
            subscribed = true;
        }

        /// <summary>
        /// シーンロード後/イベント取りこぼし時に呼び出して、現在のRunner状態から
        /// プレイヤーリストを再構築する。イベント駆動の補助。
        /// </summary>
        public void EnsureAllPlayersRegistered()
        {
            EnsureSubscribed();

            var nm = NetworkManager.Instance;
            if (nm == null || nm.Runner == null) return;

            var runner = nm.Runner;
            bool changed = false;

            // LocalPlayer を確実に登録
            var localPlayer = runner.LocalPlayer;
            if (localPlayer.IsRealPlayer && !players.ContainsKey(localPlayer))
            {
                string name = ResolvePlayerName(localPlayer);
                players[localPlayer] = new PlayerInfo
                {
                    playerName = name,
                    playerRef = localPlayer,
                    totalScore = 0,
                    selectedGenreIndex = -1,
                    isReady = false
                };
                changed = true;
            }

            // ActivePlayers の他プレイヤーも追加
            foreach (var playerRef in runner.ActivePlayers)
            {
                if (!players.ContainsKey(playerRef))
                {
                    string name = ResolvePlayerName(playerRef);
                    players[playerRef] = new PlayerInfo
                    {
                        playerName = name,
                        playerRef = playerRef,
                        totalScore = 0,
                        selectedGenreIndex = -1,
                        isReady = false
                    };
                    changed = true;
                }
                else
                {
                    UpdatePlayerNameIfEmpty(playerRef);
                }
            }

            if (changed) OnPlayerListChanged?.Invoke();
        }

        private void HandlePlayerJoined(PlayerRef player)
        {
            if (players.ContainsKey(player))
            {
                // 既存だが名前が空ならlate-updateする
                UpdatePlayerNameIfEmpty(player);
                return;
            }

            string name = ResolvePlayerName(player);

            players[player] = new PlayerInfo
            {
                playerName = name,
                playerRef = player,
                totalScore = 0,
                selectedGenreIndex = -1,
                isReady = false
            };

            Debug.Log($"[SessionManager] Player joined: {name} ({player})");
            OnPlayerListChanged?.Invoke();
        }

        private string ResolvePlayerName(PlayerRef player)
        {
            var nm = NetworkManager.Instance;
            if (nm == null || nm.Runner == null)
                return $"GUEST_{UnityEngine.Random.Range(100, 1000)}";

            bool isLocal = player == nm.Runner.LocalPlayer;
            if (isLocal)
            {
                if (!string.IsNullOrWhiteSpace(nm.LocalPlayerName))
                    return nm.LocalPlayerName;
                return $"GUEST_{UnityEngine.Random.Range(100, 1000)}";
            }

            return $"Player {player.PlayerId}";
        }

        private void UpdatePlayerNameIfEmpty(PlayerRef player)
        {
            if (!players.TryGetValue(player, out var info)) return;
            if (!string.IsNullOrWhiteSpace(info.playerName) && !info.playerName.StartsWith("Player ")) return;

            var newName = ResolvePlayerName(player);
            if (newName != info.playerName)
            {
                info.playerName = newName;
                OnPlayerListChanged?.Invoke();
            }
        }

        /// <summary>
        /// 外部から名前を設定（LobbySyncから受信した同期名等）。
        /// シーン遷移後も保持される。
        /// </summary>
        public void SetPlayerName(int playerId, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var kvp in players)
            {
                if (kvp.Key.PlayerId == playerId)
                {
                    if (kvp.Value.playerName != name)
                    {
                        kvp.Value.playerName = name;
                        OnPlayerListChanged?.Invoke();
                    }
                    return;
                }
            }
        }

        private void HandlePlayerLeft(PlayerRef player)
        {
            if (players.Remove(player))
            {
                Debug.Log($"[SessionManager] Player left: {player}");
                OnPlayerListChanged?.Invoke();
            }
        }

        /// <summary>
        /// セッション切断時に全プレイヤー情報をリセット。
        /// </summary>
        public void ClearAll()
        {
            players.Clear();
            OnPlayerListChanged?.Invoke();
        }

        public void SetPlayerGenre(PlayerRef player, int genreIndex)
        {
            if (players.TryGetValue(player, out var info))
            {
                info.selectedGenreIndex = genreIndex;
                info.isReady = true;
                OnPlayerListChanged?.Invoke();
            }
        }

        /// <summary>
        /// PlayerId 経由でジャンル選択をセット (LobbySyncからのコピー用)。
        /// シーン遷移後も保持される。
        /// </summary>
        public void SetPlayerGenreById(int playerId, int genreIndex)
        {
            foreach (var kvp in players)
            {
                if (kvp.Key.PlayerId == playerId)
                {
                    if (kvp.Value.selectedGenreIndex != genreIndex)
                    {
                        kvp.Value.selectedGenreIndex = genreIndex;
                        kvp.Value.isReady = true;
                        OnPlayerListChanged?.Invoke();
                    }
                    return;
                }
            }
        }

        public void SetPlayerSubGenreById(int playerId, int subGenreIndex)
        {
            foreach (var kvp in players)
            {
                if (kvp.Key.PlayerId == playerId)
                {
                    kvp.Value.selectedSubGenreIndex = subGenreIndex;
                    return;
                }
            }
        }

        public void SetPlayerCustomGenreById(int playerId, string customText)
        {
            foreach (var kvp in players)
            {
                if (kvp.Key.PlayerId == playerId)
                {
                    kvp.Value.customGenreText = customText ?? "";
                    return;
                }
            }
        }

        public void AddScore(PlayerRef player, int score)
        {
            if (players.TryGetValue(player, out var info))
            {
                info.totalScore += score;
            }
        }

        public List<PlayerInfo> GetSortedPlayerList()
        {
            return players.Values.OrderBy(p => p.playerRef.PlayerId).ToList();
        }

        public List<PlayerInfo> GetScoreRanking()
        {
            return players.Values.OrderByDescending(p => p.totalScore).ToList();
        }
    }

    /// <summary>
    /// プレイヤー情報。NetworkObjectではなくシンプルなクラスで管理。
    /// </summary>
    [Serializable]
    public class PlayerInfo
    {
        public string playerName;
        public PlayerRef playerRef;
        public int totalScore;
        public int selectedGenreIndex;
        public int selectedSubGenreIndex;
        public string customGenreText = "";
        public bool isReady;
    }
}
