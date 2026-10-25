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
        [Tooltip("instant / low / medium / high (Mercury 3)。medium 以上は推論だけで数千トークン使うので low 推奨")]
        [SerializeField] private string inceptionReasoningEffort = "low";

        // APIキーは Config.json から読み込む (AIConfig 参照)。Inspector値は空でなければ上書きとして使う。
        [SerializeField] private string inceptionApiKey = "";

        [Header("Fallback: Cerebras")]
        [SerializeField] private string cerebrasUrl = "https://api.cerebras.ai/v1";
        [SerializeField] private string cerebrasModel = "gpt-oss-120b";
        [SerializeField] private string cerebrasApiKey = "";

        [Header("Fallback: Groq")]
        [SerializeField] private string groqUrl = "https://api.groq.com/openai/v1";
        [SerializeField] private string groqModel = "openai/gpt-oss-120b";
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

        [Header("Wikipedia Material (無料・キー不要)")]
        [Tooltip("題材になる記事の冒頭要約を日本語版Wikipediaから取得してプロンプトに添える (出題の多様性と正確さを上げる)")]
        [SerializeField] private bool useWikipediaMaterial = true;
        [Tooltip("1ラウンドで集める記事数 (Wikipedia へのリクエストは記事数に関わらず 1〜2 回)")]
        [Range(3, 20)] [SerializeField] private int materialTopicCount = 8;
        [Tooltip("1記事あたりの要約文字数")]
        [Range(100, 1200)] [SerializeField] private int materialMaxChars = 400;
        [Tooltip("題材タイトルの列挙に使う reasoning_effort (Mercury)。instant が最速")]
        [SerializeField] private string materialReasoningEffort = "instant";

        [Header("Answer Verification (無料枠のみで運用)")]
        [Tooltip("生成した問題の正解を Gemini の Google検索グラウンディングで裏取りする (Gemini キーが必要)。" +
                 "gemini-2.5-flash-lite の無料枠はこのキーで 1日20リクエストなので、上限に当たったら自動で Wikipedia 検証に切り替わる")]
        [SerializeField] private bool verifyWithSearch = true;
        [Tooltip("Gemini が使えないとき、Wikipedia 検索の根拠文を生成モデルに渡して判定させる (キー不要・上限なし)。根拠に反する問題だけ除外する")]
        [SerializeField] private bool verifyWithWikipedia = true;
        [Tooltip("1問あたりに集める根拠記事数 (Wikipedia へ問題数ぶんのリクエスト)")]
        [Range(1, 5)] [SerializeField] private int evidencePerQuestion = 3;

        private IQuizAIClient client;
        private string clientLabel = "primary";
        private readonly List<IQuizAIClient> fallbackClients = new();
        private readonly List<string> fallbackLabels = new();
        private readonly System.Random rng = new();

        // 題材タイトル列挙用の軽量クライアント (温度高め・短い出力)
        private IQuizAIClient materialClient;
        // Wikipedia 根拠文による判定用 (温度低め)
        private IQuizAIClient judgeClient;
        // 正解の裏取り用 (Google検索グラウンディングは Gemini 専用)
        private GeminiClient verifier;
        private float verifierPausedUntil = -1f;
        private const float VerifierPauseSec = 600f;
        private const int EvidenceMaxChars = 500;

        // 同じ記事を題材に使い回さないための履歴 (直近 UsedTitleMemory 件)
        private readonly Queue<string> usedTitles = new();
        private readonly HashSet<string> usedTitleSet = new();
        private const int UsedTitleMemory = 80;

        private delegate string PromptFactory(string material, IList<string> avoidQuestions);

        private const string TitlesSchema =
            "{\"type\":\"object\",\"properties\":{\"titles\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"titles\"]}";

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

        /// <summary>生成中の工程 (題材収集 / 生成 / 検証) を表す短い文。ローディング画面の補足表示用。</summary>
        public string StatusText { get; private set; } = "";
        public event Action<string> OnStatusChanged;

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
            var inceptionKeys = ApiKeyRing.Of(inceptionApiKey, config.InceptionKeys);
            string geminiKey = FirstNonEmpty(geminiApiKey, config.gemini_api_key);
            string cerebrasKey = FirstNonEmpty(cerebrasApiKey, config.cerebras_api_key);
            string groqKey = FirstNonEmpty(groqApiKey, config.groq_api_key);

            switch (aiProvider)
            {
                case AIProvider.Inception:
                    if (inceptionKeys.IsEmpty)
                        Debug.LogError("[QuizGenerator] Inception API key not found. Put it in UserSettings/Config.json (editor) or Resources/Config.json (build).");
                    var model = string.IsNullOrWhiteSpace(inceptionModel) ? DefaultInceptionModel : inceptionModel.Trim();
                    // reasoning トークンも max_tokens に含まれる (実測: low で 2.4k〜7.2k、medium は 8k 超) ので、切れて壊れた出力にならないよう effort ごとに大きめに取る
                    int maxTokens = InceptionMaxTokens(inceptionReasoningEffort);
                    // Mercury 3 は json_schema(strict) だと推論が 8k トークンまで肥大化して壊れた配列を返す (実測 2/3 失敗) ので json_object を使う
                    client = new OpenAIChatClient(inceptionKeys, inceptionUrl, model, temperature, maxTokens,
                        "Inception", inceptionReasoningEffort, supportsJsonSchema: false);
                    materialClient = new OpenAIChatClient(inceptionKeys, inceptionUrl, model, 0.9f, 1024,
                        "Inception", materialReasoningEffort, supportsJsonSchema: false);
                    judgeClient = new OpenAIChatClient(inceptionKeys, inceptionUrl, model, 0.1f, InceptionMaxTokens("low"),
                        "Inception", "low", supportsJsonSchema: false);
                    clientLabel = $"Inception({model})";
                    Debug.Log($"[QuizGenerator] Using Inception (model: {model}, reasoning: {inceptionReasoningEffort}, keys: {inceptionKeys.Count})");
                    break;
                case AIProvider.Gemini:
                    if (string.IsNullOrEmpty(geminiKey))
                        Debug.LogError("[QuizGenerator] Gemini API key not found. Put it in UserSettings/Config.json (editor) or Resources/Config.json (build).");
                    client = new GeminiClient(geminiKey, geminiModel, temperature);
                    materialClient = new GeminiClient(geminiKey, geminiModel, 0.9f);
                    judgeClient = new GeminiClient(geminiKey, geminiModel, 0.1f);
                    clientLabel = $"Gemini({geminiModel})";
                    Debug.Log($"[QuizGenerator] Using Gemini (model: {geminiModel})");
                    break;
                default:
                    client = new OllamaClient(ollamaUrl, ollamaModel, temperature);
                    materialClient = new OllamaClient(ollamaUrl, ollamaModel, 0.9f);
                    judgeClient = new OllamaClient(ollamaUrl, ollamaModel, 0.1f);
                    clientLabel = $"Ollama({ollamaModel})";
                    Debug.Log($"[QuizGenerator] Using Ollama (model: {ollamaModel})");
                    break;
            }

            // フォールバック連鎖: Cerebras → Groq → Gemini Flash (プライマリと同じものは除く)
            fallbackClients.Clear();
            fallbackLabels.Clear();

            // gpt-oss 系は reasoning モデルなので推論は low に抑え、出力枠は推論込みで確保する
            if (!string.IsNullOrEmpty(cerebrasKey))
            {
                fallbackClients.Add(new OpenAIChatClient(cerebrasKey, cerebrasUrl, cerebrasModel, temperature, 8192, "Cerebras", "low"));
                fallbackLabels.Add($"Cerebras({cerebrasModel})");
            }
            if (!string.IsNullOrEmpty(groqKey))
            {
                fallbackClients.Add(new OpenAIChatClient(groqKey, groqUrl, groqModel, temperature, 8192, "Groq", "low"));
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

            // 正解の裏取り (Google検索グラウンディング) は Gemini 専用機能なので Gemini キーがあるときだけ有効
            verifier = string.IsNullOrEmpty(geminiKey) ? null : new GeminiClient(geminiKey, geminiModel, 0.1f);
            if (verifyWithSearch && verifier == null)
                Debug.LogWarning("[QuizGenerator] verifyWithSearch is on but no Gemini API key is configured: falling back to Wikipedia verification.");
            Debug.Log($"[QuizGenerator] Wikipedia material: {(useWikipediaMaterial ? "on" : "off")}, " +
                      $"search verification: {(verifyWithSearch && verifier != null ? geminiModel : "off")}, " +
                      $"wikipedia verification: {(verifyWithWikipedia ? "on" : "off")}");
        }

        private static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        private static int InceptionMaxTokens(string effort) => (effort ?? "").Trim().ToLowerInvariant() switch
        {
            "instant" => 4096,
            "low" => 16384,
            "medium" => 24576,
            _ => 32768
        };

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
                // 本番と同じパイプライン (題材収集 → 生成 → 検証) だが、進捗/状態は表示しない
                string material = await GatherMaterialAsync(
                    QuizPromptBuilder.DescribeScope(genre, subGenreIndex),
                    QuizPromptBuilder.SearchKeyword(genre, subGenreIndex), quiet: true);
                var result = await TryGenerateWithClient(client, material,
                    (m, avoid) => QuizPromptBuilder.Build(genre, subGenreIndex, questionsPerGenre, m, avoid),
                    2, clientLabel, quiet: true);
                if (result.Success)
                {
                    preGeneratedCache[encoded] = result.QuestionSet;
                    Debug.Log($"[QuizGenerator] Pre-generation complete for {displayName} ({result.QuestionSet.questions.Count}問)");
                }
                else
                {
                    Debug.LogWarning($"[QuizGenerator] Pre-generation failed for {displayName}: {result.ErrorMessage}");
                }
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
                QuizPromptBuilder.DescribeScope(genre, subGenreIndex),
                QuizPromptBuilder.SearchKeyword(genre, subGenreIndex),
                (material, avoid) => QuizPromptBuilder.Build(genre, subGenreIndex, questionsPerGenre, material, avoid));
        }

        /// <summary>
        /// カスタムジャンルのクイズ問題を生成する。
        /// </summary>
        public void GenerateQuestions(string customGenreName)
        {
            int encoded = GenreEncoding.CUSTOM_GENRE_CODE * 100;
            GenerateQuestionsInternal(encoded, customGenreName,
                $"the topic \"{customGenreName}\"", customGenreName,
                (material, avoid) => QuizPromptBuilder.BuildCustom(customGenreName, questionsPerGenre, material, avoid));
        }

        private async void GenerateQuestionsInternal(int encodedGenre, string displayName,
            string scopeDescription, string searchKeyword, PromptFactory buildPrompt)
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

            // 題材は全プロバイダーで共用 (失敗しても null で続行)
            string material = await GatherMaterialAsync(scopeDescription, searchKeyword, quiet: false);
            OnGenerationProgress?.Invoke(0.1f);

            var result = await TryGenerateWithClient(client, material, buildPrompt, maxRetries, clientLabel);
            string lastError = result.ErrorMessage;

            for (int i = 0; !result.Success && i < fallbackClients.Count; i++)
            {
                var label = fallbackLabels[i];
                Debug.LogWarning($"[QuizGenerator] Trying fallback {i + 1}/{fallbackClients.Count}: {label} (last error: {lastError})");
                OnGenerationProgress?.Invoke(0.1f);
                result = await TryGenerateWithClient(fallbackClients[i], material, buildPrompt, fallbackRetries, label);
                if (!result.Success) lastError = result.ErrorMessage;
            }

            if (result.Success)
            {
                FinishSuccess(result.QuestionSet);
                return;
            }

            Debug.LogError($"[QuizGenerator] All providers failed. Last error: {lastError}");
            IsGenerating = false;
            SetStatus("");
            OnGenerationFailed?.Invoke($"問題の生成に失敗しました: {lastError}");
        }

        private void FinishSuccess(QuizQuestionSet questionSet)
        {
            LastGeneratedQuestions = questionSet;
            IsGenerating = false;
            SetStatus("");
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
        /// 各試行の結果は Google検索で裏取りしてから蓄積するので、検証で落ちた分も次の試行で補充される。
        /// quiet=true (事前生成) のときは進捗イベント/状態文を出さない。
        /// </summary>
        private async Task<GenerationAttemptResult> TryGenerateWithClient(
            IQuizAIClient targetClient, string material, PromptFactory buildPrompt, int attempts, string label,
            bool quiet = false)
        {
            string lastError = "unknown";
            var accumulated = new QuizQuestionSet { questions = new List<QuizQuestion>() };
            var seenQuestions = new HashSet<string>();
            int minAcceptable = Mathf.Max(1, questionsPerGenre / 2);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    int have = accumulated.questions.Count;
                    if (!quiet)
                    {
                        OnGenerationProgress?.Invoke(0.15f + 0.35f * have / Mathf.Max(1, questionsPerGenre));
                        SetStatus(have == 0 ? "問題を生成中..." : $"不足分を追加生成中 ({have}/{questionsPerGenre})...");
                    }

                    // 2回目以降は既出の問題文を渡して同じ問題の再生成を避ける
                    var avoid = have > 0 ? accumulated.questions.ConvertAll(q => q.question) : null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var responseJson = await targetClient.GenerateAsync(buildPrompt(material, avoid), QuizSchema);
                    double genSec = sw.Elapsed.TotalSeconds;
                    var questionSet = ParseResponse(responseJson);

                    // 正解を裏取り (誤り/不確かな問題はここで落とす)
                    await VerifyAsync(questionSet, label, quiet);

                    // 形式チェック (選択肢の重複・問題文に正解が含まれる等) もここで落とし、不足分を次の試行で補充できるようにする
                    ConvertRawToChoices(questionSet);
                    SanitizeQuestions(questionSet);
                    ValidateQuestions(questionSet, throwIfEmpty: false);

                    // 同じ問題文は重複として捨てつつ蓄積
                    int added = 0;
                    foreach (var q in questionSet.questions)
                    {
                        if (q == null || string.IsNullOrWhiteSpace(q.question)) continue;
                        if (QuizPromptBuilder.IsExampleQuestion(q.question)) { Debug.LogWarning($"[QuizGenerator] 例題の流用を除外: {q.question}"); continue; }
                        if (!seenQuestions.Add(q.question.Trim())) continue;
                        accumulated.questions.Add(q);
                        added++;
                        if (accumulated.questions.Count >= questionsPerGenre) break;
                    }

                    int count = accumulated.questions.Count;
                    if (added == 0)
                    {
                        // JSONとしては読めたが使える問題が無い (出力切断・構造崩れ) → 原因が追えるよう先頭だけ残す
                        int head = Mathf.Min(200, responseJson.Length);
                        Debug.LogWarning($"[QuizGenerator][{label}] No usable question in response ({responseJson.Length} chars): {responseJson.Substring(0, head)}");
                    }
                    if (count >= questionsPerGenre)
                    {
                        Debug.Log($"[QuizGenerator][{label}] Attempt {attempt}/{attempts}: +{added}問 → {count}/{questionsPerGenre} (生成 {genSec:0.0}s)");
                        break;
                    }

                    lastError = $"問題数が規定未達 ({count}/{questionsPerGenre})";
                    Debug.LogWarning($"[QuizGenerator][{label}] Attempt {attempt}/{attempts}: +{added}問 (累計 {count}/{questionsPerGenre}, 生成 {genSec:0.0}s)" +
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
                if (!quiet)
                {
                    OnGenerationProgress?.Invoke(0.6f);
                    SetStatus("問題を整形中...");
                }
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

        private void SetStatus(string text, bool quiet = false)
        {
            if (quiet) return;
            StatusText = text ?? "";
            OnStatusChanged?.Invoke(StatusText);
        }

        // ===== 題材収集 (Wikipedia) =====

        /// <summary>
        /// 題材となる記事の要約を集めてプロンプト用のテキストにする。
        /// LLM にタイトルを列挙させ → Wikipedia から冒頭要約をまとめて取得 (1リクエスト)。
        /// タイトルが解決できなければ全文検索でフォールバック (もう1リクエスト)。失敗時は null (題材なしで生成)。
        /// </summary>
        private async Task<string> GatherMaterialAsync(string scopeDescription, string searchKeyword, bool quiet)
        {
            if (!useWikipediaMaterial || materialClient == null) return null;
            SetStatus("題材を集めています (Wikipedia)...", quiet);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var extracts = new List<WikipediaClient.Extract>();
            try
            {
                var titles = await ProposeTopicTitlesAsync(scopeDescription);
                if (titles.Count > 0)
                    extracts.AddRange(await WikipediaClient.FetchExtractsAsync(titles, materialMaxChars));

                if (extracts.Count < Mathf.Min(3, materialTopicCount))
                {
                    Debug.Log($"[QuizGenerator] Only {extracts.Count} titles resolved on Wikipedia, falling back to search: {searchKeyword}");
                    foreach (var e in await WikipediaClient.SearchExtractsAsync(searchKeyword, materialTopicCount, materialMaxChars))
                    {
                        if (!extracts.Exists(x => x.title == e.title)) extracts.Add(e);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator] Wikipedia material unavailable, generating without it: {e.Message}");
            }

            // 直近のラウンドで使った記事は避ける (全部被っていたら仕方なく使う)
            var fresh = extracts.FindAll(x => !usedTitleSet.Contains(x.title));
            if (fresh.Count >= 3) extracts = fresh;
            if (extracts.Count > materialTopicCount) extracts.RemoveRange(materialTopicCount, extracts.Count - materialTopicCount);
            if (extracts.Count == 0) return null;

            RememberTitles(extracts);

            var sb = new StringBuilder(extracts.Count * (materialMaxChars + 32));
            for (int i = 0; i < extracts.Count; i++)
            {
                sb.Append('[').Append(i + 1).Append("] ").Append(extracts[i].title).Append('\n')
                  .Append(extracts[i].extract).Append("\n\n");
            }
            Debug.Log($"[QuizGenerator] Wikipedia material: {extracts.Count} articles in {sw.Elapsed.TotalSeconds:0.0}s — {string.Join(", ", extracts.ConvertAll(x => x.title))}");
            return sb.ToString().TrimEnd();
        }

        private async Task<List<string>> ProposeTopicTitlesAsync(string scopeDescription)
        {
            var prompt = QuizPromptBuilder.BuildTopicTitles(scopeDescription, materialTopicCount, usedTitles, rng.Next(1000, 9999));
            var json = await materialClient.GenerateAsync(prompt, TitlesSchema);
            var titles = ParseTitles(json);
            if (titles.Count == 0)
                Debug.LogWarning($"[QuizGenerator] Topic list unusable: {(json.Length > 200 ? json.Substring(0, 200) : json)}");
            return titles;
        }

        private static List<string> ParseTitles(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            string cleaned = CleanJson(json);
            TitleList list = TryParse<TitleList>(cleaned);
            if (list?.titles == null)
            {
                int s = cleaned.IndexOf('[');
                int e = cleaned.LastIndexOf(']');
                if (s >= 0 && e > s)
                    list = TryParse<TitleList>("{\"titles\":" + cleaned.Substring(s, e - s + 1) + "}");
            }
            if (list?.titles == null) return result;

            foreach (var t in list.titles)
            {
                if (!string.IsNullOrWhiteSpace(t)) result.Add(t.Trim());
            }
            return result;
        }

        private void RememberTitles(List<WikipediaClient.Extract> extracts)
        {
            foreach (var e in extracts)
            {
                if (!usedTitleSet.Add(e.title)) continue;
                usedTitles.Enqueue(e.title);
                while (usedTitles.Count > UsedTitleMemory)
                    usedTitleSet.Remove(usedTitles.Dequeue());
            }
        }

        // ===== 正解の裏取り (Gemini + Google検索) =====

        /// <summary>
        /// 生成直後の問題 (answer/wrongs 形式) の正解を裏取りし、誤りと判定された問題を除外する (足りない分は次の試行で補充される)。
        ///  0. 選択肢の記事を Wikipedia で引き、別名・略称で同じ記事を指す選択肢がある問題を除外 (verifyWithWikipedia)
        ///  1. Gemini + Google検索グラウンディング (無料枠が残っていれば)。「不確か」も除外
        ///  2. Wikipedia 検索の根拠文 + 生成モデルの判定 (キー不要)。根拠に反する「誤り」だけ除外
        /// どちらの経路でも、誤答の中に「それも正解」(also_correct) があれば除外する。
        /// 検証側の正答案で自動修正はしない: 問題文の条件 (「日本の物理学者」等) を読み違えた誤修正が実測で出たため。
        /// 検証自体が失敗した場合は未検証のまま通す (ゲーム進行を止めない)。
        /// </summary>
        private async Task VerifyAsync(QuizQuestionSet questionSet, string label, bool quiet)
        {
            if (questionSet?.questions == null || questionSet.questions.Count == 0) return;

            var candidates = new List<(QuizQuestion q, string answer, string[] wrongs)>();
            foreach (var q in questionSet.questions)
            {
                if (q == null || !TryGetAnswerAndWrongs(q, out var answer, out var wrongs)) continue;
                candidates.Add((q, answer, wrongs));
            }
            if (candidates.Count == 0) return;

            // 選択肢 (正解+誤答) の記事をまとめて引き、同じ記事を指す選択肢 (アメリカ/米国 等) を持つ問題を落とす。
            // 取れた記事は Wikipedia 検証の根拠にも使う。
            Dictionary<string, WikipediaClient.Extract> pages = null;
            if (verifyWithWikipedia)
            {
                pages = await FetchChoicePagesAsync(candidates, quiet);
                if (pages != null)
                    candidates.RemoveAll(c => DropIfSameArticle(c, pages, questionSet));
                if (candidates.Count == 0) return;
            }

            var items = new List<(int id, string question, string answer, string[] wrongs)>(candidates.Count);
            var itemQuestions = new List<QuizQuestion>(candidates.Count);
            foreach (var c in candidates)
            {
                items.Add((items.Count + 1, c.q.question, c.answer, c.wrongs));
                itemQuestions.Add(c.q);
            }

            bool geminiReady = verifyWithSearch && verifier != null && Time.realtimeSinceStartup >= verifierPausedUntil;
            if (geminiReady && await VerifyWithGeminiAsync(items, itemQuestions, questionSet, label, quiet))
                return;

            if (verifyWithWikipedia && judgeClient != null)
                await VerifyWithWikipediaAsync(items, itemQuestions, questionSet, label, quiet, pages);
        }

        /// <summary>
        /// Gemini の Google検索グラウンディングで検証する。判定を適用できたら true、スキップ/失敗なら false (呼び出し側が次の手段へ)。
        /// </summary>
        private async Task<bool> VerifyWithGeminiAsync(List<(int id, string question, string answer, string[] wrongs)> items,
            List<QuizQuestion> itemQuestions, QuizQuestionSet questionSet, string label, bool quiet)
        {
            SetStatus($"正解を検索で確認中 ({items.Count}問)...", quiet);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            VerifyResponse parsed;
            string text = null;
            try
            {
                text = await verifier.GenerateGroundedAsync(QuizPromptBuilder.BuildSearchVerification(items));
                parsed = ParseVerification(text);
                if (parsed?.results == null && !string.IsNullOrWhiteSpace(text))
                {
                    // 検索付き応答は番号付きリストや自由文で返ることがある → JSONモード (検索なし・追加1リクエスト) で整形し直す
                    Debug.Log($"[QuizGenerator][{label}] Verification text is not JSON, restructuring via JSON mode. Head: {text.Substring(0, Mathf.Min(120, text.Length))}");
                    var structured = await verifier.GenerateAsync(QuizPromptBuilder.BuildVerificationToJson(text, items.Count), VerifySchema);
                    parsed = ParseVerification(structured);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator][{label}] Gemini verification skipped: {e.Message}");
                if (IsNonRetryableError(e.Message))
                {
                    verifierPausedUntil = Time.realtimeSinceStartup + VerifierPauseSec;
                    Debug.LogWarning($"[QuizGenerator] Gemini verifier paused for {VerifierPauseSec / 60:0} min (quota / rate limit); using Wikipedia verification meanwhile.");
                }
                return false;
            }

            if (parsed?.results == null)
            {
                int head = Mathf.Min(300, text?.Length ?? 0);
                Debug.LogWarning($"[QuizGenerator][{label}] Gemini verification returned no parsable results. Text head: {text?.Substring(0, head)}");
                return false;
            }

            ApplyVerdicts(parsed, items, itemQuestions, questionSet, dropUnsure: true,
                $"Gemini search verification in {sw.Elapsed.TotalSeconds:0.0}s", label);
            return true;
        }

        /// <summary>
        /// 問題ごとに Wikipedia を検索して根拠文を集め、生成モデルに判定させる (キー不要・無料枠の上限なし)。
        /// </summary>
        private async Task VerifyWithWikipediaAsync(List<(int id, string question, string answer, string[] wrongs)> items,
            List<QuizQuestion> itemQuestions, QuizQuestionSet questionSet, string label, bool quiet,
            Dictionary<string, WikipediaClient.Extract> pages)
        {
            SetStatus($"正解を確認中 (Wikipedia, {items.Count}問)...", quiet);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var evidence = new List<string>(items.Count);
            var wrongNotes = new List<string>(items.Count);
            int withHits = 0;
            foreach (var item in items)
            {
                var lines = new List<string>();
                var seenTitles = new HashSet<string>();
                // 正解そのものの記事があれば最初の根拠にする
                if (TryGetPage(pages, item.answer, out var answerPage) && seenTitles.Add(answerPage.title))
                    lines.Add($"- {answerPage.title}: {answerPage.extract.Replace("\n", " ")}");
                try
                {
                    var hits = await WikipediaClient.SearchExtractsAsync(BuildEvidenceQuery(item.question, item.answer), evidencePerQuestion, EvidenceMaxChars);
                    foreach (var h in hits)
                        if (seenTitles.Add(h.title)) lines.Add($"- {h.title}: {h.extract.Replace("\n", " ")}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[QuizGenerator] Evidence search failed for 『{item.question}』: {e.Message}");
                }
                if (lines.Count > 0) withHits++;
                evidence.Add(string.Join("\n", lines));

                // 誤答それぞれが何者か (別名・同一物を判定側が見落とさないように)
                var notes = new List<string>(3);
                if (pages != null && item.wrongs != null)
                {
                    foreach (var w in item.wrongs)
                    {
                        notes.Add(TryGetPage(pages, w, out var wrongPage)
                            ? $"     「{w}」= {wrongPage.title}: {Truncate(wrongPage.extract.Replace("\n", " "), WrongNoteMaxChars)}"
                            : $"     「{w}」= (記事なし)");
                    }
                }
                wrongNotes.Add(string.Join("\n", notes));
            }
            double searchSec = sw.Elapsed.TotalSeconds;

            VerifyResponse parsed;
            try
            {
                var json = await judgeClient.GenerateAsync(QuizPromptBuilder.BuildEvidenceVerification(items, evidence, wrongNotes), VerifySchema);
                parsed = ParseVerification(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator][{label}] Wikipedia verification skipped: {e.Message}");
                return;
            }
            if (parsed?.results == null)
            {
                Debug.LogWarning($"[QuizGenerator][{label}] Wikipedia verification returned no parsable results; keeping questions unverified.");
                return;
            }

            ApplyVerdicts(parsed, items, itemQuestions, questionSet, dropUnsure: false,
                $"Wikipedia verification in {sw.Elapsed.TotalSeconds:0.0}s (search {searchSec:0.0}s, evidence for {withHits}/{items.Count})", label);
        }

        /// <summary>
        /// 判定結果を問題セットに適用する。ok 以外を除外 (dropUnsure=false なら unsure は残す)。
        /// </summary>
        private static void ApplyVerdicts(VerifyResponse parsed, List<(int id, string question, string answer, string[] wrongs)> items,
            List<QuizQuestion> itemQuestions, QuizQuestionSet questionSet, bool dropUnsure, string summaryPrefix, string label)
        {
            int ok = 0, dropped = 0, alsoCorrect = 0, unsureKept = 0;
            var toRemove = new HashSet<QuizQuestion>();
            foreach (var r in parsed.results)
            {
                if (r == null || r.id < 1 || r.id > itemQuestions.Count) continue;
                var q = itemQuestions[r.id - 1];
                var item = items[r.id - 1];
                string givenAnswer = item.answer;
                string verdict = (r.verdict ?? "").Trim().ToLowerInvariant();

                // "wrong" なのに正答案が元の正解と同じ = 実質 ok (Gemini がこの形で返すことが実測で何度もあった)
                if (verdict == "wrong" && SameAnswer(r.correct_answer, givenAnswer))
                    verdict = "ok";

                if (verdict == "wrong" || (verdict != "ok" && dropUnsure))
                {
                    toRemove.Add(q);
                    dropped++;
                    string hint = string.IsNullOrWhiteSpace(r.correct_answer) ? "" : $" / 検証側の正答案『{r.correct_answer}』";
                    Debug.LogWarning($"[QuizGenerator] 検証で除外 ({verdict}): 『{q.question}』 正解『{givenAnswer}』 {r.note}{hint}");
                    continue;
                }

                // 正解は合っていても、誤答の中に「それも正解」が混じっていれば4択として成立しない
                string extra = FindListedWrong(r.also_correct, item);
                if (extra != null)
                {
                    toRemove.Add(q);
                    alsoCorrect++;
                    Debug.LogWarning($"[QuizGenerator] 検証で除外 (誤答『{extra}』も正解になり得る): 『{q.question}』 正解『{givenAnswer}』 {r.note}");
                    continue;
                }

                if (verdict == "ok")
                {
                    ok++;
                    continue;
                }
                unsureKept++;
                Debug.Log($"[QuizGenerator] 検証: 未確認のまま採用 ({verdict}): 『{q.question}』 {r.note}");
            }

            if (toRemove.Count > 0)
                questionSet.questions.RemoveAll(toRemove.Contains);

            Debug.Log($"[QuizGenerator][{label}] {summaryPrefix}: ok={ok}, dropped={dropped}, also-correct={alsoCorrect}, unsure-kept={unsureKept}, " +
                      $"unlisted={items.Count - ok - dropped - alsoCorrect - unsureKept}");
        }

        private const int WrongNoteMaxChars = 160;

        /// <summary>
        /// 全問の選択肢 (正解+誤答) の記事冒頭を Wikipedia から 20件ごと 1 リクエストでまとめて取得する。
        /// キーは WikipediaClient.CleanTitle 済みの選択肢文字列。失敗したら null (検証自体は続行)。
        /// </summary>
        private async Task<Dictionary<string, WikipediaClient.Extract>> FetchChoicePagesAsync(
            List<(QuizQuestion q, string answer, string[] wrongs)> candidates, bool quiet)
        {
            var titles = new List<string>(candidates.Count * 4);
            foreach (var c in candidates)
            {
                titles.Add(c.answer);
                if (c.wrongs != null) titles.AddRange(c.wrongs);
            }
            SetStatus($"選択肢を確認中 (Wikipedia, {candidates.Count}問)...", quiet);
            try
            {
                return await WikipediaClient.FetchExtractMapAsync(titles, EvidenceMaxChars);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuizGenerator] Choice lookup on Wikipedia failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 2つの選択肢が同じ Wikipedia 記事に解決される (別名・略称・リダイレクト: アメリカ/米国 等) 問題を questionSet から外す。外したら true。
        /// </summary>
        private static bool DropIfSameArticle((QuizQuestion q, string answer, string[] wrongs) c,
            Dictionary<string, WikipediaClient.Extract> pages, QuizQuestionSet questionSet)
        {
            var choices = new List<string>(4) { c.answer };
            if (c.wrongs != null) choices.AddRange(c.wrongs);

            var firstChoiceByArticle = new Dictionary<string, string>();
            foreach (var choice in choices)
            {
                if (!TryGetPage(pages, choice, out var page)) continue;
                if (firstChoiceByArticle.TryGetValue(page.title, out var other))
                {
                    if (WikipediaClient.CleanTitle(other) == WikipediaClient.CleanTitle(choice)) continue; // 完全一致は形式チェック側で落ちる
                    Debug.LogWarning($"[QuizGenerator] 検証で除外 (選択肢『{other}』と『{choice}』が同じ記事『{page.title}』を指す): 『{c.q.question}』");
                    questionSet.questions.Remove(c.q);
                    return true;
                }
                firstChoiceByArticle[page.title] = choice;
            }
            return false;
        }

        private static bool TryGetPage(Dictionary<string, WikipediaClient.Extract> pages, string choice, out WikipediaClient.Extract page)
        {
            page = null;
            if (pages == null || string.IsNullOrEmpty(choice)) return false;
            string key = WikipediaClient.CleanTitle(choice);
            return key.Length > 0 && pages.TryGetValue(key, out page);
        }

        private static string Truncate(string s, int maxChars)
            => s.Length <= maxChars ? s : s.Substring(0, maxChars) + "…";

        private static bool SameAnswer(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return ChoiceKey(a) == ChoiceKey(b);
        }

        /// <summary>検証側が also_correct に挙げた中で、実際に誤答選択肢のどれかに当たるもの (正解自身は除く) を返す。</summary>
        private static string FindListedWrong(string[] listed, (int id, string question, string answer, string[] wrongs) item)
        {
            if (listed == null || item.wrongs == null) return null;
            foreach (var l in listed)
            {
                if (string.IsNullOrWhiteSpace(l) || SameAnswer(l, item.answer)) continue;
                foreach (var w in item.wrongs)
                    if (SameAnswer(l, w)) return w;
            }
            return null;
        }

        /// <summary>
        /// 根拠検索用のクエリ: 正解 + 問題文 (記号を除き、長すぎれば切る)。
        /// </summary>
        private static string BuildEvidenceQuery(string question, string answer)
        {
            string q = Regex.Replace(question ?? "", @"[？?！!。、,.「」『』（）()]", " ");
            q = Regex.Replace(q, @"\s+", " ").Trim();
            if (q.Length > 80) q = q.Substring(0, 80);
            return $"{answer} {q}".Trim();
        }

        private static VerifyResponse ParseVerification(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string cleaned = CleanJson(text);
            var parsed = TryParse<VerifyResponse>(cleaned);
            if (parsed?.results != null) return parsed;

            // results を省いて配列だけ返してきた場合
            if (cleaned.TrimStart().StartsWith("["))
            {
                parsed = TryParse<VerifyResponse>("{\"results\":" + cleaned + "}");
                if (parsed?.results != null) return parsed;
            }

            // 前後に説明文が付いた場合: 最初の { から最後の } までを切り出す
            int s = cleaned.IndexOf('{');
            int e = cleaned.LastIndexOf('}');
            if (s >= 0 && e > s)
            {
                parsed = TryParse<VerifyResponse>(cleaned.Substring(s, e - s + 1));
                if (parsed?.results != null) return parsed;
            }

            // "1. {...}\n2. {...}" のように番号付きで個別オブジェクトを並べてきた場合: { } を拾い集めて配列にする
            var objects = ExtractTopLevelObjects(cleaned);
            if (objects.Count > 0)
            {
                parsed = TryParse<VerifyResponse>("{\"results\":[" + string.Join(",", objects) + "]}");
                if (parsed?.results != null) return parsed;
            }
            return null;
        }

        /// <summary>
        /// テキスト中の最上位 { ... } オブジェクトを (文字列リテラル内の括弧を無視して) 順に切り出す。
        /// </summary>
        private static List<string> ExtractTopLevelObjects(string s)
        {
            var result = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] != '{') i++;
                if (i >= s.Length) break;

                int start = i, depth = 0, end = -1;
                bool inString = false, escape = false;
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) { end = i; break; }
                }
                if (end < 0) break;
                result.Add(s.Substring(start, end - start + 1));
                i = end + 1;
            }
            return result;
        }

        /// <summary>
        /// 生成直後の問題から (正解, 誤答3つ) を取り出す。answer/wrongs 形式と choices/correct_index 形式の両対応。
        /// </summary>
        private static bool TryGetAnswerAndWrongs(QuizQuestion q, out string answer, out string[] wrongs)
        {
            answer = null;
            wrongs = null;
            if (string.IsNullOrWhiteSpace(q.question)) return false;

            if (!string.IsNullOrEmpty(q.answer) && q.wrongs != null && q.wrongs.Length >= 3)
            {
                answer = q.answer;
                wrongs = new[] { q.wrongs[0], q.wrongs[1], q.wrongs[2] };
                return true;
            }
            if (q.choices != null && q.choices.Length == 4 && q.correct_index >= 0 && q.correct_index < 4)
            {
                answer = q.choices[q.correct_index];
                var list = new List<string>(3);
                for (int i = 0; i < 4; i++)
                    if (i != q.correct_index) list.Add(q.choices[i]);
                wrongs = list.ToArray();
                return true;
            }
            return false;
        }

        private static T TryParse<T>(string json) where T : class
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }

        [Serializable] private class TitleList { public string[] titles; }
        [Serializable] private class VerifyResponse { public VerifyItem[] results; }
        [Serializable] private class VerifyItem { public int id; public string verdict; public string correct_answer; public string[] also_correct; public string note; }

        // 検証結果を JSON モードで整形し直すときのスキーマ (Gemini responseSchema)
        private const string VerifySchema =
            "{\"type\":\"object\",\"properties\":{\"results\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"id\":{\"type\":\"integer\"}," +
            "\"verdict\":{\"type\":\"string\",\"enum\":[\"ok\",\"wrong\",\"unsure\"]}," +
            "\"correct_answer\":{\"type\":\"string\"}," +
            "\"also_correct\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
            "\"note\":{\"type\":\"string\"}" +
            "},\"required\":[\"id\",\"verdict\"]}}},\"required\":[\"results\"]}";

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

        private static void ValidateQuestions(QuizQuestionSet questionSet, bool throwIfEmpty = true)
        {
            int removed = 0;
            for (int i = questionSet.questions.Count - 1; i >= 0; i--)
            {
                var q = questionSet.questions[i];
                string reason = GetRejectReason(q);
                if (reason != null)
                {
                    Debug.LogWarning($"[QuizGenerator] 問題{i + 1}を除外: {reason}");
                    questionSet.questions.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
                Debug.LogWarning($"[QuizGenerator] {removed}問を除外しました（残り{questionSet.questions.Count}問）");

            if (throwIfEmpty && questionSet.questions.Count == 0)
                throw new Exception("有効な問題が0件です");
        }

        /// <summary>
        /// 問題が出題に耐えなければ理由を返す。問題なければ null。
        /// </summary>
        private static string GetRejectReason(QuizQuestion q)
        {
            if (string.IsNullOrWhiteSpace(q.question)) return "問題文が空";
            if (QuizPromptBuilder.IsExampleQuestion(q.question)) return "プロンプトの例題をそのまま流用";
            if (q.choices == null || q.choices.Length != 4) return $"選択肢が4つではない({q.choices?.Length ?? 0})";
            if (q.correct_index < 0 || q.correct_index > 3) return $"correct_index({q.correct_index})が範囲外";

            foreach (var c in q.choices)
                if (string.IsNullOrWhiteSpace(c)) return "空の選択肢がある";
            string duplicate = FindDuplicateChoice(q.choices);
            if (duplicate != null) return duplicate;

            // 問題文に正解テキストが含まれている (自己言及問題)
            var correctText = q.choices[q.correct_index];
            if (correctText.Length >= 2 && q.question.Contains(correctText))
                return $"問題文に正解『{correctText}』が含まれている";

            return null;
        }

        // ===== 選択肢の同一性チェック =====

        /// <summary>
        /// 4つの選択肢に「同じもの」が混じっていれば理由を返す (無ければ null)。
        /// 表記ゆれ (全角/半角・ひらがな/カタカナ・ヴ/バ行・空白や中点の有無・末尾の長音) を吸収したうえで、
        /// 行政区分や国名の接尾辞違い (東京/東京都)、複合名の一部 (ゴッホ/フィンセント・ファン・ゴッホ) も同一とみなす。
        /// </summary>
        private static string FindDuplicateChoice(string[] choices)
        {
            for (int i = 0; i < choices.Length; i++)
                for (int j = i + 1; j < choices.Length; j++)
                {
                    string why = SameEntityReason(choices[i], choices[j]);
                    if (why != null) return $"選択肢『{choices[i]}』と『{choices[j]}』が{why}";
                }
            return null;
        }

        private static readonly string[] EntitySuffixes =
            { "都", "道", "府", "県", "市", "区", "町", "村", "郡", "州", "国", "合衆国", "王国", "共和国", "連邦", "帝国", "公国" };
        private static readonly char[] SegmentSeparators = { '・', '･', '·', '=', ' ', '-', '‐', '–', '—', '―' };
        private const string SeparatorChars = " ・･·=-‐–—―'’\".,、。「」『』";

        private static string SameEntityReason(string a, string b)
        {
            string ka = ChoiceKey(a), kb = ChoiceKey(b);
            if (ka.Length == 0 || kb.Length == 0) return null;
            if (ka == kb) return "重複している";

            bool aIsShort = ka.Length <= kb.Length;
            string shortKey = aIsShort ? ka : kb;
            string longKey = aIsShort ? kb : ka;
            if (shortKey.Length < 2) return null;

            // 接尾辞違い: 東京/東京都, アメリカ/アメリカ合衆国
            foreach (var suffix in EntitySuffixes)
                if (longKey == shortKey + suffix) return "同じものの表記違い";

            // 複合名の一部: ゴッホ/フィンセント・ファン・ゴッホ, ビートルズ/ザ・ビートルズ
            var segments = NormalizeChoiceText(aIsShort ? b : a).Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                foreach (var segment in segments)
                    if (StripSeparators(segment) == shortKey) return "同じものの表記違い";

            return null;
        }

        /// <summary>比較用キー: NormalizeChoiceText + 区切り記号の除去 + 末尾の長音除去 (コンピューター/コンピュータ)。</summary>
        private static string ChoiceKey(string s)
            => StripSeparators(NormalizeChoiceText(s)).TrimEnd('ー');

        /// <summary>全角英数記号→半角、全角空白→半角、ひらがな→カタカナ、ヴ→バ行、小文字化 (区切り記号は残す)。</summary>
        private static string NormalizeChoiceText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char raw in s.Trim())
            {
                char c = raw;
                if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);      // 全角英数記号 → 半角
                else if (c == '　') c = ' ';                                  // 全角空白
                else if (c >= 'ぁ' && c <= 'ゖ') c = (char)(c + 0x60);   // ひらがな → カタカナ
                sb.Append(c);
            }
            return sb.ToString().ToLowerInvariant()
                .Replace("ヴァ", "バ").Replace("ヴィ", "ビ").Replace("ヴェ", "ベ").Replace("ヴォ", "ボ").Replace("ヴ", "ブ");
        }

        private static string StripSeparators(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (SeparatorChars.IndexOf(c) < 0) sb.Append(c);
            return sb.ToString();
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
