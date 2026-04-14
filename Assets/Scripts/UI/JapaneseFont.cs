using UnityEngine;

namespace GemmaQuiz.UI
{
    public static class JapaneseFont
    {
        private static Font cached;

        public static Font Get()
        {
            if (cached == null)
            {
                cached = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
                if (cached == null)
                {
                    Debug.LogWarning("[JapaneseFont] NotoSansJP-Regular not found in Resources/Fonts. Falling back to LegacyRuntime.");
                    cached = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
            }
            return cached;
        }
    }
}
