using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GemmaQuiz.Data;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// AIプロバイダーの選択肢。
    /// </summary>
    public enum AIProvider
    {
        Ollama = 0,
        Inception = 1,
        Gemini = 2
    }

    /// <summary>
    /// クイズ問題の生成を管理するMonoBehaviour。
    /// ホスト側でのみ使用し、生成した問題をネットワーク経由で配信する。
    /// 2パス方式: 生成 → AI検証 → シャッフル
    /// </summary>
    public class QuizGenerator : MonoBehaviour
    {
        public static QuizGenerator Instance { get; private set; }

        [Header("AI Provider")]
        [SerializeField] private AIProvider aiProvider = AIProvider.Inception;
        public AIProvider Provider
        {
            get => aiProvider;
            set
            {
                if (aiProvider != value)
                {
                    aiProvider = value;
                    RebuildClient();
                }
            }
        }

        [Header("Ollama Settings")]
        [SerializeField] private string ollamaUrl = "http://localhost:11434";
        [SerializeField] private string ollamaModel = "gemma4:e4b";

        [Header("Inception Settings")]
        [SerializeField] private string inceptionUrl = "https://api.inceptionlabs.ai/v1";
        [SerializeField] private string inceptionModel = "mercury-2";
        [SerializeField] private string inceptionReasoningEffort = "medium"; // low / medium / high

        // APIキーは Resources/Config.json から読み込む (gitignore済み)。
        // CIではGitHub Secret (JSON丸ごと) → Resources/Config.json へ書き出し、の順で注入。
        // Inspector上書き用フィールド（空なら Config.json から読む）
        [SerializeField] private string inceptionApiKey = "";

        [Header("Gemini Settings (Fallback)")]
        [SerializeField] private string geminiModel = "gemini-2.5-flash-lite";
        [SerializeField] private string geminiApiKey = "";

        [Header("Common Settings")]
        [SerializeField] private float temperature = 0.4f;
        [SerializeField] private int questionsPerGenre = 5;
        public int QuestionsPerGenre
        {
            get => questionsPerGenre;
            set => questionsPerGenre = Mathf.Clamp(value, 1, 20);
        }
        [SerializeField] private int maxRetries = 6;

        [Header("Validation")]
        [SerializeField] private bool enableValidationPass = false;

        private IQuizAIClient client;
        private IQuizAIClient fallbackClient; // Gemini fallback (null if primary is Gemini or key absent)
        private System.Random rng;

        // Ollama Structured Output 用の JSON Schema
        // モデルの出力がこのスキーマに完全準拠するよう強制される
        private const string QuizSchema =
            "{\"type\":\"object\",\"properties\":{\"questions\":{\"type\":\"array\"," +
            "\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"question\":{\"type\":\"string\"}," +
            "\"answer\":{\"type\":\"string\"}," +
            "\"wrongs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":3,\"maxItems\":3}" +
            "},\"required\":[\"question\",\"answer\",\"wrongs\"]}}}," +
            "\"required\":[\"questions\"]}";

        public bool IsGenerating { get; private set; }
        public QuizQuestionSet LastGeneratedQuestions { get; private set; }

        // 事前生成キャッシュ: エンコード済みジャンル値 → 問題セット
        private readonly Dictionary<int, QuizQuestionSet> preGeneratedCache = new();
        // 現在事前生成中のエンコード済みジャンル値
        private readonly HashSet<int> preGeneratingGenres = new();

        public event Action<float> OnGenerationProgress;
        public event Action<QuizQuestionSet> OnGenerationComplete;
        public event Action<string> OnGenerationFailed;
        public event Action OnPreGenerationStateChanged;

        public int PreGeneratingCount => preGeneratingGenres.Count;
        public int PreGeneratedCacheCount => preGeneratedCache.Count;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            RebuildClient();
            rng = new System.Random();
        }

        private void RebuildClient()
        {
            var config = AIConfig.Load();
            string geminiKey = string.IsNullOrEmpty(geminiApiKey) ? config.gemini_api_key : geminiApiKey;

            switch (aiProvider)
            {
                case AIProvider.Inception:
                    var key = string.IsNullOrEmpty(inceptionApiKey) ? config.inception_api_key : inceptionApiKey;
                    if (string.IsNullOrEmpty(key))
                        Debug.LogError("[QuizGenerator] Inception API key not found. Place key in Resources/Config.json or set in Inspector.");
                    // mercury-coder-small は廃止済み、mercury-2 にフォールバック
                    var model = inceptionModel;
                    if (string.IsNullOrEmpty(model) || model.Contains("coder-small"))
                        model = "mercury-2";
                    client = new InceptionClient(key, inceptionUrl, model, temperature, inceptionReasoningEffort);
                    Debug.Log($"[QuizGenerator] Using Inception (model: {model}, reasoning: {inceptionReasoningEffort})");
                    break;
                case AIProvider.Gemini:
                    if (string.IsNullOrEmpty(geminiKey))
                        Debug.LogError("[QuizGenerator] Gemini API key not found. Place key in Resources/Config.json or set in Inspector.");
                    client = new GeminiClient(geminiKey, geminiModel, temperature);
                    Debug.Log($"[QuizGenerator] Using Gemini (model: {geminiModel})");
                    break;
                default:
                    client = new OllamaClient(ollamaUrl, ollamaModel, temperature);
                    Debug.Log($"[QuizGenerator] Using Ollama (model: {ollamaModel})");
                    break;
            }

            // フォールバック: プライマリが Gemini 以外でキーがあれば Gemini を予備に確保
            if (aiProvider != AIProvider.Gemini && !string.IsNullOrEmpty(geminiKey))
            {
                fallbackClient = new GeminiClient(geminiKey, geminiModel, temperature);
                Debug.Log($"[QuizGenerator] Gemini fallback armed (model: {geminiModel})");
            }
            else
            {
                fallbackClient = null;
            }
        }

        /// <summary>
        /// 事前生成キャッシュにある問題を取得する。なければnull。
        /// </summary>
        public QuizQuestionSet TakeCachedQuestions(int encodedGenre)
        {
            if (preGeneratedCache.TryGetValue(encodedGenre, out var set))
            {
                preGeneratedCache.Remove(encodedGenre);
                Debug.Log($"[QuizGenerator] Took cached questions for {GenreEncoding.GetDisplayName(encodedGenre)}");
                return set;
            }
            return null;
        }

        /// <summary>
        /// 事前生成済み（または生成中）のエンコード済みジャンル一覧を返す。
        /// </summary>
        public List<int> GetPreGeneratedGenres()
        {
            var list = new List<int>();
            list.AddRange(preGeneratedCache.Keys);
            list.AddRange(preGeneratingGenres);
            return list;
        }

        /// <summary>
        /// 指定ジャンルの問題を事前生成する（バックグラウンド）。
        /// すでにキャッシュがあれば何もしない。
        /// </summary>
        public async void PreGenerateAsync(QuizGenre genre, int subGenreIndex = 0)
        {
            int encoded = GenreEncoding.Encode((int)genre, subGenreIndex);
            if (preGeneratedCache.ContainsKey(encoded)) return;
            if (preGeneratingGenres.Contains(encoded)) return;
            if (IsGenerating) return;

            preGeneratingGenres.Add(encoded);
            OnPreGenerationStateChanged?.Invoke();
            var displayName = GenreEncoding.GetDisplayName((int)genre, subGenreIndex);
            Debug.Log($"[QuizGenerator] Pre-generating questions for {displayName}");

            try
            {
                var prompt = QuizPromptBuilder.Build(genre, subGenreIndex, questionsPerGenre);
                var responseJson = await client.GenerateAsync(prompt, QuizSchema);
                var questionSet = ParseResponse(responseJson);

                if (questionSet != null && questionSet.questions != null && questionSet.questions.Count > 0)
                {
                    ConvertRawToChoices(questionSet);
                    SanitizeQuestions(questionSet);
                    ValidateQuestions(questionSet);
                    AssignDifficulty(questionSet);
                    preGeneratedCache[encoded] = questionSet;
                    Debug.Log($"[QuizGenerator] Pre-generation complete for {displayName} ({questionSet.questions.Count}問)");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator] Pre-generation failed for {displayName}: {e.Message}");
            }
            finally
            {
                preGeneratingGenres.Remove(encoded);
                OnPreGenerationStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 指定ジャンルのクイズ問題を生成する。
        /// </summary>
        public void GenerateQuestions(QuizGenre genre, int subGenreIndex = 0)
        {
            int encoded = GenreEncoding.Encode((int)genre, subGenreIndex);
            var displayName = GenreEncoding.GetDisplayName((int)genre, subGenreIndex);
            GenerateQuestionsInternal(encoded, displayName,
                () => QuizPromptBuilder.Build(genre, subGenreIndex, questionsPerGenre));
        }

        /// <summary>
        /// カスタムジャンルのクイズ問題を生成する。
        /// </summary>
        public void GenerateQuestions(string customGenreName)
        {
            int encoded = GenreEncoding.CUSTOM_GENRE_CODE * 100;
            GenerateQuestionsInternal(encoded, customGenreName,
                () => QuizPromptBuilder.BuildCustom(customGenreName, questionsPerGenre));
        }

        private async void GenerateQuestionsInternal(int encodedGenre, string displayName, Func<string> buildPrompt)
        {
            if (IsGenerating)
            {
                Debug.LogWarning("[QuizGenerator] Already generating questions.");
                return;
            }

            var cached = TakeCachedQuestions(encodedGenre);
            if (cached != null)
            {
                LastGeneratedQuestions = cached;
                OnGenerationProgress?.Invoke(1f);
                OnGenerationComplete?.Invoke(cached);
                return;
            }

            IsGenerating = true;
            OnGenerationProgress?.Invoke(0f);

            Debug.Log($"[QuizGenerator] Generating {questionsPerGenre} questions for: {displayName}");

            // プライマリで試行 → 失敗時はフォールバック (Gemini) で再試行
            var primaryResult = await TryGenerateWithClient(client, buildPrompt, maxRetries, "primary");
            if (primaryResult.Success)
            {
                FinishSuccess(primaryResult.QuestionSet);
                return;
            }

            if (fallbackClient != null)
            {
                Debug.LogWarning($"[QuizGenerator] Primary generation failed, trying Gemini fallback: {primaryResult.ErrorMessage}");
                OnGenerationProgress?.Invoke(0.1f);
                var fallbackResult = await TryGenerateWithClient(fallbackClient, buildPrompt, Mathf.Min(3, maxRetries), "fallback(Gemini)");
                if (fallbackResult.Success)
                {
                    FinishSuccess(fallbackResult.QuestionSet);
                    return;
                }

                var combined = $"primary: {primaryResult.ErrorMessage} / fallback: {fallbackResult.ErrorMessage}";
                Debug.LogError($"[QuizGenerator] Both primary and fallback failed: {combined}");
                IsGenerating = false;
                OnGenerationFailed?.Invoke($"問題の生成に失敗しました: {fallbackResult.ErrorMessage}");
                return;
            }

            IsGenerating = false;
            OnGenerationFailed?.Invoke($"問題の生成に失敗しました: {primaryResult.ErrorMessage}");
        }

        private void FinishSuccess(QuizQuestionSet questionSet)
        {
            OnGenerationProgress?.Invoke(1f);
            Debug.Log($"[QuizGenerator] Successfully generated {questionSet.questions.Count} questions");
            LastGeneratedQuestions = questionSet;
            IsGenerating = false;
            OnGenerationComplete?.Invoke(questionSet);
        }

        private struct GenerationAttemptResult
        {
            public bool Success;
            public QuizQuestionSet QuestionSet;
            public string ErrorMessage;
        }

        private async Task<GenerationAttemptResult> TryGenerateWithClient(
            IQuizAIClient targetClient, Func<string> buildPrompt, int attempts, string label)
        {
            string lastError = "unknown";
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    OnGenerationProgress?.Invoke(0.15f);
                    var prompt = buildPrompt();
                    var responseJson = await targetClient.GenerateAsync(prompt, QuizSchema);

                    OnGenerationProgress?.Invoke(0.5f);
                    var questionSet = ParseResponse(responseJson);

                    if (questionSet == null || questionSet.questions == null || questionSet.questions.Count == 0)
                        throw new Exception("生成された問題が空です");

                    bool isLastAttempt = attempt >= attempts;
                    int minAcceptable = Mathf.Max(1, questionsPerGenre / 2);
                    if (questionSet.questions.Count < questionsPerGenre && !isLastAttempt)
                        throw new Exception($"問題数が規定未達 ({questionSet.questions.Count}/{questionsPerGenre}) - リトライ");
                    if (isLastAttempt && questionSet.questions.Count < minAcceptable)
                        throw new Exception($"問題数が最低ラインも未達: {questionSet.questions.Count}/{questionsPerGenre}");
                    if (questionSet.questions.Count < questionsPerGenre)
                        Debug.LogWarning($"[QuizGenerator] 問題数が期待値より少ない: {questionSet.questions.Count}/{questionsPerGenre} (最終試行のため続行)");

                    if (enableValidationPass)
                    {
                        OnGenerationProgress?.Invoke(0.55f);
                        Debug.Log("[QuizGenerator] Running validation pass...");
                        var validationPrompt = QuizPromptBuilder.BuildValidation(responseJson);
                        var validatedJson = await targetClient.GenerateAsync(validationPrompt, QuizSchema);
                        OnGenerationProgress?.Invoke(0.85f);
                        var validatedSet = ParseResponse(validatedJson);
                        if (validatedSet != null && validatedSet.questions != null && validatedSet.questions.Count > 0)
                            questionSet = validatedSet;
                        else
                            Debug.LogWarning("[QuizGenerator] Validation pass returned empty, using original.");
                    }

                    ConvertRawToChoices(questionSet);
                    SanitizeQuestions(questionSet);
                    ValidateQuestions(questionSet);
                    AssignDifficulty(questionSet);

                    return new GenerationAttemptResult { Success = true, QuestionSet = questionSet };
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    Debug.LogWarning($"[QuizGenerator][{label}] Attempt {attempt}/{attempts} failed: {e.Message}");
                    if (attempt < attempts)
                        await Task.Delay(1000);
                }
            }
            return new GenerationAttemptResult { Success = false, ErrorMessage = lastError };
        }

        /// <summary>
        /// Ollamaサーバーの接続確認。
        /// </summary>
        public async Task<bool> CheckConnection()
        {
            try
            {
                return await client.IsAvailableAsync();
            }
            catch
            {
                return false;
            }
        }

        private QuizQuestionSet ParseResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new Exception("レスポンスが空です");

            // 1) クリーンアップ: コードフェンス除去、前後の余計な文字除去
            string cleaned = CleanJson(json);

            // 2) 最初に直接パース
            var direct = TryParse(cleaned);
            if (direct != null && direct.questions != null && direct.questions.Count > 0)
                return direct;

            // 3) 単一オブジェクト {id,question,...} のみ返してきた場合: 配列に包む
            if (LooksLikeSingleQuestion(cleaned))
            {
                var wrapped = TryParse($"{{\"questions\":[{cleaned}]}}");
                if (wrapped != null && wrapped.questions != null && wrapped.questions.Count > 0)
                {
                    Debug.LogWarning("[QuizGenerator] Wrapped single-question response into array");
                    return wrapped;
                }
            }

            // 4) ルートが配列 [{...},{...}] の場合: questionsキーで包む
            string trimmed = cleaned.TrimStart();
            if (trimmed.StartsWith("["))
            {
                var wrapped = TryParse($"{{\"questions\":{cleaned}}}");
                if (wrapped != null && wrapped.questions != null && wrapped.questions.Count > 0)
                {
                    Debug.LogWarning("[QuizGenerator] Wrapped bare array response with questions key");
                    return wrapped;
                }
            }

            // 5) 文字列内から配列部分を抽出して再試行
            int arrStart = cleaned.IndexOf('[');
            int arrEnd = cleaned.LastIndexOf(']');
            if (arrStart >= 0 && arrEnd > arrStart)
            {
                string arrJson = cleaned.Substring(arrStart, arrEnd - arrStart + 1);
                var wrapped = TryParse($"{{\"questions\":{arrJson}}}");
                if (wrapped != null && wrapped.questions != null && wrapped.questions.Count > 0)
                {
                    Debug.LogWarning("[QuizGenerator] Extracted inner array from response");
                    return wrapped;
                }
            }

            // 6) 切断JSONを修復: 完全な { ... } オブジェクトだけを取り出して再構築
            var repaired = RepairTruncatedJson(cleaned);
            if (repaired != null)
            {
                var wrapped = TryParse(repaired);
                if (wrapped != null && wrapped.questions != null && wrapped.questions.Count > 0)
                {
                    Debug.LogWarning($"[QuizGenerator] Repaired truncated JSON ({wrapped.questions.Count} questions recovered)");
                    return wrapped;
                }
            }

            // 失敗: 生レスポンスをログに出して例外
            Debug.LogError($"[QuizGenerator] Failed to parse JSON. Raw response:\n{json}");
            throw new Exception("JSONのパースに失敗しました (構造不一致)");
        }

        /// <summary>
        /// 切断/破損JSONから完全な { ... } オブジェクトだけを抽出して questions 配列を再構築する。
        /// 中途半端な末尾要素は捨てる。
        /// </summary>
        private static string RepairTruncatedJson(string s)
        {
            int arrStart = s.IndexOf('[');
            if (arrStart < 0) return null;

            var validObjects = new List<string>();
            int i = arrStart + 1;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] != '{') i++;
                if (i >= s.Length) break;

                int objStart = i;
                int depth = 0;
                bool inString = false;
                bool escape = false;
                int objEnd = -1;

                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { objEnd = i; break; }
                    }
                }

                if (objEnd < 0) break; // 末尾が不完全
                validObjects.Add(s.Substring(objStart, objEnd - objStart + 1));
                i = objEnd + 1;
            }

            if (validObjects.Count == 0) return null;
            return "{\"questions\":[" + string.Join(",", validObjects) + "]}";
        }

        private static string CleanJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // ```json ... ``` のフェンス除去
            s = System.Text.RegularExpressions.Regex.Replace(s, @"```(?:json)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = s.Replace("```", "");
            // BOM/制御文字
            s = s.Trim('\uFEFF', ' ', '\n', '\r', '\t');
            return s;
        }

        private static QuizQuestionSet TryParse(string s)
        {
            try { return JsonUtility.FromJson<QuizQuestionSet>(s); }
            catch { return null; }
        }

        private static bool LooksLikeSingleQuestion(string s)
        {
            string t = s.TrimStart();
            return t.StartsWith("{") && t.Contains("\"question\"") && t.Contains("\"choices\"") && !t.Contains("\"questions\"");
        }

        private void ValidateQuestions(QuizQuestionSet questionSet)
        {
            // 不正な問題はスキップ（除外）して残りで続行する
            int removed = 0;
            for (int i = questionSet.questions.Count - 1; i >= 0; i--)
            {
                var q = questionSet.questions[i];

                if (string.IsNullOrWhiteSpace(q.question))
                {
                    questionSet.questions.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (q.choices == null || q.choices.Length != 4)
                {
                    Debug.LogWarning($"[QuizGenerator] 問題{i + 1}: 選択肢が4つではない({q.choices?.Length ?? 0})ので除外");
                    questionSet.questions.RemoveAt(i);
                    removed++;
                    continue;
                }

                // 空文字の選択肢があれば除外
                bool hasEmpty = false;
                for (int c = 0; c < q.choices.Length; c++)
                {
                    if (string.IsNullOrWhiteSpace(q.choices[c])) { hasEmpty = true; break; }
                }
                if (hasEmpty)
                {
                    questionSet.questions.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (q.correct_index < 0 || q.correct_index > 3)
                {
                    Debug.LogWarning($"[QuizGenerator] 問題{i + 1}: correct_index({q.correct_index})が範囲外、0にリセット");
                    q.correct_index = 0;
                }

                // 問題文に正解テキストが含まれている場合は除外（自己言及問題）
                var correctText = q.choices[q.correct_index];
                if (!string.IsNullOrWhiteSpace(correctText) && correctText.Length >= 2 &&
                    q.question != null && q.question.Contains(correctText))
                {
                    Debug.LogWarning($"[QuizGenerator] 問題{i + 1}を除外: 問題文に正解『{correctText}』が含まれている");
                    questionSet.questions.RemoveAt(i);
                    removed++;
                    continue;
                }
            }

            if (removed > 0)
                Debug.LogWarning($"[QuizGenerator] {removed}問を不正な構造のため除外しました（残り{questionSet.questions.Count}問）");

            if (questionSet.questions.Count == 0)
                throw new Exception("有効な問題が0件です");
        }

        /// <summary>
        /// 問題インデックスに基づいて難易度を割り当て。
        /// プロンプトで「簡単→普通→難しい」の順に生成するよう指示しているため
        /// インデックス順に Easy / Normal / Hard を付与する。
        /// </summary>
        private void AssignDifficulty(QuizQuestionSet questionSet)
        {
            int count = questionSet.questions.Count;
            int easy = Mathf.Max(1, Mathf.RoundToInt(count * 0.3f));
            int hard = Mathf.Max(1, Mathf.RoundToInt(count * 0.2f));
            for (int i = 0; i < count; i++)
            {
                if (i < easy)
                    questionSet.questions[i].difficulty = (int)QuizDifficulty.Easy;
                else if (i < count - hard)
                    questionSet.questions[i].difficulty = (int)QuizDifficulty.Normal;
                else
                    questionSet.questions[i].difficulty = (int)QuizDifficulty.Hard;
            }
        }

        /// <summary>
        /// LaTeX/マークダウン記法を除去してプレーンテキスト化。
        /// </summary>
        private void SanitizeQuestions(QuizQuestionSet questionSet)
        {
            foreach (var q in questionSet.questions)
            {
                q.question = SanitizeText(q.question);
                if (q.choices != null)
                {
                    for (int i = 0; i < q.choices.Length; i++)
                        q.choices[i] = SanitizeChoice(q.choices[i]);
                }
                q.explanation = SanitizeText(q.explanation);
            }
        }

        /// <summary>
        /// 選択肢専用のクリーンアップ。LLMが付加してくる括弧書き・補足・改行・前置きラベルを削ぐ。
        /// </summary>
        private static string SanitizeChoice(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // 共通サニタイズ ($、LaTeX、マークダウン)
            s = SanitizeText(s);
            // 改行があれば最初の行だけ採用
            int nlIdx = s.IndexOfAny(new[] { '\n', '\r' });
            if (nlIdx >= 0) s = s.Substring(0, nlIdx);
            // 半角/全角括弧 (...) （...） [...] 【...】 を中身ごと除去
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\(（][^\)）]*[\)）]", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\[【][^\]】]*[\]】]", "");
            // 区切り記号以降を捨てる (補足説明をくっつけてくるケース対策)
            //   例: "ハムレット → シェイクスピアの戯曲" / "パリ - フランスの首都"
            // 注: 単独 ":" や "/" は書籍名等で正当に使われるので対象にしない
            int cutAt = -1;
            string[] separators = { "→", "⇒", " - ", " — ", " – ", " ／ ", " | " };
            foreach (var sep in separators)
            {
                int idx = s.IndexOf(sep);
                if (idx > 0 && (cutAt < 0 || idx < cutAt)) cutAt = idx;
            }
            if (cutAt > 0) s = s.Substring(0, cutAt);
            // 全体を囲む引用符を除去
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^[「『""'](.*)[」』""']$", "$1");
            return s.Trim();
        }

        private static string SanitizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // LaTeX区切り $...$ や $$...$$ を除去（中身は残す）
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\$+", "");
            // \text{...} → 中身
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\text\{([^}]*)\}", "$1");
            // \mathrm{...} → 中身
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\mathrm\{([^}]*)\}", "$1");
            // _{...} や ^{...} を簡略化
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_\{([^}]*)\}", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\^\{([^}]*)\}", "$1");
            // _x や ^x の単一文字も
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[_^]([0-9a-zA-Z])", "$1");
            // マークダウンの **bold** や *italic*
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\*+([^*]+)\*+", "$1");
            // バックスラッシュコマンドを除去（残骸処理）
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[a-zA-Z]+", "");
            // 外国語文字を除去（ハングル、アラビア語、キリル文字等）
            s = StripForeignScript(s);
            return s.Trim();
        }

        /// <summary>
        /// 日本語+ASCII以外の文字を除去するホワイトリスト方式フィルタ。
        /// Gemmaが時々混入させるハングル・アラビア語・キリル文字等を一律排除する。
        /// </summary>
        private static string StripForeignScript(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 0x0020 && c <= 0x007E) { sb.Append(c); continue; } // ASCII
                if (c >= 0x3000 && c <= 0x303F) { sb.Append(c); continue; } // CJK記号・句読点
                if (c >= 0x3040 && c <= 0x309F) { sb.Append(c); continue; } // ひらがな
                if (c >= 0x30A0 && c <= 0x30FF) { sb.Append(c); continue; } // カタカナ
                if (c >= 0x4E00 && c <= 0x9FFF) { sb.Append(c); continue; } // CJK統合漢字
                if (c >= 0x3400 && c <= 0x4DBF) { sb.Append(c); continue; } // CJK拡張A
                if (c >= 0xF900 && c <= 0xFAFF) { sb.Append(c); continue; } // CJK互換漢字
                if (c >= 0xFF00 && c <= 0xFFEF) { sb.Append(c); continue; } // 全角英数・半角カナ
                if (c >= 0x2000 && c <= 0x206F) { sb.Append(c); continue; } // 一般句読点
                if (c >= 0x2190 && c <= 0x21FF) { sb.Append(c); continue; } // 矢印
                if (c >= 0x2500 && c <= 0x257F) { sb.Append(c); continue; } // 罫線
                if (c >= 0x25A0 && c <= 0x25FF) { sb.Append(c); continue; } // 幾何学図形
                if (c >= 0x00A0 && c <= 0x00FF) { sb.Append(c); continue; } // Latin-1補助 (ä,ö等)
                // ↑に該当しない文字は除去 (ハングル、アラビア、キリル、タイ等)
            }
            return sb.ToString();
        }

        /// <summary>
        /// 新フォーマット (answer + wrongs) から choices と correct_index を構築する。
        /// 4つを Fisher-Yates でシャッフルしつつ正解位置を記録する。
        /// すでに choices が4つ揃っている問題はそのままシャッフルだけ行う。
        /// </summary>
        private void ConvertRawToChoices(QuizQuestionSet questionSet)
        {
            for (int qi = questionSet.questions.Count - 1; qi >= 0; qi--)
            {
                var q = questionSet.questions[qi];

                string correctAnswer = null;
                List<string> options = null;

                // ケースA: answer + wrongs[3] が来ている (新フォーマット)
                if (!string.IsNullOrEmpty(q.answer) && q.wrongs != null && q.wrongs.Length >= 3)
                {
                    correctAnswer = q.answer;
                    options = new List<string> { q.answer, q.wrongs[0], q.wrongs[1], q.wrongs[2] };
                }
                // ケースB: 旧フォーマットの choices + correct_index
                else if (q.choices != null && q.choices.Length == 4 && q.correct_index >= 0 && q.correct_index < 4)
                {
                    correctAnswer = q.choices[q.correct_index];
                    options = new List<string>(q.choices);
                }
                else
                {
                    // どちらの形式でも揃っていない → 除外
                    questionSet.questions.RemoveAt(qi);
                    continue;
                }

                // Fisher-Yates シャッフル
                for (int i = options.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (options[i], options[j]) = (options[j], options[i]);
                }

                q.choices = options.ToArray();
                q.correct_index = options.IndexOf(correctAnswer);
                if (q.correct_index < 0) q.correct_index = 0;
            }
        }
    }
}
