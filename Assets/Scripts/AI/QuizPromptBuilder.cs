using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// クイズ問題生成用のプロンプトを構築する。
    /// </summary>
    public static class QuizPromptBuilder
    {
        /// <summary>
        /// 問題生成用プロンプト（従来互換: サブジャンル=すべて）。
        /// </summary>
        public static string Build(QuizGenre genre, int questionCount = 10)
        {
            return Build(genre, 0, questionCount);
        }

        /// <summary>
        /// 問題生成用プロンプト（サブジャンル指定対応）。
        /// subGenreIndex: 0=すべて, 1+=特定サブジャンル
        /// </summary>
        public static string Build(QuizGenre genre, int subGenreIndex, int questionCount = 10)
        {
            var genreName = genre.ToJapanese();
            var subGenres = genre.GetSubGenres();
            var examples = GetExamples(genre);

            // 難易度の配分を計算
            int easy = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.3f));
            int hard = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.2f));
            int normal = questionCount - easy - hard;

            string genreInstruction;
            if (genre == QuizGenre.NonGenre)
            {
                genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese, drawing evenly from these Japanese genre labels: アニメ・ゲーム, スポーツ, 芸能, 生活, 社会, 文系学問, 理系学問. Each question must be from a different genre; do not let the same genre appear in consecutive questions.";
            }
            else if (subGenreIndex >= 1)
            {
                var subs = genre.GetSubGenreList();
                if (subs != null && subGenreIndex <= subs.Length)
                {
                    var specificSub = subs[subGenreIndex - 1];
                    genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the genre ""{genreName}"", strictly limited to the sub-topic ""{specificSub}"". Every question must be about ""{specificSub}"" and must not stray into other topics or other genres.";
                }
                else
                {
                    genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the genre ""{genreName}"" (scope: {subGenres}). Every question must stay inside this genre.";
                }
            }
            else
            {
                genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the genre ""{genreName}"" (scope: {subGenres}). Every question must stay inside this genre.";
            }

            return BuildBody(genreInstruction, examples, questionCount, easy, normal, hard);
        }

        /// <summary>
        /// カスタムジャンル用プロンプト。
        /// </summary>
        public static string BuildCustom(string customGenreName, int questionCount = 10)
        {
            int easy = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.3f));
            int hard = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.2f));
            int normal = questionCount - easy - hard;

            string genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the topic ""{customGenreName}"". Every question must be directly about ""{customGenreName}""; do not drift to unrelated topics.";
            var examples = GetExamples(QuizGenre.NonGenre);

            return BuildBody(genreInstruction, examples, questionCount, easy, normal, hard);
        }

        private static string BuildBody(string genreInstruction, string examples, int questionCount, int easy, int normal, int hard)
        {
            return $@"{genreInstruction}

IMPORTANT: Stay strictly inside the requested genre. Do not use topics from any other genre under any circumstance. The examples below are ONLY for JSON format reference — never copy their subject matter, named entities, or domain.

Difficulty distribution (arrange questions in this order):
- {easy} easy question(s): facts commonly known to the general public
- {normal} normal question(s): solvable with moderate knowledge
- {hard} hard question(s): require deeper / specialised knowledge

Question content rules:
- Each question must ask for a proper noun or a number with exactly one definitively correct answer
- Do NOT ask for years or dates as the answer
- Do NOT ask for definitions, concepts, or subjective ""most famous / most representative"" questions
- The correct answer must be a factually certain proper noun or number
- The three wrong answers must be real, well-known entities from the same category
- Wrong answers must come from the same category as the correct one but must not themselves be correct substitutes
- For hard questions, make wrong answers plausible distractors

Question-text prohibitions (critical):
- The question text MUST NOT contain the correct answer string, nor any substring or paraphrase of it
- Bad pattern: question reveals the answer inside the prompt (e.g. mentioning the exact name being asked)
- Good pattern: question describes the answer indirectly so it cannot be solved without actual knowledge
- Avoid wording whose hints make the answer self-evident

Factual accuracy:
- Do not create questions on facts you are not certain about
- Avoid ""first / reason / why"" style questions — they often contain factual errors
- Use only well-established, unambiguous knowledge

Diversity:
- All {questionCount} questions must cover different subjects; never ask about the same subject from a different angle
- Avoid worn-out clichés such as: most populous country, highest mountain in Japan, largest planet in the solar system, chemical formula of water, capital of Japan, height of Mt. Fuji, speed of light, number of blood types

Output text rules (applied to every field):
- All Japanese output only. Never use Hangul, Arabic, Thai, Cyrillic, or other non-Japanese / non-ASCII scripts
- ""answer"" and each ""wrongs"" entry: short noun only — no parentheses, annotations, category labels, explanations, or line breaks
- Output plain names or numbers only

JSON format examples (FORMAT REFERENCE ONLY — do not reuse the subjects):
{examples}

Output exactly {questionCount} questions as JSON in the following shape:
{{""questions"":[
{{""question"":""問題文"",""answer"":""短い名詞"",""wrongs"":[""短い名詞"",""短い名詞"",""短い名詞""]}}
]}}";
        }

        /// <summary>
        /// 生成された問題を検証するプロンプト。
        /// </summary>
        public static string BuildValidation(string questionsJson)
        {
            return $@"Audit the following quiz. For each question, check:
1. Every ""wrongs"" entry must be a real, well-known entity (not fictional). Replace fictional names with real, well-known entities from the same category.
2. The ""answer"" must be factually correct. Remove any question whose answer is factually wrong.
Keep all text Japanese. Output the corrected quiz in the exact same JSON shape as the input.

{questionsJson}";
        }

        private static string GetExamples(QuizGenre genre)
        {
            // 例題は定番すぎない問題にする (Gemmaが例題をコピーして出す傾向があるため)
            return genre switch
            {
                QuizGenre.AnimeGame => @"{""question"":""漫画『ジョジョの奇妙な冒険』第3部の主人公は？"",""answer"":""空条承太郎"",""wrongs"":[""ジョセフ・ジョースター"",""ジョルノ・ジョバァーナ"",""東方仗助""]}
{""question"":""ゲーム『スプラトゥーン』シリーズに登場するインクを塗る武器の総称は？"",""answer"":""ブキ"",""wrongs"":[""ギア"",""ウェポン"",""スプラッシュ""]}",

                QuizGenre.Sports => @"{""question"":""テニスのグランドスラムで唯一クレーコートで行われる大会は？"",""answer"":""全仏オープン"",""wrongs"":[""全豪オープン"",""ウィンブルドン"",""全米オープン""]}
{""question"":""1998年FIFAワールドカップの開催国は？"",""answer"":""フランス"",""wrongs"":[""日本"",""ドイツ"",""アメリカ""]}",

                QuizGenre.Entertainment => @"{""question"":""映画『もののけ姫』でアシタカが旅立つきっかけとなった呪いを与えた生き物は？"",""answer"":""タタリ神"",""wrongs"":[""シシ神"",""モロ"",""乙事主""]}
{""question"":""楽曲『Lemon』を歌ったアーティストは？"",""answer"":""米津玄師"",""wrongs"":[""あいみょん"",""King Gnu"",""Official髭男dism""]}",

                QuizGenre.Lifestyle => @"{""question"":""紅茶の産地として有名なインドの地域ダージリンがある州は？"",""answer"":""西ベンガル州"",""wrongs"":[""アッサム州"",""ケーララ州"",""タミル・ナードゥ州""]}
{""question"":""日本酒の原料に使われる米の品種で最も有名なものは？"",""answer"":""山田錦"",""wrongs"":[""コシヒカリ"",""五百万石"",""あきたこまち""]}",

                QuizGenre.Society => @"{""question"":""スエズ運河がある国は？"",""answer"":""エジプト"",""wrongs"":[""パナマ"",""トルコ"",""イラン""]}
{""question"":""日本の都道府県で最も市町村数が多いのは？"",""answer"":""北海道"",""wrongs"":[""長野県"",""新潟県"",""岩手県""]}",

                QuizGenre.Humanities => @"{""question"":""小説『走れメロス』の作者は？"",""answer"":""太宰治"",""wrongs"":[""芥川龍之介"",""夏目漱石"",""川端康成""]}
{""question"":""絵画『星月夜』を描いた画家は？"",""answer"":""ゴッホ"",""wrongs"":[""モネ"",""ルノワール"",""セザンヌ""]}",

                QuizGenre.Science => @"{""question"":""人間の体で最も硬い組織は？"",""answer"":""エナメル質"",""wrongs"":[""骨"",""象牙質"",""爪""]}
{""question"":""元素記号Auが表す元素は？"",""answer"":""金"",""wrongs"":[""銀"",""銅"",""アルミニウム""]}",

                _ => @"{""question"":""パナマ運河が太平洋と結んでいるもう一方の海は？"",""answer"":""大西洋"",""wrongs"":[""インド洋"",""北極海"",""カリブ海""]}
{""question"":""小説『走れメロス』の作者は？"",""answer"":""太宰治"",""wrongs"":[""芥川龍之介"",""夏目漱石"",""川端康成""]}"
            };
        }
    }
}
