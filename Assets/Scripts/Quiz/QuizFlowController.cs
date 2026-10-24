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
    /// プレイヤーの選択ジャンルでラウンド順を構築し、各ラウンドで 問題生成 → 出題 → 次のラウンド と進行する。
    /// ホストが進行し、クライアントは QuizManager の [Networked] 状態 (IsLoadingRound 等) を見て表示を切り替える。
    /// </summary>
    public class QuizFlowController : MonoBehaviour
    {
        [Header("Loading UI")]
        [SerializeField] private GameObject loadingPanel;
        [SerializeField] private Text loadingText;
        [SerializeField] private Image loadingBar;

        [Header("Timing")]
        [SerializeField] private float roundStartDelay = 0.8f;
        [SerializeField] private float roundEndDelay = 3f;
        [SerializeField] private float failedRoundSkipDelay = 2.5f;

        private readonly List<RoundInfo> rounds = new();
        private int currentRoundIndex = -1;
        private QuizUI quizUI;
        private bool lastObservedLoadingState = true;

        // 擬似プログレスバー用（API呼び出し中は中間進捗が取れないので時間ベースで進める）
        private float loadingStartTime;
        private const float ExpectedLoadSeconds = 12f;
        private float loadingRealProgress;

        private System.Collections.IEnumerator Start()
        {
            quizUI = FindAnyObjectByType<QuizUI>();
            if (quizUI != null) quizUI.SetContentVisible(false);
            ApplyLoadingState(true);

            var nm = NetworkManager.Instance;
            if (nm == null || nm.Runner == null) yield break;

            // QuizManager (シーン上 NetworkObject) の Spawned 完了を待つ。
            // 待たないと StartNextRound での [Networked] 書き込みが取りこぼされ、初回ラウンドの情報が空のままクライアントに伝わる
            float waited = 0f;
            while (QuizManager.Instance == null && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (QuizManager.Instance == null)
            {
                Debug.LogError("[QuizFlowController] QuizManager.Instance did not appear within timeout");
                yield break;
            }

            if (!nm.IsHost) yield break;

            while (!QuizManager.Instance.HasStateAuthority && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            BuildRoundOrder();
            StartNextRound();
        }

        private void Update()
        {
            // ネットワーク状態 (IsLoadingRound) と表示を同期する
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
            // 経過時間からの推定(0→0.9) と 実進捗値 のうち大きい方を採用
            float elapsed = Mathf.Max(0f, Time.time - loadingStartTime);
            float estimated = Mathf.Min(0.9f, elapsed / ExpectedLoadSeconds * 0.9f);
            float target = Mathf.Max(estimated, loadingRealProgress);
            loadingBar.fillAmount = Mathf.MoveTowards(loadingBar.fillAmount, target, Time.deltaTime * 0.6f);
        }

        private void ApplyLoadingState(bool isLoading)
        {
            if (isLoading)
            {
                if (quizUI != null) quizUI.SetContentVisible(false);
                if (loadingPanel != null) loadingPanel.SetActive(true);
                loadingStartTime = Time.time;
                loadingRealProgress = 0f;
                if (loadingBar != null) loadingBar.fillAmount = 0f;
                UpdateLoadingPanelText();
            }
            else
            {
                if (loadingPanel != null) loadingPanel.SetActive(false);
                if (quizUI != null) quizUI.SetContentVisible(true);
            }
        }

        /// <summary>
        /// ローディング画面のテキスト。ラウンド番号・ジャンル・誰の選択かを [Networked] 値から組み立てる。
        /// </summary>
        private void UpdateLoadingPanelText()
        {
            var qm = QuizManager.Instance;
            if (qm == null || loadingText == null) return;

            string genreName = qm.LoadingRoundGenreName.ToString();
            if (string.IsNullOrEmpty(genreName))
            {
                loadingText.text = "問題を準備中...";
                return;
            }

            var sb = new System.Text.StringBuilder();
            if (qm.TotalRounds > 0)
                sb.Append($"ラウンド {qm.CurrentRoundIndex + 1} / {qm.TotalRounds}\n");
            sb.Append($"{genreName} の問題を生成中...");
            string owner = qm.CurrentRoundOwnerName.ToString();
            if (!string.IsNullOrEmpty(owner))
                sb.Append($"\n<size=60%>({owner} の選択)</size>");
            loadingText.text = sb.ToString();
        }

        /// <summary>
        /// プレイヤーの選択ジャンルからラウンド順を構築する (ホストのみ)。
        /// </summary>
        private void BuildRoundOrder()
        {
            rounds.Clear();

            var sync = LobbySync.Instance;
            if (sync != null)
            {
                for (int i = 0; i < LobbySync.MaxSlots; i++)
                {
                    if (sync.IsSlotEmpty(i)) continue;
                    int encoded = sync.SelectedGenres[i];
                    if (encoded < 0) continue;

                    var (genreIdx, subIdx) = GenreEncoding.Decode(encoded);
                    string playerName = sync.PlayerNames[i].ToString();
                    if (string.IsNullOrEmpty(playerName)) playerName = $"Player {i}";

                    bool isCustom = GenreEncoding.IsCustomGenre(encoded);
                    rounds.Add(new RoundInfo
                    {
                        genre = isCustom ? QuizGenre.NonGenre : (QuizGenre)genreIdx,
                        subGenreIndex = subIdx,
                        customGenreText = isCustom ? sync.CustomGenreTexts[i].ToString() : "",
                        ownerName = playerName
                    });
                }
            }
            else
            {
                // フォールバック: SessionManagerのローカルデータから
                var session = SessionManager.Instance;
                if (session != null)
                {
                    foreach (var player in session.GetSortedPlayerList())
                    {
                        if (player.selectedGenreIndex < 0) continue;
                        bool isCustom = player.selectedGenreIndex == GenreEncoding.CUSTOM_GENRE_CODE;
                        rounds.Add(new RoundInfo
                        {
                            genre = isCustom ? QuizGenre.NonGenre : (QuizGenre)player.selectedGenreIndex,
                            subGenreIndex = player.selectedSubGenreIndex,
                            customGenreText = isCustom ? player.customGenreText : "",
                            ownerName = player.playerName
                        });
                    }
                }
            }

            Debug.Log($"[QuizFlowController] Round order ({rounds.Count} rounds):");
            for (int i = 0; i < rounds.Count; i++)
                Debug.Log($"  Round {i + 1}: {rounds[i].DisplayName} ({rounds[i].ownerName})");

            var qm = QuizManager.Instance;
            if (qm != null && qm.HasStateAuthority)
                qm.TotalRounds = rounds.Count;
        }

        private void StartNextRound()
        {
            currentRoundIndex++;

            if (currentRoundIndex >= rounds.Count)
            {
                // 全ラウンド終了 (通常は HandleRoundFinished から LoadResultScene に行くのでここには来ない)
                LoadResultScene();
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
                qm.LoadingRoundGenreName = round.DisplayName;
                qm.CurrentRoundOwnerName = round.ownerName;
                qm.IsLoadingRound = true;
            }

            // ホスト自身も即座にローディング画面へ
            lastObservedLoadingState = true;
            ApplyLoadingState(true);

            StartGeneration(round);
        }

        private void StartGeneration(RoundInfo round)
        {
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

            Invoke(nameof(StartQuizRound), roundStartDelay);
        }

        private void HandleGenerationFailed(string error)
        {
            UnsubscribeGenerator();
            Debug.LogWarning($"[QuizFlowController] Generation failed for round {currentRoundIndex + 1}: {error}");

            bool hasMoreRounds = currentRoundIndex + 1 < rounds.Count;
            SetLoadingText(hasMoreRounds
                ? "問題生成に失敗しました。次のラウンドへスキップします..."
                : "問題生成に失敗しました。結果画面へ移動します...");

            CancelInvoke(nameof(StartNextRound));
            CancelInvoke(nameof(LoadResultScene));
            Invoke(hasMoreRounds ? nameof(StartNextRound) : nameof(LoadResultScene), failedRoundSkipDelay);
        }

        private void StartQuizRound()
        {
            var quizManager = QuizManager.Instance;
            var generator = QuizGenerator.Instance;

            if (quizManager == null || generator == null || generator.LastGeneratedQuestions == null)
            {
                Debug.LogError("[QuizFlowController] QuizManager or questions not available");
                return;
            }

            quizManager.OnQuizFinished -= HandleRoundFinished;
            quizManager.OnQuizFinished += HandleRoundFinished;

            // ロード完了をクライアントへ通知 (これでクライアント側もUIが切り替わる)
            if (quizManager.HasStateAuthority)
                quizManager.IsLoadingRound = false;

            lastObservedLoadingState = false;
            ApplyLoadingState(false);
            quizManager.StartQuiz(generator.LastGeneratedQuestions);
        }

        private void HandleRoundFinished(List<PlayerScoreEntry> scores)
        {
            var quizManager = QuizManager.Instance;
            if (quizManager != null)
                quizManager.OnQuizFinished -= HandleRoundFinished;

            Invoke(currentRoundIndex + 1 < rounds.Count ? nameof(StartNextRound) : nameof(LoadResultScene), roundEndDelay);
        }

        private void LoadResultScene()
        {
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHost)
                nm.LoadScene("ResultScene");
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
                quizManager.OnQuizFinished -= HandleRoundFinished;
        }

        private struct RoundInfo
        {
            public QuizGenre genre;
            public int subGenreIndex;
            public string customGenreText;
            public string ownerName;

            public string DisplayName => !string.IsNullOrEmpty(customGenreText)
                ? customGenreText
                : GenreEncoding.GetDisplayName((int)genre, subGenreIndex);
        }
    }
}
