using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Inception (Mercury) REST APIとの通信を担当。
    /// OpenAI互換の Chat Completions エンドポイントを使用する。
    /// </summary>
    public class InceptionClient : IQuizAIClient
    {
        private readonly string baseUrl;
        private readonly string model;
        private readonly string apiKey;
        private readonly float temperature;

        public InceptionClient(
            string apiKey,
            string baseUrl = "https://api.inceptionlabs.ai/v1",
            string model = "mercury-coder-small",
            float temperature = 0.7f)
        {
            this.apiKey = apiKey;
            this.baseUrl = baseUrl.TrimEnd('/');
            this.model = model;
            this.temperature = temperature;
        }

        public async Task<string> GenerateAsync(string prompt, string jsonSchema = null)
        {
            var url = $"{baseUrl}/chat/completions";

            // response_format の構築
            string responseFormatBlock;
            if (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
            {
                // Structured Output: OpenAI互換の json_schema 形式
                responseFormatBlock =
                    $"\"response_format\":{{\"type\":\"json_schema\",\"json_schema\":{{\"name\":\"quiz\",\"strict\":true,\"schema\":{jsonSchema}}}}}";
            }
            else
            {
                // 通常のJSONモード
                responseFormatBlock = "\"response_format\":{\"type\":\"json_object\"}";
            }

            // リクエストボディ構築
            var json = $"{{\"model\":\"{EscapeJson(model)}\","
                + $"\"messages\":[{{\"role\":\"user\",\"content\":\"{EscapeJson(prompt)}\"}}],"
                + $"\"temperature\":{temperature},"
                + $"\"max_tokens\":4096,"
                + $"{responseFormatBlock}}}";

            Debug.Log($"[InceptionClient] POST {url} (model: {model}, prompt length: {prompt.Length})");

            var bodyBytes = Encoding.UTF8.GetBytes(json);

            var request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = 120;

                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    var errorDetail = request.downloadHandler?.text ?? "no body";
                    throw new Exception($"Inception API error: {request.error} (URL: {url}, Response: {errorDetail})");
                }

                var responseJson = request.downloadHandler.text;
                Debug.Log($"[InceptionClient] Response length: {responseJson.Length}");

                // OpenAI互換レスポンスからcontentを抽出
                var response = JsonUtility.FromJson<ChatCompletionResponse>(responseJson);
                if (response?.choices == null || response.choices.Length == 0)
                {
                    throw new Exception("Inception API: choices が空です");
                }

                return response.choices[0].message.content;
            }
            finally
            {
                request.Dispose();
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            // modelsエンドポイントで接続確認
            var url = $"{baseUrl}/models";

            var request = UnityWebRequest.Get(url);
            try
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
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
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        // OpenAI互換レスポンス構造
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
