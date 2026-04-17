using UnityEngine;
using UnityEngine.UI;

namespace GemmaQuiz.Audio
{
    /// <summary>
    /// 右上に常駐する BGM 音量スライダーとミュートボタンの制御。
    /// AudioManager と同じ DontDestroyOnLoad な GameObject 配下の Canvas に配置する。
    /// </summary>
    public class AudioControlUI : MonoBehaviour
    {
        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Button muteButton;
        [SerializeField] private Text muteButtonLabel;

        private void Start()
        {
            var am = AudioManager.Instance;
            if (am == null) return;

            if (volumeSlider != null)
            {
                volumeSlider.minValue = 0f;
                volumeSlider.maxValue = 1f;
                volumeSlider.value = am.BgmVolume;
                volumeSlider.onValueChanged.AddListener(OnSliderChanged);
            }

            if (muteButton != null)
                muteButton.onClick.AddListener(OnMuteClicked);

            am.OnAudioStateChanged += RefreshMuteLabel;
            RefreshMuteLabel();
        }

        private void OnDestroy()
        {
            var am = AudioManager.Instance;
            if (am != null) am.OnAudioStateChanged -= RefreshMuteLabel;
        }

        private void OnSliderChanged(float v) => AudioManager.Instance?.SetBgmVolume(v);
        private void OnMuteClicked() => AudioManager.Instance?.ToggleMute();

        private void RefreshMuteLabel()
        {
            if (muteButtonLabel == null) return;
            var am = AudioManager.Instance;
            muteButtonLabel.text = (am != null && am.IsMuted) ? "OFF" : "ON";
        }
    }
}
