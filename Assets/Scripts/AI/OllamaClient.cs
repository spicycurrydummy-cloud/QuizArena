using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Ollama REST API (/api/generate) クライアント。ローカル実行用。
    /// </summary>
    public class OllamaClient : IQuizAIClient
    {
        private readonly string baseUrl;
        private readonly string model;
        private readonly float temperature;

        public OllamaClient(string baseUrl = "http://localhost:11434", string model = "gemma4", float temperature = 0.7f)
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.model = model;
            this.temperature = temperature;
        }

        public async Task<string> GenerateAsync(string prompt, string jsonSchema = null)
        {
            var url = $"{baseUrl}/api/generate";

            // jsonSchema が指定されていれば format 欄に埋め込む (Structured Output)。未指定なら "json"。
            string formatField = (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
                ? jsonSchema
                : "\"json\"";

            var sb = new StringBuilder(prompt.Length + 256);
            sb.Append("{\"model\":\"").Append(AIHttp.EscapeJson(model)).Append("\",");
            sb.Append("\"prompt\":\"").Append(AIHttp.EscapeJson(prompt)).Append("\",");
            sb.Append("\"stream\":false,\"format\":").Append(formatField).Append(',');
            // num_predict: デフォルト(128)だと日本語複数問の生成途中で切れるので大きく取る
            sb.Append("\"options\":{\"temperature\":").Append(AIHttp.FormatFloat(temperature)).Append(",\"num_predict\":4096}}");

            Debug.Log($"[OllamaClient] POST {url} (model: {model}, prompt length: {prompt.Length})");

            var responseJson = await AIHttp.PostJsonAsync(url, sb.ToString(), null, "Ollama");
            var response = JsonUtility.FromJson<OllamaResponse>(responseJson);
            return response?.response;
        }

        public Task<bool> IsAvailableAsync() => AIHttp.GetOkAsync($"{baseUrl}/api/tags");

        [Serializable] private class OllamaResponse { public string response; }
    }
}
