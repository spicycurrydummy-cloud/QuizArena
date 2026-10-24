using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GemmaQuiz.Data;
using GemmaQuiz.Quiz;
using GemmaQuiz.Audio;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// クイズ画面のUI制御。アニメーション演出付き。
    /// </summary>
    public class QuizUI : MonoBehaviour
    {
        [Header("Question")]
        [SerializeField] private Text questionNumberText;
        [SerializeField] private Text questionText;
        [SerializeField] private RectTransform questionPanel;

        [Header("Choices")]
        [SerializeField] private Button[] choiceButtons = new Button[4];
        [SerializeField] private Text[] choiceTexts = new Text[4];
        [SerializeField] private Image[] choiceImages = new Image[4];

        [Header("Timer")]
        [SerializeField] private Text timerText;
        [SerializeField] private Image timerBar;
        [SerializeField] private RectTransform timerTextRect;

        [Header("Answer List (left side)")]
        [SerializeField] private Transform answerListContent;
        [SerializeField] private GameObject answerListEntryPrefab;

        [Header("Result")]
        [SerializeField] private GameObject resultPanel;
        [SerializeField] private Text resultText;
        [SerializeField] private Text scoreText;
        [SerializeField] private CanvasGroup resultCanvasGroup;

        [Header("Score Board")]
        [SerializeField] private Text totalScoreText;

        [Header("Timing")]
        [SerializeField] private float autoAdvanceDelay = 4f;
        [Tooltip("問題文タイプライター演出の1文字あたり秒数。制限時間を食うので短めに")]
        [SerializeField] private float typewriterCharDelay = 0.02f;

        [Header("Colors")]
        [SerializeField] private Color selectedChoiceColor = new Color(0.3f, 0.5f, 0.8f);
        [SerializeField] private Color correctChoiceColor = new Color(0.2f, 0.7f, 0.3f);
        [SerializeField] private Color wrongChoiceColor = new Color(0.8f, 0.2f, 0.2f);
        [SerializeField] private Color[] choiceBaseColors =
        {
            new Color(0.85f, 0.25f, 0.25f),
            new Color(0.2f, 0.55f, 0.85f),
            new Color(0.85f, 0.65f, 0.1f),
            new Color(0.2f, 0.7f, 0.4f)
        };

        private static readonly Color DimmedChoiceColor = new Color(0.15f, 0.15f, 0.2f, 0.6f);
        private static readonly Color TimerNormalColor = new Color(0.2f, 0.7f, 0.9f);

        private int selectedChoice = -1;
        private bool hasAnswered;
        private bool showingResult;
        private int previousTotalScore;
        private bool timerWarning;
        private bool subscribedToQuizManager;

        // 解答リストエントリー
        private readonly List<GameObject> answerEntryInstances = new();
        private const string AnswerEntryPrefix = "AnswerEntry_";

        // 選択肢ボタンの定位置 (スライド演出の途中で次の問題が来ても位置がズレないよう Start で固定)
        private Vector2[] choiceHomePositions;
        private Coroutine[] choiceSlideRoutines;

        // ScalePunch の競合防止: Transform → (元スケール, 実行中コルーチン)
        private readonly Dictionary<Transform, Vector3> punchOriginalScales = new();
        private readonly Dictionary<Transform, Coroutine> punchRoutines = new();

        private void Start()
        {
            choiceHomePositions = new Vector2[choiceButtons.Length];
            choiceSlideRoutines = new Coroutine[choiceButtons.Length];
            for (int i = 0; i < choiceButtons.Length; i++)
            {
                int index = i;
                choiceButtons[i].onClick.AddListener(() => OnChoiceSelected(index));
                choiceHomePositions[i] = choiceButtons[i].GetComponent<RectTransform>().anchoredPosition;
            }

            resultPanel.SetActive(false);
            TrySubscribeToQuizManager();
        }

        private void TrySubscribeToQuizManager()
        {
            if (subscribedToQuizManager) return;
            var qm = QuizManager.Instance;
            if (qm == null) return;

            qm.OnQuestionStarted += HandleQuestionStarted;
            qm.OnTimerExpired += HandleTimerExpired;
            qm.OnQuestionResult += HandleQuestionResult;
            qm.OnPlayerAnswered += HandlePlayerAnswered;
            subscribedToQuizManager = true;
            Debug.Log("[QuizUI] Subscribed to QuizManager events");
        }

        private void OnDestroy()
        {
            var qm = QuizManager.Instance;
            if (qm == null) return;
            qm.OnQuestionStarted -= HandleQuestionStarted;
            qm.OnTimerExpired -= HandleTimerExpired;
            qm.OnQuestionResult -= HandleQuestionResult;
            qm.OnPlayerAnswered -= HandlePlayerAnswered;
        }

        private void Update()
        {
            TrySubscribeToQuizManager();

            if (showingResult) return;
            var qm = QuizManager.Instance;
            if (qm == null) return;

            float remaining = qm.RemainingTime;
            timerText.text = Mathf.CeilToInt(remaining).ToString();
            timerBar.fillAmount = remaining / ScoreCalculator.TimeLimit;

            if (remaining <= 3f && remaining > 0f)
            {
                timerText.color = Color.Lerp(Color.red, Color.yellow, Mathf.PingPong(Time.time * 4f, 1f));
                timerBar.color = Color.Lerp(new Color(0.8f, 0.2f, 0.2f), new Color(0.9f, 0.5f, 0.1f), Mathf.PingPong(Time.time * 3f, 1f));
                if (!timerWarning && timerTextRect != null)
                {
                    timerWarning = true;
                    StartCoroutine(AnimShake(timerTextRect, 5f, 0.3f));
                }
            }
            else
            {
                timerText.color = Color.white;
                timerBar.color = TimerNormalColor;
                timerWarning = false;
            }
        }

        private void HandleQuestionStarted(QuizQuestion question, int index)
        {
            showingResult = false;
            hasAnswered = false;
            selectedChoice = -1;
            timerWarning = false;

            AudioManager.Play(SfxKind.Question);
            resultPanel.SetActive(false);
            ClearAnswerList();

            questionNumberText.text = BuildQuestionHeader(index);
            StartScalePunch(questionNumberText.transform, 1.3f, 0.4f);
            StartCoroutine(AnimTypeWriter(questionText, question.question, typewriterCharDelay));

            for (int i = 0; i < choiceButtons.Length; i++)
            {
                choiceTexts[i].text = i < question.choices.Length ? question.choices[i] : "";
                choiceImages[i].color = choiceBaseColors[i % choiceBaseColors.Length];
                choiceButtons[i].interactable = true;

                // 前回問題の演出が残っていた場合に備えて元スケール/定位置へ戻す
                var btnTr = choiceButtons[i].transform;
                if (punchOriginalScales.TryGetValue(btnTr, out var origScale))
                    btnTr.localScale = origScale;

                var rect = choiceButtons[i].GetComponent<RectTransform>();
                if (choiceSlideRoutines[i] != null) StopCoroutine(choiceSlideRoutines[i]);
                var home = choiceHomePositions[i];
                float offsetX = (i % 2 == 0) ? -800f : 800f;
                choiceSlideRoutines[i] = StartCoroutine(AnimSlide(rect, home + new Vector2(offsetX, 0), home, 0.4f, 0.3f + i * 0.1f));
            }

            timerBar.fillAmount = 1f;
            timerText.color = Color.white;
            timerBar.color = TimerNormalColor;
        }

        /// <summary>
        /// 例: "R2/4  スポーツ-野球  Q3/5  Normal"
        /// </summary>
        private static string BuildQuestionHeader(int index)
        {
            var qm = QuizManager.Instance;
            if (qm == null) return $"Q{index + 1}";

            var difficulty = (QuizDifficulty)qm.CurrentQuestionDifficulty;
            var sb = new System.Text.StringBuilder();
            if (qm.TotalRounds > 1)
                sb.Append($"R{qm.CurrentRoundIndex + 1}/{qm.TotalRounds}  ");
            string genreName = qm.LoadingRoundGenreName.ToString();
            if (!string.IsNullOrEmpty(genreName))
                sb.Append(genreName).Append("  ");
            sb.Append(qm.TotalQuestions > 0 ? $"Q{index + 1}/{qm.TotalQuestions}" : $"Q{index + 1}");
            sb.Append("  ").Append(difficulty.ToColorTag()).Append(difficulty.ToLabel()).Append("</color>");
            return sb.ToString();
        }

        private void OnChoiceSelected(int index)
        {
            if (hasAnswered) return;
            hasAnswered = true;
            selectedChoice = index;

            StartScalePunch(choiceButtons[index].transform, 1.1f, 0.25f);

            for (int i = 0; i < choiceButtons.Length; i++)
            {
                choiceImages[i].color = (i == index) ? selectedChoiceColor : DimmedChoiceColor;
                choiceButtons[i].interactable = false;
            }

            QuizManager.Instance?.SubmitAnswer(index);
        }

        private void HandleTimerExpired()
        {
            showingResult = true;
            for (int i = 0; i < choiceButtons.Length; i++)
                choiceButtons[i].interactable = false;
        }

        // ===== 解答リスト =====

        private void ClearAnswerList()
        {
            foreach (var e in answerEntryInstances) Destroy(e);
            answerEntryInstances.Clear();
        }

        private void HandlePlayerAnswered(Fusion.PlayerRef player, string playerName, int order)
        {
            if (answerListContent == null || answerListEntryPrefab == null) return;

            // 結果開示後の重複イベントは無視（Render順序で UpdateAnswerListWithResults より後に届くことがある）
            if (showingResult) return;

            // 同じプレイヤーのエントリが既にあれば作らない
            string entryName = AnswerEntryPrefix + player.PlayerId;
            foreach (var e in answerEntryInstances)
                if (e != null && e.name == entryName) return;

            var entryObj = Instantiate(answerListEntryPrefab, answerListContent);
            entryObj.name = entryName;

            // 順位 + 名前 を表示（解答番号は結果開示時に表示）
            var texts = entryObj.GetComponentsInChildren<Text>();
            if (texts.Length > 0) texts[0].text = $"{order}. {playerName}";
            if (texts.Length > 1) texts[1].text = "?";

            answerEntryInstances.Add(entryObj);
            StartScalePunch(entryObj.transform, 1.1f, 0.25f);
        }

        private void HandleQuestionResult(QuestionResult result)
        {
            showingResult = true;
            resultPanel.SetActive(true);

            for (int i = 0; i < choiceButtons.Length; i++)
            {
                if (i == result.correctIndex)
                {
                    choiceImages[i].color = correctChoiceColor;
                    StartScalePunch(choiceButtons[i].transform, 1.15f, 0.4f);
                    StartCoroutine(AnimColorPulse(choiceImages[i], Color.white, 0.6f, 2));
                }
                else if (i == selectedChoice)
                {
                    choiceImages[i].color = wrongChoiceColor;
                    StartCoroutine(AnimShake(choiceButtons[i].GetComponent<RectTransform>(), 8f, 0.4f));
                }
                else
                {
                    choiceImages[i].color = new Color(0.15f, 0.15f, 0.2f, 0.4f);
                }
            }

            if (resultCanvasGroup != null)
            {
                resultCanvasGroup.alpha = 0f;
                StartCoroutine(AnimFade(resultCanvasGroup, 0f, 1f, 0.4f, 0.3f));
            }

            // 自分の結果が見つからなかった場合に前問の表示が残らないよう先にリセット
            resultText.text = "";
            scoreText.text = "";

            var runner = Network.NetworkManager.Instance?.Runner;
            if (runner != null)
            {
                foreach (var pr in result.playerResults)
                {
                    if (pr.playerRef != runner.LocalPlayer) continue;
                    ShowLocalResult(pr);
                    break;
                }
            }

            UpdateAnswerListWithResults(result);

            // 自動的に次の問題へ (ホストのみ実際に進める。QuizManager.NextQuestion は非ホストでは無視される)
            CancelInvoke(nameof(AdvanceToNext));
            Invoke(nameof(AdvanceToNext), autoAdvanceDelay);
        }

        private void ShowLocalResult(PlayerQuestionResult pr)
        {
            if (pr.isCorrect)
            {
                AudioManager.Play(SfxKind.Correct);
                resultText.text = "正解!";
                resultText.color = correctChoiceColor;
                scoreText.text = $"+{pr.score}点 ({pr.elapsedTime:F1}秒)";
                StartScalePunch(resultText.transform, 1.4f, 0.5f);
            }
            else if (pr.choiceIndex < 0)
            {
                resultText.text = "時間切れ";
                resultText.color = Color.gray;
                scoreText.text = "+0点";
            }
            else
            {
                AudioManager.Play(SfxKind.Wrong);
                resultText.text = "不正解...";
                resultText.color = wrongChoiceColor;
                scoreText.text = "+0点";
                StartCoroutine(AnimShake(resultText.GetComponent<RectTransform>(), 6f, 0.3f));
            }

            int newTotal = previousTotalScore + pr.score;
            if (totalScoreText != null)
            {
                if (pr.score > 0)
                    StartCoroutine(AnimCountUp(totalScoreText, previousTotalScore, newTotal, 0.8f, "合計: {0}点"));
                else
                    totalScoreText.text = $"合計: {newTotal}点";
            }
            previousTotalScore = newTotal;
        }

        private void UpdateAnswerListWithResults(QuestionResult result)
        {
            // Render() の change detector の順序に依存せず、結果データから解答順リストを作り直す。
            // 早く答えた順 → 答えなかった人は最後。
            if (answerListContent == null || answerListEntryPrefab == null) return;

            var ordered = new List<PlayerQuestionResult>(result.playerResults);
            ordered.Sort((a, b) =>
            {
                bool aAns = a.choiceIndex >= 0;
                bool bAns = b.choiceIndex >= 0;
                if (aAns != bAns) return aAns ? -1 : 1;
                if (!aAns) return 0;
                return a.elapsedTime.CompareTo(b.elapsedTime);
            });

            // 既存エントリを PlayerId でマップ化（再利用するため）
            var existingMap = new Dictionary<int, GameObject>();
            foreach (var e in answerEntryInstances)
            {
                if (e == null || !e.name.StartsWith(AnswerEntryPrefix)) continue;
                if (int.TryParse(e.name.Substring(AnswerEntryPrefix.Length), out int pid))
                    existingMap[pid] = e;
            }

            var newList = new List<GameObject>();
            int answeredOrder = 1;

            foreach (var pr in ordered)
            {
                bool answered = pr.choiceIndex >= 0;
                int playerId = pr.playerRef.PlayerId;

                if (!existingMap.Remove(playerId, out var entry))
                {
                    entry = Instantiate(answerListEntryPrefab, answerListContent);
                    entry.name = AnswerEntryPrefix + playerId;
                }
                entry.transform.SetSiblingIndex(newList.Count);

                var texts = entry.GetComponentsInChildren<Text>();
                if (texts.Length > 0)
                    texts[0].text = answered ? $"{answeredOrder}. {pr.playerName}" : $"-. {pr.playerName}";
                if (texts.Length > 1)
                    texts[1].text = answered ? $"{pr.choiceIndex + 1}" : "×";

                var bgImg = entry.GetComponent<Image>();
                if (bgImg != null)
                {
                    bgImg.color = !answered ? new Color(0.4f, 0.4f, 0.4f, 0.85f)
                        : pr.isCorrect ? new Color(0.2f, 0.7f, 0.3f, 0.85f)
                        : new Color(0.8f, 0.2f, 0.2f, 0.85f);
                }

                newList.Add(entry);
                if (answered) answeredOrder++;
            }

            // 使われなかった古いエントリは破棄
            foreach (var leftover in existingMap.Values)
                if (leftover != null) Destroy(leftover);

            answerEntryInstances.Clear();
            answerEntryInstances.AddRange(newList);
        }

        private void AdvanceToNext()
        {
            QuizManager.Instance?.NextQuestion();
        }

        /// <summary>
        /// クイズ画面の中身を表示/非表示にする。ラウンド間ロード中はfalseで隠す。
        /// </summary>
        public void SetContentVisible(bool visible)
        {
            if (questionPanel != null) questionPanel.gameObject.SetActive(visible);
            foreach (var btn in choiceButtons)
                if (btn != null) btn.gameObject.SetActive(visible);
            if (resultPanel != null && !visible) resultPanel.SetActive(false);
            if (answerListContent != null)
            {
                // 「解答順」ヘッダーごと隠す (Content の親がパネル。親が Canvas 直下なら Content 自身)
                var parent = answerListContent.parent;
                var listRoot = (parent != null && parent.GetComponent<Canvas>() == null) ? parent.gameObject : answerListContent.gameObject;
                listRoot.SetActive(visible);
            }
            if (timerBar != null) timerBar.gameObject.SetActive(visible);
            if (timerText != null) timerText.gameObject.SetActive(visible);
            if (questionNumberText != null) questionNumberText.gameObject.SetActive(visible);
            if (totalScoreText != null) totalScoreText.gameObject.SetActive(visible);

            if (visible)
            {
                showingResult = false;
                hasAnswered = false;
                ClearAnswerList();
            }
        }

        // === Animation Coroutines ===

        private static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - Mathf.Pow(1f - t, 3f); }

        private IEnumerator AnimFade(CanvasGroup g, float from, float to, float dur, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            g.alpha = from;
            for (float t = 0; t < dur; t += Time.deltaTime) { g.alpha = Mathf.Lerp(from, to, Ease(t / dur)); yield return null; }
            g.alpha = to;
        }

        private void StartScalePunch(Transform tr, float punch, float dur)
        {
            if (tr == null) return;
            // 既存のアニメがあれば停止して元スケールに戻す
            if (punchRoutines.TryGetValue(tr, out var existing) && existing != null)
            {
                StopCoroutine(existing);
                if (punchOriginalScales.TryGetValue(tr, out var prevOrig))
                    tr.localScale = prevOrig;
            }
            // 真の元スケールを記録（初回のみ）
            if (!punchOriginalScales.ContainsKey(tr))
                punchOriginalScales[tr] = tr.localScale;

            punchRoutines[tr] = StartCoroutine(AnimScalePunch(tr, punch, dur));
        }

        private IEnumerator AnimScalePunch(Transform tr, float punch, float dur)
        {
            var orig = punchOriginalScales[tr];
            var big = orig * punch; float h = dur * 0.4f;
            for (float t = 0; t < h; t += Time.deltaTime) { tr.localScale = Vector3.Lerp(orig, big, Ease(t / h)); yield return null; }
            for (float t = 0; t < dur - h; t += Time.deltaTime) { tr.localScale = Vector3.Lerp(big, orig, Ease(t / (dur - h))); yield return null; }
            tr.localScale = orig;
            punchRoutines.Remove(tr);
        }

        private IEnumerator AnimSlide(RectTransform tr, Vector2 from, Vector2 to, float dur, float delay)
        {
            tr.anchoredPosition = from;
            if (delay > 0f) yield return new WaitForSeconds(delay);
            for (float t = 0; t < dur; t += Time.deltaTime) { tr.anchoredPosition = Vector2.Lerp(from, to, Ease(t / dur)); yield return null; }
            tr.anchoredPosition = to;
        }

        private IEnumerator AnimColorPulse(Graphic g, Color pulse, float dur, int count)
        {
            var orig = g.color; float s = dur / count;
            for (int i = 0; i < count; i++)
            {
                float h = s * 0.5f;
                for (float t = 0; t < h; t += Time.deltaTime) { g.color = Color.Lerp(orig, pulse, t / h); yield return null; }
                for (float t = 0; t < h; t += Time.deltaTime) { g.color = Color.Lerp(pulse, orig, t / h); yield return null; }
            }
            g.color = orig;
        }

        private IEnumerator AnimCountUp(Text txt, int from, int to, float dur, string fmt)
        {
            for (float t = 0; t < dur; t += Time.deltaTime) { txt.text = string.Format(fmt, Mathf.RoundToInt(Mathf.Lerp(from, to, Ease(t / dur)))); yield return null; }
            txt.text = string.Format(fmt, to);
        }

        private IEnumerator AnimShake(RectTransform tr, float intensity, float dur)
        {
            var orig = tr.anchoredPosition;
            for (float t = 0; t < dur; t += Time.deltaTime)
            {
                float d = 1f - t / dur;
                tr.anchoredPosition = orig + new Vector2(Random.Range(-intensity, intensity) * d, Random.Range(-intensity, intensity) * d);
                yield return null;
            }
            tr.anchoredPosition = orig;
        }

        private IEnumerator AnimTypeWriter(Text txt, string full, float charDelay)
        {
            if (string.IsNullOrEmpty(full))
            {
                txt.text = "";
                yield break;
            }

            // 折り返し位置がガクガク動かないように、最初から全文をレイアウトし、
            // 未表示部分を透明色のリッチテキストタグで隠して徐々に可視化する
            // (問題文の < > は QuizGenerator 側で全角に置換済み)
            for (int i = 0; i < full.Length; i++)
            {
                txt.text = full.Substring(0, i) + "<color=#00000000>" + full.Substring(i) + "</color>";
                yield return new WaitForSeconds(charDelay);
            }
            txt.text = full;
        }
    }
}
