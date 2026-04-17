using UnityEngine;
using UnityEngine.UI;

namespace GemmaQuiz.Audio
{
    [RequireComponent(typeof(Button))]
    public class ButtonClickSfx : MonoBehaviour
    {
        [SerializeField] private SfxKind kind = SfxKind.Click;

        private void Awake()
        {
            var btn = GetComponent<Button>();
            btn.onClick.AddListener(() => AudioManager.Play(kind));
        }
    }
}
