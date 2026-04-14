using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using GemmaQuiz.Network;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// 結果発表シーンのUI制御。
    /// 最終ランキングを表示し、もう一度プレイ／タイトルへ戻るボタンを提供する。
    /// </summary>
    public class ResultUI : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Text titleText;
        [SerializeField] private Transform rankingContainer;
        [SerializeField] private GameObject rankingEntryPrefab;
        [SerializeField] private Button playAgainButton;
        [SerializeField] private Button backToTitleButton;

        private void Start()
        {
            playAgainButton.onClick.AddListener(OnPlayAgain);
            backToTitleButton.onClick.AddListener(OnBackToTitle);

            titleText.text = "クイズ終了!";
            ShowRanking();
        }

        private void ShowRanking()
        {
            var session = SessionManager.Instance;
            if (session == null) return;

            var ranking = session.GetScoreRanking();

            for (int i = 0; i < ranking.Count; i++)
            {
                var info = ranking[i];
                var entryObj = Instantiate(rankingEntryPrefab, rankingContainer);

                string medal = i switch
                {
                    0 => "<color=#FFD700>1st</color>",
                    1 => "<color=#C0C0C0>2nd</color>",
                    2 => "<color=#CD7F32>3rd</color>",
                    _ => $"{i + 1}th"
                };

                var texts = entryObj.GetComponentsInChildren<Text>();
                if (texts.Length > 0) texts[0].text = medal;
                if (texts.Length > 1) texts[1].text = info.playerName;
                if (texts.Length > 2) texts[2].text = $"{info.totalScore}点";

                // 上位3位を強調
                var img = entryObj.GetComponent<Image>();
                if (img != null && i < 3)
                {
                    Color[] colors = {
                        new Color(0.85f, 0.7f, 0.1f, 0.85f),
                        new Color(0.7f, 0.7f, 0.7f, 0.85f),
                        new Color(0.7f, 0.45f, 0.2f, 0.85f)
                    };
                    img.color = colors[i];
                }
            }
        }

        private void OnPlayAgain()
        {
            // スコアと選択ジャンルをリセット
            var session = SessionManager.Instance;
            if (session != null)
            {
                foreach (var kvp in session.Players)
                {
                    kvp.Value.totalScore = 0;
                    kvp.Value.selectedGenreIndex = -1;
                    kvp.Value.isReady = false;
                }
            }

            // ロビーへ戻る
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHost)
            {
                nm.LoadScene("LobbyScene");
            }
            else
            {
                SceneManager.LoadScene("LobbyScene");
            }
        }

        private void OnBackToTitle()
        {
            var nm = NetworkManager.Instance;
            if (nm != null)
                nm.LeaveSession();

            SceneManager.LoadScene("TitleScene");
        }
    }
}
