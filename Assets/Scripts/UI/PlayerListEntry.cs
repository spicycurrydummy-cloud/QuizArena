using UnityEngine;
using UnityEngine.UI;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// プレイヤーリスト1行分のUI表示。
    /// </summary>
    public class PlayerListEntry : MonoBehaviour
    {
        [SerializeField] private Text playerNameText;
        [SerializeField] private Text roleText;

        private bool initialized;

        public void Setup(string playerName, bool isHost)
        {
            Setup(playerName, isHost, isHost ? "Host" : "");
        }

        public void Setup(string playerName, bool isHost, string role)
        {
            playerNameText.text = playerName;
            roleText.text = role;

            // 初回のみレイアウト調整
            if (!initialized && roleText != null)
            {
                initialized = true;
                roleText.alignment = TextAnchor.MiddleRight;
                roleText.horizontalOverflow = HorizontalWrapMode.Overflow;
                roleText.fontSize = Mathf.Max(roleText.fontSize, 18);

                var rt = roleText.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // アンカーを右端に伸ばして幅を確保
                    rt.anchorMin = new Vector2(0.35f, 0f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.offsetMin = new Vector2(0, rt.offsetMin.y);
                    rt.offsetMax = new Vector2(-10, rt.offsetMax.y);
                }
            }
        }
    }
}
