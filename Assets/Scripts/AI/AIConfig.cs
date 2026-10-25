using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// AIプロバイダー用の資格情報をまとめた設定。
    /// 読み込み優先順:
    ///  1. UserSettings/Config.json (エディタのみ。gitignore済なのでローカル開発用)
    ///  2. Resources/Config.json   (CIが GitHub Secret AI_CONFIG_JSON の内容をそのまま書き出す)
    /// JSON形式:
    ///  {"inception_api_key":"...","gemini_api_key":"...","cerebras_api_key":"...","groq_api_key":"..."}
    /// Inception は予備キーも持てる。inception_api_keys (配列) に並べた順で使い、
    /// 認証エラーや枠切れが出たら次のキーに切り替わる:
    ///  {"inception_api_key":"メイン","inception_api_keys":["予備1","予備2"], ...}
    /// (inception_api_key を省いて inception_api_keys だけに全部並べてもよい)
    /// </summary>
    [Serializable]
    public class AIConfig
    {
        public string inception_api_key;
        public string[] inception_api_keys;
        public string gemini_api_key;
        public string cerebras_api_key;
        public string groq_api_key;

        /// <summary>
        /// Inception のキーを優先順に並べたもの (inception_api_key → inception_api_keys の順、空と重複は除く)。
        /// </summary>
        public List<string> InceptionKeys
        {
            get
            {
                var list = new List<string>();
                Add(list, inception_api_key);
                if (inception_api_keys != null)
                    foreach (var k in inception_api_keys) Add(list, k);
                return list;

                static void Add(List<string> list, string key)
                {
                    var t = key?.Trim();
                    if (!string.IsNullOrEmpty(t) && !list.Contains(t)) list.Add(t);
                }
            }
        }

        public bool HasInceptionKey => InceptionKeys.Count > 0;
        public bool HasGeminiKey => !string.IsNullOrEmpty(gemini_api_key);
        public bool HasCerebrasKey => !string.IsNullOrEmpty(cerebras_api_key);
        public bool HasGroqKey => !string.IsNullOrEmpty(groq_api_key);

        public static AIConfig Load()
        {
            var cfg = new AIConfig();

#if UNITY_EDITOR
            try
            {
                var userJsonPath = Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName, "UserSettings", "Config.json");
                if (File.Exists(userJsonPath))
                    cfg.MergeFrom(TryParse(File.ReadAllText(userJsonPath), "UserSettings/Config.json"));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIConfig] UserSettings/Config.json read error: {e.Message}");
            }
#endif

            var asset = Resources.Load<TextAsset>("Config");
            if (asset != null)
                cfg.MergeFrom(TryParse(asset.text, "Resources/Config.json"));

            return cfg;
        }

        /// <summary>
        /// 自分に無いキーだけ other から補う。
        /// </summary>
        private void MergeFrom(AIConfig other)
        {
            if (other == null) return;
            if (!HasInceptionKey)
            {
                inception_api_key = other.inception_api_key;
                inception_api_keys = other.inception_api_keys;
            }
            if (!HasGeminiKey) gemini_api_key = other.gemini_api_key;
            if (!HasCerebrasKey) cerebras_api_key = other.cerebras_api_key;
            if (!HasGroqKey) groq_api_key = other.groq_api_key;
        }

        private static AIConfig TryParse(string text, string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonUtility.FromJson<AIConfig>(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIConfig] {sourceLabel} parse error: {e.Message}");
                return null;
            }
        }
    }
}
