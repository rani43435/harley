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
            size = new Vector2(0.48f, 0.47f);
        }

        protected override void Start()
        {
            if (brain == null) brain = FindAnyObjectByType<CompanionBrain>();
            base.Start();
            ApplySaved();
            if (showOnStart) Invoke(nameof(ShowNearPlayer), 1.5f);
            else gameObject.SetActive(false);
        }

        readonly PanelButton[] _toolButtons = new PanelButton[5];
        PanelButton _world, _option;

        protected override void Build()
        {
            var c = Content;
            const float L = 22f, W = 436f;
            var title = Label(c, (brain != null && brain.config != null ? brain.config.companionName : "Harley"), 30, text, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            PlaceTL(title.rectTransform, L, 14, 300, 38);

            _status = Label(c, "", 16, muted, TextAlignmentOptions.Left);
            PlaceTL(_status.rectTransform, L, 52, W, 26);

            // Talk / Type
            float y = 86f;
            _talk = AddButton(c, "Talk", Talk, primary, 24);
            PlaceTL(_talk.rect, L, y, 213, 54);
            PlaceTL(AddButton(c, "Type", Type, null, 24).rect, L + 223, y, 213, 54);

            // Apps / Room-World
            y += 64f;
            PlaceTL(AddButton(c, "Apps", () => { LauncherPanel.Show(head); gameObject.SetActive(false); }, null, 22).rect, L, y, 213, 48);
            _world = AddButton(c, "View: My room", ToggleWorld, null, 19);
            PlaceTL(_world.rect, L + 223, y, 213, 48);

            // Play tools
            y += 60f;
            var playLabel = Label(c, "Play", 15, muted, TextAlignmentOptions.Left);
            PlaceTL(playLabel.rectTransform, L, y, 100, 20);
            y += 22f;
            string[] names = { "Draw", "Add", "Break", "Cover", "Fetch" };
            float bw = (W - 4 * 6f) / 5f;
            for (int i = 0; i < 5; i++)
            {
                var tool = (HarleyTools.Tool)(i + 1);
                _toolButtons[i] = AddButton(c, names[i], () => PickTool(tool), null, 17);
                PlaceTL(_toolButtons[i].rect, L + i * (bw + 6f), y, bw, 42);
            }
            y += 48f;
            _option = AddButton(c, "Colour", NextOption, null, 16);
            PlaceTL(_option.rect, L, y, 140, 36);
            PlaceTL(AddButton(c, "Clear all", () => HarleyTools.Instance?.ClearAll(), null, 16).rect, L + 148, y, 140, 36);
            PlaceTL(AddButton(c, "Hide & seek", () => { gameObject.SetActive(false); brain?.PlayHideAndSeek(); }, null, 16).rect, L + 296, y, 140, 36);

            // Settings
            y += 50f;
            var cfg = brain != null ? brain.config : null;
            PlaceTL(AddToggle(c, "Follow me", Pref(PrefFollow, true), v => { Save(PrefFollow, v); Follow(v); }).rect, L, y, 140, 42);
            PlaceTL(AddToggle(c, "Comments", Pref(PrefComments, cfg == null || cfg.proactiveComments), v => { Save(PrefComments, v); if (cfg) cfg.proactiveComments = v; }).rect, L + 148, y, 140, 42);
            PlaceTL(AddToggle(c, "Voice", Pref(PrefVoice, cfg == null || cfg.witVoiceOutput), v => { Save(PrefVoice, v); if (cfg) { cfg.witVoiceOutput = v; cfg.deviceVoice = v; } }).rect, L + 296, y, 140, 42);

            y += 50f;
            PlaceTL(AddButton(c, "Come here", () => brain?.movement?.Perform(CompanionAction.ComeClose), null, 18).rect, L, y, 140, 42);
            PlaceTL(AddButton(c, "Forget me", ForgetMe, null, 18).rect, L + 148, y, 140, 42);
            PlaceTL(AddButton(c, "Close", () => gameObject.SetActive(false), null, 18).rect, L + 296, y, 140, 42);
        }

        void PickTool(HarleyTools.Tool t)
        {
            var tools = HarleyTools.Instance;
            if (tools == null) return;
            tools.SetTool(t);
            gameObject.SetActive(false); // out of the way: go play
            string hint = tools.Current switch
            {
                HarleyTools.Tool.Draw => "(Draw: hold the trigger or pinch and move)",
                HarleyTools.Tool.Add => "(Add: point at a table or the floor and pull the trigger)",
                HarleyTools.Tool.Break => "(Break: point at something and pull the trigger)",
                HarleyTools.Tool.Cover => "(Cover: point at furniture to hide it, again to uncover)",
                HarleyTools.Tool.Fetch => "(Fetch: hold the trigger, swing, let go to throw)",
                _ => "(Play tools off)"
            };
            brain?.voice?.ShowStatus(hint, 4f);
        }

        void NextOption()
        {
            var tools = HarleyTools.Instance;
            if (tools == null) return;
            if (tools.Current == HarleyTools.Tool.Add) tools.NextItem(); else tools.NextInk();
        }

        void ToggleWorld()
        {
            HarleyWorld.Instance?.Toggle(); // My room -> Room + world -> Harley's world -> My room
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
            gameObject.SetActive(false); // the keyboard takes its place
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
                cfg.deviceVoice = cfg.witVoiceOutput;
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
            var tools = HarleyTools.Instance;
            for (int i = 0; i < _toolButtons.Length; i++)
                if (_toolButtons[i] != null) _toolButtons[i].baseColor = tools != null && (int)tools.Current == i + 1 ? primary : button;
            if (_option != null && _option.label != null)
                _option.label.text = tools != null && tools.Current == HarleyTools.Tool.Add ? "Thing: " + tools.AddItemName : "Colour";
            if (_option != null && _option.image != null && tools != null && tools.Current != HarleyTools.Tool.Add) _option.baseColor = tools.InkColor * new Color(1, 1, 1, 0.55f);
            else if (_option != null) _option.baseColor = button;
            if (_world != null && _world.label != null && HarleyWorld.Instance != null) _world.label.text = "View: " + HarleyWorld.Label(HarleyWorld.Instance.Current);
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
