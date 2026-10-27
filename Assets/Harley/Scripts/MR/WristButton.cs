using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// A small purple "H" button that appears over your left hand when you look at it
    /// (hand or controller). Tap it with your right finger, or point and click, to open Harley's menu.
    /// Also polls the left controller's menu button.
    /// </summary>
    public class WristButton : SpatialPanel
    {
        public HarleyMenu menu;
        [Tooltip("Left hand / controller anchor. Found by name (LeftHandAnchor) if empty.")]
        public Transform leftHand;
        [Tooltip("Shows up when you look at your hand closer than this angle.")]
        public float lookAngle = 28f;
        public float maxDistance = 0.65f;

        GameObject _visual;
        bool _visible;
        float _nextSearch, _shownAt;

        protected override void Awake()
        {
            base.Awake();
            size = new Vector2(0.05f, 0.05f);
            barHeight = 0f;
        }

        protected override void Build()
        {
            var c = Content;
            var b = AddButton(c, "H", () => menu?.Toggle(), primary, 30);
            Place(b.rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            b.label.fontStyle = FontStyles.Bold;
            Content.GetComponent<UnityEngine.UI.Image>().color = new Color(0, 0, 0, 0);
            _visual = Root.gameObject;
            _visual.SetActive(false);
        }

        public override bool Interactable => base.Interactable && _visible;

        protected override void Update()
        {
            base.Update();
            if (menu != null) menu.PollMenuButton();

            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (leftHand == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 2f;
                foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (t.name == "LeftHandAnchor") { leftHand = t; break; }
            }

            bool show = false;
            if (head != null && leftHand != null && leftHand.gameObject.activeInHierarchy)
            {
                Vector3 to = leftHand.position - head.position;
                show = to.magnitude < maxDistance && Vector3.Angle(head.forward, to) < lookAngle;
                // Stay a moment after you look away, so you can move your finger to it.
                if (show) _shownAt = Time.unscaledTime;
                else show = _visible && Time.unscaledTime - _shownAt < 0.8f;

                if (show)
                {
                    Vector3 towardHead = (head.position - leftHand.position).normalized;
                    transform.position = leftHand.position + Vector3.up * 0.06f + towardHead * 0.03f;
                    FaceHead();
                }
            }

            if (show != _visible)
            {
                _visible = show;
                if (_visual) _visual.SetActive(show);
            }
        }
    }
}
