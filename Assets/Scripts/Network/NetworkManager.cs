using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Fusion;
using Fusion.Sockets;

namespace GemmaQuiz.Network
{
    /// <summary>
    /// Photon Fusion 2 の接続・セッション管理を担当するシングルトン。
    /// NetworkRunner の生成とライフサイクルを管理する。
    /// </summary>
    public class NetworkManager : MonoBehaviour, INetworkRunnerCallbacks
    {
        public static NetworkManager Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private int maxPlayers = 8;

        public NetworkRunner Runner { get; private set; }
        public string LocalPlayerName { get; private set; }
        public bool IsHost => Runner != null && Runner.IsServer;

        public event Action OnSessionJoined;
        public event Action<string> OnSessionError;
        public event Action<PlayerRef> OnPlayerJoinedSession;
        public event Action<PlayerRef> OnPlayerLeftSession;
        public event Action<List<SessionInfo>> OnSessionListUpdatedEvent;

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

        /// <summary>
        /// ホストとしてセッションを作成する。
        /// </summary>
        public async void StartHost(string sessionName, string playerName)
        {
            LocalPlayerName = playerName;
            await StartSession(GameMode.Host, sessionName);
        }

        /// <summary>
        /// クライアントとしてセッションに参加する。
        /// </summary>
        public async void JoinSession(string sessionName, string playerName)
        {
            LocalPlayerName = playerName;
            await StartSession(GameMode.Client, sessionName);
        }

        /// <summary>
        /// セッションから切断する。
        /// </summary>
        public async void LeaveSession()
        {
            await CleanupRunner();
        }

        private async System.Threading.Tasks.Task CleanupRunner()
        {
            if (Runner != null)
            {
                try
                {
                    await Runner.Shutdown(destroyGameObject: false);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[NetworkManager] Shutdown error: {e.Message}");
                }
            }

            Runner = null;

            // 古いNetworkRunnerコンポーネントが残っていれば全削除（Immediate）
            if (this != null && gameObject != null)
            {
                var oldRunners = GetComponents<NetworkRunner>();
                foreach (var r in oldRunners)
                {
                    if (r != null)
                        DestroyImmediate(r);
                }
            }

            // SessionManagerのプレイヤー情報もクリア
            SessionManager.Instance?.ClearAll();
        }

        private async System.Threading.Tasks.Task StartSession(GameMode mode, string sessionName)
        {
            // 既存のRunnerを完全にクリーンアップ
            await CleanupRunner();

            // 1フレーム待ってDestroyImmediateの後始末を確実にする
            await System.Threading.Tasks.Task.Yield();

            if (this == null || gameObject == null)
            {
                Debug.LogError("[NetworkManager] GameObject was destroyed during cleanup");
                return;
            }

            Runner = gameObject.AddComponent<NetworkRunner>();
            if (Runner == null)
            {
                Debug.LogError("[NetworkManager] Failed to create NetworkRunner");
                OnSessionError?.Invoke("NetworkRunnerの作成に失敗しました");
                return;
            }
            Runner.ProvideInput = true;

            var sceneMgr = gameObject.GetComponent<NetworkSceneManagerDefault>();
            if (sceneMgr == null)
                sceneMgr = gameObject.AddComponent<NetworkSceneManagerDefault>();

            var startArgs = new StartGameArgs
            {
                GameMode = mode,
                SessionName = sessionName,
                PlayerCount = maxPlayers,
                SceneManager = sceneMgr,
                IsVisible = true,
                IsOpen = true,
                CustomLobbyName = "GemmaQuizLobby"
            };

            var result = await Runner.StartGame(startArgs);

            if (result.Ok)
            {
                Debug.Log($"[NetworkManager] Session '{sessionName}' started as {mode}.");
            }
            else
            {
                Debug.LogError($"[NetworkManager] Failed to start session: {result.ShutdownReason}");
                OnSessionError?.Invoke(GetErrorMessage(result.ShutdownReason));

                if (Runner != null)
                {
                    Destroy(Runner);
                    Runner = null;
                }
            }
        }

        public void LoadScene(string sceneName)
        {
            if (Runner == null)
            {
                Debug.LogError("[NetworkManager] LoadScene: Runner is null");
                return;
            }
            if (!Runner.IsServer)
            {
                Debug.LogWarning("[NetworkManager] LoadScene: not server, ignored");
                return;
            }

            int idx = SceneUtility.GetBuildIndexByScenePath($"Assets/Scenes/{sceneName}.unity");
            Debug.Log($"[NetworkManager] LoadScene: {sceneName} (buildIndex={idx})");
            Runner.LoadScene(SceneRef.FromIndex(idx));
        }

        private string GetErrorMessage(ShutdownReason reason)
        {
            return reason switch
            {
                ShutdownReason.GameNotFound => "セッションが見つかりませんでした",
                ShutdownReason.GameIsFull => "セッションが満員です",
                ShutdownReason.ServerInRoom => "サーバーは既にルームに存在します",
                ShutdownReason.DisconnectedByPluginLogic => "プラグインにより切断されました",
                _ => $"接続に失敗しました ({reason})"
            };
        }

        // --- INetworkRunnerCallbacks ---

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            Debug.Log($"[NetworkManager] Player joined: {player}");
            OnPlayerJoinedSession?.Invoke(player);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            Debug.Log($"[NetworkManager] Player left: {player}");
            OnPlayerLeftSession?.Invoke(player);
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
            Debug.Log("[NetworkManager] Connected to server.");
            OnSessionJoined?.Invoke();
        }

        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadDone(NetworkRunner runner) { Debug.Log("[NetworkManager] OnSceneLoadDone"); }
        public void OnSceneLoadStart(NetworkRunner runner) { Debug.Log("[NetworkManager] OnSceneLoadStart"); }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            Debug.LogWarning($"[NetworkManager] Disconnected from server: {reason}");
            OnSessionError?.Invoke("サーバーから切断されました");

            // 自動的にタイトルへ戻る
            ReturnToTitle();
        }

        private void ReturnToTitle()
        {
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (currentScene != "TitleScene")
            {
                Debug.Log("[NetworkManager] Returning to TitleScene");
                UnityEngine.SceneManagement.SceneManager.LoadScene("TitleScene");
            }
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
            request.Accept();
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            Debug.LogError($"[NetworkManager] Connect failed: {reason}");
            OnSessionError?.Invoke("接続に失敗しました");
        }

        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            Debug.Log($"[NetworkManager] Shutdown: {shutdownReason}");

            // クライアント要求以外（ホスト切断など）でシャットダウンした場合はタイトルへ
            if (shutdownReason != ShutdownReason.Ok &&
                shutdownReason != ShutdownReason.OperationCanceled)
            {
                ReturnToTitle();
            }
        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            Debug.Log($"[NetworkManager] Session list updated: {sessionList.Count} sessions");
            OnSessionListUpdatedEvent?.Invoke(sessionList);
        }

        /// <summary>
        /// セッション一覧を取得するためにロビーに接続する。
        /// </summary>
        public async void JoinSessionLobby(string playerName)
        {
            // 既にロビーに接続済みなら再接続しない（更新ボタン用）
            if (Runner != null && Runner.IsRunning && !Runner.IsServer && !Runner.SessionInfo.IsValid)
            {
                LocalPlayerName = playerName;
                Debug.Log("[NetworkManager] Already in lobby, skipping re-join");
                return;
            }

            await CleanupRunner();
            await System.Threading.Tasks.Task.Yield();

            if (this == null || gameObject == null) return;

            LocalPlayerName = playerName;

            Runner = gameObject.AddComponent<NetworkRunner>();
            if (Runner == null)
            {
                Debug.LogError("[NetworkManager] Failed to create NetworkRunner for lobby");
                return;
            }
            Runner.ProvideInput = false;

            var result = await Runner.JoinSessionLobby(SessionLobby.Custom, "GemmaQuizLobby");
            if (!result.Ok)
            {
                Debug.LogError($"[NetworkManager] Failed to join lobby: {result.ShutdownReason}");
                OnSessionError?.Invoke(GetErrorMessage(result.ShutdownReason));
            }
            else
            {
                Debug.Log("[NetworkManager] Joined session lobby");
            }
        }
    }
}
