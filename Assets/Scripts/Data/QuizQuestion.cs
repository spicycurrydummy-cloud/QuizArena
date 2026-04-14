using System;
using System.Collections.Generic;

namespace GemmaQuiz.Data
{
    [Serializable]
    public class QuizQuestion
    {
        public int id;
        public string question;
        public string[] choices;
        public int correct_index;
        public string explanation;

        // 新フォーマット用 (LLMには answer + wrongs[] で出力させる)
        public string answer;
        public string[] wrongs;

        // 難易度 (0=Easy, 1=Normal, 2=Hard) — 生成後にインデックスから割り当て
        public int difficulty;
    }

    public enum QuizDifficulty
    {
        Easy = 0,
        Normal = 1,
        Hard = 2
    }

    public static class QuizDifficultyExtensions
    {
        public static string ToLabel(this QuizDifficulty d) => d switch
        {
            QuizDifficulty.Easy => "Easy",
            QuizDifficulty.Normal => "Normal",
            QuizDifficulty.Hard => "Hard",
            _ => "Normal"
        };

        public static string ToColorTag(this QuizDifficulty d) => d switch
        {
            QuizDifficulty.Easy => "<color=#4CAF50>",
            QuizDifficulty.Normal => "<color=#FF9800>",
            QuizDifficulty.Hard => "<color=#F44336>",
            _ => "<color=#FF9800>"
        };

        public static float ScoreMultiplier(this QuizDifficulty d) => d switch
        {
            QuizDifficulty.Easy => 0.5f,
            QuizDifficulty.Normal => 1.0f,
            QuizDifficulty.Hard => 1.5f,
            _ => 1.0f
        };
    }

    [Serializable]
    public class QuizQuestionSet
    {
        public List<QuizQuestion> questions;
    }

    public enum QuizGenre
    {
        AnimeGame,
        Sports,
        Entertainment,
        Lifestyle,
        Society,
        Humanities,
        Science,
        NonGenre
    }

    public static class QuizGenreExtensions
    {
        public static string ToJapanese(this QuizGenre genre)
        {
            return genre switch
            {
                QuizGenre.AnimeGame => "アニメ・ゲーム",
                QuizGenre.Sports => "スポーツ",
                QuizGenre.Entertainment => "芸能",
                QuizGenre.Lifestyle => "生活",
                QuizGenre.Society => "社会",
                QuizGenre.Humanities => "文系学問",
                QuizGenre.Science => "理系学問",
                QuizGenre.NonGenre => "ノンジャンル",
                _ => "ノンジャンル"
            };
        }

        public static string ToId(this QuizGenre genre)
        {
            return genre switch
            {
                QuizGenre.AnimeGame => "anime_game",
                QuizGenre.Sports => "sports",
                QuizGenre.Entertainment => "entertainment",
                QuizGenre.Lifestyle => "lifestyle",
                QuizGenre.Society => "society",
                QuizGenre.Humanities => "humanities",
                QuizGenre.Science => "science",
                QuizGenre.NonGenre => "non_genre",
                _ => "non_genre"
            };
        }

        public static string GetSubGenres(this QuizGenre genre)
        {
            var list = genre.GetSubGenreList();
            if (list == null || list.Length == 0) return "全ジャンルからランダム";
            return string.Join("、", list);
        }

        public static string[] GetSubGenreList(this QuizGenre genre)
        {
            return genre switch
            {
                QuizGenre.AnimeGame => new[] { "アニメ", "マンガ", "ゲーム", "特撮", "ライトノベル" },
                QuizGenre.Sports => new[] { "野球", "サッカー", "バスケットボール", "テニス", "格闘技", "オリンピック" },
                QuizGenre.Entertainment => new[] { "映画", "音楽", "テレビ", "お笑い", "アイドル" },
                QuizGenre.Lifestyle => new[] { "グルメ", "ファッション", "ホビー", "旅行", "健康" },
                QuizGenre.Society => new[] { "地理", "政治", "経済", "時事", "法律" },
                QuizGenre.Humanities => new[] { "歴史", "美術", "文学", "ことわざ・慣用句", "哲学" },
                QuizGenre.Science => new[] { "物理", "化学", "生物", "数学", "地学", "天文学" },
                _ => null
            };
        }
    }

    /// <summary>
    /// ジャンル + サブジャンルをint1つにエンコード/デコードするユーティリティ。
    /// encoded = genreIndex * 100 + subGenreIndex
    /// subGenreIndex: 0=すべて, 1+=特定サブジャンル
    /// </summary>
    public static class GenreEncoding
    {
        public const int CUSTOM_GENRE_CODE = 99;

        public static int Encode(int genreIndex, int subGenreIndex)
        {
            return genreIndex * 100 + subGenreIndex;
        }

        public static (int genreIndex, int subGenreIndex) Decode(int encoded)
        {
            if (encoded < 0) return (-1, 0);
            return (encoded / 100, encoded % 100);
        }

        public static bool IsCustomGenre(int encoded)
        {
            return encoded >= 0 && encoded / 100 == CUSTOM_GENRE_CODE;
        }

        /// <summary>
        /// 表示名を返す。"理系学問" or "理系学問-物理"
        /// </summary>
        public static string GetDisplayName(int genreIndex, int subGenreIndex)
        {
            if (genreIndex == CUSTOM_GENRE_CODE) return "カスタム";
            if (genreIndex < 0) return "未選択";

            var genre = (QuizGenre)genreIndex;
            string baseName = genre.ToJapanese();

            if (subGenreIndex <= 0) return baseName;

            var subs = genre.GetSubGenreList();
            if (subs != null && subGenreIndex >= 1 && subGenreIndex <= subs.Length)
                return $"{baseName}-{subs[subGenreIndex - 1]}";

            return baseName;
        }

        /// <summary>
        /// エンコード値から表示名を返す。
        /// </summary>
        public static string GetDisplayName(int encoded)
        {
            var (g, s) = Decode(encoded);
            return GetDisplayName(g, s);
        }
    }
}
