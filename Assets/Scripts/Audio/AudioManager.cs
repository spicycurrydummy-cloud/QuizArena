using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemmaQuiz.Audio
{
    public enum SfxKind
    {
        Click,
        Tap,
        Correct,
        Wrong,
        Tick,
        Question
    }

    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [Header("Sources")]
        [SerializeField] private AudioSource bgmSource;
        [SerializeField] private AudioSource sfxSource;

        [Header("BGM")]
        [SerializeField] private AudioClip titleBgm;
        [SerializeField] private AudioClip lobbyBgm;
        [SerializeField] private AudioClip quizBgm;
        [SerializeField] private AudioClip resultBgm;
        [SerializeField] private AudioClip[] quizBgmPool;

        [Header("SFX")]
        [SerializeField] private AudioClip sfxClick;
        [SerializeField] private AudioClip sfxTap;
        [SerializeField] private AudioClip sfxCorrect;
        [SerializeField] private AudioClip sfxWrong;
        [SerializeField] private AudioClip sfxTick;
        [SerializeField] private AudioClip sfxQuestion;

        [Header("Volumes")]
        [SerializeField, Range(0f, 1f)] private float bgmVolume = 0.15f;
        [SerializeField, Range(0f, 1f)] private float sfxVolume = 0.6f;

        private AudioClip currentBgm;
        private bool muted;

        public float BgmVolume => bgmVolume;
        public bool IsMuted => muted;
        public event System.Action OnAudioStateChanged;

        public void SetBgmVolume(float v)
        {
            bgmVolume = Mathf.Clamp01(v);
            ApplyVolumes();
            OnAudioStateChanged?.Invoke();
        }

        public void SetMuted(bool value)
        {
            muted = value;
            ApplyVolumes();
            OnAudioStateChanged?.Invoke();
        }

        public void ToggleMute() => SetMuted(!muted);

        private void ApplyVolumes()
        {
            if (bgmSource != null) bgmSource.volume = muted ? 0f : bgmVolume;
            if (sfxSource != null) sfxSource.volume = muted ? 0f : sfxVolume;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (bgmSource == null)
            {
                bgmSource = gameObject.AddComponent<AudioSource>();
                bgmSource.loop = true;
                bgmSource.playOnAwake = false;
            }
            if (sfxSource == null)
            {
                sfxSource = gameObject.AddComponent<AudioSource>();
                sfxSource.loop = false;
                sfxSource.playOnAwake = false;
            }
            ApplyVolumes();

            SceneManager.sceneLoaded += OnSceneLoaded;
            UpdateBgmForScene(SceneManager.GetActiveScene().name);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            UpdateBgmForScene(scene.name);
        }

        private void UpdateBgmForScene(string sceneName)
        {
            AudioClip target = sceneName switch
            {
                "TitleScene" => titleBgm,
                "LobbyScene" => lobbyBgm,
                "QuizScene" => quizBgm,
                "ResultScene" => resultBgm,
                _ => null
            };
            PlayBgm(target);
        }

        public void PlayBgm(AudioClip clip)
        {
            if (clip == currentBgm && bgmSource.isPlaying) return;
            currentBgm = clip;
            bgmSource.clip = clip;
            if (clip != null) bgmSource.Play();
            else bgmSource.Stop();
        }

        /// <summary>
        /// クイズ用BGMプールから現在と異なる曲をランダム再生する。
        /// ラウンド切り替え時に呼び出される。
        /// </summary>
        public void PlayRandomQuizBgm()
        {
            if (quizBgmPool == null || quizBgmPool.Length == 0) return;

            var candidates = new System.Collections.Generic.List<AudioClip>();
            foreach (var c in quizBgmPool)
                if (c != null && c != currentBgm) candidates.Add(c);

            if (candidates.Count == 0)
            {
                // 全て現在曲と同じ(=プールが1個しかない)ケース
                PlayBgm(quizBgmPool[0]);
                return;
            }

            var picked = candidates[Random.Range(0, candidates.Count)];
            PlayBgm(picked);
        }

        public void PlaySfx(SfxKind kind)
        {
            AudioClip clip = kind switch
            {
                SfxKind.Click => sfxClick,
                SfxKind.Tap => sfxTap,
                SfxKind.Correct => sfxCorrect,
                SfxKind.Wrong => sfxWrong,
                SfxKind.Tick => sfxTick,
                SfxKind.Question => sfxQuestion,
                _ => null
            };
            if (clip != null) sfxSource.PlayOneShot(clip, sfxVolume);
        }

        public static void Play(SfxKind kind) => Instance?.PlaySfx(kind);
    }
}
