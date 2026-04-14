using UnityEditor;
using UnityEngine;

namespace GemmaQuiz.Editor
{
    /// <summary>
    /// WebGLビルド用の設定を適用するエディタツール。
    /// GitHub Pagesデプロイ向けに最適化。
    /// </summary>
    public static class WebGLBuildSettings
    {
        [MenuItem("GemmaQuiz/Apply WebGL Build Settings")]
        public static void Apply()
        {
            // GitHub Pages は Brotli/Gzip の Content-Encoding ヘッダーを返さないため
            // Decompression Fallback を有効にする（JS側で解凍）
            PlayerSettings.WebGL.decompressionFallback = true;

            // 圧縮形式: Gzip (Brotliより互換性が高い)
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;

            // テンプレート: Default
            PlayerSettings.WebGL.template = "APPLICATION:Default";

            // メモリサイズ (Photon Fusion + AI生成で多めに)
            PlayerSettings.WebGL.initialMemorySize = 64;
            PlayerSettings.WebGL.maximumMemorySize = 512;

            // 例外処理を有効にしてデバッグしやすく
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;

            Debug.Log("[WebGLBuildSettings] Applied WebGL settings for GitHub Pages deployment");
        }
    }
}
