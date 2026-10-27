using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Harley
{
    /// <summary>
    /// Window app version: the buttons in Harley's window, built at runtime.
    ///  Talk   - start / stop listening
    ///  Type   - type a message with the Quest keyboard
    ///  Screen - start / stop screen sharing (so Harley sees what you see)
    /// plus a small status line that says whether Harley can see your screen.
    /// </summary>
    public class PanelControls : MonoBehaviour
    {
        public CompanionBrain brain;
        public CompanionVoice voice;
        public CompanionInput input;
        public ScreenEyes screen;

        [Header("Colours")]
        public Color barButton = new Color(0.141f, 0.110f, 0.220f, 0.92f);   // #241C38
        public Color primary = new Color(0.545f, 0.361f, 0.965f, 1f);        // #8B5CF6
        public Color listening = new Color(0.086f, 0.639f, 0.290f, 1f);      // #16A34A
        public Color text = new Color(0.925f, 0.910f, 0.969f, 1f);           // #ECE8F7
        public Color muted = new Color(0.612f, 0.584f, 0.710f, 1f);          // #9C95B5

        TextMeshProUGUI _status, _talkLabel, _screenLabel;
        Image _talkImage;
        GameObject _typeRow;
        TMP_InputField _field;
        float _nextStatus;

        void Start()
        {
            if (!brain) brain = FindAnyObjectByType<CompanionBrain>();
            if (!voice && brain) voice = brain.voice;
            if (!input && brain) input = brain.interaction;
            if (!screen && brain) screen = brain.eyes as ScreenEyes;
            Build();
        }

        void Build()
        {
            EnsureEventSystem();

            var canvasGO = new GameObject("Harley UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(560, 720);
            scaler.matchWidthOrHeight = 0.5f;
            var root = (RectTransform)canvasGO.transform;

            // Status line (top-left)
            _status = Label(root, "Status", "", 19, muted, TextAlignmentOptions.Left);
            Place(_status.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(20, -52), new Vector2(-20, -14));

            // Bottom bar: Talk / Type / Screen
            var talk = MakeButton(root, "Talk", primary, OnTalk, out _talkLabel);
            Place((RectTransform)talk.transform, new Vector2(0, 0), new Vector2(0.44f, 0), new Vector2(16, 16), new Vector2(-6, 84));
            _talkImage = talk.GetComponent<Image>();

            var type = MakeButton(root, "Type", barButton, OnType, out _);
            Place((RectTransform)type.transform, new Vector2(0.44f, 0), new Vector2(0.7f, 0), new Vector2(6, 16), new Vector2(-6, 84));

            var share = MakeButton(root, "Screen", barButton, OnScreen, out _screenLabel);
            Place((RectTransform)share.transform, new Vector2(0.7f, 0), new Vector2(1, 0), new Vector2(6, 16), new Vector2(-16, 84));

            // Type row (hidden until Type is pressed)
            _typeRow = new GameObject("Type Row", typeof(RectTransform));
            _typeRow.transform.SetParent(root, false);
            Place((RectTransform)_typeRow.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 96), new Vector2(-16, 152));
            _field = MakeInputField((RectTransform)_typeRow.transform);
            Place((RectTransform)_field.transform, new Vector2(0, 0), new Vector2(0.75f, 1), Vector2.zero, new Vector2(-6, 0));
            var send = MakeButton((RectTransform)_typeRow.transform, "Send", primary, Submit, out _);
            Place((RectTransform)send.transform, new Vector2(0.75f, 0), new Vector2(1, 1), new Vector2(6, 0), Vector2.zero);
            _typeRow.SetActive(false);

            UpdateStatus();
        }

        void Update()
        {
            if (Time.unscaledTime >= _nextStatus) UpdateStatus();

            bool isListening = voice != null && voice.IsListening;
            if (_talkLabel) _talkLabel.text = isListening ? "Listening... tap to stop" : "Talk";
            if (_talkImage) _talkImage.color = isListening ? listening : primary;
        }

        void UpdateStatus()
        {
            _nextStatus = Time.unscaledTime + 0.5f;
            bool sees = screen != null && screen.IsSharing;
            if (_status)
            {
                _status.text = sees ? "<color=#4ADE80>Seeing your screen</color>" : "Can't see your screen. Tap Screen to share it.";
                _status.color = sees ? text : muted;
            }
            if (_screenLabel) _screenLabel.text = sees ? "Stop screen" : "Screen";
        }

        // ---------- buttons ----------

        void OnTalk()
        {
            if (voice != null && voice.IsListening) { voice.StopListening(); return; }
            if (voice == null || !voice.HasDictation) { OnType(); return; } // no speech-to-text set up: type instead
            input?.RequestTalk();
        }

        void OnType()
        {
            bool show = !_typeRow.activeSelf;
            _typeRow.SetActive(show);
            if (show) { _field.text = ""; _field.ActivateInputField(); }
        }

        void OnScreen()
        {
            if (screen == null) return;
            if (screen.IsSharing) screen.StopSharing(); else screen.StartSharing();
            _nextStatus = Time.unscaledTime + 1f;
        }

        void Submit()
        {
            string msg = _field != null ? _field.text.Trim() : "";
            if (msg.Length > 0) brain?.SubmitText(msg);
            if (_field != null) _field.text = "";
            _typeRow.SetActive(false);
        }

        // ---------- building blocks ----------

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        TextMeshProUGUI Label(RectTransform parent, string name, string value, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = value;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            return t;
        }

        Button MakeButton(RectTransform parent, string label, Color color, UnityEngine.Events.UnityAction onClick, out TextMeshProUGUI text)
        {
            var go = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var b = go.GetComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(onClick);
            text = Label((RectTransform)go.transform, "Label", label, 24, this.text, TextAlignmentOptions.Center);
            Place(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(8, 0), new Vector2(-8, 0));
            return b;
        }

        TMP_InputField MakeInputField(RectTransform parent)
        {
            var go = new GameObject("Message", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.06f, 0.05f, 0.09f, 0.95f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            var areaRt = (RectTransform)area.transform;
            Place(areaRt, Vector2.zero, Vector2.one, new Vector2(14, 6), new Vector2(-14, -6));

            var placeholder = Label(areaRt, "Placeholder", "Write to Harley...", 22, muted, TextAlignmentOptions.Left);
            Place(placeholder.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var value = Label(areaRt, "Text", "", 22, text, TextAlignmentOptions.Left);
            Place(value.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var field = go.AddComponent<TMP_InputField>();
            field.textViewport = areaRt;
            field.textComponent = value;
            field.placeholder = placeholder;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.onSubmit.AddListener(_ => Submit());
            return field;
        }

        static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }
    }
}
