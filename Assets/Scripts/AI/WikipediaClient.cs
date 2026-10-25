using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// 日本語版Wikipedia (MediaWiki Action API) から記事の冒頭要約を取得する。
    /// APIキー不要・CORS対応 (origin=*) で WebGL ビルドからも直接呼べる。
    /// titles を | 区切りでまとめて渡すので 1ラウンドあたり数リクエストで済み、
    /// 未認証ブラウザの上限 (200 req/分) に対して問題生成の回転数は十分余裕がある。
    /// </summary>
    public static class WikipediaClient
    {
        public const int MaxTitlesPerRequest = 20;
        private const int TimeoutSec = 15;

        // Wikimedia の API 利用ポリシーで求められる識別用ヘッダ (ブラウザでは User-Agent を書き換えられないので Api-User-Agent を使う)
        private const string ApiUserAgent = "GemmaQuiz/1.0 (https://github.com/spicycurrydummy-cloud/QuizArena)";

        [Serializable]
        public class Extract
        {
            /// <summary>解決後の記事タイトル (リダイレクト先)。</summary>
            public string title;
            public string extract;
            /// <summary>この記事に解決された要求タイトル (CleanTitle 済み)。検索結果では null。</summary>
            public string[] requested;
        }

        /// <summary>
        /// 記事タイトルの冒頭 maxChars 文字を取得する (20件ごとに1リクエスト)。リダイレクトは解決され、存在しない記事は除外される。
        /// 複数のタイトルが同じ記事に解決された場合は 1 件にまとまり、requested に両方入る。
        /// </summary>
        public static async Task<List<Extract>> FetchExtractsAsync(IEnumerable<string> titles, int maxChars, string lang = "ja")
        {
            var list = new List<string>();
            foreach (var t in titles)
            {
                var clean = CleanTitle(t);
                if (clean.Length == 0 || list.Contains(clean)) continue;
                list.Add(clean);
            }

            var result = new List<Extract>();
            for (int i = 0; i < list.Count; i += MaxTitlesPerRequest)
            {
                var chunk = list.GetRange(i, Math.Min(MaxTitlesPerRequest, list.Count - i));
                string url = BuildBaseUrl(lang, maxChars) + "&redirects=1&titles=" + UnityWebRequest.EscapeURL(string.Join("|", chunk));
                result.AddRange(await RequestAsync(url, chunk));
            }
            return result;
        }

        /// <summary>
        /// FetchExtractsAsync の結果を「要求タイトル (CleanTitle 済み) → 記事」の辞書にしたもの。
        /// 同じ記事に解決されたタイトル同士は同じ Extract インスタンスを指す (title で同一判定できる)。
        /// </summary>
        public static async Task<Dictionary<string, Extract>> FetchExtractMapAsync(IEnumerable<string> titles, int maxChars, string lang = "ja")
        {
            var map = new Dictionary<string, Extract>();
            foreach (var e in await FetchExtractsAsync(titles, maxChars, lang))
            {
                if (e.requested == null) continue;
                foreach (var r in e.requested) map[r] = e;
            }
            return map;
        }

        /// <summary>
        /// 全文検索の上位 limit 件の冒頭要約を取得する (タイトルが分からない / 解決できなかったときのフォールバック)。
        /// </summary>
        public static async Task<List<Extract>> SearchExtractsAsync(string query, int limit, int maxChars, string lang = "ja")
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<Extract>();
            limit = Mathf.Clamp(limit, 1, MaxTitlesPerRequest);
            string url = BuildBaseUrl(lang, maxChars)
                         + $"&generator=search&gsrnamespace=0&gsrlimit={limit}&gsrsearch="
                         + UnityWebRequest.EscapeURL(query.Trim());
            return await RequestAsync(url, null);
        }

        private static string BuildBaseUrl(string lang, int maxChars)
        {
            maxChars = Mathf.Clamp(maxChars, 100, 1200);
            return $"https://{lang}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&origin=*"
                   + $"&prop=extracts&exintro=1&explaintext=1&exlimit=max&exchars={maxChars}";
        }

        /// <summary>
        /// requested を渡すと normalized / redirects の情報で「要求タイトル → 解決後の記事」を対応付ける。
        /// </summary>
        private static async Task<List<Extract>> RequestAsync(string url, IList<string> requested)
        {
            var json = await AIHttp.GetJsonAsync(url, "Wikipedia", TimeoutSec, ("Api-User-Agent", ApiUserAgent));
            var root = JsonUtility.FromJson<QueryResponse>(json);

            var result = new List<Extract>();
            if (root?.query?.pages == null) return result;

            var byTitle = new Dictionary<string, Extract>();
            foreach (var p in root.query.pages)
            {
                if (p == null || p.missing || string.IsNullOrWhiteSpace(p.title) || string.IsNullOrWhiteSpace(p.extract)) continue;
                var text = NormalizeExtract(p.extract);
                if (text.Length < 20) continue; // 曖昧さ回避ページ等の中身のないもの
                var e = new Extract { title = p.title.Trim(), extract = text };
                result.Add(e);
                byTitle[e.title] = e;
            }

            if (requested != null)
            {
                var normalized = ToMap(root.query.normalized);
                var redirects = ToMap(root.query.redirects);
                var owners = new Dictionary<Extract, List<string>>();
                foreach (var t in requested)
                {
                    string resolved = t;
                    if (normalized.TryGetValue(resolved, out var n)) resolved = n;
                    if (redirects.TryGetValue(resolved, out var r)) resolved = r;
                    if (!byTitle.TryGetValue(resolved, out var e)) continue;
                    if (!owners.TryGetValue(e, out var list)) owners[e] = list = new List<string>();
                    list.Add(t);
                }
                foreach (var kv in owners) kv.Key.requested = kv.Value.ToArray();
            }
            return result;
        }

        private static Dictionary<string, string> ToMap(Mapping[] mappings)
        {
            var map = new Dictionary<string, string>();
            if (mappings == null) return map;
            foreach (var m in mappings)
                if (m != null && !string.IsNullOrEmpty(m.from) && !string.IsNullOrEmpty(m.to)) map[m.from] = m.to;
            return map;
        }

        /// <summary>
        /// 記事タイトルとして使えるよう、LLM が付けがちな装飾・セクション指定・名前空間を落とす (選択肢→記事の照合キーにも使う)。
        /// </summary>
        public static string CleanTitle(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return "";
            t = t.Replace("|", " ").Replace("\n", " ").Replace("\r", " ");
            int hash = t.IndexOf('#');
            if (hash >= 0) t = t.Substring(0, hash);
            t = Regex.Replace(t, @"^(Wikipedia|wikipedia|ウィキペディア)\s*[:：]\s*", "");
            t = Regex.Replace(t, @"^[\s\-\*・\d\.\)）]+", "");
            t = t.Trim().Trim('「', '」', '『', '』', '"', '\'');
            return Regex.Replace(t, @"\s+", " ");
        }

        private static string NormalizeExtract(string s)
        {
            s = s.Replace("\r", "");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\n{2,}", "\n");
            return s.Trim();
        }

        [Serializable] private class QueryResponse { public Query query; }
        [Serializable] private class Query { public Page[] pages; public Mapping[] normalized; public Mapping[] redirects; }
        [Serializable] private class Page { public string title; public string extract; public bool missing; }
        [Serializable] private class Mapping { public string from; public string to; }
    }
}
