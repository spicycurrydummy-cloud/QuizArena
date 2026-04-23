using System;
using UnityEngine;

namespace GemmaQuiz.AI
{
    /// <summary>
    /// AIプロバイダー用の資格情報をまとめた設定。
    /// Resources/Config.json から読み込まれる。CI/CDでは
    /// GitHub Secret の内容 (JSON丸ごと) を Resources/Config.json に書き出して注入する。
    /// エディタ開発では UserSettings/Config.json (gitignore済) が優先される。
    /// </summary>
    [Serializable]
    public class AIConfig
    {
        public string inception_api_key;
        public string gemini_api_key;

        public bool HasInceptionKey => !string.IsNullOrEmpty(inception_api_key);
        public bool HasGeminiKey => !string.IsNullOrEmpty(gemini_api_key);

        /// <summary>
        /// 設定を読み込む。優先順:
        ///  1. UserSettings/Config.json (エディタのみ, gitignore済)
        ///  2. Resources/Config.json (CIで書き出される)
        ///  3. Legacy: Resources/InceptionKey.txt + UserSettings/InceptionKey.txt
        /// </summary>
        public static AIConfig Load()
        {
            var cfg = new AIConfig();

#if UNITY_EDITOR
            try
            {
                var userJsonPath = System.IO.Path.Combine(
                    System.IO.Directory.GetParent(Application.dataPath).FullName,
                    "UserSettings", "Config.json");
                if (System.IO.File.Exists(userJsonPath))
                {
                    var text = System.IO.File.ReadAllText(userJsonPath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var parsed = JsonUtility.FromJson<AIConfig>(text);
                        if (parsed != null) cfg = parsed;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIConfig] UserSettings/Config.json read error: {e.Message}");
            }
#endif

            if (!cfg.HasInceptionKey || !cfg.HasGeminiKey)
            {
                var asset = Resources.Load<TextAsset>("Config");
                if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
                {
                    try
                    {
                        var parsed = JsonUtility.FromJson<AIConfig>(asset.text);
                        if (parsed != null)
                        {
                            if (!cfg.HasInceptionKey && parsed.HasInceptionKey)
                                cfg.inception_api_key = parsed.inception_api_key;
                            if (!cfg.HasGeminiKey && parsed.HasGeminiKey)
                                cfg.gemini_api_key = parsed.gemini_api_key;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[AIConfig] Resources/Config.json parse error: {e.Message}");
                    }
                }
            }

            // Legacy: Inception単体のプレーンテキスト
            if (!cfg.HasInceptionKey)
            {
#if UNITY_EDITOR
                try
                {
                    var legacyUser = System.IO.Path.Combine(
                        System.IO.Directory.GetParent(Application.dataPath).FullName,
                        "UserSettings", "InceptionKey.txt");
                    if (System.IO.File.Exists(legacyUser))
                    {
                        var t = System.IO.File.ReadAllText(legacyUser).Trim();
                        if (!string.IsNullOrEmpty(t)) cfg.inception_api_key = t;
                    }
                }
                catch { /* ignore */ }
#endif
                if (!cfg.HasInceptionKey)
                {
                    var legacyRes = Resources.Load<TextAsset>("InceptionKey");
                    if (legacyRes != null)
                    {
                        var t = legacyRes.text.Trim();
                        if (!string.IsNullOrEmpty(t)) cfg.inception_api_key = t;
                    }
                }
            }

            return cfg;
        }
    }
}
