using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GemmaQuiz.Data;
using GemmaQuiz.Network;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// ロビー画面のUI制御。プレイヤーリスト、ジャンル選択、ゲーム開始ボタン。
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

        [Header("Question Count")]
        [SerializeField] private Text questionCountText;
        [SerializeField] private Button questionCountMinusButton;
        [SerializeField] private Button questionCountPlusButton;

        [Header("Buttons")]
        [SerializeField] private Button startGameButton;
        [SerializeField] private Button leaveButton;

        [Header("Status")]
        [SerializeField] private Text statusText;

        private readonly List<GameObject> entryInstances = new();
        private readonly Dictionary<QuizGenre, Image> genreButtonImages = new();
        private int myEncodedGenre = -1; // GenreEncoding 済みの値

        // サブジャンルポップアップ
        private GameObject subGenrePopup;
        private readonly List<GameObject> subGenreButtons = new();
        private int popupGenreIndex = -1;

        // カスタムジャンル入力
        [SerializeField] private InputField customGenreInput;

        private static readonly Color selectedColor = new Color(0.3f, 0.7f, 0.4f);
        private static readonly Color deselectedColor = new Color(0.2f, 0.2f, 0.3f);

        private void Start()
        {
            startGameButton.onClick.AddListener(OnStartGame);
            leaveButton.onClick.AddListener(OnLeave);

            if (questionCountMinusButton != null)
                questionCountMinusButton.onClick.AddListener(() => ChangeQuestionCount(-1));
            if (questionCountPlusButton != null)
                questionCountPlusButton.onClick.AddListener(() => ChangeQuestionCount(+1));

            CreateGenreButtons();
            if (customGenreInput != null)
                customGenreInput.onEndEdit.AddListener(OnCustomGenreSubmit);
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

            // ロビー入室時の事前生成は無効化（2026-04: Mercury-2 high reasoning で十分速いため）
            // var nm = NetworkManager.Instance;
            // if (nm != null && nm.IsHost) StartPreGeneration();
        }

        private void StartPreGeneration()
        {
            var gen = AI.QuizGenerator.Instance;
            if (gen == null) return;

            var allGenres = new[]
            {
                QuizGenre.AnimeGame, QuizGenre.Sports, QuizGenre.Entertainment,
                QuizGenre.Lifestyle, QuizGenre.Society, QuizGenre.Humanities,
                QuizGenre.Science, QuizGenre.NonGenre
            };
            for (int i = 0; i < 2; i++)
            {
                var g = allGenres[Random.Range(0, allGenres.Length)];
                gen.PreGenerateAsync(g, 0);
            }
            Debug.Log("[LobbyUI] Started pre-generating random round questions");
        }

        private void ChangeQuestionCount(int delta)
        {
            var gen = AI.QuizGenerator.Instance;
            if (gen == null) return;

            var nm = NetworkManager.Instance;
            if (nm != null && !nm.IsHost) return;

            gen.QuestionsPerGenre = gen.QuestionsPerGenre + delta;
            UpdateQuestionCountText();
        }

        private void UpdateQuestionCountText()
        {
            if (questionCountText == null) return;
            var gen = AI.QuizGenerator.Instance;
            int count = gen != null ? gen.QuestionsPerGenre : 5;
            questionCountText.text = $"問題数: {count}問 / ジャンル";
        }

        private void OnDestroy()
        {
            var session = SessionManager.Instance;
            if (session != null)
                session.OnPlayerListChanged -= RefreshPlayerList;
            LobbySync.OnSyncChanged -= RefreshPlayerList;
        }

        private float syncPollTimer;
        private void Update()
        {
            if (LobbySync.Instance == null) return;
            syncPollTimer += Time.deltaTime;
            if (syncPollTimer < 1f) return;
            syncPollTimer = 0f;
            RefreshPlayerList();
        }

        // ===== ジャンルボタン =====

        private void CreateGenreButtons()
        {
            if (genreButtonPrefab == null || genreButtonContainer == null) return;

            var genres = new[]
            {
                QuizGenre.AnimeGame, QuizGenre.Sports, QuizGenre.Entertainment,
                QuizGenre.Lifestyle, QuizGenre.Society, QuizGenre.Humanities,
                QuizGenre.Science, QuizGenre.NonGenre
            };

            foreach (var genre in genres)
            {
                var btnObj = Instantiate(genreButtonPrefab, genreButtonContainer);
                var btnText = btnObj.GetComponentInChildren<Text>();
                if (btnText != null)
                    btnText.text = genre.ToJapanese();

                var btnImage = btnObj.GetComponent<Image>();
                btnImage.color = deselectedColor;
                genreButtonImages[genre] = btnImage;

                // 既存のButtonコンポーネントのonClickを無効化（LongPressButtonで制御）
                var btn = btnObj.GetComponent<Button>();
                if (btn != null) btn.onClick.RemoveAllListeners();

                // LongPressButton を追加
                var longPress = btnObj.AddComponent<LongPressButton>();
                int index = (int)genre;

                longPress.OnClick += () => OnGenreSelected(index, 0);

                // NonGenre以外は長押しでサブジャンルポップアップ
                if (genre != QuizGenre.NonGenre)
                {
                    longPress.OnLongPress += () => ShowSubGenrePopup(index);
                }
            }
        }

        private void OnGenreSelected(int genreIndex, int subGenreIndex)
        {
            HideSubGenrePopup();

            int encoded = GenreEncoding.Encode(genreIndex, subGenreIndex);
            myEncodedGenre = encoded;

            // カスタム入力をクリア
            if (customGenreInput != null) customGenreInput.text = "";

            // ボタンの色更新
            foreach (var kvp in genreButtonImages)
            {
                kvp.Value.color = ((int)kvp.Key == genreIndex) ? selectedColor : deselectedColor;
            }

            string displayName = GenreEncoding.GetDisplayName(genreIndex, subGenreIndex);
            if (selectedGenreText != null)
                selectedGenreText.text = $"選択中: {displayName}";

            // ローカル更新
            var session = SessionManager.Instance;
            var nm = NetworkManager.Instance;
            var runner = nm?.Runner;
            if (session != null && runner != null)
            {
                session.SetPlayerGenre(runner.LocalPlayer, genreIndex);
                session.SetPlayerSubGenreById(runner.LocalPlayer.PlayerId, subGenreIndex);
            }

            // ネットワーク同期
            var sync = LobbySync.Instance;
            if (sync != null && nm != null)
            {
                sync.RpcSelectGenre(encoded, nm.LocalPlayerName ?? "");
            }

            RefreshPlayerList();
        }

        // ===== サブジャンルポップアップ =====

        private void ShowSubGenrePopup(int genreIndex)
        {
            var genre = (QuizGenre)genreIndex;
            var subs = genre.GetSubGenreList();
            if (subs == null || subs.Length == 0) return;

            HideSubGenrePopup();
            popupGenreIndex = genreIndex;

            var canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;

            // ポップアップパネル
            subGenrePopup = new GameObject("SubGenrePopup");
            subGenrePopup.transform.SetParent(canvas.transform, false);

            var popupRect = subGenrePopup.AddComponent<RectTransform>();
            popupRect.anchoredPosition = Vector2.zero;
            popupRect.sizeDelta = new Vector2(300, (subs.Length + 1) * 44 + 20);

            // 背景
            var popupImage = subGenrePopup.AddComponent<Image>();
            popupImage.color = new Color(0.1f, 0.1f, 0.2f, 0.95f);

            // レイアウト
            var layout = subGenrePopup.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 4;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = subGenrePopup.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 「すべて」ボタン
            AddSubGenreButton($"{genre.ToJapanese()} (すべて)", genreIndex, 0);

            // 各サブジャンル
            for (int i = 0; i < subs.Length; i++)
            {
                AddSubGenreButton(subs[i], genreIndex, i + 1);
            }
        }

        private void AddSubGenreButton(string label, int genreIndex, int subGenreIndex)
        {
            var btnObj = DefaultControls.CreateButton(new DefaultControls.Resources());
            btnObj.transform.SetParent(subGenrePopup.transform, false);

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredHeight = 40;

            var btnImage = btnObj.GetComponent<Image>();
            btnImage.color = new Color(0.2f, 0.2f, 0.35f, 1f);

            var text = btnObj.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = label;
                text.fontSize = 18;
                text.color = Color.white;
                text.font = JapaneseFont.Get();
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            var btn = btnObj.GetComponent<Button>();
            int gi = genreIndex, si = subGenreIndex;
            btn.onClick.AddListener(() => OnGenreSelected(gi, si));

            subGenreButtons.Add(btnObj);
        }

        private void HideSubGenrePopup()
        {
            if (subGenrePopup != null)
            {
                Destroy(subGenrePopup);
                subGenrePopup = null;
            }
            subGenreButtons.Clear();
            popupGenreIndex = -1;
        }

        // ===== カスタムジャンル入力 =====

        private void OnCustomGenreSubmit(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            HideSubGenrePopup();

            myEncodedGenre = GenreEncoding.CUSTOM_GENRE_CODE * 100;

            // ジャンルボタンの選択色をリセット
            foreach (var kvp in genreButtonImages)
                kvp.Value.color = deselectedColor;

            if (selectedGenreText != null)
                selectedGenreText.text = $"選択中: {text}";

            // ローカル更新
            var session = SessionManager.Instance;
            var nm = NetworkManager.Instance;
            var runner = nm?.Runner;
            if (session != null && runner != null)
            {
                session.SetPlayerGenre(runner.LocalPlayer, GenreEncoding.CUSTOM_GENRE_CODE);
                session.SetPlayerCustomGenreById(runner.LocalPlayer.PlayerId, text);
                // isReady はSetPlayerGenre で既にtrue
            }

            // ネットワーク同期
            var sync = LobbySync.Instance;
            if (sync != null && nm != null)
            {
                sync.RpcSelectCustomGenre(text, nm.LocalPlayerName ?? "");
            }

            RefreshPlayerList();
        }

        // ===== セッション情報 =====

        private void UpdateSessionInfo()
        {
            var nm = NetworkManager.Instance;
            if (nm == null || nm.Runner == null) return;

            var runner = nm.Runner;
            if (runner.SessionInfo != null)
            {
                sessionNameText.text = $"セッション: {runner.SessionInfo.Name}";
            }
        }

        // ===== プレイヤーリスト =====

        private void RefreshPlayerList()
        {
            var session = SessionManager.Instance;
            if (session == null) return;

            var players = session.GetSortedPlayerList();
            var nm = NetworkManager.Instance;
            var runner = nm != null ? nm.Runner : null;

            int readyCount = 0;

            var sync = LobbySync.Instance;

            int hostPlayerId = -1;
            if (sync != null) hostPlayerId = sync.HostPlayerId;
            if (hostPlayerId < 0 && runner != null && runner.IsServer)
                hostPlayerId = runner.LocalPlayer.PlayerId;

            while (entryInstances.Count < players.Count)
            {
                var newObj = Instantiate(playerListEntryPrefab, playerListContent);
                entryInstances.Add(newObj);
            }
            while (entryInstances.Count > players.Count)
            {
                var last = entryInstances[entryInstances.Count - 1];
                entryInstances.RemoveAt(entryInstances.Count - 1);
                if (last != null) Destroy(last);
            }

            for (int i = 0; i < players.Count; i++)
            {
                var playerData = players[i];
                var entryObj = entryInstances[i];
                if (entryObj == null) continue;
                var entry = entryObj.GetComponent<PlayerListEntry>();

                bool playerIsHost = (hostPlayerId >= 0 && playerData.playerRef.PlayerId == hostPlayerId);

                int encoded = -1;
                string syncedName = playerData.playerName;
                string customText = "";

                if (sync != null)
                {
                    int slot = -1;
                    for (int j = 0; j < 8; j++)
                    {
                        if (sync.PlayerSlotIds[j] == playerData.playerRef.PlayerId) { slot = j; break; }
                    }
                    if (slot >= 0)
                    {
                        encoded = sync.SelectedGenres[slot];
                        customText = sync.CustomGenreTexts[slot].ToString();
                        var name = sync.PlayerNames[slot].ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            syncedName = name;
                            session.SetPlayerName(playerData.playerRef.PlayerId, name);
                        }
                    }
                }
                else
                {
                    // フォールバック
                    if (playerData.selectedGenreIndex >= 0)
                        encoded = GenreEncoding.Encode(playerData.selectedGenreIndex, playerData.selectedSubGenreIndex);
                    customText = playerData.customGenreText;
                }

                string roleText;
                if (encoded >= 0)
                {
                    if (GenreEncoding.IsCustomGenre(encoded) && !string.IsNullOrEmpty(customText))
                        roleText = customText;
                    else
                        roleText = GenreEncoding.GetDisplayName(encoded);
                    readyCount++;
                }
                else
                {
                    roleText = "選択中...";
                }

                string displayName = playerIsHost ? $"{syncedName} [Host]" : syncedName;
                if (entry != null)
                    entry.Setup(displayName, playerIsHost, roleText);
            }

            int maxPlayers = (runner != null && runner.SessionInfo != null && runner.SessionInfo.IsValid && runner.SessionInfo.MaxPlayers > 0) ? runner.SessionInfo.MaxPlayers : 8;
            playerCountText.text = $"{session.PlayerCount} / {maxPlayers}";

            bool isHost = nm != null && nm.IsHost;
            startGameButton.gameObject.SetActive(isHost);

            bool allReady = readyCount >= session.PlayerCount && session.PlayerCount >= 1;
            startGameButton.interactable = allReady;

            if (allReady)
            {
                SetStatus("全員準備完了! ゲームを開始できます");
            }
            else if (session.PlayerCount >= 1)
            {
                SetStatus($"ジャンル選択待ち ({readyCount}/{session.PlayerCount})");
            }
            else
            {
                SetStatus("プレイヤーを待っています...");
            }
        }

        // ===== ゲーム開始 =====

        private void OnStartGame()
        {
            if (!NetworkManager.Instance.IsHost) return;

            SetStatus("クイズを準備中...");
            startGameButton.interactable = false;

            CommitLobbyDataToSession();

            NetworkManager.Instance.LoadScene("QuizScene");
        }

        private void CommitLobbyDataToSession()
        {
            var sync = LobbySync.Instance;
            var session = SessionManager.Instance;
            if (sync == null || session == null) return;

            for (int i = 0; i < 8; i++)
            {
                int pid = sync.PlayerSlotIds[i];
                if (pid < 0) continue;
                int encoded = sync.SelectedGenres[i];
                string name = sync.PlayerNames[i].ToString();

                if (!string.IsNullOrEmpty(name))
                    session.SetPlayerName(pid, name);

                if (encoded >= 0)
                {
                    var (genreIdx, subIdx) = GenreEncoding.Decode(encoded);
                    session.SetPlayerGenreById(pid, genreIdx);
                    session.SetPlayerSubGenreById(pid, subIdx);

                    if (GenreEncoding.IsCustomGenre(encoded))
                    {
                        string customText = sync.CustomGenreTexts[i].ToString();
                        session.SetPlayerCustomGenreById(pid, customText);
                    }
                }
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
