using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;

namespace GemmaQuiz.Editor
{
    public static class ReplaceFontTool
    {
        [MenuItem("GemmaQuiz/Replace All Fonts to NotoSansJP")]
        public static void ReplaceAll()
        {
            var jpFont = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
            if (jpFont == null)
            {
                Debug.LogError("NotoSansJP-Regular not found at Assets/Resources/Fonts/NotoSansJP-Regular.ttf");
                return;
            }

            string[] scenePaths = {
                "Assets/Scenes/TitleScene.unity",
                "Assets/Scenes/LobbyScene.unity",
                "Assets/Scenes/QuizScene.unity",
                "Assets/Scenes/ResultScene.unity"
            };

            int totalUpdated = 0;

            foreach (var path in scenePaths)
            {
                if (!File.Exists(path)) continue;
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int count = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var t in root.GetComponentsInChildren<Text>(true))
                    {
                        if (t.font == null || t.font.name == "LegacyRuntime" || t.font.name == "Arial")
                        {
                            Undo.RecordObject(t, "Replace Font");
                            t.font = jpFont;
                            count++;
                        }
                    }
                }
                if (count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                Debug.Log($"[ReplaceFont] {path}: {count} Text updated");
                totalUpdated += count;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = PrefabUtility.LoadPrefabContents(path);
                if (prefab == null) continue;
                int count = 0;
                foreach (var t in prefab.GetComponentsInChildren<Text>(true))
                {
                    if (t.font == null || t.font.name == "LegacyRuntime" || t.font.name == "Arial")
                    {
                        t.font = jpFont;
                        count++;
                    }
                }
                if (count > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefab, path);
                    Debug.Log($"[ReplaceFont] {path}: {count} Text updated");
                    totalUpdated += count;
                }
                PrefabUtility.UnloadPrefabContents(prefab);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[ReplaceFont] DONE. Total: {totalUpdated}");
        }
    }
}
