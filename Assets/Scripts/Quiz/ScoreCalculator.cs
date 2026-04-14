using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.Quiz
{
    /// <summary>
    /// スコア計算ロジック。
    /// 基本点: 回答速度に応じて 20 → 10 点に線形スケール
    /// 難易度倍率: Easy=0.5x, Normal=1x, Hard=2x
    /// 不正解/未回答: 0点
    /// </summary>
    public static class ScoreCalculator
    {
        public const float TimeLimit = 10f;
        public const int MaxScore = 20;
        public const int MinScore = 10;

        /// <summary>
        /// スコアを計算する。
        /// </summary>
        public static int Calculate(bool isCorrect, float elapsedSeconds, int difficulty = (int)QuizDifficulty.Normal)
        {
            if (!isCorrect)
                return 0;

            float t = Mathf.Clamp01(elapsedSeconds / TimeLimit);
            float baseScore = Mathf.Lerp(MaxScore, MinScore, t);
            float multiplier = ((QuizDifficulty)difficulty).ScoreMultiplier();
            return Mathf.RoundToInt(baseScore * multiplier);
        }
    }
}
