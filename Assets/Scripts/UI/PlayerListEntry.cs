using UnityEngine;
using UnityEngine.UI;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// プレイヤーリスト1行分のUI表示。レイアウトはプレハブ側 (PlayerListEntry.prefab) で定義する。
    /// </summary>
    public class PlayerListEntry : MonoBehaviour
    {
        [SerializeField] private Text playerNameText;
        [SerializeField] private Text roleText;

        public void Setup(string playerName, string role)
        {
            if (playerNameText != null) playerNameText.text = playerName;
            if (roleText != null) roleText.text = role;
        }
    }
}
