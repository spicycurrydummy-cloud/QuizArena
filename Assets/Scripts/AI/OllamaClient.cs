using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Ollama REST APIとの通信を担当。
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

            // jsonSchema が指定されていればそれをformat欄に埋め込む (Structured Output)。
            // 未指定なら "json" を使う。
            string formatField;
            if (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
                formatField = jsonSchema;
            else
                formatField = "\"json\"";

            // JsonUtilityを避けて手動でJSON構築（boolシリアライズの問題回避）
            // num_predict: デフォルト(128)だと日本語複数問の生成途中で切れるので大きく取る
            var json = $"{{\"model\":\"{EscapeJson(model)}\",\"prompt\":\"{EscapeJson(prompt)}\",\"stream\":false,\"format\":{formatField},\"options\":{{\"temperature\":{temperature},\"num_predict\":4096}}}}";

            Debug.Log($"[OllamaClient] POST {url} (prompt length: {prompt.Length})");

            var bodyBytes = Encoding.UTF8.GetBytes(json);

            var request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 120;

                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    var errorDetail = request.downloadHandler?.text ?? "no body";
                    throw new Exception($"Ollama API error: {request.error} (URL: {url}, Response: {errorDetail})");
                }

                var responseJson = request.downloadHandler.text;
                var response = JsonUtility.FromJson<OllamaResponse>(responseJson);
                return response.response;
            }
            finally
            {
                request.Dispose();
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            var url = $"{baseUrl}/api/tags";

            var request = UnityWebRequest.Get(url);
            try
            {
                request.timeout = 5;
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }
                return request.result == UnityWebRequest.Result.Success;
            }
            finally
            {
                request.Dispose();
            }
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        [Serializable]
        private class OllamaResponse
        {
            public string response;
        }
    }
}
