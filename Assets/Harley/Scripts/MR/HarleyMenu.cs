using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>
    /// Harley's settings panel, floating in the room. Harley itself stays outside, in 3D.
    /// Open / close it: look at your left wrist and tap the purple button that appears,
    /// or press the menu button on the left controller.
    /// </summary>
    public class HarleyMenu : SpatialPanel
    {
        public CompanionBrain brain;
        public bool showOnStart = true;

        const string PrefFollow = "harley.follow", PrefComments = "harley.comments", PrefVoice = "harley.voice";

        TextMeshProUGUI _status;
        PanelButton _talk;
        bool _menuWasDown;
        float _nextStatus;

        protected override void Awake()
        {
            base.Awake();
            size = new Vector2(0.46f, 0.33f);
        }

        protected override void Start()
        {
            if (brain == null) brain = FindAnyObjectByType<CompanionBrain>();
            base.Start();
            ApplySaved();
            if (showOnStart) Invoke(nameof(ShowNearPlayer), 1.5f);
            else gameObject.SetActive(false);
        }

        protected override void Build()
        {
            var c = Content;
            var title = Label(c, (brain != null && brain.config != null ? brain.config.companionName : "Harley"), 30, text, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            PlaceTL(title.rectTransform, 22, 14, 300, 38);

            _status = Label(c, "", 16, muted, TextAlignmentOptions.Left);
            PlaceTL(_status.rectTransform, 22, 52, 416, 26);

            float y = 88f;
            _talk = AddButton(c, "Talk", Talk, primary, 24);
            PlaceTL(_talk.rect, 20, y, 205, 56);
            PlaceTL(AddButton(c, "Type", Type, null, 24).rect, 235, y, 205, 56);

            y += 66f;
            PlaceTL(AddButton(c, "Browser", () => OpenWeb("https://www.google.com"), null, 22).rect, 20, y, 205, 48);
            PlaceTL(AddButton(c, "YouTube", () => OpenWeb("https://m.youtube.com"), null, 22).rect, 235, y, 205, 48);

            y += 58f;
            var cfg = brain != null ? brain.config : null;
            PlaceTL(AddToggle(c, "Follow me", Pref(PrefFollow, true), v => { Save(PrefFollow, v); Follow(v); }).rect, 20, y, 133, 44);
            PlaceTL(AddToggle(c, "Comments", Pref(PrefComments, cfg == null || cfg.proactiveComments), v => { Save(PrefComments, v); if (cfg) cfg.proactiveComments = v; }).rect, 163, y, 134, 44);
            PlaceTL(AddToggle(c, "Voice", Pref(PrefVoice, cfg == null || cfg.witVoiceOutput), v => { Save(PrefVoice, v); if (cfg) cfg.witVoiceOutput = v; }).rect, 307, y, 133, 44);

            y += 54f;
            PlaceTL(AddButton(c, "Come here", () => brain?.movement?.Perform(CompanionAction.ComeClose), null, 19).rect, 20, y, 133, 44);
            PlaceTL(AddButton(c, "Forget me", ForgetMe, null, 19).rect, 163, y, 134, 44);
            PlaceTL(AddButton(c, "Hide", () => gameObject.SetActive(false), null, 19).rect, 307, y, 133, 44);
        }

        // ---------- actions ----------

        void Talk()
        {
            var voice = brain != null ? brain.voice : null;
            if (voice == null) return;
            if (voice.IsListening) { voice.StopListening(); return; }
            if (!voice.HasDictation) { Type(); return; } // no voice set up yet: type instead
            brain.interaction?.RequestTalk();
        }

        void Type()
        {
            HarleyKeyboard.Open("", "Say something to Harley", t =>
            {
                if (!string.IsNullOrWhiteSpace(t)) brain?.SubmitText(t.Trim());
            });
        }

        void OpenWeb(string url)
        {
            // Reuse a window if one is open, so they don't pile up.
            if (WebPanel.Windows.Count >= 2)
            {
                var w = WebPanel.Windows[WebPanel.Windows.Count - 1];
                w.Load(url);
                w.ShowInFront(0.95f, 0.05f);
                return;
            }
            WebPanel.Create(url, head);
            gameObject.SetActive(false); // get the menu out of the way
        }

        void ForgetMe()
        {
            brain?.memory?.ForgetAll();
            brain?.voice?.ShowStatus("(I forgot everything about you)", 3f);
        }

        void Follow(bool on) => brain?.movement?.Perform(on ? CompanionAction.Follow : CompanionAction.Stay);

        void ApplySaved()
        {
            var cfg = brain != null ? brain.config : null;
            if (cfg)
            {
                cfg.proactiveComments = Pref(PrefComments, cfg.proactiveComments);
                cfg.witVoiceOutput = Pref(PrefVoice, cfg.witVoiceOutput);
            }
            if (!Pref(PrefFollow, true)) Follow(false);
        }

        static bool Pref(string key, bool fallback) => PlayerPrefs.GetInt(key, fallback ? 1 : 0) == 1;
        static void Save(string key, bool v) { PlayerPrefs.SetInt(key, v ? 1 : 0); PlayerPrefs.Save(); }

        // ---------- open / close ----------

        public void Toggle()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
            else ShowNearPlayer();
        }

        void ShowNearPlayer() => ShowInFront(0.55f, 0.18f, -0.12f);

        protected override void Update()
        {
            base.Update();
            if (Time.unscaledTime >= _nextStatus) UpdateStatus();
        }

        void UpdateStatus()
        {
            _nextStatus = Time.unscaledTime + 0.3f;
            if (_status == null) return;
            var cfg = brain != null ? brain.config : null;
            var voice = brain != null ? brain.voice : null;
            string s;
            if (cfg == null || !cfg.HasBrainKey) s = "<color=#F87171>No " + (cfg != null ? cfg.provider.ToString() : "AI") + " API key in the config</color>";
            else if (voice != null && voice.IsListening) s = "<color=#4ADE80>Listening...</color>";
            else if (voice != null && !voice.HasDictation) s = "Talk needs a free Wit token (config). Type works now.";
            else s = "Talk, or just look at Harley to talk.";
            _status.text = s;
            if (_talk != null && _talk.label != null) _talk.label.text = voice != null && voice.IsListening ? "Stop" : "Talk";
        }

        /// <summary>Left controller menu button toggles the menu (runs on the always-active watcher).</summary>
        public void PollMenuButton()
        {
            InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            bool down = false;
            if (left.isValid) left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out down);
            if (down && !_menuWasDown) Toggle();
            _menuWasDown = down;
        }
    }
}
