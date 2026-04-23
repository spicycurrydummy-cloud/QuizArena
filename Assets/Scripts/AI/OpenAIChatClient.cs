using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// OpenAI互換 /chat/completions エンドポイントを叩く汎用クライアント。
    /// Cerebras / Groq などで共有する。Structured Output は json_schema 形式を使う。
    /// </summary>
    public class OpenAIChatClient : IQuizAIClient
    {
        private readonly string chatUrl;
        private readonly string modelsUrl;
        private readonly string model;
        private readonly string apiKey;
        private readonly float temperature;
        private readonly int maxTokens;
        private readonly string providerLabel;

        public OpenAIChatClient(
            string apiKey,
            string baseUrl,
            string model,
            float temperature = 0.4f,
            int maxTokens = 4096,
            string providerLabel = "OpenAIChat")
        {
            this.apiKey = apiKey;
            var trimmed = (baseUrl ?? "").TrimEnd('/');
            this.chatUrl = $"{trimmed}/chat/completions";
            this.modelsUrl = $"{trimmed}/models";
            this.model = model;
            this.temperature = temperature;
            this.maxTokens = maxTokens;
            this.providerLabel = providerLabel;
        }

        public async Task<string> GenerateAsync(string prompt, string jsonSchema = null)
        {
            // Cerebras/Groq の多くのモデルは json_schema 非対応なので json_object で統一する。
            // スキーマ要件はプロンプト側 (QuizPromptBuilder) で記述済み。
            string responseFormatBlock = "\"response_format\":{\"type\":\"json_object\"}";

            var json = $"{{\"model\":\"{EscapeJson(model)}\","
                + $"\"messages\":[{{\"role\":\"user\",\"content\":\"{EscapeJson(prompt)}\"}}],"
                + $"\"temperature\":{temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                + $"\"max_tokens\":{maxTokens},"
                + $"{responseFormatBlock}}}";

            Debug.Log($"[{providerLabel}] POST {chatUrl} (model: {model}, prompt length: {prompt.Length})");

            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var request = new UnityWebRequest(chatUrl, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 120;

                var operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    var errorDetail = request.downloadHandler?.text ?? "no body";
                    throw new Exception($"{providerLabel} API error: {request.error} (Response: {errorDetail})");
                }

                var responseJson = request.downloadHandler.text;
                Debug.Log($"[{providerLabel}] Response length: {responseJson.Length}");

                var response = JsonUtility.FromJson<ChatCompletionResponse>(responseJson);
                if (response?.choices == null || response.choices.Length == 0)
                    throw new Exception($"{providerLabel} API: choices が空です");

                return response.choices[0].message.content;
            }
            finally
            {
                request.Dispose();
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            var request = UnityWebRequest.Get(modelsUrl);
            try
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 5;
                var operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield();
                return request.result == UnityWebRequest.Result.Success;
            }
            finally
            {
                request.Dispose();
            }
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        [Serializable]
        private class ChatCompletionResponse
        {
            public Choice[] choices;
        }

        [Serializable]
        private class Choice
        {
            public Message message;
        }

        [Serializable]
        private class Message
        {
            public string content;
        }
    }
}
