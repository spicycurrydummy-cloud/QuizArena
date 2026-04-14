using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GemmaQuiz.UI
{
    /// <summary>
    /// タップと長押しを区別するボタンコンポーネント。
    /// </summary>
    public class LongPressButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public float longPressThreshold = 0.5f;

        public event Action OnClick;
        public event Action OnLongPress;

        private bool isPointerDown;
        private float pointerDownTime;
        private bool longPressFired;

        public void OnPointerDown(PointerEventData eventData)
        {
            isPointerDown = true;
            longPressFired = false;
            pointerDownTime = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!isPointerDown) return;
            isPointerDown = false;

            if (!longPressFired)
            {
                OnClick?.Invoke();
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isPointerDown = false;
        }

        private void Update()
        {
            if (!isPointerDown || longPressFired) return;

            if (Time.unscaledTime - pointerDownTime >= longPressThreshold)
            {
                longPressFired = true;
                OnLongPress?.Invoke();
            }
        }
    }
}
