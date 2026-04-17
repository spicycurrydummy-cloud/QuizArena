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
        Tick
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

        [Header("SFX")]
        [SerializeField] private AudioClip sfxClick;
        [SerializeField] private AudioClip sfxTap;
        [SerializeField] private AudioClip sfxCorrect;
        [SerializeField] private AudioClip sfxWrong;
        [SerializeField] private AudioClip sfxTick;

        [Header("Volumes")]
        [SerializeField, Range(0f, 1f)] private float bgmVolume = 0.35f;
        [SerializeField, Range(0f, 1f)] private float sfxVolume = 0.8f;

        private AudioClip currentBgm;

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
            bgmSource.volume = bgmVolume;
            sfxSource.volume = sfxVolume;

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

        public void PlaySfx(SfxKind kind)
        {
            AudioClip clip = kind switch
            {
                SfxKind.Click => sfxClick,
                SfxKind.Tap => sfxTap,
                SfxKind.Correct => sfxCorrect,
                SfxKind.Wrong => sfxWrong,
                SfxKind.Tick => sfxTick,
                _ => null
            };
            if (clip != null) sfxSource.PlayOneShot(clip, sfxVolume);
        }

        public static void Play(SfxKind kind) => Instance?.PlaySfx(kind);
    }
}
