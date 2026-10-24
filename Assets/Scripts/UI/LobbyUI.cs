using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GemmaQuiz.Data;
using GemmaQuiz.Network;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// ロビー画面のUI制御。プレイヤーリスト、ジャンル選択、問題数、ゲーム開始ボタン。
    /// プレイヤーの状態は LobbySync ([Networked]) を正とし、SessionManager はそのローカルコピー。
    /// </summary>
    public class LobbyUI : MonoBehaviour
    {
        [Header("Room Info")]
        [SerializeField] private Text sessionNameText;
        [SerializeField] private Text playerCountText;

        [Header("Player List")]
        [SerializeField] private Transform playerListContent;
        [SerializeField] private GameObject playerListEntryPrefab;

        [Header("Genre Selection")]
        [SerializeField] private Transform genreButtonContainer;
        [SerializeField] private GameObject genreButtonPrefab;
        [SerializeField] private Text selectedGenreText;
        [SerializeField] private InputField customGenreInput;

        [Header("Question Count (host only)")]
        [SerializeField] private Text questionCountText;
        [SerializeField] private Button questionCountMinusButton;
        [SerializeField] private Button questionCountPlusButton;

        [Header("Buttons")]
        [SerializeField] private Button startGameButton;
        [SerializeField] private Button leaveButton;

        [Header("Status")]
        [SerializeField] private Text statusText;

        [Header("Sub-genre Popup")]
        [SerializeField] private int popupFontSize = 22;
        [SerializeField] private float popupWidth = 380f;

        private readonly List<GameObject> entryInstances = new();
        private readonly Dictionary<QuizGenre, Image> genreButtonImages = new();

        // サブジャンルポップアップ (内容がジャンルごとに変わるので動的生成)
        private GameObject subGenrePopupRoot;

        private static readonly Color selectedColor = new Color(0.3f, 0.7f, 0.4f);
        private static readonly Color deselectedColor = new Color(0.2f, 0.2f, 0.3f);

        private bool IsHost => NetworkManager.Instance != null && NetworkManager.Instance.IsHost;

        private void Start()
        {
            startGameButton.onClick.AddListener(OnStartGame);
            leaveButton.onClick.AddListener(OnLeave);

            if (questionCountMinusButton != null)
                questionCountMinusButton.onClick.AddListener(() => ChangeQuestionCount(-1));
            if (questionCountPlusButton != null)
                questionCountPlusButton.onClick.AddListener(() => ChangeQuestionCount(+1));
            if (customGenreInput != null)
                customGenreInput.onEndEdit.AddListener(OnCustomGenreSubmit);

            CreateGenreButtons();

            // 問題数の +/- はホストだけが操作できる
            bool isHost = IsHost;
            if (questionCountMinusButton != null) questionCountMinusButton.gameObject.SetActive(isHost);
            if (questionCountPlusButton != null) questionCountPlusButton.gameObject.SetActive(isHost);
            startGameButton.gameObject.SetActive(isHost);
            UpdateQuestionCountText();

            var session = SessionManager.Instance;
            if (session != null)
            {
                session.OnPlayerListChanged += RefreshPlayerList;
                session.EnsureAllPlayersRegistered();
            }
            LobbySync.OnSyncChanged += RefreshPlayerList;

            UpdateSessionInfo();
            RefreshPlayerList();
        }

        private void OnDestroy()
        {
            var session = SessionManager.Instance;
            if (session != null)
                session.OnPlayerListChanged -= RefreshPlayerList;
            LobbySync.OnSyncChanged -= RefreshPlayerList;
        }

        // イベント取りこぼし保険: 数秒に一度だけ同期を取り直す
        private float syncPollTimer;
        private const float SyncPollInterval = 2f;

        private void Update()
        {
            if (LobbySync.Instance == null) return;
            syncPollTimer += Time.deltaTime;
            if (syncPollTimer < SyncPollInterval) return;
            syncPollTimer = 0f;
            RefreshPlayerList();
        }

        // ===== 問題数 =====

        private void ChangeQuestionCount(int delta)
        {
            var gen = AI.QuizGenerator.Instance;
            if (gen == null || !IsHost) return;

            gen.QuestionsPerGenre += delta;
            LobbySync.Instance?.SetQuestionsPerGenre(gen.QuestionsPerGenre);
            UpdateQuestionCountText();
        }

        private void UpdateQuestionCountText()
        {
            if (questionCountText == null) return;

            // クライアントは LobbySync 経由でホストの設定値を表示する
            var sync = LobbySync.Instance;
            var gen = AI.QuizGenerator.Instance;
            int count = (!IsHost && sync != null && sync.QuestionsPerGenre > 0) ? sync.QuestionsPerGenre
                      : gen != null ? gen.QuestionsPerGenre : 5;
            questionCountText.text = $"問題数: {count}問 / ジャンル";
        }

        // ===== ジャンルボタン =====

        private void CreateGenreButtons()
        {
            if (genreButtonPrefab == null || genreButtonContainer == null) return;

            foreach (QuizGenre genre in System.Enum.GetValues(typeof(QuizGenre)))
            {
                var btnObj = Instantiate(genreButtonPrefab, genreButtonContainer);
                var btnText = btnObj.GetComponentInChildren<Text>();
                if (btnText != null)
                    btnText.text = genre.ToJapanese();

                var btnImage = btnObj.GetComponent<Image>();
                btnImage.color = deselectedColor;
                genreButtonImages[genre] = btnImage;

                // クリック/長押しは LongPressButton で判定する (Button.onClick は使わない)
                var btn = btnObj.GetComponent<Button>();
                if (btn != null) btn.onClick.RemoveAllListeners();

                var longPress = btnObj.AddComponent<LongPressButton>();
                int index = (int)genre;
                longPress.OnClick += () => OnGenreSelected(index, 0);

                // NonGenre以外は長押しでサブジャンルポップアップ
                if (genre != QuizGenre.NonGenre)
                    longPress.OnLongPress += () => ShowSubGenrePopup(index);
            }
        }

        private void OnGenreSelected(int genreIndex, int subGenreIndex)
        {
            HideSubGenrePopup();

            int encoded = GenreEncoding.Encode(genreIndex, subGenreIndex);

            // カスタム入力をクリア (テキストをプログラムから書き換えても onEndEdit は発火しない)
            if (customGenreInput != null) customGenreInput.text = "";

            foreach (var kvp in genreButtonImages)
                kvp.Value.color = ((int)kvp.Key == genreIndex) ? selectedColor : deselectedColor;

            if (selectedGenreText != null)
                selectedGenreText.text = $"選択中: {GenreEncoding.GetDisplayName(genreIndex, subGenreIndex)}";

            ApplyLocalSelection(genreIndex, subGenreIndex, "");

            var sync = LobbySync.Instance;
            var nm = NetworkManager.Instance;
            if (sync != null && nm != null)
                sync.RpcSelectGenre(encoded, nm.LocalPlayerName ?? "");

            RefreshPlayerList();
        }

        private void OnCustomGenreSubmit(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            HideSubGenrePopup();

            foreach (var kvp in genreButtonImages)
                kvp.Value.color = deselectedColor;

            if (selectedGenreText != null)
                selectedGenreText.text = $"選択中: {text}";

            ApplyLocalSelection(GenreEncoding.CUSTOM_GENRE_CODE, 0, text);

            var sync = LobbySync.Instance;
            var nm = NetworkManager.Instance;
            if (sync != null && nm != null)
                sync.RpcSelectCustomGenre(text, nm.LocalPlayerName ?? "");

            RefreshPlayerList();
        }

        /// <summary>
        /// 自分の選択を SessionManager (ローカルコピー) に反映する。RPC が届く前の即時表示用。
        /// </summary>
        private void ApplyLocalSelection(int genreIndex, int subGenreIndex, string customText)
        {
            var session = SessionManager.Instance;
            var runner = NetworkManager.Instance?.Runner;
            if (session == null || runner == null) return;

            int pid = runner.LocalPlayer.PlayerId;
            session.SetPlayerGenre(runner.LocalPlayer, genreIndex);
            session.SetPlayerSubGenreById(pid, subGenreIndex);
            session.SetPlayerCustomGenreById(pid, customText);
        }

        // ===== サブジャンルポップアップ =====

        private void ShowSubGenrePopup(int genreIndex)
        {
            var genre = (QuizGenre)genreIndex;
            var subs = genre.GetSubGenreList();
            if (subs == null || subs.Length == 0) return;

            HideSubGenrePopup();

            var canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;

            // 画面全体を覆う半透明の背景。クリックで閉じる
            subGenrePopupRoot = new GameObject("SubGenrePopup", typeof(RectTransform), typeof(Image), typeof(Button));
            subGenrePopupRoot.transform.SetParent(canvas.transform, false);
            var rootRect = subGenrePopupRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            subGenrePopupRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            var backdropButton = subGenrePopupRoot.GetComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropButton.onClick.AddListener(HideSubGenrePopup);

            // ポップアップパネル本体
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            panel.transform.SetParent(subGenrePopupRoot.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(popupWidth, 0);
            panel.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.2f, 0.98f);

            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 14, 14);
            layout.spacing = 6;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            panel.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // タイトル
            AddPopupLabel(panel.transform, $"{genre.ToJapanese()} のサブジャンル", popupFontSize + 2);

            // 「すべて」+ 各サブジャンル
            AddPopupButton(panel.transform, $"{genre.ToJapanese()} (すべて)", new Color(0.25f, 0.45f, 0.35f), () => OnGenreSelected(genreIndex, 0));
            for (int i = 0; i < subs.Length; i++)
            {
                int si = i + 1;
                AddPopupButton(panel.transform, subs[i], new Color(0.2f, 0.2f, 0.35f), () => OnGenreSelected(genreIndex, si));
            }

            // 閉じる
            AddPopupButton(panel.transform, "閉じる", new Color(0.35f, 0.2f, 0.2f), HideSubGenrePopup);
        }

        private void AddPopupLabel(Transform parent, string label, int fontSize)
        {
            var obj = new GameObject("Title", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<LayoutElement>().preferredHeight = fontSize + 18;
            var text = obj.GetComponent<Text>();
            text.text = label;
            text.font = JapaneseFont.Get();
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        private void AddPopupButton(Transform parent, string label, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
            btnObj.transform.SetParent(parent, false);
            btnObj.AddComponent<LayoutElement>().preferredHeight = popupFontSize + 24;
            btnObj.GetComponent<Image>().color = color;

            var text = btnObj.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = label;
                text.font = JapaneseFont.Get();
                text.fontSize = popupFontSize;
                text.color = Color.white;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            btnObj.GetComponent<Button>().onClick.AddListener(onClick);
        }

        private void HideSubGenrePopup()
        {
            if (subGenrePopupRoot == null) return;
            Destroy(subGenrePopupRoot);
            subGenrePopupRoot = null;
        }

        // ===== セッション情報 =====

        private void UpdateSessionInfo()
        {
            var runner = NetworkManager.Instance?.Runner;
            if (runner == null || runner.SessionInfo == null || sessionNameText == null) return;
            sessionNameText.text = $"セッション: {runner.SessionInfo.Name}";
        }

        // ===== プレイヤーリスト =====

        private void RefreshPlayerList()
        {
            var session = SessionManager.Instance;
            if (session == null) return;

            var sync = LobbySync.Instance;
            var runner = NetworkManager.Instance?.Runner;

            // 先に LobbySync の同期名を SessionManager に反映しておく
            // (SetPlayerName は OnPlayerListChanged を発火するので、描画ループの中では呼ばない)
            if (sync != null)
            {
                for (int slot = 0; slot < LobbySync.MaxSlots; slot++)
                {
                    if (sync.IsSlotEmpty(slot)) continue;
                    var name = sync.PlayerNames[slot].ToString();
                    if (!string.IsNullOrEmpty(name))
                        session.SetPlayerName(sync.PlayerSlotIds[slot], name);
                }
            }

            var players = session.GetSortedPlayerList();

            int hostPlayerId = sync != null ? sync.HostPlayerId : -1;
            if (hostPlayerId < 0 && runner != null && runner.IsServer)
                hostPlayerId = runner.LocalPlayer.PlayerId;

            // エントリ数を合わせる
            while (entryInstances.Count < players.Count)
                entryInstances.Add(Instantiate(playerListEntryPrefab, playerListContent));
            while (entryInstances.Count > players.Count)
            {
                var last = entryInstances[entryInstances.Count - 1];
                entryInstances.RemoveAt(entryInstances.Count - 1);
                if (last != null) Destroy(last);
            }

            int readyCount = 0;
            for (int i = 0; i < players.Count; i++)
            {
                var playerData = players[i];
                var entryObj = entryInstances[i];
                if (entryObj == null) continue;

                int pid = playerData.playerRef.PlayerId;
                bool playerIsHost = hostPlayerId >= 0 && pid == hostPlayerId;

                int encoded = -1;
                string customText = "";
                int slot = sync != null ? sync.FindSlot(pid) : -1;
                if (slot >= 0)
                {
                    encoded = sync.SelectedGenres[slot];
                    customText = sync.CustomGenreTexts[slot].ToString();
                }
                else if (sync == null && playerData.selectedGenreIndex >= 0)
                {
                    encoded = GenreEncoding.Encode(playerData.selectedGenreIndex, playerData.selectedSubGenreIndex);
                    customText = playerData.customGenreText;
                }

                string roleText;
                if (encoded >= 0)
                {
                    roleText = (GenreEncoding.IsCustomGenre(encoded) && !string.IsNullOrEmpty(customText))
                        ? customText
                        : GenreEncoding.GetDisplayName(encoded);
                    readyCount++;
                }
                else
                {
                    roleText = "選択中...";
                }

                string displayName = playerIsHost ? $"{playerData.playerName} [Host]" : playerData.playerName;
                entryObj.GetComponent<PlayerListEntry>()?.Setup(displayName, roleText);
            }

            int maxPlayers = (runner != null && runner.SessionInfo != null && runner.SessionInfo.IsValid && runner.SessionInfo.MaxPlayers > 0)
                ? runner.SessionInfo.MaxPlayers : LobbySync.MaxSlots;
            if (playerCountText != null)
                playerCountText.text = $"{session.PlayerCount} / {maxPlayers}";

            UpdateQuestionCountText();

            bool isHost = IsHost;
            startGameButton.gameObject.SetActive(isHost);

            bool allReady = readyCount >= session.PlayerCount && session.PlayerCount >= 1;
            startGameButton.interactable = allReady;

            if (allReady)
                SetStatus(isHost ? "全員準備完了! ゲームを開始できます" : "全員準備完了! ホストの開始を待っています");
            else if (session.PlayerCount >= 1)
                SetStatus($"ジャンル選択待ち ({readyCount}/{session.PlayerCount})");
            else
                SetStatus("プレイヤーを待っています...");
        }

        // ===== ゲーム開始 / 退出 =====

        private void OnStartGame()
        {
            if (!IsHost) return;

            SetStatus("クイズを準備中...");
            startGameButton.interactable = false;

            CommitLobbyDataToSession();
            NetworkManager.Instance.LoadScene("QuizScene");
        }

        /// <summary>
        /// シーン遷移前に LobbySync の内容を SessionManager に書き写す (QuizScene 側のフォールバック用)。
        /// </summary>
        private void CommitLobbyDataToSession()
        {
            var sync = LobbySync.Instance;
            var session = SessionManager.Instance;
            if (sync == null || session == null) return;

            for (int i = 0; i < LobbySync.MaxSlots; i++)
            {
                if (sync.IsSlotEmpty(i)) continue;
                int pid = sync.PlayerSlotIds[i];
                int encoded = sync.SelectedGenres[i];
                string name = sync.PlayerNames[i].ToString();

                if (!string.IsNullOrEmpty(name))
                    session.SetPlayerName(pid, name);

                if (encoded < 0) continue;
                var (genreIdx, subIdx) = GenreEncoding.Decode(encoded);
                session.SetPlayerGenreById(pid, genreIdx);
                session.SetPlayerSubGenreById(pid, subIdx);
                if (GenreEncoding.IsCustomGenre(encoded))
                    session.SetPlayerCustomGenreById(pid, sync.CustomGenreTexts[i].ToString());
            }
            Debug.Log("[LobbyUI] Committed lobby data to SessionManager before scene change");
        }

        private void OnLeave()
        {
            NetworkManager.Instance.LeaveSession();
            UnityEngine.SceneManagement.SceneManager.LoadScene("TitleScene");
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
                statusText.text = message;
        }
    }
}
