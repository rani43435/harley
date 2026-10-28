using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Harley
{
    /// <summary>
    /// A floating glass panel in the room with buttons, built in code.
    ///  - Point + trigger/pinch (or touch with a finger) to press buttons.
    ///  - Grab the bar at the bottom to move the panel; it turns to face you.
    /// HarleyMenu and WebPanel build on this.
    /// </summary>
    public class SpatialPanel : PanelSurface
    {
        public const float PixelsPerMeter = 1000f;

        [Header("Look")]
        // Calm dark glass, like Quest's own windows, with Harley's purple as the accent
        public Color background = new Color(0.105f, 0.105f, 0.125f, 0.95f);
        public Color button = new Color(1f, 1f, 1f, 0.09f);
        public Color buttonHover = new Color(1f, 1f, 1f, 0.16f);
        public Color primary = new Color(0.545f, 0.361f, 0.965f, 1f);         // #8B5CF6
        public Color on = new Color(0.086f, 0.639f, 0.290f, 1f);              // #16A34A
        public Color text = new Color(0.96f, 0.96f, 0.98f, 1f);
        public Color muted = new Color(0.66f, 0.66f, 0.72f, 1f);

        public Transform head;
        [Tooltip("Height of the grab bar under the panel, meters.")]
        public float barHeight = 0.028f;

        public class PanelButton
        {
            public RectTransform rect;
            public Image image;
            public TextMeshProUGUI label;
            public Action onClick;
            public Color baseColor;
            public bool isToggle, value;
            public bool hovered;
            public float pressedAt = -1f;
        }

        protected RectTransform Root { get; private set; }
        protected Canvas Canvas { get; private set; }
        readonly List<PanelButton> _buttons = new List<PanelButton>();
        PanelButton _pressed;
        Image _bar;
        bool _dragging;
        float _dragDistance;
        Vector3 _dragOffset;
        bool _built;

        static Sprite _rounded;

        protected virtual void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
        }

        protected virtual void Start() => EnsureBuilt();

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            BuildCanvas();
            Build();
        }

        /// <summary>Add your content here (subclasses).</summary>
        protected virtual void Build() { }

        void BuildCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one / PixelsPerMeter;
            Canvas = go.GetComponent<Canvas>();
            Canvas.renderMode = RenderMode.WorldSpace;
            go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            Root = (RectTransform)go.transform;
            Root.sizeDelta = new Vector2(size.x, size.y + barHeight) * PixelsPerMeter;
            Root.localPosition = Vector3.zero;
            Root.localRotation = Quaternion.identity;

            // Card
            var card = Box(Root, "Background", background);
            card.pixelsPerUnitMultiplier = 0.55f; // big soft corners
            Place(card.rectTransform, Vector2.zero, Vector2.one, new Vector2(0, barHeight * PixelsPerMeter), Vector2.zero);

            // Grab bar under the card
            if (barHeight > 0f)
            {
                _bar = Box(Root, "Grab Bar", new Color(1f, 1f, 1f, 0.22f));
                float barPx = barHeight * PixelsPerMeter;
                Place(_bar.rectTransform, new Vector2(0.38f, 0), new Vector2(0.62f, 0), new Vector2(0, barPx * 0.3f), new Vector2(0, barPx * 0.62f));
            }

            // The surface includes the bar
            size = new Vector2(size.x, size.y + barHeight);
        }

        /// <summary>The card area (without the grab bar) to put content in.</summary>
        protected RectTransform Content => (RectTransform)Root.Find("Background");

        // ---------- pointer ----------

        public override void PointerHover(PanelPointer p)
        {
            foreach (var b in _buttons) b.hovered = b.rect.gameObject.activeInHierarchy && Contains(b.rect, p.world);
        }

        public override void PointerExit(int id)
        {
            foreach (var b in _buttons) b.hovered = false;
        }

        public override void PointerDown(PanelPointer p)
        {
            if (OnBar(p.world))
            {
                _dragging = true;
                _dragDistance = p.distance;
                _dragOffset = transform.position - p.world;
                return;
            }
            _pressed = null;
            foreach (var b in _buttons)
                if (b.rect.gameObject.activeInHierarchy && Contains(b.rect, p.world)) { _pressed = b; break; }
            if (_pressed == null) ContentDown(p);
        }

        public override void PointerDrag(PanelPointer p)
        {
            if (_dragging)
            {
                Vector3 target = p.origin + p.direction * _dragDistance + _dragOffset;
                transform.position = target;
                FaceHead();
                return;
            }
            if (_pressed == null) ContentDrag(p);
        }

        public override void PointerUp(PanelPointer p)
        {
            if (_dragging) { _dragging = false; return; }
            if (_pressed != null)
            {
                var b = _pressed;
                _pressed = null;
                if (b.rect.gameObject.activeInHierarchy && Contains(b.rect, p.world))
                {
                    b.pressedAt = Time.unscaledTime;
                    if (b.isToggle) SetToggle(b, !b.value);
                    try { b.onClick?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
                }
                return;
            }
            ContentUp(p);
        }

        /// <summary>Pointer pressed on the panel but not on a button (web pages use this).</summary>
        protected virtual void ContentDown(PanelPointer p) { }
        protected virtual void ContentDrag(PanelPointer p) { }
        protected virtual void ContentUp(PanelPointer p) { }

        bool OnBar(Vector3 world)
        {
            if (_bar == null) return false;
            // A bit bigger than it looks, easier to grab.
            Vector3 lp = _bar.rectTransform.InverseTransformPoint(world);
            Rect r = _bar.rectTransform.rect;
            r.xMin -= 30; r.xMax += 30; r.yMin -= 14; r.yMax += 10;
            return r.Contains(lp);
        }

        static bool Contains(RectTransform rt, Vector3 world)
        {
            Vector3 lp = rt.InverseTransformPoint(world);
            return rt.rect.Contains(lp);
        }

        protected virtual void Update()
        {
            foreach (var b in _buttons)
            {
                if (b.image == null) continue;
                Color c = b.isToggle && b.value ? on : b.baseColor;
                if (b.hovered) c = Color.Lerp(c, Color.white, 0.18f);
                if (Time.unscaledTime - b.pressedAt < 0.12f) c = Color.Lerp(c, Color.white, 0.45f);
                b.image.color = c;
            }
            if (_bar) _bar.color = new Color(1f, 1f, 1f, _dragging ? 0.6f : 0.22f);
        }

        // ---------- placing ----------

        /// <summary>Put the panel in front of the player, facing them.</summary>
        public void ShowInFront(float distance = 0.6f, float down = 0.12f, float side = 0f)
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (head == null) return;
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = head.up;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            transform.position = head.position + fwd * distance + Vector3.down * down + right * side;
            FaceHead();
            gameObject.SetActive(true);
        }

        public void FaceHead()
        {
            if (head == null) return;
            Vector3 away = transform.position - head.position;
            away.y *= 0.5f; // stay mostly upright
            if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        // ---------- building blocks ----------

        public PanelButton AddButton(RectTransform parent, string label, Action onClick, Color? color = null, float fontSize = 22f)
        {
            var img = Box(parent, label + " Button", color ?? button);
            var b = new PanelButton { rect = img.rectTransform, image = img, onClick = onClick, baseColor = color ?? button };
            b.label = Label(img.rectTransform, label, fontSize, text, TextAlignmentOptions.Center);
            Place(b.label.rectTransform, Vector2.zero, Vector2.one, new Vector2(10, 0), new Vector2(-10, 0));
            _buttons.Add(b);
            return b;
        }

        public PanelButton AddToggle(RectTransform parent, string label, bool value, Action<bool> changed)
        {
            PanelButton b = null;
            b = AddButton(parent, label, () => changed?.Invoke(b.value), button, 19f);
            b.isToggle = true;
            SetToggle(b, value);
            return b;
        }

        public void SetToggle(PanelButton b, bool value)
        {
            b.value = value; // shown by colour: green = on
        }

        public void RemoveButton(PanelButton b)
        {
            _buttons.Remove(b);
            if (b?.rect != null) Destroy(b.rect.gameObject);
        }

        public Image Box(RectTransform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = RoundedSprite();
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1.4f;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public TextMeshProUGUI Label(RectTransform parent, string value, float fontSize, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = value;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        public static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        /// <summary>Place in pixels from the card's top-left: x, y down, width, height.</summary>
        public static void PlaceTL(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>A white rounded-rectangle sprite, made once, 9-sliced for any size.</summary>
        static Sprite RoundedSprite()
        {
            if (_rounded != null) return _rounded;
            const int s = 64, r = 18;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, r, s - r), cy = Mathf.Clamp(y + 0.5f, r, s - r);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255f);
                px[y * s + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _rounded = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return _rounded;
        }
    }
}
