using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// クイズ問題生成・題材列挙・正解検証の各プロンプトを構築する。
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
        /// material: Wikipedia 等から集めた参考資料 (null なら知識のみで生成)
        /// avoidQuestions: 追加生成時に重複を避けたい既出の問題文
        /// </summary>
        public static string Build(QuizGenre genre, int subGenreIndex, int questionCount = 10,
            string material = null, IList<string> avoidQuestions = null)
        {
            var genreName = genre.ToJapanese();
            var subGenres = genre.GetSubGenres();
            var examples = GetExamples(genre);
            var specificSub = GetSubGenreName(genre, subGenreIndex);

            string genreInstruction;
            if (genre == QuizGenre.NonGenre)
            {
                genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese, drawing evenly from these Japanese genre labels: アニメ・ゲーム, スポーツ, 芸能, 生活, 社会, 文系学問, 理系学問. Each question must be from a different genre; do not let the same genre appear in consecutive questions.";
            }
            else if (specificSub != null)
            {
                genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the genre ""{genreName}"", strictly limited to the sub-topic ""{specificSub}"". Every question must be about ""{specificSub}"" and must not stray into other topics or other genres.";
            }
            else
            {
                genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the genre ""{genreName}"" (scope: {subGenres}). Every question must stay inside this genre.";
            }

            return BuildBody(genreInstruction, examples, questionCount, material, avoidQuestions);
        }

        /// <summary>
        /// カスタムジャンル用プロンプト。
        /// </summary>
        public static string BuildCustom(string customGenreName, int questionCount = 10,
            string material = null, IList<string> avoidQuestions = null)
        {
            string genreInstruction = $@"Generate a {questionCount}-question four-choice quiz in Japanese on the topic ""{customGenreName}"". Every question must be directly about ""{customGenreName}""; do not drift to unrelated topics.";
            var examples = GetExamples(QuizGenre.NonGenre);

            return BuildBody(genreInstruction, examples, questionCount, material, avoidQuestions);
        }

        private static string BuildBody(string genreInstruction, string examples, int questionCount,
            string material, IList<string> avoidQuestions)
        {
            // 難易度の配分を計算
            int easy = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.3f));
            int hard = Mathf.Max(1, Mathf.RoundToInt(questionCount * 0.2f));
            int normal = questionCount - easy - hard;

            var sb = new StringBuilder(4096);
            sb.Append(genreInstruction).Append("\n\n");
            sb.Append("IMPORTANT: Stay strictly inside the requested genre. Do not use topics from any other genre under any circumstance. The examples below are ONLY for JSON format reference — never copy their subject matter, named entities, or domain.\n");

            if (!string.IsNullOrWhiteSpace(material))
            {
                sb.Append(@"
Reference material (numbered excerpts from Japanese Wikipedia):
").Append(material.Trim()).Append(@"

How to use the reference material:
- Base the correct answer of as many questions as possible on facts stated in the excerpts, so every answer can be verified
- At most one question per excerpt; if more questions are needed than excerpts, use well-established general knowledge inside the genre
- Do not copy excerpt sentences verbatim, and never mention Wikipedia or ""the material"" in a question
- Ignore excerpts that are off-genre or unsuitable for a quiz
");
            }

            if (avoidQuestions != null && avoidQuestions.Count > 0)
            {
                sb.Append("\nAlready used questions — do NOT repeat or rephrase any of them; choose different subjects:\n");
                foreach (var q in avoidQuestions)
                {
                    if (string.IsNullOrWhiteSpace(q)) continue;
                    sb.Append("- ").Append(q.Trim()).Append('\n');
                }
            }

            sb.Append($@"
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
]}}");

            return sb.ToString();
        }

        // ===== 題材 (Wikipedia) =====

        /// <summary>
        /// 題材列挙プロンプト用のスコープ説明。
        /// </summary>
        public static string DescribeScope(QuizGenre genre, int subGenreIndex)
        {
            if (genre == QuizGenre.NonGenre)
                return "any of these genres, mixed evenly: アニメ・ゲーム, スポーツ, 芸能, 生活, 社会, 文系学問, 理系学問";

            var genreName = genre.ToJapanese();
            var specificSub = GetSubGenreName(genre, subGenreIndex);
            return specificSub != null
                ? $"the genre \"{genreName}\", restricted to the sub-topic \"{specificSub}\""
                : $"the genre \"{genreName}\" (scope: {genre.GetSubGenres()})";
        }

        /// <summary>
        /// Wikipedia 全文検索フォールバック用のキーワード。
        /// </summary>
        public static string SearchKeyword(QuizGenre genre, int subGenreIndex)
        {
            if (genre == QuizGenre.NonGenre) return "日本の文化";
            return GetSubGenreName(genre, subGenreIndex) ?? genre.ToJapanese();
        }

        /// <summary>
        /// 日本語版Wikipediaの記事タイトルを列挙させるプロンプト (題材の多様化用)。
        /// </summary>
        public static string BuildTopicTitles(string scopeDescription, int count, IEnumerable<string> avoidTitles, int seed)
        {
            var sb = new StringBuilder(1024);
            sb.Append($@"List {count} article titles from Japanese Wikipedia (ja.wikipedia.org) that would make good subjects for a four-choice trivia quiz on {scopeDescription}.

Requirements:
- Write each title exactly as the Japanese Wikipedia article is named, so the page can be looked up directly
- Each title must be a concrete subject inside the scope (a person, work, team, place, event, product, species, technical term...), not a broad overview article such as the genre name itself
- About half should be subjects most Japanese adults recognise, the rest moderately known ones; vary eras, regions and sub-topics
- All {count} titles must be different subjects
- Random seed {seed}: choose a fresh, varied set rather than only the most obvious picks
");
            bool any = false;
            if (avoidTitles != null)
            {
                foreach (var t in avoidTitles)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (!any)
                    {
                        sb.Append("- Do NOT include any of these already used titles:\n");
                        any = true;
                    }
                    sb.Append("  ・").Append(t.Trim()).Append('\n');
                }
            }
            sb.Append(@"
Output JSON only, in this shape:
{""titles"":[""記事タイトル"",""記事タイトル""]}");
            return sb.ToString();
        }

        // ===== 正解の裏取り (Gemini + Google検索) =====

        /// <summary>
        /// 生成済み問題の正解をWeb検索で検証させるプロンプト。
        /// グラウンディング時はJSONモードが使えないので、本文にJSONだけを書かせる。
        /// </summary>
        public static string BuildSearchVerification(IList<(int id, string question, string answer, string[] wrongs)> items)
        {
            var sb = new StringBuilder(2048);
            sb.Append(@"You are the fact-checker for a Japanese four-choice trivia quiz used in a casual party game. Use Google Search to verify every item below.

For each item give exactly one verdict:
- ""ok"": the given answer is correct, and none of the wrong choices could also be accepted as a correct answer
- ""wrong"": the given answer is incorrect. If one of the wrong choices is actually the correct answer, copy that choice EXACTLY into ""correct_answer""; otherwise leave ""correct_answer"" empty. Never use ""wrong"" when the given answer itself is correct
- ""unsure"": you could not confirm the fact, the question is ambiguous, has more than one valid answer, or depends on a fact that changes over time

Rules:
- Judge the answer against ALL conditions stated in the question (nationality, era, category, ""Japanese"", ""first"", etc.), not only the general topic
- If the premise is debatable or another choice could reasonably be argued, answer ""unsure"" rather than guessing
- Be strict about factual correctness, but do not reject a question only because it is easy or well known

Items:
");
            foreach (var item in items)
            {
                sb.Append(item.id).Append(". Q: ").Append(Clean(item.question)).Append('\n');
                sb.Append("   answer: ").Append(Clean(item.answer)).Append('\n');
                sb.Append("   wrong choices: ");
                if (item.wrongs != null)
                {
                    foreach (var w in item.wrongs)
                        sb.Append('「').Append(Clean(w)).Append('」');
                }
                sb.Append('\n');
            }

            sb.Append(@"
Output format: exactly ONE JSON object and nothing else — no markdown fences, no numbering, do not repeat the questions:
{""results"":[{""id"":1,""verdict"":""ok"",""correct_answer"":"""",""note"":""短い理由 (日本語)""}]}
Include one result for every item, in order.");
            return sb.ToString();
        }

        /// <summary>
        /// 検索付き検証の応答がJSONになっていなかったとき、JSONモードで整形し直すためのプロンプト。
        /// </summary>
        public static string BuildVerificationToJson(string notes, int itemCount)
        {
            return $@"Convert the following fact-check notes about a quiz into JSON. There are {itemCount} items numbered 1 to {itemCount}.
For each item output: ""id"", ""verdict"" (one of ok / wrong / unsure; use ""unsure"" when the notes give no clear verdict for that item), ""correct_answer"" (the alternative answer the notes name, otherwise an empty string) and a short ""note"" in Japanese.
Do not add items that are not in the notes.

Notes:
{notes}

Output shape:
{{""results"":[{{""id"":1,""verdict"":""ok"",""correct_answer"":"""",""note"":""短い理由""}}]}}";
        }

        /// <summary>
        /// Wikipedia 検索で集めた根拠文をもとに生成モデル自身に正解を判定させるプロンプト
        /// (Gemini の無料枠が無いときの検証経路。根拠が無いだけでは落とさない)。evidence[i] は items[i] に対応。
        /// </summary>
        public static string BuildEvidenceVerification(IList<(int id, string question, string answer, string[] wrongs)> items, IList<string> evidence)
        {
            var sb = new StringBuilder(4096);
            sb.Append(@"You are the fact-checker for a Japanese four-choice trivia quiz used in a casual party game. For each item you get the question, the intended answer, the wrong choices, and evidence excerpts retrieved from Japanese Wikipedia.

Give exactly one verdict per item:
- ""wrong"": the evidence or well-established knowledge clearly contradicts the intended answer, or one of the wrong choices is actually the correct answer. Put that choice in ""correct_answer"" when it is one of the listed choices, otherwise leave it empty
- ""ok"": the evidence supports the intended answer, or nothing contradicts it and the answer is consistent with well-established knowledge
- ""unsure"": the question itself is ambiguous, has more than one valid answer, or depends on a fact that changes over time

Rules:
- Judge against ALL conditions stated in the question (nationality, era, category, ""first"", etc.)
- Only answer ""wrong"" when you are confident; missing evidence alone is never a reason for ""wrong""
- Never use ""wrong"" when the intended answer itself is correct

Items:
");
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                sb.Append(item.id).Append(". Q: ").Append(Clean(item.question)).Append('\n');
                sb.Append("   answer: ").Append(Clean(item.answer)).Append('\n');
                sb.Append("   wrong choices: ");
                if (item.wrongs != null)
                {
                    foreach (var w in item.wrongs)
                        sb.Append('「').Append(Clean(w)).Append('」');
                }
                sb.Append('\n');
                string ev = evidence != null && i < evidence.Count ? evidence[i] : null;
                sb.Append("   evidence: ").Append(string.IsNullOrWhiteSpace(ev) ? "(none found)" : ev.Trim()).Append("\n\n");
            }

            sb.Append(@"Output exactly ONE JSON object and nothing else:
{""results"":[{""id"":1,""verdict"":""ok"",""correct_answer"":"""",""note"":""短い理由 (日本語)""}]}
Include one result for every item, in order.");
            return sb.ToString();
        }

        private static string Clean(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Trim();

        private static string GetSubGenreName(QuizGenre genre, int subGenreIndex)
        {
            if (subGenreIndex < 1) return null;
            var subs = genre.GetSubGenreList();
            return subs != null && subGenreIndex <= subs.Length ? subs[subGenreIndex - 1] : null;
        }

        private static HashSet<string> exampleQuestions;

        /// <summary>
        /// プロンプト内の書式例をそのまま出題してきた問題かどうか (LLM が例題を流用することがあるので除外用)。
        /// </summary>
        public static bool IsExampleQuestion(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;
            if (exampleQuestions == null)
            {
                exampleQuestions = new HashSet<string>();
                foreach (QuizGenre g in System.Enum.GetValues(typeof(QuizGenre)))
                {
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(GetExamples(g), "\"question\":\"([^\"]+)\""))
                        exampleQuestions.Add(m.Groups[1].Value.Trim());
                }
            }
            return exampleQuestions.Contains(question.Trim());
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
