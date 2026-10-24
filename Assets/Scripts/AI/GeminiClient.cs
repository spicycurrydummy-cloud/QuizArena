using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Google Gemini (Generative Language API) クライアント。
    /// Inception/Mercury が失敗した際のフォールバックとして使用する。
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
            var url = $"{baseUrl}/models/{model}:generateContent?key={UnityWebRequest.EscapeURL(apiKey)}";

            var sb = new StringBuilder(prompt.Length + 512);
            sb.Append("{\"contents\":[{\"parts\":[{\"text\":\"").Append(AIHttp.EscapeJson(prompt)).Append("\"}]}],");
            sb.Append("\"generationConfig\":{");
            sb.Append("\"temperature\":").Append(AIHttp.FormatFloat(temperature)).Append(',');
            sb.Append("\"maxOutputTokens\":8192,");
            sb.Append("\"responseMimeType\":\"application/json\"");
            if (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
                sb.Append(",\"responseSchema\":").Append(jsonSchema);
            sb.Append("}}");

            Debug.Log($"[GeminiClient] POST {baseUrl}/models/{model}:generateContent (prompt length: {prompt.Length})");

            var responseJson = await AIHttp.PostJsonAsync(url, sb.ToString(), null, "Gemini");

            var response = JsonUtility.FromJson<GenerateContentResponse>(responseJson);
            if (response?.candidates == null || response.candidates.Length == 0)
                throw new Exception("Gemini API: candidates が空です");

            var candidate = response.candidates[0];
            if (candidate.content?.parts == null || candidate.content.parts.Length == 0)
                throw new Exception("Gemini API: content.parts が空です");

            return candidate.content.parts[0].text;
        }

        public Task<bool> IsAvailableAsync()
            => AIHttp.GetOkAsync($"{baseUrl}/models?key={UnityWebRequest.EscapeURL(apiKey)}");

        [Serializable] private class GenerateContentResponse { public Candidate[] candidates; }
        [Serializable] private class Candidate { public Content content; }
        [Serializable] private class Content { public Part[] parts; }
        [Serializable] private class Part { public string text; }
    }
}
