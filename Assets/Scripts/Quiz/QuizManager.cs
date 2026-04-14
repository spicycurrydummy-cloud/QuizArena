using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using GemmaQuiz.Data;
using GemmaQuiz.Network;

namespace GemmaQuiz.Quiz
{
    /// <summary>
    /// クイズのゲームループを管理する。
    /// シーン配置のNetworkObject。ホストが進行管理、クライアントは[Networked]プロパティで状態同期。
    /// </summary>
    public class QuizManager : NetworkBehaviour
    {
        public static QuizManager Instance { get; private set; }

        // [Networked] 同期データ
        [Networked] public NetworkString<_256> CurrentQuestionText { get; set; }
        [Networked, Capacity(4)] public NetworkArray<NetworkString<_64>> CurrentChoices { get; }
        [Networked] public int CurrentQuestionIndex { get; set; }
        [Networked] public int TotalQuestions { get; set; }
        [Networked] public int RevealedCorrectIndex { get; set; } // -1 = まだ非表示
        [Networked] public NetworkBool IsAcceptingAnswers { get; set; }
        [Networked] public int CurrentQuestionDifficulty { get; set; } // 0=Easy, 1=Normal, 2=Hard
        [Networked] public TickTimer QuestionTimer { get; set; }
        [Networked] public NetworkBool ShowingResult { get; set; }
        [Networked] public NetworkBool QuizFinished { get; set; }

        // 解答順 (PlayerId, choiceIndex, elapsedTimeMs)
        [Networked, Capacity(8)] public NetworkArray<int> AnswerOrderPlayerIds { get; }
        [Networked, Capacity(8)] public NetworkArray<int> AnswerChoices { get; }
        [Networked, Capacity(8)] public NetworkArray<int> AnswerTimesMs { get; }
        [Networked] public int AnswerCount { get; set; }

        // 累計スコア (PlayerId → score)
        [Networked, Capacity(8)] public NetworkArray<int> ScorePlayerIds { get; }
        [Networked, Capacity(8)] public NetworkArray<int> PlayerTotalScores { get; }

        // ラウンド進行 (QuizFlowControllerが書き込む)
        [Networked] public int CurrentRoundIndex { get; set; }
        [Networked] public int TotalRounds { get; set; }
        [Networked] public NetworkBool IsLoadingRound { get; set; }
        [Networked] public NetworkString<_32> LoadingRoundGenreName { get; set; }
        [Networked] public NetworkString<_32> CurrentRoundOwnerName { get; set; }

        // ホスト側のローカル状態
        private QuizQuestionSet currentQuestionSet;
        private float questionStartTime;
        private bool localShowingResultFlag;
        private int localAnswerCountLast;
        private int lastQuestionIndexShown = -2;

        public float RemainingTime => QuestionTimer.IsRunning ? QuestionTimer.RemainingTime(Runner) ?? 0f : 0f;

        // events (UI用)
        public event Action<QuizQuestion, int> OnQuestionStarted;
        public event Action OnTimerExpired;
        public event Action<QuestionResult> OnQuestionResult;
        public event Action<List<PlayerScoreEntry>> OnQuizFinished;
        public event Action<Fusion.PlayerRef, string, int> OnPlayerAnswered;
        public event Action<bool> OnRoundLoadingChanged; // true=loading, false=ready

        private ChangeDetector changeDetector;

        public QuizQuestion CurrentQuestion
        {
            get
            {
                // ホスト: ローカルセットから取得
                if (currentQuestionSet != null && CurrentQuestionIndex >= 0 && CurrentQuestionIndex < currentQuestionSet.questions.Count)
                    return currentQuestionSet.questions[CurrentQuestionIndex];
                // クライアント: [Networked]データから構築
                return new QuizQuestion
                {
                    id = CurrentQuestionIndex + 1,
                    question = CurrentQuestionText.ToString(),
                    choices = new[] { CurrentChoices[0].ToString(), CurrentChoices[1].ToString(), CurrentChoices[2].ToString(), CurrentChoices[3].ToString() },
                    correct_index = RevealedCorrectIndex
                };
            }
        }

        public override void Spawned()
        {
            Instance = this;
            changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);

            if (HasStateAuthority)
            {
                CurrentQuestionIndex = -1;
                TotalQuestions = 0;
                RevealedCorrectIndex = -1;
                IsAcceptingAnswers = false;
                ShowingResult = false;
                QuizFinished = false;
                AnswerCount = 0;
                CurrentRoundIndex = 0;
                TotalRounds = 0;
                IsLoadingRound = true; // 初期はロード中（最初の生成完了で false）
                for (int i = 0; i < 8; i++)
                {
                    ScorePlayerIds.Set(i, -1);
                    PlayerTotalScores.Set(i, 0);
                }
            }

            Debug.Log($"[QuizManager] Spawned (HasStateAuthority={HasStateAuthority})");
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        public override void Render()
        {
            if (changeDetector == null) return;
            foreach (var change in changeDetector.DetectChanges(this))
            {
                switch (change)
                {
                    case nameof(CurrentQuestionIndex):
                        if (CurrentQuestionIndex != lastQuestionIndexShown && CurrentQuestionIndex >= 0 && CurrentQuestionIndex < TotalQuestions)
                        {
                            lastQuestionIndexShown = CurrentQuestionIndex;
                            localAnswerCountLast = 0; // 解答カウントをリセット
                            OnQuestionStarted?.Invoke(CurrentQuestion, CurrentQuestionIndex);
                        }
                        break;
                    case nameof(ShowingResult):
                        if (ShowingResult && !localShowingResultFlag)
                        {
                            localShowingResultFlag = true;
                            OnTimerExpired?.Invoke();
                            BuildAndFireQuestionResult();
                        }
                        else if (!ShowingResult && localShowingResultFlag)
                        {
                            localShowingResultFlag = false;
                        }
                        break;
                    case nameof(AnswerCount):
                        if (AnswerCount > localAnswerCountLast)
                        {
                            for (int i = localAnswerCountLast; i < AnswerCount; i++)
                            {
                                int pid = AnswerOrderPlayerIds[i];
                                string name = "?";
                                Fusion.PlayerRef playerRef = default;
                                var session = SessionManager.Instance;
                                if (session != null)
                                {
                                    foreach (var kvp in session.Players)
                                    {
                                        if (kvp.Key.PlayerId == pid)
                                        {
                                            name = kvp.Value.playerName;
                                            playerRef = kvp.Key;
                                            break;
                                        }
                                    }
                                }
                                OnPlayerAnswered?.Invoke(playerRef, name, i + 1);
                            }
                            localAnswerCountLast = AnswerCount;
                        }
                        break;
                    case nameof(QuizFinished):
                        if (QuizFinished)
                        {
                            BuildAndFireQuizFinished();
                        }
                        break;
                    case nameof(IsLoadingRound):
                        OnRoundLoadingChanged?.Invoke(IsLoadingRound);
                        break;
                }
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;
            if (!IsAcceptingAnswers) return;

            if (QuestionTimer.Expired(Runner))
            {
                IsAcceptingAnswers = false;
                ProcessQuestionResult();
            }
        }

        /// <summary>
        /// ホスト: 問題セットを設定してクイズを開始する。
        /// </summary>
        public void StartQuiz(QuizQuestionSet questionSet)
        {
            if (!HasStateAuthority) return;
            currentQuestionSet = questionSet;
            CurrentQuestionIndex = -1;
            TotalQuestions = questionSet.questions.Count;
            QuizFinished = false;
            Debug.Log($"[QuizManager] Starting quiz with {questionSet.questions.Count} questions");
            NextQuestion();
        }

        public void NextQuestion()
        {
            if (!HasStateAuthority) return;

            // 次の問題があるかを先にチェック（インクリメント前に判定して、
            // CurrentQuestionIndexを範囲外にしない）
            int nextIndex = CurrentQuestionIndex + 1;
            if (nextIndex >= TotalQuestions)
            {
                FinishQuiz();
                return;
            }

            CurrentQuestionIndex = nextIndex;
            // クリア
            for (int i = 0; i < 8; i++)
            {
                AnswerOrderPlayerIds.Set(i, -1);
                AnswerChoices.Set(i, -1);
                AnswerTimesMs.Set(i, 0);
            }
            AnswerCount = 0;
            ShowingResult = false;
            RevealedCorrectIndex = -1;

            var q = currentQuestionSet.questions[CurrentQuestionIndex];

            // [Networked] に書き込み（クライアントに伝わる）
            CurrentQuestionText = q.question;
            CurrentQuestionDifficulty = q.difficulty;
            for (int i = 0; i < 4; i++)
                CurrentChoices.Set(i, q.choices != null && i < q.choices.Length ? q.choices[i] : "");

            QuestionTimer = TickTimer.CreateFromSeconds(Runner, ScoreCalculator.TimeLimit);
            IsAcceptingAnswers = true;
            questionStartTime = Time.time;

            Debug.Log($"[QuizManager] Question {CurrentQuestionIndex + 1}: {q.question}");
        }

        /// <summary>
        /// クライアント: 回答を送信。
        /// </summary>
        public void SubmitAnswer(int choiceIndex)
        {
            if (!IsAcceptingAnswers) return;
            float elapsed = ScoreCalculator.TimeLimit - RemainingTime;
            RpcSubmitAnswer(choiceIndex, Mathf.RoundToInt(elapsed * 1000));
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RpcSubmitAnswer(int choiceIndex, int elapsedMs, RpcInfo info = default)
        {
            if (!IsAcceptingAnswers) return;
            int playerId = info.Source.IsRealPlayer ? info.Source.PlayerId : Runner.LocalPlayer.PlayerId;

            // 重複チェック
            for (int i = 0; i < AnswerCount; i++)
                if (AnswerOrderPlayerIds[i] == playerId) return;

            int slot = AnswerCount;
            if (slot >= 8) return;

            AnswerOrderPlayerIds.Set(slot, playerId);
            AnswerChoices.Set(slot, choiceIndex);
            AnswerTimesMs.Set(slot, elapsedMs);
            AnswerCount = slot + 1;

            Debug.Log($"[QuizManager] Player(id={playerId}) answered {choiceIndex} in {elapsedMs}ms");

            // 全員回答済みなら早期締め切り
            int playerCount = SessionManager.Instance != null ? SessionManager.Instance.PlayerCount : 1;
            if (AnswerCount >= playerCount)
            {
                IsAcceptingAnswers = false;
                ProcessQuestionResult();
            }
        }

        private void ProcessQuestionResult()
        {
            if (!HasStateAuthority) return;
            if (currentQuestionSet == null || CurrentQuestionIndex >= currentQuestionSet.questions.Count) return;

            var q = currentQuestionSet.questions[CurrentQuestionIndex];

            // スコア計算
            var sessionManager = SessionManager.Instance;
            for (int i = 0; i < AnswerCount; i++)
            {
                int pid = AnswerOrderPlayerIds[i];
                int choice = AnswerChoices[i];
                float elapsed = AnswerTimesMs[i] / 1000f;
                bool correct = choice == q.correct_index;
                int score = ScoreCalculator.Calculate(correct, elapsed, q.difficulty);

                // SessionManager にスコア追加 (host)
                if (sessionManager != null)
                {
                    foreach (var kvp in sessionManager.Players)
                    {
                        if (kvp.Key.PlayerId == pid)
                        {
                            sessionManager.AddScore(kvp.Key, score);
                            break;
                        }
                    }
                }

                // [Networked] スコア配列を更新（クライアント同期用）
                AddScoreToNetworkedArray(pid, score);
            }

            RevealedCorrectIndex = q.correct_index;
            ShowingResult = true;
        }

        private void AddScoreToNetworkedArray(int playerId, int delta)
        {
            // 既存スロットを探す
            for (int i = 0; i < 8; i++)
            {
                if (ScorePlayerIds[i] == playerId)
                {
                    PlayerTotalScores.Set(i, PlayerTotalScores[i] + delta);
                    return;
                }
            }
            // 新規スロット
            for (int i = 0; i < 8; i++)
            {
                if (ScorePlayerIds[i] == -1)
                {
                    ScorePlayerIds.Set(i, playerId);
                    PlayerTotalScores.Set(i, delta);
                    return;
                }
            }
        }

        private void BuildAndFireQuestionResult()
        {
            // クライアント・ホスト共にこのメソッドで結果イベントを発火
            var result = new QuestionResult
            {
                questionIndex = CurrentQuestionIndex,
                correctIndex = RevealedCorrectIndex,
                explanation = "",
                playerResults = new List<PlayerQuestionResult>()
            };

            var session = SessionManager.Instance;
            if (session != null)
            {
                foreach (var kvp in session.Players)
                {
                    var playerRef = kvp.Key;
                    var playerData = kvp.Value;
                    var pr = new PlayerQuestionResult
                    {
                        playerRef = playerRef,
                        playerName = playerData.playerName,
                        choiceIndex = -1,
                        isCorrect = false,
                        score = 0,
                        elapsedTime = ScoreCalculator.TimeLimit
                    };

                    // [Networked] arrayから自分の回答を探す
                    for (int i = 0; i < AnswerCount; i++)
                    {
                        if (AnswerOrderPlayerIds[i] == playerRef.PlayerId)
                        {
                            pr.choiceIndex = AnswerChoices[i];
                            pr.elapsedTime = AnswerTimesMs[i] / 1000f;
                            pr.isCorrect = pr.choiceIndex == RevealedCorrectIndex;
                            pr.score = ScoreCalculator.Calculate(pr.isCorrect, pr.elapsedTime, CurrentQuestionDifficulty);
                            break;
                        }
                    }

                    result.playerResults.Add(pr);
                }
            }

            OnQuestionResult?.Invoke(result);
        }

        private void FinishQuiz()
        {
            if (!HasStateAuthority) return;
            QuizFinished = true;
        }

        private void BuildAndFireQuizFinished()
        {
            var sessionManager = SessionManager.Instance;

            // クライアント側: [Networked]スコア配列からSessionManagerに同期
            if (sessionManager != null && !HasStateAuthority)
            {
                for (int i = 0; i < 8; i++)
                {
                    int pid = ScorePlayerIds[i];
                    if (pid < 0) continue;
                    int score = PlayerTotalScores[i];
                    foreach (var kvp in sessionManager.Players)
                    {
                        if (kvp.Key.PlayerId == pid)
                        {
                            kvp.Value.totalScore = score;
                            break;
                        }
                    }
                }
            }

            var finalScores = new List<PlayerScoreEntry>();
            if (sessionManager != null)
            {
                foreach (var kvp in sessionManager.Players)
                {
                    finalScores.Add(new PlayerScoreEntry
                    {
                        playerName = kvp.Value.playerName,
                        totalScore = kvp.Value.totalScore
                    });
                }
            }
            finalScores.Sort((a, b) => b.totalScore.CompareTo(a.totalScore));
            OnQuizFinished?.Invoke(finalScores);
        }
    }

    public struct PlayerAnswer
    {
        public int choiceIndex;
        public float elapsedTime;
    }

    [Serializable]
    public class QuestionResult
    {
        public int questionIndex;
        public int correctIndex;
        public string explanation;
        public List<PlayerQuestionResult> playerResults;
    }

    [Serializable]
    public class PlayerQuestionResult
    {
        public Fusion.PlayerRef playerRef;
        public string playerName;
        public int choiceIndex;
        public bool isCorrect;
        public int score;
        public float elapsedTime;
    }

    [Serializable]
    public class PlayerScoreEntry
    {
        public string playerName;
        public int totalScore;
    }
}
