using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// 同じプロバイダー用の API キーを複数持ち、認証エラーや枠切れ (HTTP 401/402/403/429) が出たら
    /// 次のキーに切り替える。生成・題材・判定の各クライアントで 1 つのリングを共有するので、
    /// どれかが切り替えれば以降の呼び出しは全部予備キーを使う。
    /// 最後のキーが失敗したら先頭に戻る (一時的な 429 なら次のラウンドで自然に元のキーへ復帰する)。
    /// </summary>
    public class ApiKeyRing
    {
        private readonly string[] keys;
        private int current;

        public ApiKeyRing(IEnumerable<string> candidates)
        {
            var list = new List<string>();
            if (candidates != null)
            {
                foreach (var k in candidates)
                {
                    var t = k?.Trim();
                    if (string.IsNullOrEmpty(t) || list.Contains(t)) continue;
                    list.Add(t);
                }
            }
            keys = list.ToArray();
        }

        /// <summary>
        /// primary (Inspector 等で直接指定されたキー) を先頭に、設定ファイルのキーを後ろに並べたリングを作る。
        /// </summary>
        public static ApiKeyRing Of(string primary, IEnumerable<string> more)
        {
            var list = new List<string> { primary };
            if (more != null) list.AddRange(more);
            return new ApiKeyRing(list);
        }

        public int Count => keys.Length;
        public bool IsEmpty => keys.Length == 0;
        public int CurrentIndex => current;
        public string Current => keys.Length == 0 ? null : keys[current];

        /// <summary>
        /// failedKey がまだ現在のキーなら次へ進める (並行呼び出しで二重に飛ばさないため)。
        /// 切り替えたら true。キーが 1 つ以下なら何もしない。
        /// </summary>
        public bool Advance(string failedKey)
        {
            if (keys.Length < 2) return false;
            if (!string.Equals(keys[current], failedKey, StringComparison.Ordinal)) return false;
            current = (current + 1) % keys.Length;
            return true;
        }

        /// <summary>
        /// ログ用にキーを伏せる (末尾 4 文字だけ)。
        /// </summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(none)";
            return key.Length <= 8 ? "****" : "..." + key.Substring(key.Length - 4);
        }

        /// <summary>
        /// キーを変えれば通る可能性のあるエラーか (AIHttp が "HTTP 429" の形でステータスを埋め込む)。
        /// </summary>
        public static bool IsKeyError(string message)
        {
            var status = StatusOf(message);
            return status == 401 || status == 402 || status == 403 || status == 429;
        }

        public static int StatusOf(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            var m = Regex.Match(message, @"HTTP (\d{3})");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
    }
}
