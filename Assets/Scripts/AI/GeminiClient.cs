using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// Google Gemini (Generative Language API) クライアント。
    /// Inception/Mercury がトークン切れ等で失敗した際のフォールバックとして使用する。
    /// 既定モデル: gemini-2.5-flash-lite
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

            // Structured Output: responseMimeType + responseSchema
            string generationConfig;
            if (!string.IsNullOrEmpty(jsonSchema) && jsonSchema.TrimStart().StartsWith("{"))
            {
                generationConfig =
                    $"\"temperature\":{temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"maxOutputTokens\":8192," +
                    $"\"responseMimeType\":\"application/json\"," +
                    $"\"responseSchema\":{jsonSchema}";
            }
            else
            {
                generationConfig =
                    $"\"temperature\":{temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"maxOutputTokens\":8192," +
                    $"\"responseMimeType\":\"application/json\"";
            }

            var json =
                "{" +
                    $"\"contents\":[{{\"parts\":[{{\"text\":\"{EscapeJson(prompt)}\"}}]}}]," +
                    $"\"generationConfig\":{{{generationConfig}}}" +
                "}";

            Debug.Log($"[GeminiClient] POST {baseUrl}/models/{model}:generateContent (prompt length: {prompt.Length})");

            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 120;

                var operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    var errorDetail = request.downloadHandler?.text ?? "no body";
                    throw new Exception($"Gemini API error: {request.error} (Response: {errorDetail})");
                }

                var responseJson = request.downloadHandler.text;
                Debug.Log($"[GeminiClient] Response length: {responseJson.Length}");

                var response = JsonUtility.FromJson<GenerateContentResponse>(responseJson);
                if (response?.candidates == null || response.candidates.Length == 0)
                    throw new Exception("Gemini API: candidates が空です");

                var candidate = response.candidates[0];
                if (candidate.content?.parts == null || candidate.content.parts.Length == 0)
                    throw new Exception("Gemini API: content.parts が空です");

                return candidate.content.parts[0].text;
            }
            finally
            {
                request.Dispose();
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            // modelsエンドポイントで疎通確認
            var url = $"{baseUrl}/models?key={UnityWebRequest.EscapeURL(apiKey)}";
            var request = UnityWebRequest.Get(url);
            try
            {
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
        private class GenerateContentResponse
        {
            public Candidate[] candidates;
        }

        [Serializable]
        private class Candidate
        {
            public Content content;
        }

        [Serializable]
        private class Content
        {
            public Part[] parts;
        }

        [Serializable]
        private class Part
        {
            public string text;
        }
    }
}
