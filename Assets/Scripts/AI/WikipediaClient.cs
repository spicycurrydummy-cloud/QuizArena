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
    /// titles を | 区切りでまとめて渡すので 1ラウンドあたり 1〜2 リクエストで済み、
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
            public string title;
            public string extract;
        }

        /// <summary>
        /// 記事タイトル (最大20件) の冒頭 maxChars 文字を取得する。リダイレクトは解決され、存在しない記事は除外される。
        /// </summary>
        public static async Task<List<Extract>> FetchExtractsAsync(IEnumerable<string> titles, int maxChars, string lang = "ja")
        {
            var list = new List<string>();
            foreach (var t in titles)
            {
                var clean = CleanTitle(t);
                if (clean.Length == 0 || list.Contains(clean)) continue;
                list.Add(clean);
                if (list.Count >= MaxTitlesPerRequest) break;
            }
            if (list.Count == 0) return new List<Extract>();

            string url = BuildBaseUrl(lang, maxChars) + "&redirects=1&titles=" + UnityWebRequest.EscapeURL(string.Join("|", list));
            return await RequestAsync(url);
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
            return await RequestAsync(url);
        }

        private static string BuildBaseUrl(string lang, int maxChars)
        {
            maxChars = Mathf.Clamp(maxChars, 100, 1200);
            return $"https://{lang}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&origin=*"
                   + $"&prop=extracts&exintro=1&explaintext=1&exlimit=max&exchars={maxChars}";
        }

        private static async Task<List<Extract>> RequestAsync(string url)
        {
            var json = await AIHttp.GetJsonAsync(url, "Wikipedia", TimeoutSec, ("Api-User-Agent", ApiUserAgent));
            var root = JsonUtility.FromJson<QueryResponse>(json);

            var result = new List<Extract>();
            if (root?.query?.pages == null) return result;
            foreach (var p in root.query.pages)
            {
                if (p == null || p.missing || string.IsNullOrWhiteSpace(p.title) || string.IsNullOrWhiteSpace(p.extract)) continue;
                var text = NormalizeExtract(p.extract);
                if (text.Length < 20) continue; // 曖昧さ回避ページ等の中身のないもの
                result.Add(new Extract { title = p.title.Trim(), extract = text });
            }
            return result;
        }

        private static string CleanTitle(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return "";
            // LLM が付けがちな装飾・セクション指定・名前空間を落とす
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
        [Serializable] private class Query { public Page[] pages; }
        [Serializable] private class Page { public string title; public string extract; public bool missing; }
    }
}
