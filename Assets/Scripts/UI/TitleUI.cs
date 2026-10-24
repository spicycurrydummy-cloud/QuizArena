using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Fusion;
using GemmaQuiz.AI;
using GemmaQuiz.Network;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// タイトル画面のUI制御。セッション作成/参加/一覧、AIプロバイダー選択。
    /// UI要素はすべて TitleScene 上に配置し Inspector で参照する。
    /// </summary>
    public class TitleUI : MonoBehaviour
    {
        [Header("Input Fields")]
        [SerializeField] private InputField playerNameInput;
        [SerializeField] private InputField sessionNameInput;

        [Header("Buttons")]
        [SerializeField] private Button createSessionButton;
        [SerializeField] private Button joinSessionButton;
        [SerializeField] private Button soloTestButton;
        [SerializeField] private Button refreshSessionListButton;

        [Header("Session List")]
        [SerializeField] private Transform sessionListContent;
        [SerializeField] private GameObject sessionListEntryPrefab;

        [Header("AI Provider")]
        [SerializeField] private Dropdown aiProviderDropdown;

        [Header("Status")]
        [SerializeField] private Text statusText;

        // Dropdown の並び。WebGL では localhost の Ollama に届かないので除外する。
        private readonly List<(string label, AIProvider provider)> providerOptions = new();

        private readonly List<GameObject> sessionEntries = new();
        private bool browsingLobby;
        private string guestName; // 名前未入力時のGUEST名 (セッション中は固定)

        private void Start()
        {
            createSessionButton.onClick.AddListener(OnCreateSession);
            joinSessionButton.onClick.AddListener(OnJoinSession);

            if (soloTestButton != null)
                soloTestButton.onClick.AddListener(OnSoloTest);
            if (refreshSessionListButton != null)
                refreshSessionListButton.onClick.AddListener(OnRefreshSessionList);
            if (sessionNameInput != null)
                sessionNameInput.onSubmit.AddListener(_ => OnJoinSession()); // Enterで参加

            SetupAIProviderDropdown();

            var nm = NetworkManager.Instance;
            if (nm != null)
            {
                nm.OnSessionJoined += HandleSessionJoined;
                nm.OnSessionError += HandleSessionError;
                nm.OnPlayerJoinedSession += HandlePlayerJoinedAsHost;
                nm.OnSessionListUpdatedEvent += HandleSessionListUpdated;
            }

            SetStatus("名前未入力ならGUEST、セッション名を入力するか一覧から選択");

            // 自動的にロビーへ接続して一覧取得
            OnRefreshSessionList();
        }

        private void OnDestroy()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnSessionJoined -= HandleSessionJoined;
            nm.OnSessionError -= HandleSessionError;
            nm.OnPlayerJoinedSession -= HandlePlayerJoinedAsHost;
            nm.OnSessionListUpdatedEvent -= HandleSessionListUpdated;
        }

        private string GetPlayerName()
        {
            var input = playerNameInput != null ? playerNameInput.text?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(input))
                return input;
            if (string.IsNullOrEmpty(guestName))
                guestName = $"GUEST_{Random.Range(100, 1000)}";
            return guestName;
        }

        private string GetSessionName()
        {
            var input = sessionNameInput != null ? sessionNameInput.text?.Trim() : null;
            if (string.IsNullOrWhiteSpace(input))
                return $"Room_{Random.Range(100, 1000)}";
            return input;
        }

        private void OnCreateSession()
        {
            SetButtonsInteractable(false);
            SetStatus("セッションを作成中...");
            browsingLobby = false;

            NetworkManager.Instance.StartHost(GetSessionName(), GetPlayerName());
        }

        private void OnJoinSession()
        {
            if (sessionNameInput == null || string.IsNullOrWhiteSpace(sessionNameInput.text))
            {
                SetStatus("セッション名を入力するか一覧から選択してください");
                return;
            }
            if (!joinSessionButton.interactable) return; // 二重送信防止 (Enter + クリック)

            SetButtonsInteractable(false);
            SetStatus("セッションに参加中...");
            browsingLobby = false;

            NetworkManager.Instance.JoinSession(sessionNameInput.text.Trim(), GetPlayerName());
        }

        private void OnSoloTest()
        {
            SetButtonsInteractable(false);
            SetStatus("ソロテストモードで起動中...");
            browsingLobby = false;

            NetworkManager.Instance.StartHost("SoloTest_" + Random.Range(1000, 9999), GetPlayerName(), maxPlayersOverride: 1);
        }

        private void OnRefreshSessionList()
        {
            browsingLobby = true;
            SetStatus("セッション一覧を取得中...");
            NetworkManager.Instance.JoinSessionLobby(GetPlayerName());
        }

        private void HandleSessionListUpdated(List<SessionInfo> sessions)
        {
            // 作成/参加処理中はステータスを上書きしない
            if (!browsingLobby) return;

            foreach (var e in sessionEntries) Destroy(e);
            sessionEntries.Clear();

            if (sessionListContent == null || sessionListEntryPrefab == null) return;

            if (sessions.Count == 0)
            {
                SetStatus("セッションがありません");
                return;
            }

            SetStatus($"{sessions.Count}件のセッションが見つかりました");

            foreach (var session in sessions)
            {
                var entryObj = Instantiate(sessionListEntryPrefab, sessionListContent);
                var text = entryObj.GetComponentInChildren<Text>();
                if (text != null)
                    text.text = $"{session.Name}  ({session.PlayerCount}/{session.MaxPlayers})";

                var btn = entryObj.GetComponent<Button>();
                if (btn != null)
                {
                    var capturedName = session.Name;
                    btn.onClick.AddListener(() =>
                    {
                        sessionNameInput.text = capturedName;
                        OnJoinSession();
                    });
                }

                sessionEntries.Add(entryObj);
            }
        }

        private void HandleSessionJoined()
        {
            if (browsingLobby) return; // ロビー閲覧中は遷移しない
            SetStatus("接続完了! ロビーに移動します...");
        }

        private void HandlePlayerJoinedAsHost(PlayerRef player)
        {
            if (browsingLobby) return;
            if (NetworkManager.Instance.IsHost)
            {
                SetStatus("ロビーに移動します...");
                NetworkManager.Instance.LoadScene("LobbyScene");
            }
        }

        private void HandleSessionError(string message)
        {
            SetStatus(message);
            SetButtonsInteractable(true);
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
                statusText.text = message;
        }

        private void SetButtonsInteractable(bool interactable)
        {
            createSessionButton.interactable = interactable;
            joinSessionButton.interactable = interactable;
            if (soloTestButton != null) soloTestButton.interactable = interactable;
            if (refreshSessionListButton != null) refreshSessionListButton.interactable = interactable;
        }

        // ===== AIプロバイダー =====

        private void SetupAIProviderDropdown()
        {
            if (aiProviderDropdown == null) return;

            providerOptions.Clear();
            providerOptions.Add(("Inception (Mercury 3)", AIProvider.Inception));
            providerOptions.Add(("Gemini (Flash Lite)", AIProvider.Gemini));
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                providerOptions.Add(("Ollama (ローカル)", AIProvider.Ollama));

            var labels = new List<string>();
            foreach (var (label, _) in providerOptions) labels.Add(label);

            aiProviderDropdown.ClearOptions();
            aiProviderDropdown.AddOptions(labels);

            var gen = QuizGenerator.Instance;
            int current = gen != null ? providerOptions.FindIndex(o => o.provider == gen.Provider) : 0;
            aiProviderDropdown.SetValueWithoutNotify(Mathf.Max(0, current));

            aiProviderDropdown.onValueChanged.AddListener(OnAIProviderChanged);
        }

        private void OnAIProviderChanged(int index)
        {
            var gen = QuizGenerator.Instance;
            if (gen == null || index < 0 || index >= providerOptions.Count) return;

            gen.Provider = providerOptions[index].provider;
            Debug.Log($"[TitleUI] AI Provider changed to: {gen.Provider}");
        }
    }
}
