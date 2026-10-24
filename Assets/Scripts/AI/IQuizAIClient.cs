using System.Threading.Tasks;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// クイズ問題生成AIクライアントの共通インターフェース。
    /// Inception(Mercury) / Cerebras / Groq は OpenAIChatClient、他は専用クライアントで実装する。
    /// </summary>
    public interface IQuizAIClient
    {
        /// <summary>
        /// プロンプトを送信し、生成結果のJSON文字列を返す。
        /// </summary>
        /// <param name="prompt">プロンプト文字列</param>
        /// <param name="jsonSchema">JSON Schema文字列（Structured Output用。非対応プロバイダーは無視）</param>
        Task<string> GenerateAsync(string prompt, string jsonSchema);

        /// <summary>
        /// サーバーとの接続確認。
        /// </summary>
        Task<bool> IsAvailableAsync();
    }
}
