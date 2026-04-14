using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace GemmaQuiz.Editor
{
    /// <summary>
    /// TitleSceneにAIプロバイダー選択ドロップダウンを配置するエディタツール。
    /// メニュー: GemmaQuiz > Setup AI Provider Dropdown
    /// </summary>
    public static class SetupAIProviderDropdown
    {
        [MenuItem("GemmaQuiz/Setup AI Provider Dropdown")]
        public static void Execute()
        {
            var canvas = GameObject.Find("Canvas");
            if (canvas == null)
            {
                Debug.LogError("[Setup] Canvas not found. TitleSceneを開いてから実行してください。");
                return;
            }

            // 既存があれば削除
            var existingLabel = canvas.transform.Find("AIProviderLabel");
            if (existingLabel != null) Undo.DestroyObjectImmediate(existingLabel.gameObject);
            var existingDD = canvas.transform.Find("AIProviderDropdown");
            if (existingDD != null) Undo.DestroyObjectImmediate(existingDD.gameObject);

            // SessionNameInputの位置を基準にする
            var sessionInput = canvas.transform.Find("SessionNameInput");
            float baseY = 10f;
            float baseX = -480f;
            if (sessionInput != null)
            {
                var siRect = sessionInput.GetComponent<RectTransform>();
                if (siRect != null)
                {
                    baseY = siRect.anchoredPosition.y - 55f;
                    baseX = siRect.anchoredPosition.x;
                }
            }

            // ラベル
            var labelObj = new GameObject("AIProviderLabel");
            Undo.RegisterCreatedObjectUndo(labelObj, "Create AI Provider Label");
            labelObj.transform.SetParent(canvas.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchoredPosition = new Vector2(baseX - 120f, baseY);
            labelRect.sizeDelta = new Vector2(180, 36);
            labelObj.AddComponent<CanvasRenderer>();
            var label = labelObj.AddComponent<Text>();
            label.text = "問題生成AI:";
            label.font = GemmaQuiz.UI.JapaneseFont.Get();
            label.fontSize = 20;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleRight;

            // Dropdown
            var ddObj = DefaultControls.CreateDropdown(new DefaultControls.Resources());
            Undo.RegisterCreatedObjectUndo(ddObj, "Create AI Provider Dropdown");
            ddObj.name = "AIProviderDropdown";
            ddObj.transform.SetParent(canvas.transform, false);
            var ddRect = ddObj.GetComponent<RectTransform>();
            ddRect.anchoredPosition = new Vector2(baseX + 110f, baseY);
            ddRect.sizeDelta = new Vector2(260, 36);

            var ddImage = ddObj.GetComponent<Image>();
            if (ddImage != null) ddImage.color = new Color(0.2f, 0.2f, 0.3f, 1f);

            var ddLabel = ddObj.transform.Find("Label");
            if (ddLabel != null)
            {
                var t = ddLabel.GetComponent<Text>();
                if (t != null) { t.color = Color.white; t.fontSize = 16; }
            }

            var tmpl = ddObj.transform.Find("Template");
            if (tmpl != null)
            {
                var ti = tmpl.GetComponent<Image>();
                if (ti != null) ti.color = new Color(0.15f, 0.15f, 0.25f, 1f);
            }

            // SessionNameInputの直後に配置
            if (sessionInput != null)
            {
                int idx = sessionInput.GetSiblingIndex() + 1;
                labelObj.transform.SetSiblingIndex(idx);
                ddObj.transform.SetSiblingIndex(idx + 1);
            }

            // TitleUIにワイヤリング
            var titleUIObj = GameObject.Find("TitleUI");
            if (titleUIObj != null)
            {
                var titleUI = titleUIObj.GetComponent<MonoBehaviour>();
                // TitleUIコンポーネントを探す
                foreach (var mb in titleUIObj.GetComponents<MonoBehaviour>())
                {
                    if (mb.GetType().Name == "TitleUI")
                    {
                        titleUI = mb;
                        break;
                    }
                }
                if (titleUI != null)
                {
                    var so = new SerializedObject(titleUI);
                    var prop = so.FindProperty("aiProviderDropdown");
                    if (prop != null)
                    {
                        prop.objectReferenceValue = ddObj.GetComponent<Dropdown>();
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(titleUI);
                        Debug.Log("[Setup] TitleUI.aiProviderDropdown をワイヤリングしました");
                    }
                }
            }

            // QuizGeneratorのデフォルトをInceptionに
            var nmObj = GameObject.Find("NetworkManager");
            if (nmObj != null)
            {
                foreach (var mb in nmObj.GetComponents<MonoBehaviour>())
                {
                    if (mb.GetType().Name == "QuizGenerator")
                    {
                        var so = new SerializedObject(mb);
                        var provProp = so.FindProperty("aiProvider");
                        if (provProp != null)
                        {
                            provProp.intValue = 1; // Inception
                            so.ApplyModifiedProperties();
                            EditorUtility.SetDirty(mb);
                            Debug.Log("[Setup] QuizGenerator.aiProvider を Inception に設定しました");
                        }
                        break;
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Setup] AIプロバイダーDropdownを配置しました。Ctrl+Sでシーンを保存してください。");
        }
    }
}
