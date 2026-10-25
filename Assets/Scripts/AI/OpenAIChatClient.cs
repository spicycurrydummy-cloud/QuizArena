using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// OpenAI互換 /chat/completions エンドポイントを叩く汎用クライアント。
    /// Inception (Mercury 3) / Cerebras / Groq で共有する。
    ///  - reasoningEffort: Mercury など reasoning_effort をサポートするAPI向け (null で送らない)
    ///  - supportsJsonSchema: true なら response_format=json_schema、false なら json_object
    ///  - keys: 複数キーを渡すと、認証/枠切れエラーのときに次のキーで同じリクエストをやり直す
    /// </summary>
    public class OpenAIChatClient : IQuizAIClient
    {
        private readonly string chatUrl;
        private readonly string modelsUrl;
        private readonly string model;
        private readonly ApiKeyRing keys;
        private readonly float temperature;
        private readonly int maxTokens;
        private readonly string providerLabel;
        private readonly string reasoningEffort;
        private readonly bool supportsJsonSchema;

        public OpenAIChatClient(
            string apiKey,
            string baseUrl,
            string model,
            float temperature = 0.4f,
            int maxTokens = 4096,
            string providerLabel = "OpenAIChat",
            string reasoningEffort = null,
            bool supportsJsonSchema = false)
            : this(new ApiKeyRing(new[] { apiKey }), baseUrl, model, temperature, maxTokens, providerLabel, reasoningEffort, supportsJsonSchema)
        {
        }

        public OpenAIChatClient(
            ApiKeyRing keys,
            string baseUrl,
            string model,
            float temperature = 0.4f,
            int maxTokens = 4096,
            string providerLabel = "OpenAIChat",
            string reasoningEffort = null,
            bool supportsJsonSchema = false)
        {
            this.keys = keys ?? new ApiKeyRing(null);
            var trimmed = (baseUrl ?? "").TrimEnd('/');
            chatUrl = $"{trimmed}/chat/completions";
            modelsUrl = $"{trimmed}/models";
            this.model = model;
            this.temperature = temperature;
            this.maxTokens = maxTokens;
            this.providerLabel = providerLabel;
            this.reasoningEffort = string.IsNullOrEmpty(reasoningEffort) ? null : reasoningEffort;
            this.supportsJsonSchema = supportsJsonSchema;
        }

        public async Task<string> GenerateAsync(string prompt, string jsonSchema = null)
        {
            var sb = new StringBuilder(prompt.Length + 512);
            sb.Append("{\"model\":\"").Append(AIHttp.EscapeJson(model)).Append("\",");
            sb.Append("\"messages\":[{\"role\":\"user\",\"content\":\"").Append(AIHttp.EscapeJson(prompt)).Append("\"}],");
            sb.Append("\"temperature\":").Append(AIHttp.FormatFloat(temperature)).Append(',');
            sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
            if (reasoningEffort != null)
                sb.Append("\"reasoning_effort\":\"").Append(AIHttp.EscapeJson(reasoningEffort)).Append("\",");

            bool useSchema = supportsJsonSchema && !string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{");
            if (useSchema)
                sb.Append("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"quiz\",\"strict\":true,\"schema\":")
                  .Append(jsonSchema).Append("}}");
            else
                sb.Append("\"response_format\":{\"type\":\"json_object\"}");
            sb.Append('}');
            var body = sb.ToString();

            Debug.Log($"[{providerLabel}] POST {chatUrl} (model: {model}, prompt length: {prompt.Length})");

            // キーが複数あるときは、キー起因のエラー (401/402/403/429) に限り次のキーで同じリクエストをやり直す
            int maxTries = Math.Max(1, keys.Count);
            for (int i = 0; ; i++)
            {
                var key = keys.Current;
                try
                {
                    var responseJson = await AIHttp.PostJsonAsync(chatUrl, body, key, providerLabel);
                    return AIHttp.ExtractChatContent(responseJson, providerLabel);
                }
                catch (Exception e) when (i < maxTries - 1 && ApiKeyRing.IsKeyError(e.Message))
                {
                    keys.Advance(key);
                    Debug.LogWarning($"[{providerLabel}] key {ApiKeyRing.Mask(key)} rejected (HTTP {ApiKeyRing.StatusOf(e.Message)}), " +
                                     $"retrying with key {ApiKeyRing.Mask(keys.Current)} ({keys.CurrentIndex + 1}/{keys.Count})");
                }
            }
        }

        public Task<bool> IsAvailableAsync() => AIHttp.GetOkAsync(modelsUrl, keys.Current);
    }
}
