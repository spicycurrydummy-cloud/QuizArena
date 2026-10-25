using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Google Gemini (Generative Language API) クライアント。
    ///  - GenerateAsync: JSONモードで問題生成 (Inception/Mercury 失敗時のフォールバック、または主プロバイダー)
    ///  - GenerateGroundedAsync: Google検索グラウンディング付き生成。正解の裏取りに使う。
    ///    2.5 Flash / Flash-Lite は無料枠で 1日500リクエストまで (両モデル合算)。
    ///    グラウンディングと responseMimeType=application/json は併用できない (HTTP 400) ので、
    ///    呼び出し側がプロンプトでJSONを要求して本文から抜き出す。
    /// </summary>
    public class GeminiClient : IQuizAIClient
    {
        private readonly string baseUrl;
        private readonly string model;
        private readonly string apiKey;
        private readonly float temperature;

        public GeminiClient(
            string apiKey,
            string model = "gemini-2.5-flash-lite",
            float temperature = 0.4f,
            string baseUrl = "https://generativelanguage.googleapis.com/v1beta")
        {
            this.apiKey = apiKey;
            this.model = model;
            this.temperature = temperature;
            this.baseUrl = baseUrl.TrimEnd('/');
        }

        public async Task<string> GenerateAsync(string prompt, string jsonSchema = null)
        {
            var sb = new StringBuilder(prompt.Length + 512);
            sb.Append("{\"contents\":[{\"parts\":[{\"text\":\"").Append(AIHttp.EscapeJson(prompt)).Append("\"}]}],");
            sb.Append("\"generationConfig\":{");
            sb.Append("\"temperature\":").Append(AIHttp.FormatFloat(temperature)).Append(',');
            sb.Append("\"maxOutputTokens\":8192,");
            sb.Append("\"responseMimeType\":\"application/json\"");
            if (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
                sb.Append(",\"responseSchema\":").Append(jsonSchema);
            sb.Append("}}");

            Debug.Log($"[GeminiClient] POST {model}:generateContent (prompt length: {prompt.Length})");

            var responseJson = await AIHttp.PostJsonAsync(GenerateUrl(), sb.ToString(), null, "Gemini");
            return ExtractText(responseJson, "Gemini");
        }

        /// <summary>
        /// Google検索でグラウンディングした回答を返す (JSONモード不可なので生テキスト)。
        /// </summary>
        public async Task<string> GenerateGroundedAsync(string prompt, int maxOutputTokens = 4096)
        {
            var sb = new StringBuilder(prompt.Length + 256);
            sb.Append("{\"contents\":[{\"parts\":[{\"text\":\"").Append(AIHttp.EscapeJson(prompt)).Append("\"}]}],");
            sb.Append("\"tools\":[{\"google_search\":{}}],");
            sb.Append("\"generationConfig\":{");
            sb.Append("\"temperature\":").Append(AIHttp.FormatFloat(temperature)).Append(',');
            sb.Append("\"maxOutputTokens\":").Append(maxOutputTokens);
            sb.Append("}}");

            Debug.Log($"[GeminiClient] POST {model}:generateContent + google_search (prompt length: {prompt.Length})");

            var responseJson = await AIHttp.PostJsonAsync(GenerateUrl(), sb.ToString(), null, "Gemini(grounded)");
            return ExtractText(responseJson, "Gemini(grounded)");
        }

        public Task<bool> IsAvailableAsync()
            => AIHttp.GetOkAsync($"{baseUrl}/models?key={UnityWebRequest.EscapeURL(apiKey)}");

        private string GenerateUrl()
            => $"{baseUrl}/models/{model}:generateContent?key={UnityWebRequest.EscapeURL(apiKey)}";

        /// <summary>
        /// candidates[0] の全 text パートを連結して返す。空ならブロック理由/終了理由を添えて例外。
        /// </summary>
        private static string ExtractText(string responseJson, string label)
        {
            var response = JsonUtility.FromJson<GenerateContentResponse>(responseJson);
            if (response?.candidates == null || response.candidates.Length == 0)
                throw new Exception($"{label} API: candidates が空です (blockReason={response?.promptFeedback?.blockReason ?? "-"})");

            var candidate = response.candidates[0];
            var sb = new StringBuilder();
            if (candidate.content?.parts != null)
            {
                foreach (var part in candidate.content.parts)
                    if (!string.IsNullOrEmpty(part?.text)) sb.Append(part.text);
            }
            if (sb.Length == 0)
                throw new Exception($"{label} API: テキストが空です (finishReason={candidate.finishReason ?? "?"})");

            int queries = candidate.groundingMetadata?.webSearchQueries?.Length ?? 0;
            if (queries > 0)
                Debug.Log($"[GeminiClient] Google検索 {queries} 件を参照: {string.Join(" / ", candidate.groundingMetadata.webSearchQueries)}");

            return sb.ToString();
        }

        [Serializable] private class GenerateContentResponse { public Candidate[] candidates; public PromptFeedback promptFeedback; }
        [Serializable] private class PromptFeedback { public string blockReason; }
        [Serializable] private class Candidate { public Content content; public string finishReason; public GroundingMetadata groundingMetadata; }
        [Serializable] private class Content { public Part[] parts; }
        [Serializable] private class Part { public string text; }
        [Serializable] private class GroundingMetadata { public string[] webSearchQueries; }
    }
}
