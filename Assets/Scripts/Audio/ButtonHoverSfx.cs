using UnityEngine;
using UnityEngine.EventSystems;

namespace GemmaQuiz.Audio
{
    public class ButtonHoverSfx : MonoBehaviour, IPointerEnterHandler
    {
        [SerializeField] private SfxKind kind = SfxKind.Tick;

        public void OnPointerEnter(PointerEventData eventData)
        {
            AudioManager.Play(kind);
        }
    }
}
