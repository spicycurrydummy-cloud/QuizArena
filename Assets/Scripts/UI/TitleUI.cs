using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Fusion;
using GemmaQuiz.AI;
using GemmaQuiz.Network;

namespace GemmaQuiz.UI
{
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

        private readonly List<GameObject> sessionEntries = new();
        private bool browsingLobby;

        private void Start()
        {
            createSessionButton.onClick.AddListener(OnCreateSession);
            joinSessionButton.onClick.AddListener(OnJoinSession);

            if (soloTestButton != null)
                soloTestButton.onClick.AddListener(OnSoloTest);
            if (refreshSessionListButton != null)
                refreshSessionListButton.onClick.AddListener(OnRefreshSessionList);

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
            var input = playerNameInput?.text?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return $"GUEST_{Random.Range(100, 1000)}";
            return input;
        }

        private string GetSessionName()
        {
            var input = sessionNameInput?.text?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return $"Room_{Random.Range(100, 1000)}";
            return input;
        }

        private void OnCreateSession()
        {
            SetButtonsInteractable(false);
            SetStatus("セッションを作成中...");
            browsingLobby = false;

            NetworkManager.Instance.StartHost(
                GetSessionName(),
                GetPlayerName()
            );
        }

        private void OnJoinSession()
        {
            if (string.IsNullOrWhiteSpace(sessionNameInput.text))
            {
                SetStatus("セッション名を入力するか一覧から選択してください");
                return;
            }

            SetButtonsInteractable(false);
            SetStatus("セッションに参加中...");
            browsingLobby = false;

            NetworkManager.Instance.JoinSession(
                sessionNameInput.text.Trim(),
                GetPlayerName()
            );
        }

        private void OnSoloTest()
        {
            var playerName = GetPlayerName();
            var sessionName = "SoloTest_" + Random.Range(1000, 9999);

            SetButtonsInteractable(false);
            SetStatus("ソロテストモードで起動中...");
            browsingLobby = false;

            NetworkManager.Instance.StartHost(sessionName, playerName, maxPlayersOverride: 1);
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

            // 既存エントリをクリア
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
                var texts = entryObj.GetComponentsInChildren<Text>();
                if (texts.Length > 0)
                    texts[0].text = $"{session.Name}  ({session.PlayerCount}/{session.MaxPlayers})";

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

        private void HandlePlayerJoinedAsHost(Fusion.PlayerRef player)
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

        private void SetupAIProviderDropdown()
        {
            // Inspectorで未割り当てならランタイムで動的生成
            if (aiProviderDropdown == null)
                aiProviderDropdown = CreateAIProviderDropdownUI();
            if (aiProviderDropdown == null) return;

            aiProviderDropdown.ClearOptions();
            aiProviderDropdown.AddOptions(new List<string> { "Ollama (ローカル)", "Inception (Mercury)" });

            var gen = QuizGenerator.Instance;
            if (gen != null)
                aiProviderDropdown.value = (int)gen.Provider;

            aiProviderDropdown.onValueChanged.AddListener(OnAIProviderChanged);
        }

        private void OnAIProviderChanged(int index)
        {
            var gen = QuizGenerator.Instance;
            if (gen == null) return;

            gen.Provider = (AIProvider)index;
            Debug.Log($"[TitleUI] AI Provider changed to: {gen.Provider}");
        }

        private Dropdown CreateAIProviderDropdownUI()
        {
            var canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return null;

            // SessionNameInputの位置を基準にする
            var sessionInput = canvas.transform.Find("SessionNameInput");
            float baseX = -480f;
            float baseY = -30f;
            if (sessionInput != null)
            {
                var siRect = sessionInput.GetComponent<RectTransform>();
                if (siRect != null)
                {
                    baseX = siRect.anchoredPosition.x;
                    baseY = siRect.anchoredPosition.y - 80f;
                }
            }

            // ラベル（SessionNameLabel と同じ位置感）
            var labelObj = new GameObject("AIProviderLabel");
            labelObj.transform.SetParent(canvas.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchoredPosition = new Vector2(baseX, baseY + 35f);
            labelRect.sizeDelta = new Vector2(560, 40);
            labelObj.AddComponent<CanvasRenderer>();
            var label = labelObj.AddComponent<Text>();
            label.text = "問題生成AI";
            label.font = JapaneseFont.Get();
            label.fontSize = 24;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleLeft;

            // Dropdown（SessionNameInput と同じサイズ感: 560x50）
            var ddObj = DefaultControls.CreateDropdown(new DefaultControls.Resources());
            ddObj.name = "AIProviderDropdown";
            ddObj.transform.SetParent(canvas.transform, false);
            var ddRect = ddObj.GetComponent<RectTransform>();
            ddRect.anchoredPosition = new Vector2(baseX, baseY);
            ddRect.sizeDelta = new Vector2(560, 50);

            var ddImage = ddObj.GetComponent<Image>();
            if (ddImage != null)
                ddImage.color = new Color(0.2f, 0.2f, 0.3f, 1f);

            var ddLabel = ddObj.transform.Find("Label");
            if (ddLabel != null)
            {
                var ddLabelText = ddLabel.GetComponent<Text>();
                if (ddLabelText != null)
                {
                    ddLabelText.color = Color.white;
                    ddLabelText.fontSize = 22;
                }
            }

            // Arrow の大きさ調整
            var arrow = ddObj.transform.Find("Arrow");
            if (arrow != null)
            {
                var arrowRect = arrow.GetComponent<RectTransform>();
                if (arrowRect != null)
                    arrowRect.sizeDelta = new Vector2(30, 30);
            }

            var template = ddObj.transform.Find("Template");
            if (template != null)
            {
                var templateImage = template.GetComponent<Image>();
                if (templateImage != null)
                    templateImage.color = new Color(0.15f, 0.15f, 0.25f, 1f);

                // テンプレート内のアイテムも大きく
                var item = template.Find("Viewport/Content/Item");
                if (item != null)
                {
                    var itemRect = item.GetComponent<RectTransform>();
                    if (itemRect != null)
                        itemRect.sizeDelta = new Vector2(0, 45);
                    var itemLabel = item.Find("Item Label");
                    if (itemLabel != null)
                    {
                        var ilText = itemLabel.GetComponent<Text>();
                        if (ilText != null) ilText.fontSize = 20;
                    }
                }
            }

            // SessionNameInputの直後に配置
            if (sessionInput != null)
            {
                int idx = sessionInput.GetSiblingIndex() + 1;
                labelObj.transform.SetSiblingIndex(idx);
                ddObj.transform.SetSiblingIndex(idx + 1);
            }

            return ddObj.GetComponent<Dropdown>();
        }
    }
}
