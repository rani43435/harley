using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley's own floating keyboard: tap the keys with your finger, or point and pinch / pull the trigger.
    /// (The Quest system keyboard doesn't open reliably inside apps like this, so Harley brings her own.)
    /// </summary>
    public class HarleyKeyboard : SpatialPanel
    {
        static HarleyKeyboard _instance;

        public static bool IsOpen => _instance != null && _instance.gameObject.activeSelf;

        public static void Open(string text, string placeholder, Action<string> done, Action cancelled = null)
        {
            if (_instance == null)
            {
                var go = new GameObject("Harley Keyboard");
                _instance = go.AddComponent<HarleyKeyboard>();
                _instance.head = Camera.main != null ? Camera.main.transform : null;
                _instance.EnsureBuilt();
            }
            _instance.Begin(text, placeholder, done, cancelled);
        }

        const float KeyW = 54f, KeyH = 54f, Gap = 7f, Pad = 18f, FieldH = 58f;
        static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" };

        Action<string> _done;
        Action _cancelled;
        string _text = "", _placeholder = "";
        bool _shift;
        TextMeshProUGUI _field;
        readonly List<(PanelButton b, char c)> _letters = new List<(PanelButton, char)>();
        PanelButton _shiftKey;

        protected override void Awake()
        {
            base.Awake();
            float w = Pad * 2 + 10 * KeyW + 9 * Gap;
            float h = Pad * 2 + FieldH + Gap + 5 * KeyH + 4 * Gap + 6f;
            size = new Vector2(w / PixelsPerMeter, h / PixelsPerMeter);
        }

        protected override void Build()
        {
            var c = Content;
            float w = Pad * 2 + 10 * KeyW + 9 * Gap;

            // Text field
            var field = Box(c, "Field", new Color(1f, 1f, 1f, 0.07f));
            PlaceTL(field.rectTransform, Pad, Pad, w - Pad * 2 - 60f, FieldH);
            _field = Label(field.rectTransform, "", 22, text, TextAlignmentOptions.Left);
            Place(_field.rectTransform, Vector2.zero, Vector2.one, new Vector2(14, 4), new Vector2(-14, -4));
            _field.textWrappingMode = TextWrappingModes.NoWrap;
            _field.overflowMode = TextOverflowModes.Ellipsis;
            PlaceTL(AddButton(c, "X", Cancel, button, 20).rect, w - Pad - 52f, Pad, 52f, FieldH);

            float y = Pad + FieldH + Gap + 6f;
            for (int r = 0; r < Rows.Length; r++)
            {
                string row = Rows[r];
                float rowW = row.Length * KeyW + (row.Length - 1) * Gap;
                float x = (w - rowW) / 2f;
                if (r == 3) x = Pad + KeyW * 1.5f + Gap; // room for shift on the left
                foreach (char ch in row)
                {
                    char k = ch;
                    var b = AddButton(c, k.ToString(), () => Type(_shift && char.IsLetter(k) ? char.ToUpperInvariant(k) : k), null, 24);
                    PlaceTL(b.rect, x, y, KeyW, KeyH);
                    if (char.IsLetter(k)) _letters.Add((b, k));
                    x += KeyW + Gap;
                }
                if (r == 3)
                {
                    _shiftKey = AddButton(c, "shift", ToggleShift, null, 16);
                    PlaceTL(_shiftKey.rect, Pad, y, KeyW * 1.5f, KeyH);
                    PlaceTL(AddButton(c, "del", Backspace, null, 16).rect, x, y, w - Pad - x, KeyH);
                }
                y += KeyH + Gap;
            }

            // Bottom row: symbols, space, send
            float bx = Pad;
            foreach (string s in new[] { "@", ",", "." })
            {
                string sym = s;
                PlaceTL(AddButton(c, sym, () => Type(sym[0]), null, 22).rect, bx, y, KeyW, KeyH);
                bx += KeyW + Gap;
            }
            float sendW = KeyW * 2f + Gap;
            float spaceW = w - Pad - bx - (KeyW + Gap) - sendW - Gap;
            PlaceTL(AddButton(c, "space", () => Type(' '), null, 16).rect, bx, y, spaceW, KeyH);
            bx += spaceW + Gap;
            PlaceTL(AddButton(c, "?", () => Type('?'), null, 22).rect, bx, y, KeyW, KeyH);
            bx += KeyW + Gap;
            PlaceTL(AddButton(c, "Send", Done, primary, 20).rect, bx, y, sendW, KeyH);
        }

        void Begin(string text, string placeholder, Action<string> done, Action cancelled)
        {
            _done = done;
            _cancelled = cancelled;
            _text = text ?? "";
            _placeholder = placeholder ?? "";
            _shift = _text.Length == 0;
            if (head == null && Camera.main != null) head = Camera.main.transform;
            gameObject.SetActive(true);
            ShowInFront(0.45f, 0.28f);
            Refresh();
        }

        void Type(char ch)
        {
            if (_text.Length >= 400) return;
            _text += ch;
            if (_shift && char.IsLetter(ch)) _shift = false; // one capital, like a phone keyboard
            Refresh();
        }

        void Backspace()
        {
            if (_text.Length > 0) _text = _text.Substring(0, _text.Length - 1);
            Refresh();
        }

        void ToggleShift() { _shift = !_shift; Refresh(); }

        void Done()
        {
            var d = _done; _done = null; _cancelled = null;
            gameObject.SetActive(false);
            d?.Invoke(_text);
        }

        void Cancel()
        {
            var c = _cancelled; _done = null; _cancelled = null;
            gameObject.SetActive(false);
            c?.Invoke();
        }

        void Refresh()
        {
            if (_field != null)
            {
                bool empty = _text.Length == 0;
                _field.text = empty ? _placeholder : _text + "<color=#8B5CF6>|</color>";
                _field.color = empty ? muted : text;
            }
            foreach (var (b, ch) in _letters)
                if (b.label) b.label.text = (_shift ? char.ToUpperInvariant(ch) : ch).ToString();
            if (_shiftKey != null) _shiftKey.baseColor = _shift ? primary : button;
        }
    }
}
