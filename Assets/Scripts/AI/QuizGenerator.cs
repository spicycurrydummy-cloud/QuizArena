using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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
    /// 流れ: プライマリで生成 (リトライ付き) → 失敗時はフォールバック連鎖 → 後処理 (整形・検証・シャッフル)
    /// </summary>
    public class QuizGenerator : MonoBehaviour
    {
        public static QuizGenerator Instance { get; private set; }

        public const string DefaultInceptionModel = "mercury-3";

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

        [Header("Inception (Mercury) Settings")]
        [SerializeField] private string inceptionUrl = "https://api.inceptionlabs.ai/v1";
        [SerializeField] private string inceptionModel = DefaultInceptionModel;
        [Tooltip("instant / low / medium / high (Mercury 3)")]
        [SerializeField] private string inceptionReasoningEffort = "medium";

        // APIキーは Config.json から読み込む (AIConfig 参照)。Inspector値は空でなければ上書きとして使う。
        [SerializeField] private string inceptionApiKey = "";

        [Header("Fallback: Cerebras")]
        [SerializeField] private string cerebrasUrl = "https://api.cerebras.ai/v1";
        [SerializeField] private string cerebrasModel = "llama3.1-8b";
        [SerializeField] private string cerebrasApiKey = "";

        [Header("Fallback: Groq (Llama)")]
        [SerializeField] private string groqUrl = "https://api.groq.com/openai/v1";
        [SerializeField] private string groqModel = "llama-3.3-70b-versatile";
        [SerializeField] private string groqApiKey = "";

        [Header("Fallback: Gemini Flash")]
        [SerializeField] private string geminiModel = "gemini-2.5-flash-lite";
        [SerializeField] private string geminiApiKey = "";

        [Header("Common Settings")]
        [SerializeField] private float temperature = 0.4f;
        [SerializeField] private int questionsPerGenre = 5;
        public int QuestionsPerGenre
        {
            get => questionsPerGenre;
            set => questionsPerGenre = Mathf.Clamp(value, MinQuestionsPerGenre, MaxQuestionsPerGenre);
        }
        public const int MinQuestionsPerGenre = 1;
        public const int MaxQuestionsPerGenre = 20;

        [Tooltip("プライマリの試行回数。足りない分は次の試行で生成した問題と合算するので、多すぎると待ち時間だけ伸びる")]
        [SerializeField] private int maxRetries = 4;
        [SerializeField] private int fallbackRetries = 2;

        [Header("Validation")]
        [SerializeField] private bool enableValidationPass = false;

        private IQuizAIClient client;
        private string clientLabel = "primary";
        private readonly List<IQuizAIClient> fallbackClients = new();
        private readonly List<string> fallbackLabels = new();
        private readonly System.Random rng = new();

        // Structured Output 用の JSON Schema (対応プロバイダーではこの形に強制される)
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
        private readonly HashSet<int> preGeneratingGenres = new();

        public event Action<float> OnGenerationProgress;
        public event Action<QuizQuestionSet> OnGenerationComplete;
        public event Action<string> OnGenerationFailed;

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
        }

        private void RebuildClient()
        {
            var config = AIConfig.Load();
            string inceptionKey = FirstNonEmpty(inceptionApiKey, config.inception_api_key);
            string geminiKey = FirstNonEmpty(geminiApiKey, config.gemini_api_key);
            string cerebrasKey = FirstNonEmpty(cerebrasApiKey, config.cerebras_api_key);
            string groqKey = FirstNonEmpty(groqApiKey, config.groq_api_key);

            switch (aiProvider)
            {
                case AIProvider.Inception:
                    if (string.IsNullOrEmpty(inceptionKey))
                        Debug.LogError("[QuizGenerator] Inception API key not found. Put it in UserSettings/Config.json (editor) or Resources/Config.json (build).");
                    var model = string.IsNullOrWhiteSpace(inceptionModel) ? DefaultInceptionModel : inceptionModel.Trim();
                    // reasoning トークンも max_tokens に含まれるので余裕を持たせる (Mercury 3 は最大 131k 出力可)
                    int maxTokens = inceptionReasoningEffort == "high" ? 8192 : 6144;
                    client = new OpenAIChatClient(inceptionKey, inceptionUrl, model, temperature, maxTokens,
                        "Inception", inceptionReasoningEffort, supportsJsonSchema: true);
                    clientLabel = $"Inception({model})";
                    Debug.Log($"[QuizGenerator] Using Inception (model: {model}, reasoning: {inceptionReasoningEffort})");
                    break;
                case AIProvider.Gemini:
                    if (string.IsNullOrEmpty(geminiKey))
                        Debug.LogError("[QuizGenerator] Gemini API key not found. Put it in UserSettings/Config.json (editor) or Resources/Config.json (build).");
                    client = new GeminiClient(geminiKey, geminiModel, temperature);
                    clientLabel = $"Gemini({geminiModel})";
                    Debug.Log($"[QuizGenerator] Using Gemini (model: {geminiModel})");
                    break;
                default:
                    client = new OllamaClient(ollamaUrl, ollamaModel, temperature);
                    clientLabel = $"Ollama({ollamaModel})";
                    Debug.Log($"[QuizGenerator] Using Ollama (model: {ollamaModel})");
                    break;
            }

            // フォールバック連鎖: Cerebras → Groq → Gemini Flash (プライマリと同じものは除く)
            fallbackClients.Clear();
            fallbackLabels.Clear();

            if (!string.IsNullOrEmpty(cerebrasKey))
            {
                fallbackClients.Add(new OpenAIChatClient(cerebrasKey, cerebrasUrl, cerebrasModel, temperature, 4096, "Cerebras"));
                fallbackLabels.Add($"Cerebras({cerebrasModel})");
            }
            if (!string.IsNullOrEmpty(groqKey))
            {
                fallbackClients.Add(new OpenAIChatClient(groqKey, groqUrl, groqModel, temperature, 4096, "Groq"));
                fallbackLabels.Add($"Groq({groqModel})");
            }
            if (aiProvider != AIProvider.Gemini && !string.IsNullOrEmpty(geminiKey))
            {
                fallbackClients.Add(new GeminiClient(geminiKey, geminiModel, temperature));
                fallbackLabels.Add($"Gemini({geminiModel})");
            }

            Debug.Log(fallbackClients.Count > 0
                ? $"[QuizGenerator] Fallback chain armed: {string.Join(" → ", fallbackLabels)}"
                : "[QuizGenerator] No fallback clients configured.");
        }

        private static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        /// <summary>
        /// 事前生成キャッシュにある問題を取り出す (取り出した時点でキャッシュから消える)。なければnull。
        /// </summary>
        public QuizQuestionSet TakeCachedQuestions(int encodedGenre)
        {
            if (preGeneratedCache.Remove(encodedGenre, out var set))
            {
                Debug.Log($"[QuizGenerator] Took cached questions for {GenreEncoding.GetDisplayName(encodedGenre)}");
                return set;
            }
            return null;
        }

        /// <summary>
        /// 指定ジャンルの問題をバックグラウンドで事前生成する。すでにキャッシュ/生成中なら何もしない。
        /// </summary>
        public async void PreGenerateAsync(QuizGenre genre, int subGenreIndex = 0)
        {
            int encoded = GenreEncoding.Encode((int)genre, subGenreIndex);
            if (preGeneratedCache.ContainsKey(encoded) || preGeneratingGenres.Contains(encoded) || IsGenerating) return;

            preGeneratingGenres.Add(encoded);
            var displayName = GenreEncoding.GetDisplayName((int)genre, subGenreIndex);
            Debug.Log($"[QuizGenerator] Pre-generating questions for {displayName}");

            try
            {
                var prompt = QuizPromptBuilder.Build(genre, subGenreIndex, questionsPerGenre);
                var responseJson = await client.GenerateAsync(prompt, QuizSchema);
                var questionSet = ParseResponse(responseJson);
                PostProcess(questionSet);
                preGeneratedCache[encoded] = questionSet;
                Debug.Log($"[QuizGenerator] Pre-generation complete for {displayName} ({questionSet.questions.Count}問)");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator] Pre-generation failed for {displayName}: {e.Message}");
            }
            finally
            {
                preGeneratingGenres.Remove(encoded);
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
                FinishSuccess(cached);
                return;
            }

            IsGenerating = true;
            OnGenerationProgress?.Invoke(0f);
            Debug.Log($"[QuizGenerator] Generating {questionsPerGenre} questions for: {displayName}");

            var result = await TryGenerateWithClient(client, buildPrompt, maxRetries, clientLabel);
            string lastError = result.ErrorMessage;

            for (int i = 0; !result.Success && i < fallbackClients.Count; i++)
            {
                var label = fallbackLabels[i];
                Debug.LogWarning($"[QuizGenerator] Trying fallback {i + 1}/{fallbackClients.Count}: {label} (last error: {lastError})");
                OnGenerationProgress?.Invoke(0.1f);
                result = await TryGenerateWithClient(fallbackClients[i], buildPrompt, fallbackRetries, label);
                if (!result.Success) lastError = result.ErrorMessage;
            }

            if (result.Success)
            {
                FinishSuccess(result.QuestionSet);
                return;
            }

            Debug.LogError($"[QuizGenerator] All providers failed. Last error: {lastError}");
            IsGenerating = false;
            OnGenerationFailed?.Invoke($"問題の生成に失敗しました: {lastError}");
        }

        private void FinishSuccess(QuizQuestionSet questionSet)
        {
            LastGeneratedQuestions = questionSet;
            IsGenerating = false;
            OnGenerationProgress?.Invoke(1f);
            Debug.Log($"[QuizGenerator] Successfully generated {questionSet.questions.Count} questions");
            OnGenerationComplete?.Invoke(questionSet);
        }

        private struct GenerationAttemptResult
        {
            public bool Success;
            public QuizQuestionSet QuestionSet;
            public string ErrorMessage;
        }

        /// <summary>
        /// 1クライアントで最大 attempts 回生成を試みる。
        /// 問題数が足りない応答は捨てずに蓄積し、合計が規定数に達した時点で成功とする
        /// (LLMが 3/5 問しか返さないケースで丸ごと捨てて再生成するより速くて安い)。
        /// </summary>
        private async Task<GenerationAttemptResult> TryGenerateWithClient(
            IQuizAIClient targetClient, Func<string> buildPrompt, int attempts, string label)
        {
            string lastError = "unknown";
            var accumulated = new QuizQuestionSet { questions = new List<QuizQuestion>() };
            var seenQuestions = new HashSet<string>();
            int minAcceptable = Mathf.Max(1, questionsPerGenre / 2);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    OnGenerationProgress?.Invoke(0.15f + 0.35f * accumulated.questions.Count / Mathf.Max(1, questionsPerGenre));
                    var responseJson = await targetClient.GenerateAsync(buildPrompt(), QuizSchema);
                    var questionSet = ParseResponse(responseJson);

                    if (enableValidationPass)
                    {
                        Debug.Log("[QuizGenerator] Running validation pass...");
                        var validatedJson = await targetClient.GenerateAsync(QuizPromptBuilder.BuildValidation(responseJson), QuizSchema);
                        try
                        {
                            questionSet = ParseResponse(validatedJson);
                        }
                        catch (Exception ve)
                        {
                            Debug.LogWarning($"[QuizGenerator] Validation pass unusable, using original: {ve.Message}");
                        }
                    }

                    // 同じ問題文は重複として捨てつつ蓄積
                    int added = 0;
                    foreach (var q in questionSet.questions)
                    {
                        if (q == null || string.IsNullOrWhiteSpace(q.question)) continue;
                        if (!seenQuestions.Add(q.question.Trim())) continue;
                        accumulated.questions.Add(q);
                        added++;
                        if (accumulated.questions.Count >= questionsPerGenre) break;
                    }

                    int count = accumulated.questions.Count;
                    if (count >= questionsPerGenre)
                        break;

                    lastError = $"問題数が規定未達 ({count}/{questionsPerGenre})";
                    Debug.LogWarning($"[QuizGenerator][{label}] Attempt {attempt}/{attempts}: +{added}問 (累計 {count}/{questionsPerGenre})" +
                                     (attempt < attempts ? " - 不足分を追加生成" : ""));
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    Debug.LogWarning($"[QuizGenerator][{label}] Attempt {attempt}/{attempts} failed: {e.Message}");

                    // レート制限/クォータ/認証エラーはリトライしても通らないので即フォールバックへ
                    if (IsNonRetryableError(e.Message))
                    {
                        Debug.LogWarning($"[QuizGenerator][{label}] Non-retryable error detected, skipping remaining retries.");
                        break;
                    }
                }

                if (attempt < attempts)
                    await Task.Delay(1000);
            }

            if (accumulated.questions.Count < minAcceptable)
                return new GenerationAttemptResult { Success = false, ErrorMessage = lastError };

            if (accumulated.questions.Count < questionsPerGenre)
                Debug.LogWarning($"[QuizGenerator][{label}] 問題数が期待値より少ないまま続行: {accumulated.questions.Count}/{questionsPerGenre}");

            try
            {
                OnGenerationProgress?.Invoke(0.6f);
                PostProcess(accumulated);
                return new GenerationAttemptResult { Success = true, QuestionSet = accumulated };
            }
            catch (Exception e)
            {
                return new GenerationAttemptResult { Success = false, ErrorMessage = e.Message };
            }
        }

        private static bool IsNonRetryableError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var m = message.ToLowerInvariant();
            // HTTPステータス (AIHttp が "HTTP 429" の形で埋め込む)
            if (m.Contains("http 401") || m.Contains("http 402") || m.Contains("http 403") || m.Contains("http 429")) return true;
            // よくあるキーワード
            string[] keywords =
            {
                "rate limit", "rate_limit", "quota", "payment required", "payment_required", "billing", "exceeded",
                "unauthorized", "invalid api key", "invalid_api_key", "authentication"
            };
            foreach (var k in keywords)
                if (m.Contains(k)) return true;
            if (m.Contains("insufficient") && (m.Contains("credit") || m.Contains("balance"))) return true;
            return false;
        }

        // ===== 後処理 =====

        /// <summary>
        /// 生の生成結果を出題可能な形に整える: 選択肢構築+シャッフル → テキスト整形 → 検証 → 難易度付与。
        /// 有効な問題が0件なら例外。
        /// </summary>
        private void PostProcess(QuizQuestionSet questionSet)
        {
            ConvertRawToChoices(questionSet);
            SanitizeQuestions(questionSet);
            ValidateQuestions(questionSet);
            AssignDifficulty(questionSet);
        }

        private QuizQuestionSet ParseResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new Exception("レスポンスが空です");

            string cleaned = CleanJson(json);

            // 1) そのままパース
            if (TryParseSet(cleaned, out var direct)) return direct;

            // 2) 単一問題オブジェクトだけ返ってきた場合: 配列に包む
            if (LooksLikeSingleQuestion(cleaned) && TryParseSet($"{{\"questions\":[{cleaned}]}}", out var single))
            {
                Debug.LogWarning("[QuizGenerator] Wrapped single-question response into array");
                return single;
            }

            // 3) ルートが配列の場合: questionsキーで包む
            if (cleaned.TrimStart().StartsWith("[") && TryParseSet($"{{\"questions\":{cleaned}}}", out var bare))
            {
                Debug.LogWarning("[QuizGenerator] Wrapped bare array response with questions key");
                return bare;
            }

            // 4) 文字列内から配列部分を抽出して再試行
            int arrStart = cleaned.IndexOf('[');
            int arrEnd = cleaned.LastIndexOf(']');
            if (arrStart >= 0 && arrEnd > arrStart &&
                TryParseSet($"{{\"questions\":{cleaned.Substring(arrStart, arrEnd - arrStart + 1)}}}", out var inner))
            {
                Debug.LogWarning("[QuizGenerator] Extracted inner array from response");
                return inner;
            }

            // 5) 切断JSONを修復: 完全な { ... } オブジェクトだけを取り出して再構築
            var repaired = RepairTruncatedJson(cleaned);
            if (repaired != null && TryParseSet(repaired, out var fixedSet))
            {
                Debug.LogWarning($"[QuizGenerator] Repaired truncated JSON ({fixedSet.questions.Count} questions recovered)");
                return fixedSet;
            }

            Debug.LogError($"[QuizGenerator] Failed to parse JSON. Raw response:\n{json}");
            throw new Exception("JSONのパースに失敗しました (構造不一致)");
        }

        private static bool TryParseSet(string s, out QuizQuestionSet set)
        {
            try
            {
                set = JsonUtility.FromJson<QuizQuestionSet>(s);
            }
            catch
            {
                set = null;
            }
            return set?.questions != null && set.questions.Count > 0;
        }

        /// <summary>
        /// 切断/破損JSONから完全な { ... } オブジェクトだけを抽出して questions 配列を再構築する。
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
            s = Regex.Replace(s, @"```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            s = s.Replace("```", "");
            return s.Trim('\uFEFF', ' ', '\n', '\r', '\t');
        }

        private static bool LooksLikeSingleQuestion(string s)
        {
            string t = s.TrimStart();
            return t.StartsWith("{") && t.Contains("\"question\"") && !t.Contains("\"questions\"")
                && (t.Contains("\"answer\"") || t.Contains("\"choices\""));
        }

        private static void ValidateQuestions(QuizQuestionSet questionSet)
        {
            int removed = 0;
            var seen = new HashSet<string>();
            for (int i = questionSet.questions.Count - 1; i >= 0; i--)
            {
                var q = questionSet.questions[i];
                string reason = GetRejectReason(q, seen);
                if (reason != null)
                {
                    Debug.LogWarning($"[QuizGenerator] 問題{i + 1}を除外: {reason}");
                    questionSet.questions.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
                Debug.LogWarning($"[QuizGenerator] {removed}問を除外しました（残り{questionSet.questions.Count}問）");

            if (questionSet.questions.Count == 0)
                throw new Exception("有効な問題が0件です");
        }

        /// <summary>
        /// 問題が出題に耐えなければ理由を返す。問題なければ null。
        /// </summary>
        private static string GetRejectReason(QuizQuestion q, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(q.question)) return "問題文が空";
            if (q.choices == null || q.choices.Length != 4) return $"選択肢が4つではない({q.choices?.Length ?? 0})";
            if (q.correct_index < 0 || q.correct_index > 3) return $"correct_index({q.correct_index})が範囲外";

            seen.Clear();
            foreach (var c in q.choices)
            {
                if (string.IsNullOrWhiteSpace(c)) return "空の選択肢がある";
                if (!seen.Add(c.Trim().ToLowerInvariant())) return $"選択肢が重複している『{c}』";
            }

            // 問題文に正解テキストが含まれている (自己言及問題)
            var correctText = q.choices[q.correct_index];
            if (correctText.Length >= 2 && q.question.Contains(correctText))
                return $"問題文に正解『{correctText}』が含まれている";

            return null;
        }

        /// <summary>
        /// プロンプトで「簡単→普通→難しい」順に生成させているので、インデックス順に難易度を付与する。
        /// </summary>
        private static void AssignDifficulty(QuizQuestionSet questionSet)
        {
            int count = questionSet.questions.Count;
            int easy = Mathf.Max(1, Mathf.RoundToInt(count * 0.3f));
            int hard = Mathf.Max(1, Mathf.RoundToInt(count * 0.2f));
            for (int i = 0; i < count; i++)
            {
                questionSet.questions[i].difficulty =
                    i < easy ? (int)QuizDifficulty.Easy :
                    i < count - hard ? (int)QuizDifficulty.Normal :
                    (int)QuizDifficulty.Hard;
            }
        }

        private static void SanitizeQuestions(QuizQuestionSet questionSet)
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

        private static readonly string[] ChoiceSeparators = { "→", "⇒", " - ", " — ", " – ", " ／ ", " | " };

        /// <summary>
        /// 選択肢専用のクリーンアップ。LLMが付加してくる括弧書き・補足・改行を削ぐ。
        /// </summary>
        private static string SanitizeChoice(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = SanitizeText(s);
            // 改行があれば最初の行だけ採用
            int nlIdx = s.IndexOfAny(new[] { '\n', '\r' });
            if (nlIdx >= 0) s = s.Substring(0, nlIdx);
            // 括弧書きを中身ごと除去
            s = Regex.Replace(s, @"\s*[\(（][^\)）]*[\)）]", "");
            s = Regex.Replace(s, @"\s*[\[【][^\]】]*[\]】]", "");
            // 区切り記号以降の補足説明を捨てる (例: "ハムレット → シェイクスピアの戯曲")
            int cutAt = -1;
            foreach (var sep in ChoiceSeparators)
            {
                int idx = s.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && (cutAt < 0 || idx < cutAt)) cutAt = idx;
            }
            if (cutAt > 0) s = s.Substring(0, cutAt);
            // 全体を囲む引用符を除去
            s = Regex.Replace(s, @"^[「『""'](.*)[」』""']$", "$1");
            return s.Trim();
        }

        private static string SanitizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // LaTeX区切り $...$ / \text{...} / \mathrm{...} / 添字
            s = Regex.Replace(s, @"\$+", "");
            s = Regex.Replace(s, @"\\(?:text|mathrm)\{([^}]*)\}", "$1");
            s = Regex.Replace(s, @"[_^]\{([^}]*)\}", "$1");
            s = Regex.Replace(s, @"[_^]([0-9a-zA-Z])", "$1");
            // マークダウンの **bold** / *italic*
            s = Regex.Replace(s, @"\*+([^*]+)\*+", "$1");
            // 残ったバックスラッシュコマンド
            s = Regex.Replace(s, @"\\[a-zA-Z]+", "");
            // 日本語+ASCII以外の文字を除去
            s = StripForeignScript(s);
            // uGUIのリッチテキストタグと誤認されないよう < > は全角に
            s = s.Replace('<', '＜').Replace('>', '＞');
            return s.Trim();
        }

        /// <summary>
        /// 日本語で使う文字以外を除去するホワイトリスト方式フィルタ。
        /// LLMが時々混入させるハングル・アラビア語・キリル文字等を排除する。
        /// </summary>
        private static string StripForeignScript(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (IsAllowedChar(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsAllowedChar(char c)
        {
            return (c >= 0x0020 && c <= 0x007E)   // ASCII
                || (c >= 0x00A0 && c <= 0x00FF)   // Latin-1補助 (ä, ö, ° など)
                || (c >= 0x0370 && c <= 0x03FF)   // ギリシャ文字 (α線, πなど理系問題で使う)
                || (c >= 0x2000 && c <= 0x206F)   // 一般句読点
                || (c >= 0x2100 && c <= 0x214F)   // 文字様記号 (℃, № など)
                || (c >= 0x2150 && c <= 0x218F)   // ローマ数字
                || (c >= 0x2190 && c <= 0x21FF)   // 矢印
                || (c >= 0x2200 && c <= 0x22FF)   // 数学記号 (√, ∞, ≠ など)
                || (c >= 0x2460 && c <= 0x24FF)   // 囲み英数字 (①②③)
                || (c >= 0x2500 && c <= 0x257F)   // 罫線
                || (c >= 0x25A0 && c <= 0x25FF)   // 幾何学図形
                || (c >= 0x3000 && c <= 0x303F)   // CJK記号・句読点
                || (c >= 0x3040 && c <= 0x309F)   // ひらがな
                || (c >= 0x30A0 && c <= 0x30FF)   // カタカナ
                || (c >= 0x3300 && c <= 0x33FF)   // CJK互換 (㎡, ㎞ など)
                || (c >= 0x3400 && c <= 0x4DBF)   // CJK拡張A
                || (c >= 0x4E00 && c <= 0x9FFF)   // CJK統合漢字
                || (c >= 0xF900 && c <= 0xFAFF)   // CJK互換漢字
                || (c >= 0xFF00 && c <= 0xFFEF);  // 全角英数・半角カナ
        }

        /// <summary>
        /// answer + wrongs[3] (または旧形式の choices + correct_index) から
        /// 4択をシャッフルして choices / correct_index を構築する。どちらの形式にも合わない問題は除外。
        /// </summary>
        private void ConvertRawToChoices(QuizQuestionSet questionSet)
        {
            for (int qi = questionSet.questions.Count - 1; qi >= 0; qi--)
            {
                var q = questionSet.questions[qi];

                string correctAnswer;
                List<string> options;

                if (!string.IsNullOrEmpty(q.answer) && q.wrongs != null && q.wrongs.Length >= 3)
                {
                    correctAnswer = q.answer;
                    options = new List<string> { q.answer, q.wrongs[0], q.wrongs[1], q.wrongs[2] };
                }
                else if (q.choices != null && q.choices.Length == 4 && q.correct_index >= 0 && q.correct_index < 4)
                {
                    correctAnswer = q.choices[q.correct_index];
                    options = new List<string>(q.choices);
                }
                else
                {
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
                q.correct_index = Mathf.Max(0, options.IndexOf(correctAnswer));
            }
        }
    }
}
