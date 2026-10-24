using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// 各AIクライアントで共通のHTTP/JSONユーティリティ。
    /// UnityWebRequestのasync化・JSON文字列エスケープ・OpenAI互換レスポンスの取り出しをまとめる。
    /// </summary>
    internal static class AIHttp
    {
        public const int DefaultTimeoutSec = 120;
        public const int ProbeTimeoutSec = 5;

        /// <summary>
        /// JSON文字列リテラル用にエスケープする。
        /// </summary>
        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string FormatFloat(float v)
            => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// JSONボディをPOSTしてレスポンス本文を返す。HTTPエラーは例外にする
        /// (メッセージにステータスコードを含めるので、呼び出し側でレート制限/認証エラーを判別できる)。
        /// </summary>
        public static async Task<string> PostJsonAsync(string url, string jsonBody, string bearerToken, string label,
            int timeoutSec = DefaultTimeoutSec)
        {
            var request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(bearerToken))
                    request.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
                request.timeout = timeoutSec;

                await SendAsync(request);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    var body = request.downloadHandler?.text;
                    if (string.IsNullOrEmpty(body)) body = "no body";
                    throw new Exception($"{label} API error: {request.error} (HTTP {request.responseCode}, Response: {body})");
                }

                var text = request.downloadHandler.text;
                Debug.Log($"[{label}] Response length: {text.Length}");
                return text;
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// GETして 2xx が返るかだけを確認する (疎通チェック用)。
        /// </summary>
        public static async Task<bool> GetOkAsync(string url, string bearerToken = null, int timeoutSec = ProbeTimeoutSec)
        {
            var request = UnityWebRequest.Get(url);
            try
            {
                if (!string.IsNullOrEmpty(bearerToken))
                    request.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
                request.timeout = timeoutSec;
                await SendAsync(request);
                return request.result == UnityWebRequest.Result.Success;
            }
            catch
            {
                return false;
            }
            finally
            {
                request.Dispose();
            }
        }

        private static async Task SendAsync(UnityWebRequest request)
        {
            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();
        }

        /// <summary>
        /// OpenAI互換 chat/completions レスポンスから choices[0].message.content を取り出す。
        /// </summary>
        public static string ExtractChatContent(string responseJson, string label)
        {
            var response = JsonUtility.FromJson<ChatCompletionResponse>(responseJson);
            if (response?.choices == null || response.choices.Length == 0)
                throw new Exception($"{label} API: choices が空です");

            var choice = response.choices[0];
            var content = choice.message?.content;
            if (string.IsNullOrWhiteSpace(content))
            {
                // 空応答の原因 (finish_reason=length で reasoning にトークンを使い切った等) を残す
                int head = Mathf.Min(300, responseJson.Length);
                throw new Exception($"{label} API: content が空です (finish_reason={choice.finish_reason ?? "?"}, body: {responseJson.Substring(0, head)})");
            }
            return content;
        }

        [Serializable]
        private class ChatCompletionResponse { public Choice[] choices; }
        [Serializable]
        private class Choice { public Message message; public string finish_reason; }
        [Serializable]
        private class Message { public string content; }
    }
}
