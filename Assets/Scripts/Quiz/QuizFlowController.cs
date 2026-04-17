using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GemmaQuiz.AI;
using GemmaQuiz.Data;
using GemmaQuiz.Network;
using GemmaQuiz.UI;

namespace GemmaQuiz.Quiz
{
    /// <summary>
    /// QuizScene上でゲームフローを制御する。
    /// プレイヤーの選択ジャンル + ランダムジャンルでラウンド順を構築し、
    /// 各ラウンドで問題生成 → 出題 → 次のラウンドへ進行する。
    /// </summary>
    public class QuizFlowController : MonoBehaviour
    {
        [Header("Loading UI")]
        [SerializeField] private GameObject loadingPanel;
        [SerializeField] private Text loadingText;
        [SerializeField] private Image loadingBar;

        [Header("Round Info")]
        [SerializeField] private GameObject roundInfoPanel;
        [SerializeField] private Text roundInfoText;

        [Header("Settings")]
        [SerializeField] private int randomRoundsToAdd = 1;

        private List<RoundInfo> rounds = new();
        private int currentRoundIndex = -1;
        private QuizUI quizUI;
        private bool subscribedToQuizManager;
        private bool lastObservedLoadingState = true;

        // 擬似プログレスバー用（API同期呼び出しで中間進捗が取れないので時間ベース）
        private float loadingStartTime;
        private const float ExpectedLoadSeconds = 12f;
        private float loadingRealProgress; // OnGenerationProgress から届いた実値

        private System.Collections.IEnumerator Start()
        {
            if (loadingPanel != null) loadingPanel.SetActive(false);
            if (roundInfoPanel != null) roundInfoPanel.SetActive(false);

            quizUI = FindAnyObjectByType<QuizUI>();
            if (quizUI != null) quizUI.SetContentVisible(false);

            ShowLoading("問題を準備中...");

            var nm = NetworkManager.Instance;
            if (nm == null || nm.Runner == null) yield break;

            // QuizManager (シーン上 NetworkObject) の Spawned 完了を待つ
            // 待たないと StartNextRound での [Networked] フィールド書き込みが取りこぼされ、
            // 初回ラウンドだけジャンル名等が空のままクライアントに伝わる
            float waitTimeout = 0f;
            while (QuizManager.Instance == null && waitTimeout < 5f)
            {
                waitTimeout += Time.deltaTime;
                yield return null;
            }
            if (QuizManager.Instance == null)
            {
                Debug.LogError("[QuizFlowController] QuizManager.Instance did not appear within timeout");
                yield break;
            }
            // ホスト側は StateAuthority が確立されるまで待つ
            if (nm.IsHost)
            {
                while (!QuizManager.Instance.HasStateAuthority && waitTimeout < 5f)
                {
                    waitTimeout += Time.deltaTime;
                    yield return null;
                }
            }

            if (nm.IsHost)
            {
                BuildRoundOrder();
                StartNextRound();
            }
        }

        private void Update()
        {
            TrySubscribeToQuizManager();

            // 万一イベントを取り逃した時のための同期: ネットワーク状態と表示が乖離していたら直す
            var qm = QuizManager.Instance;
            if (qm == null) return;
            bool loading = qm.IsLoadingRound;
            if (loading != lastObservedLoadingState)
            {
                lastObservedLoadingState = loading;
                ApplyLoadingState(loading);
            }
            if (loading)
            {
                UpdateLoadingPanelText();
                UpdateLoadingBar();
            }
        }

        private void UpdateLoadingBar()
        {
            if (loadingBar == null) return;
            // 経過時間から推定(0→0.9 over ExpectedLoadSeconds) と 実進捗値 のうち大きい方を採用
            float elapsed = Mathf.Max(0f, Time.time - loadingStartTime);
            float estimated = Mathf.Min(0.9f, elapsed / ExpectedLoadSeconds * 0.9f);
            float target = Mathf.Max(estimated, loadingRealProgress);
            loadingBar.fillAmount = Mathf.MoveTowards(loadingBar.fillAmount, target, Time.deltaTime * 0.6f);
        }

        private void TrySubscribeToQuizManager()
        {
            if (subscribedToQuizManager) return;
            var qm = QuizManager.Instance;
            if (qm == null) return;
            qm.OnRoundLoadingChanged += HandleRoundLoadingChanged;
            subscribedToQuizManager = true;
            // 初回適用
            lastObservedLoadingState = qm.IsLoadingRound;
            ApplyLoadingState(qm.IsLoadingRound);
        }

        private void HandleRoundLoadingChanged(bool isLoading)
        {
            lastObservedLoadingState = isLoading;
            ApplyLoadingState(isLoading);
        }

        private void ApplyLoadingState(bool isLoading)
        {
            if (isLoading)
            {
                if (quizUI != null) quizUI.SetContentVisible(false);
                if (roundInfoPanel != null) roundInfoPanel.SetActive(false);
                if (loadingPanel != null) loadingPanel.SetActive(true);
                loadingStartTime = Time.time;
                loadingRealProgress = 0f;
                if (loadingBar != null) loadingBar.fillAmount = 0f;
                UpdateLoadingPanelText();
            }
            else
            {
                if (loadingPanel != null) loadingPanel.SetActive(false);
                if (roundInfoPanel != null) roundInfoPanel.SetActive(false);
                if (quizUI != null) quizUI.SetContentVisible(true);
            }
        }

        private void UpdateLoadingPanelText()
        {
            var qm = QuizManager.Instance;
            if (qm == null || loadingText == null) return;
            string genreName = qm.LoadingRoundGenreName.ToString();

            string newText = string.IsNullOrEmpty(genreName)
                ? "問題を準備中..."
                : $"{genreName} の問題を生成中...";

            loadingText.text = newText;
        }

        /// <summary>
        /// プレイヤーの選択ジャンルからラウンド順を構築し、ランダムジャンルを混ぜる。
        /// </summary>
        private void BuildRoundOrder()
        {
            rounds.Clear();

            // 1. ランダムラウンドを先頭に追加（事前生成済みキャッシュを活用）
            //    ロビーで事前生成したジャンルを優先使用
            var preGenList = AI.QuizGenerator.Instance != null ? AI.QuizGenerator.Instance.GetPreGeneratedGenres() : null;
            if (preGenList != null && preGenList.Count > 0)
            {
                foreach (var encoded in preGenList)
                {
                    var (gIdx, sIdx) = GenreEncoding.Decode(encoded);
                    if (gIdx < 0 || gIdx >= System.Enum.GetValues(typeof(QuizGenre)).Length) continue;
                    rounds.Add(new RoundInfo
                    {
                        genre = (QuizGenre)gIdx,
                        subGenreIndex = sIdx,
                        ownerName = "ランダム",
                        isRandom = true
                    });
                    if (rounds.Count >= randomRoundsToAdd) break;
                }
            }

            // 不足分はその場でランダム選出
            var allGenres = new[]
            {
                QuizGenre.AnimeGame, QuizGenre.Sports, QuizGenre.Entertainment,
                QuizGenre.Lifestyle, QuizGenre.Society, QuizGenre.Humanities,
                QuizGenre.Science
            };
            while (rounds.Count < randomRoundsToAdd)
            {
                rounds.Add(new RoundInfo
                {
                    genre = allGenres[Random.Range(0, allGenres.Length)],
                    ownerName = "ランダム",
                    isRandom = true
                });
            }

            // 2. プレイヤー選択ジャンルを後に追加（LobbySync同期データを使用）
            var sync = Network.LobbySync.Instance;
            if (sync != null)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (sync.PlayerSlotIds[i] == -1) continue;
                    int encoded = sync.SelectedGenres[i];
                    if (encoded < 0) continue;

                    var (genreIdx, subIdx) = GenreEncoding.Decode(encoded);
                    string playerName = sync.PlayerNames[i].ToString();
                    if (string.IsNullOrEmpty(playerName)) playerName = $"Player {i}";

                    string customText = "";
                    if (GenreEncoding.IsCustomGenre(encoded))
                        customText = sync.CustomGenreTexts[i].ToString();

                    rounds.Add(new RoundInfo
                    {
                        genre = GenreEncoding.IsCustomGenre(encoded) ? QuizGenre.NonGenre : (QuizGenre)genreIdx,
                        subGenreIndex = subIdx,
                        customGenreText = customText,
                        ownerName = playerName,
                        isRandom = false
                    });
                }
            }
            else
            {
                // フォールバック: SessionManagerのローカルデータから
                var session = Network.SessionManager.Instance;
                if (session != null)
                {
                    foreach (var player in session.GetSortedPlayerList())
                    {
                        if (player.selectedGenreIndex < 0) continue;
                        rounds.Add(new RoundInfo
                        {
                            genre = (QuizGenre)player.selectedGenreIndex,
                            subGenreIndex = player.selectedSubGenreIndex,
                            customGenreText = player.customGenreText,
                            ownerName = player.playerName,
                            isRandom = false
                        });
                    }
                }
            }

            Debug.Log($"[QuizFlowController] Round order ({rounds.Count} rounds):");
            for (int i = 0; i < rounds.Count; i++)
            {
                var r = rounds[i];
                var rName = !string.IsNullOrEmpty(r.customGenreText) ? r.customGenreText : GenreEncoding.GetDisplayName((int)r.genre, r.subGenreIndex);
                Debug.Log($"  Round {i + 1}: {rName} ({r.ownerName})");
            }

            // ネットワーク状態に総ラウンド数を書き込む
            var qm = QuizManager.Instance;
            if (qm != null && qm.HasStateAuthority)
                qm.TotalRounds = rounds.Count;
        }

        private void StartNextRound()
        {
            currentRoundIndex++;

            if (currentRoundIndex >= rounds.Count)
            {
                // 全ラウンド終了 → QuizManagerが最終結果を処理
                return;
            }

            var round = rounds[currentRoundIndex];

            // ラウンド(=ジャンル)切り替え時にBGMをランダムに差し替え
            Audio.AudioManager.Instance?.PlayRandomQuizBgm();

            // ネットワーク状態に書き込み (クライアントにも伝わる)
            var qm = QuizManager.Instance;
            if (qm != null && qm.HasStateAuthority)
            {
                qm.CurrentRoundIndex = currentRoundIndex;
                if (!string.IsNullOrEmpty(round.customGenreText))
                    qm.LoadingRoundGenreName = round.customGenreText;
                else
                    qm.LoadingRoundGenreName = GenreEncoding.GetDisplayName((int)round.genre, round.subGenreIndex);
                qm.CurrentRoundOwnerName = round.isRandom ? "ランダム" : round.ownerName;
                qm.IsLoadingRound = true;
            }

            // 自前でも即座にローディング画面に切り替え
            ApplyLoadingState(true);

            // 問題生成を即開始
            StartGeneration();
        }

        private void StartGeneration()
        {
            var round = rounds[currentRoundIndex];
            UpdateLoadingPanelText();

            var generator = QuizGenerator.Instance;
            if (generator == null)
            {
                SetLoadingText("エラー: QuizGeneratorが見つかりません");
                return;
            }

            generator.OnGenerationProgress += HandleProgress;
            generator.OnGenerationComplete += HandleGenerationComplete;
            generator.OnGenerationFailed += HandleGenerationFailed;

            if (!string.IsNullOrEmpty(round.customGenreText))
                generator.GenerateQuestions(round.customGenreText);
            else
                generator.GenerateQuestions(round.genre, round.subGenreIndex);
        }

        private void HandleProgress(float progress)
        {
            // 実進捗は UpdateLoadingBar で使う（経過時間ベースと合わせて大きい方を採用）
            loadingRealProgress = progress;
        }

        private void HandleGenerationComplete(QuizQuestionSet questionSet)
        {
            UnsubscribeGenerator();
            loadingRealProgress = 1f;
            if (loadingBar != null) loadingBar.fillAmount = 1f;

            // 次のラウンドのジャンルを事前生成開始
            if (currentRoundIndex + 1 < rounds.Count)
            {
                var next = rounds[currentRoundIndex + 1];
                if (string.IsNullOrEmpty(next.customGenreText))
                    QuizGenerator.Instance?.PreGenerateAsync(next.genre, next.subGenreIndex);
            }

            Invoke(nameof(StartQuizRound), 0.8f);
        }

        private void HandleGenerationFailed(string error)
        {
            UnsubscribeGenerator();
            SetLoadingText($"エラー: {error}");
        }

        private void StartQuizRound()
        {
            var quizManager = QuizManager.Instance;
            var generator = QuizGenerator.Instance;

            if (quizManager != null && generator != null && generator.LastGeneratedQuestions != null)
            {
                quizManager.OnQuizFinished -= HandleRoundFinished;
                quizManager.OnQuizFinished += HandleRoundFinished;

                // ロード完了をクライアントへ通知 (これでクライアント側もUI切り替わる)
                if (quizManager.HasStateAuthority)
                    quizManager.IsLoadingRound = false;

                ApplyLoadingState(false);
                quizManager.StartQuiz(generator.LastGeneratedQuestions);
            }
            else
            {
                Debug.LogError("[QuizFlowController] QuizManager or questions not available");
            }
        }

        private void HandleRoundFinished(List<PlayerScoreEntry> scores)
        {
            var quizManager = QuizManager.Instance;
            if (quizManager != null)
                quizManager.OnQuizFinished -= HandleRoundFinished;

            if (currentRoundIndex + 1 < rounds.Count)
            {
                Invoke(nameof(StartNextRound), 3f);
            }
            else
            {
                // 全ラウンド終了 - ResultSceneへ遷移
                Invoke(nameof(LoadResultScene), 3f);
            }
        }

        private void LoadResultScene()
        {
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHost)
                nm.LoadScene("ResultScene");
            else
                UnityEngine.SceneManagement.SceneManager.LoadScene("ResultScene");
        }

        private void ShowLoading(string text)
        {
            if (loadingPanel != null)
                loadingPanel.SetActive(true);
            SetLoadingText(text);
        }

        private void SetLoadingText(string text)
        {
            if (loadingText != null)
                loadingText.text = text;
        }

        private void UnsubscribeGenerator()
        {
            var generator = QuizGenerator.Instance;
            if (generator == null) return;

            generator.OnGenerationProgress -= HandleProgress;
            generator.OnGenerationComplete -= HandleGenerationComplete;
            generator.OnGenerationFailed -= HandleGenerationFailed;
        }

        private void OnDestroy()
        {
            UnsubscribeGenerator();
            var quizManager = QuizManager.Instance;
            if (quizManager != null)
            {
                quizManager.OnQuizFinished -= HandleRoundFinished;
                quizManager.OnRoundLoadingChanged -= HandleRoundLoadingChanged;
            }
        }

        private struct RoundInfo
        {
            public QuizGenre genre;
            public int subGenreIndex;
            public string customGenreText;
            public string ownerName;
            public bool isRandom;
        }
    }
}
