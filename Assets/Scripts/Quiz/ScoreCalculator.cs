using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.Quiz
{
    /// <summary>
    /// スコア計算ロジック。
    /// 基本点: 1秒以内の回答は 20点満点、以降は制限時間(10秒)に向けて 10点まで線形に減少
    ///         score = 10 + 10 * (1 - (elapsed - 1) / 9)
    /// 難易度倍率: Easy=0.5x, Normal=1x, Hard=1.5x (QuizDifficultyExtensions.ScoreMultiplier)
    /// 不正解/未回答: 0点
    /// </summary>
    public static class ScoreCalculator
    {
        public const float TimeLimit = 10f;
        public const float FullScoreWindow = 1f; // この秒数以内なら満点
        public const int MaxScore = 20;
        public const int MinScore = 10;

        public static int Calculate(bool isCorrect, float elapsedSeconds, int difficulty = (int)QuizDifficulty.Normal)
        {
            if (!isCorrect)
                return 0;

            float t = Mathf.Clamp01((elapsedSeconds - FullScoreWindow) / (TimeLimit - FullScoreWindow));
            float baseScore = Mathf.Lerp(MaxScore, MinScore, t);
            float multiplier = ((QuizDifficulty)difficulty).ScoreMultiplier();
            return Mathf.RoundToInt(baseScore * multiplier);
        }
    }
}
