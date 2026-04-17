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
                genreInstruction = $@"以下の全ジャンルから満遍なく混ぜて4択クイズを{questionCount}問作ってください。
ジャンル: アニメ・ゲーム、スポーツ、芸能、生活、社会、文系学問、理系学問
各問題はそれぞれ異なるジャンルから出題し、同じジャンルが連続しないようにしてください。";
            }
            else if (subGenreIndex >= 1)
            {
                var subs = genre.GetSubGenreList();
                if (subs != null && subGenreIndex <= subs.Length)
                {
                    var specificSub = subs[subGenreIndex - 1];
                    genreInstruction = $@"{genreName}の中の「{specificSub}」に関する4択クイズを{questionCount}問作ってください。
出題範囲は{specificSub}に限定してください。";
                }
                else
                {
                    genreInstruction = $@"{genreName}（{subGenres}）の4択クイズを{questionCount}問作ってください。";
                }
            }
            else
            {
                genreInstruction = $@"{genreName}（{subGenres}）の4択クイズを{questionCount}問作ってください。";
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

            string genreInstruction = $@"「{customGenreName}」に関する4択クイズを{questionCount}問作ってください。";
            var examples = GetExamples(QuizGenre.NonGenre);

            return BuildBody(genreInstruction, examples, questionCount, easy, normal, hard);
        }

        private static string BuildBody(string genreInstruction, string examples, int questionCount, int easy, int normal, int hard)
        {
            return $@"{genreInstruction}

難易度の配分:
- 簡単な問題を{easy}問: 誰もが知っている有名な事実
- 普通の問題を{normal}問: ある程度の知識があれば解ける問題
- 難しい問題を{hard}問: マニアックな知識が必要な問題
この順番で並べてください。

問題のルール:
- 問題は固有名詞・数値を問う、答えがただ1つに決まるものにする
- 年号や西暦を答えさせる問題は禁止
- 定義や概念を問う問題は禁止
- 「有名な〜は？」「代表的な〜は？」のような主観で複数答えうる問題は禁止
- 正解は事実として確実に正しい1つの固有名詞・数値
- 不正解の3つも実在する有名な名称にする
- 不正解は正解と同じカテゴリから選ぶが、正解の代わりに当てはまるものを入れない
- 難しい問題は不正解の選択肢も紛らわしいものにする

問題文の禁止事項（重要）:
- 問題文に正解そのものを絶対に含めない
  悪い例: 「ワンピースの主人公、麦わら海賊団のルフィの所属は？」→ 答え「麦わら海賊団」が文中にある
  良い例: 「ワンピースの主人公ルフィが所属する海賊団は？」→ 答え「麦わら海賊団」は文中にない
- 問題文に正解の一部や言い換えを含めない
  悪い例: 「スペインの首都マドリードがある国は？」→ 答え「スペイン」が文中にある
- 問題文の手がかりから答えが自明になる表現を避ける
- 問題文は答えを知らなければ解けない形にする

事実の正確さ:
- 自信がない事実は問題にしない
- 因果関係や「最初」「理由」を問う問題は誤りが混じりやすいので避ける
- 事実確認に迷う問題は作らず、確定した知識のみを出題する

多様性:
- {questionCount}問は全て異なるテーマから出題する。同じ対象を別の角度で問うのも禁止
- 以下のような使い古された定番問題は避けること:
  世界一人口が多い国、日本一高い山、太陽系最大の惑星、水の化学式、
  日本の首都、富士山の高さ、光の速さ、血液型の種類

回答テキストのルール:
- 全て日本語で出力すること。ハングル、アラビア語、タイ語などの他言語は絶対に使わない
- answer と wrongs の中身は短い名詞のみ
- 括弧書き、補足説明、カテゴリ名、解説、改行を含めない
- 単に名前や数値だけ書く

良い例:
{examples}

以下のJSON形式で{questionCount}問出力してください:
{{""questions"":[
{{""question"":""問題文"",""answer"":""短い名詞"",""wrongs"":[""短い名詞"",""短い名詞"",""短い名詞""]}}
]}}";
        }

        /// <summary>
        /// 生成された問題を検証するプロンプト。
        /// </summary>
        public static string BuildValidation(string questionsJson)
        {
            return $@"以下のクイズの不正解の選択肢に架空の名前が含まれていないか確認してください。
架空の名前があれば、同じカテゴリの実在する有名なものに差し替えてください。
また、正解が事実として間違っている問題は削除してください。
同じJSON形式で出力してください。

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
