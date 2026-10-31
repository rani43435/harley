using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>
    /// Harley's menu, floating in the room. Four pages on a rail at the left:
    /// Home (talk, type, come here, screens, apps), Play (fetch, sword fight, hide and seek, learn + language),
    /// World (my room, the 360 views, Harley's world, apps) and Settings (toggles, computer, forget me).
    /// Open / close it: the purple wrist button, the left controller menu button, or the hand menu gesture.
    /// </summary>
    public class HarleyMenu : SpatialPanel
    {
        public CompanionBrain brain;
        public bool showOnStart = true;

        const string PrefFollow = "harley.follow", PrefComments = "harley.comments", PrefVoice = "harley.voice", PrefWake = "harley.wake";

        // Layout (pixels on the card, 1 px = 1 mm)
        const float W = 620f, H = 460f, RailW = 88f, X0 = 112f, CW = 484f, Top = 88f;

        static readonly Color Card = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color CardOn = new Color(0.545f, 0.361f, 0.965f, 0.30f);
        static readonly Color Muted = new Color(0.655f, 0.627f, 0.741f, 1f);
        static readonly Color Faint = new Color(0.56f, 0.525f, 0.68f, 1f);
        static readonly Color Lilac = new Color(0.77f, 0.69f, 0.98f, 1f);
        static readonly Color Clear = new Color(1f, 1f, 1f, 0f);
        static readonly Color Danger = new Color(0.99f, 0.64f, 0.69f, 1f);

        bool _menuWasDown;
        float _nextStatus;

        protected override void Awake()
        {
            base.Awake();
            size = new Vector2(W / PixelsPerMeter, H / PixelsPerMeter);
            background = new Color(0.094f, 0.078f, 0.141f, 0.95f);
            text = new Color(0.95f, 0.93f, 0.98f, 1f);
        }

        protected override void Start()
        {
            if (brain == null) brain = FindAnyObjectByType<CompanionBrain>();
            base.Start();
            ApplySaved();
            if (showOnStart) Invoke(nameof(ShowNearPlayer), 1.5f);
            else gameObject.SetActive(false);
        }

        // ---------- building ----------

        class CardRef { public PanelButton button; public TextMeshProUGUI title, sub; public Image icon; }
        class Usage { public GameObject root, bar; public TextMeshProUGUI pill, text; public RectTransform fill; public Image fillImage; }
        class Switch { public PanelButton button; public RectTransform knob; public Image track; public Func<bool> get; }
        class Tab { public PanelButton button; public Image icon; public TextMeshProUGUI label; public GameObject page; public string title, subtitle; }

        readonly List<Tab> _tabs = new List<Tab>();
        readonly List<Usage> _usage = new List<Usage>();
        readonly List<Switch> _toggles = new List<Switch>();
        readonly List<(PanelButton chip, string lang)> _chips = new List<(PanelButton, string)>();
        readonly List<(PanelButton card, Image frame, int option)> _views = new List<(PanelButton, Image, int)>();
        TextMeshProUGUI _title, _subtitle, _talkTitle, _talkSub;
        PanelButton _talk;
        CardRef _fetch, _fight, _learn, _computer, _forget, _ambient, _watch;
        int _tab;
        float _forgetArmedUntil;

        protected override void Build()
        {
            var c = Content;

            // Rail
            var rail = Box(c, "Rail", new Color(1f, 1f, 1f, 0.035f));
            rail.pixelsPerUnitMultiplier = 0.55f;
            Place(rail.rectTransform, Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(RailW, 0));
            var logo = Box(c, "Logo", primary);
            PlaceTL(logo.rectTransform, 16, 16, 56, 56);
            var h = Label(logo.rectTransform, "H", 28, Color.white, TextAlignmentOptions.Center);
            h.fontStyle = FontStyles.Bold;
            Place(h.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // Header + close
            _title = Text(c, "", 26, text, X0, 18, 380, 34, true);
            _subtitle = Text(c, "", 13, Muted, X0, 52, 380, 20);
            var close = AddButton(c, "", () => gameObject.SetActive(false), Card);
            PlaceTL(close.rect, W - 24 - 38, 20, 38, 38);
            Icon(close.rect, "close", 11, 11, 16, Muted);

            BuildHome(AddTab("Home", "home", "Harley", "Say \"Hi Harley\", or tap Talk"));
            BuildPlay(AddTab("Play", "play", "Play", "Pick something to do together"));
            BuildWorld(AddTab("World", "world", "World", "Where are you?"));
            BuildSettings(AddTab("Settings", "settings", "Settings", "How Harley behaves"));
            ShowTab(0);
        }

        RectTransform AddTab(string name, string icon, string title, string subtitle)
        {
            int i = _tabs.Count;
            var b = AddButton(Content, "", () => ShowTab(i), Clear);
            PlaceTL(b.rect, 10, 92 + i * 76, 68, 66);
            var t = new Tab { button = b, title = title, subtitle = subtitle };
            t.icon = Icon(b.rect, icon, 23, 10, 22, Muted);
            t.label = Text(b.rect, name, 11, Muted, 0, 38, 68, 18, true, TextAlignmentOptions.Center);
            var page = new GameObject(name + " Page", typeof(RectTransform));
            page.transform.SetParent(Content, false);
            var rt = (RectTransform)page.transform;
            Place(rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.page = page;
            _tabs.Add(t);
            return rt;
        }

        void ShowTab(int i)
        {
            _tab = i;
            for (int k = 0; k < _tabs.Count; k++)
            {
                bool on = k == i;
                var t = _tabs[k];
                t.page.SetActive(on);
                t.button.baseColor = on ? new Color(0.545f, 0.361f, 0.965f, 0.22f) : Clear;
                t.icon.color = on ? Lilac : Muted;
                t.label.color = on ? Color.white : Muted;
            }
            _title.text = _tabs[i].title;
            _subtitle.text = _tabs[i].subtitle;
            _nextStatus = 0f;
        }

        void BuildHome(RectTransform p)
        {
            _talk = AddButton(p, "", Talk, primary);
            PlaceTL(_talk.rect, X0, Top, 330, 86);
            var mic = Box(_talk.rect, "Mic", new Color(1f, 1f, 1f, 0.18f));
            PlaceTL(mic.rectTransform, 16, 17, 52, 52);
            Icon(mic.rectTransform, "mic", 13, 13, 26, Color.white);
            _talkTitle = Text(_talk.rect, "Talk", 22, Color.white, 82, 20, 230, 28, true);
            _talkSub = Text(_talk.rect, "or just say \"Hi Harley\"", 12, new Color(1f, 1f, 1f, 0.85f), 82, 48, 230, 18);

            var type = AddButton(p, "", Type, Card);
            PlaceTL(type.rect, X0 + 342, Top, 142, 86);
            Icon(type.rect, "keyboard", 22, 31, 24, Lilac);
            Text(type.rect, "Type", 19, text, 56, 28, 80, 30, true);

            Section(p, "QUICK", 190);
            float w = (CW - 20f) / 3f;
            MakeCard(p, X0, 212, w, 60, "here", "Come here", "to your side", () => brain?.movement?.Perform(CompanionAction.ComeClose));
            MakeCard(p, X0 + w + 10, 212, w, 60, "screens", "Screens", "bring them here", () => { if (head != null) HarleyWindows.Recenter(head); });
            MakeCard(p, X0 + 2 * (w + 10), 212, w, 60, "apps", "Apps", "YouTube, Maps…", OpenApps);

            MakeUsage(p);
        }

        void BuildPlay(RectTransform p)
        {
            float w = (CW - 12f) / 2f;
            _fetch = MakeCard(p, X0, Top, w, 66, "ball", "Fetch", "throw the ball, she brings it", () => PickTool(HarleyTools.Tool.Fetch));
            _fight = MakeCard(p, X0 + w + 12, Top, w, 66, "sword", "Sword fight", "first to 5 hits", () => PickTool(HarleyTools.Tool.Fight));
            MakeCard(p, X0, Top + 78, w, 66, "hide", "Hide & seek", "she hides, you find her", () => { gameObject.SetActive(false); brain?.PlayHideAndSeek(); });
            _learn = MakeCard(p, X0 + w + 12, Top + 78, w, 66, "learn", "Learn a language", "words from your room", Learn);

            Section(p, "LANGUAGE FOR LEARN", 252);
            float x = X0, y = 274;
            foreach (var lang in HarleyLanguage.Languages)
            {
                float cw = 26f + lang.Length * 8.2f;
                if (x + cw > X0 + CW) { x = X0; y += 40; }
                string l = lang;
                var chip = AddButton(p, lang, () => SetLanguage(l), Card, 13);
                PlaceTL(chip.rect, x, y, cw, 32);
                _chips.Add((chip, lang));
                x += cw + 8;
            }
        }

        void BuildWorld(RectTransform p)
        {
            var world = HarleyWorld.Instance != null ? HarleyWorld.Instance : FindAnyObjectByType<HarleyWorld>(FindObjectsInactive.Include);
            int views = world != null ? world.ViewCount : 0;
            float w = (CW - 30f) / 4f, h = 76f;
            for (int i = -1; i <= views; i++)
            {
                int n = i + 1, col = n % 4, row = n / 4;
                float x = X0 + col * (w + 10), y = Top + row * (h + 10);
                string name = i < 0 ? "My room" : i == views ? "Harley's world" : world.ViewName(i);
                Color fill = i < 0 ? new Color(0.36f, 0.34f, 0.32f, 1f) : i == views ? new Color(0.40f, 0.26f, 0.68f, 1f) : new Color(0.2f, 0.2f, 0.25f, 1f);

                var frame = Box(p, "Selected", new Color(0.655f, 0.545f, 0.98f, 1f));
                PlaceTL(frame.rectTransform, x - 3, y - 3, w + 6, h + 6);
                int option = i;
                var card = AddButton(p, "", () => SelectView(option), fill);
                PlaceTL(card.rect, x, y, w, h);
                card.rect.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                var tex = i >= 0 && i < views ? world.ViewTexture(i) : null;
                if (tex != null)
                {
                    var thumb = new GameObject("Picture", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                    thumb.transform.SetParent(card.rect, false);
                    thumb.texture = tex;
                    thumb.uvRect = new Rect(0.2f, 0.36f, 0.22f, 0.3f); // a slice around the horizon
                    thumb.raycastTarget = false;
                    Place(thumb.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                }
                var label = Text(card.rect, name, 13, Color.white, 10, h - 26, w - 14, 20, true);
                label.outlineWidth = 0.18f;
                label.outlineColor = new Color32(0, 0, 0, 160);
                _views.Add((card, frame, option));
            }

            int rows = (views + 2 + 3) / 4;
            float sy = Top + rows * (h + 10) + 6;
            Section(p, "SCREENS", sy);
            float cw = (CW - 12f) / 2f;
            MakeCard(p, X0, sy + 22, cw, 52, "apps", "Apps", "open a screen", OpenApps);
            MakeCard(p, X0 + cw + 12, sy + 22, cw, 52, "screens", "Screens here", "bring them to you", () => { if (head != null) HarleyWindows.Recenter(head); });
            _ambient = MakeCard(p, X0, sy + 84, cw, 52, "ambient", "Ambient light", "screen colours on your walls", () => { HarleyAmbient.Instance?.Toggle(); _nextStatus = 0f; });
            _watch = MakeCard(p, X0 + cw + 12, sy + 84, cw, 52, "watch", "Watch together", "Harley watches with you", () => { HarleyWatch.Instance?.Toggle(); _nextStatus = 0f; });
        }

        void BuildSettings(RectTransform p)
        {
            var cfg = brain != null ? brain.config : null;
            float w = (CW - 12f) / 2f;
            MakeToggle(p, X0, Top, w, "Follow me", "floats next to you", () => Pref(PrefFollow, true), v => { Save(PrefFollow, v); Follow(v); });
            MakeToggle(p, X0 + w + 12, Top, w, "\"Hi Harley\"", "wake word", () => cfg == null || cfg.wakeWord, v => { Save(PrefWake, v); if (cfg) cfg.wakeWord = v; });
            MakeToggle(p, X0, Top + 68, w, "Voice", "speaks out loud", () => cfg == null || cfg.witVoiceOutput, v => { Save(PrefVoice, v); if (cfg) { cfg.witVoiceOutput = v; cfg.deviceVoice = v; } });
            MakeToggle(p, X0 + w + 12, Top + 68, w, "Comments", "talks on her own", () => cfg == null || cfg.proactiveComments, v => { Save(PrefComments, v); if (cfg) cfg.proactiveComments = v; });

            _computer = MakeCard(p, X0, Top + 148, w, 62, "computer", "Computer", "your own AI", () => { gameObject.SetActive(false); ComputerPanel.Show(head); });
            _forget = MakeCard(p, X0 + w + 12, Top + 148, w, 62, "trash", "Forget me", "clear her memory", ForgetMe);
            _forget.title.color = Danger;

            MakeUsage(p);
        }

        // ---------- pieces ----------

        TextMeshProUGUI Text(RectTransform parent, string value, float size, Color color, float x, float y, float w, float h,
                             bool bold = false, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var t = Label(parent, value, size, color, align);
            if (bold) t.fontStyle = FontStyles.Bold;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            PlaceTL(t.rectTransform, x, y, w, h);
            return t;
        }

        Image Icon(RectTransform parent, string name, float x, float y, float size, Color color)
        {
            var img = new GameObject("Icon " + name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            img.transform.SetParent(parent, false);
            img.sprite = IconSprite(name);
            img.preserveAspect = true;
            img.color = color;
            img.raycastTarget = false;
            PlaceTL(img.rectTransform, x, y, size, size);
            return img;
        }

        static readonly Dictionary<string, Sprite> _icons = new Dictionary<string, Sprite>();
        static Sprite IconSprite(string name)
        {
            if (_icons.TryGetValue(name, out var s)) return s;
            s = Resources.Load<Sprite>("MenuIcons/" + name);
            if (s == null)
            {
                var tex = Resources.Load<Texture2D>("MenuIcons/" + name); // imported as a plain texture: make the sprite here
                if (tex != null) s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            _icons[name] = s;
            return s;
        }

        void Section(RectTransform p, string name, float y)
        {
            var t = Text(p, name, 11, Faint, X0, y, 300, 16, true);
            t.characterSpacing = 8f;
        }

        CardRef MakeCard(RectTransform p, float x, float y, float w, float h, string icon, string title, string sub, Action click)
        {
            var b = AddButton(p, "", click, Card);
            PlaceTL(b.rect, x, y, w, h);
            var r = new CardRef { button = b };
            r.icon = Icon(b.rect, icon, 14, (h - 22f) / 2f, 22, Lilac);
            r.title = Text(b.rect, title, 15, text, 46, h / 2f - 17f, w - 54, 20, true);
            r.sub = Text(b.rect, sub, 11, Muted, 46, h / 2f + 3f, w - 54, 16);
            return r;
        }

        void MakeToggle(RectTransform p, float x, float y, float w, string title, string sub, Func<bool> get, Action<bool> set)
        {
            var t = new Switch { get = get };
            t.button = AddButton(p, "", () => { bool v = !t.get(); set(v); _nextStatus = 0f; }, Card);
            PlaceTL(t.button.rect, x, y, w, 58);
            Text(t.button.rect, title, 15, text, 16, 10, w - 80, 20, true);
            Text(t.button.rect, sub, 11, Muted, 16, 32, w - 80, 16);
            t.track = Box(t.button.rect, "Switch", primary);
            PlaceTL(t.track.rectTransform, w - 16 - 44, 16, 44, 26);
            var knob = Box(t.track.rectTransform, "Knob", Color.white);
            t.knob = knob.rectTransform;
            PlaceTL(t.knob, 3, 3, 20, 20);
            _toggles.Add(t);
        }

        void MakeUsage(RectTransform p)
        {
            var u = new Usage();
            var bg = Box(p, "Usage", new Color(1f, 1f, 1f, 0.04f));
            PlaceTL(bg.rectTransform, X0, H - 20 - 44, CW, 44);
            u.root = bg.gameObject;
            var pill = Box(bg.rectTransform, "Plan", new Color(0.545f, 0.361f, 0.965f, 0.25f));
            PlaceTL(pill.rectTransform, 12, 10, 52, 24);
            u.pill = Text(pill.rectTransform, "Free", 12, new Color(0.89f, 0.86f, 1f, 1f), 0, 2, 52, 20, true, TextAlignmentOptions.Center);
            var bar = Box(bg.rectTransform, "Bar", new Color(1f, 1f, 1f, 0.1f));
            PlaceTL(bar.rectTransform, 76, 19, 170, 6);
            u.bar = bar.gameObject;
            u.fillImage = Box(bar.rectTransform, "Fill", primary);
            u.fill = u.fillImage.rectTransform;
            Place(u.fill, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, Vector2.zero);
            u.text = Text(bg.rectTransform, "", 12, Muted, 256, 0, CW - 268, 44, false, TextAlignmentOptions.Right);
            _usage.Add(u);
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

        void OpenApps()
        {
            LauncherPanel.Show(head);
            gameObject.SetActive(false);
        }

        void PickTool(HarleyTools.Tool t)
        {
            var tools = HarleyTools.Instance;
            if (tools == null) return;
            tools.SetTool(t);
            gameObject.SetActive(false); // out of the way: go play
            string hint = tools.Current switch
            {
                HarleyTools.Tool.Fight => "(Fight: your sword is in your right hand. Swing at Harley, block her swings!)",
                HarleyTools.Tool.Fetch => "(Fetch: grab the ball (grip or pinch), throw it, Harley brings it back)",
                _ => "(Play tools off)"
            };
            brain?.voice?.ShowStatus(hint, 4f);
        }

        void Learn()
        {
            var lang = HarleyLanguage.Instance;
            if (lang == null && brain != null) lang = brain.gameObject.AddComponent<HarleyLanguage>();
            if (lang == null) return;
            lang.MenuPress();
            if (lang.Active) gameObject.SetActive(false); // go learn
            brain?.voice?.ShowStatus(lang.Active ? "(Learning " + lang.Language + ")" : "(Lesson over)", 3f);
        }

        void SetLanguage(string l)
        {
            var lang = HarleyLanguage.Instance;
            if (lang == null && brain != null) lang = brain.gameObject.AddComponent<HarleyLanguage>();
            lang?.SetLanguage(l);
            _nextStatus = 0f;
        }

        void SelectView(int option)
        {
            HarleyWorld.Instance?.Select(option);
            _nextStatus = 0f;
        }

        void ForgetMe()
        {
            if (Time.unscaledTime > _forgetArmedUntil)
            {
                _forgetArmedUntil = Time.unscaledTime + 3f; // a second tap within 3 s really forgets
                _nextStatus = 0f;
                return;
            }
            _forgetArmedUntil = 0f;
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
                cfg.wakeWord = Pref(PrefWake, cfg.wakeWord);
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

        void ShowNearPlayer() => ShowInFront(0.6f, 0.16f, -0.1f);

        protected override void Update()
        {
            base.Update();
            if (Time.unscaledTime >= _nextStatus) UpdateStatus();
        }

        void UpdateStatus()
        {
            _nextStatus = Time.unscaledTime + 0.3f;
            var cfg = brain != null ? brain.config : null;
            var voice = brain != null ? brain.voice : null;

            // Home: what Harley needs / is doing
            if (_tab == 0)
            {
                if (cfg == null || !cfg.HasBrainKey) _subtitle.text = "<color=#F87171>No AI key in the config</color>";
                else if (voice != null && voice.IsListening) _subtitle.text = "<color=#4ADE80>Listening…</color>";
                else if (voice != null && !voice.HasDictation) _subtitle.text = "Talk needs a voice key. Type works now.";
                else _subtitle.text = _tabs[0].subtitle;
            }
            bool listening = voice != null && voice.IsListening;
            if (_talkTitle != null) { _talkTitle.text = listening ? "Stop" : "Talk"; _talkSub.text = listening ? "listening…" : "or just say \"Hi Harley\""; }

            // Play
            var tools = HarleyTools.Instance;
            if (_fetch != null) _fetch.button.baseColor = tools != null && tools.Current == HarleyTools.Tool.Fetch ? CardOn : Card;
            if (_fight != null) _fight.button.baseColor = tools != null && tools.Current == HarleyTools.Tool.Fight ? CardOn : Card;
            var lang = HarleyLanguage.Instance;
            if (_learn != null)
            {
                bool on = lang != null && lang.Active;
                _learn.button.baseColor = on ? CardOn : Card;
                _learn.sub.text = on ? "on · " + lang.Language + " · tap to stop" : "words from your room";
            }
            string current = lang != null ? lang.Language : PlayerPrefs.GetString("harley.learn.lang", "German");
            foreach (var (chip, l) in _chips) chip.baseColor = l == current ? primary : Card;

            // World
            int sel = HarleyWorld.Instance != null ? HarleyWorld.Instance.Selected : -1;
            foreach (var (card, frame, option) in _views) frame.enabled = option == sel;
            if (_ambient != null)
            {
                bool on = HarleyAmbient.Instance != null && HarleyAmbient.Instance.On;
                _ambient.button.baseColor = on ? CardOn : Card;
                _ambient.sub.text = on ? "on · tap to turn off" : "screen colours on your walls";
            }
            if (_watch != null)
            {
                bool on = HarleyWatch.Instance != null && HarleyWatch.Instance.On;
                _watch.button.baseColor = on ? CardOn : Card;
                _watch.sub.text = on ? "watching · tap to stop" : "Harley watches with you";
            }

            // Settings
            foreach (var t in _toggles)
            {
                bool v = t.get();
                t.track.color = v ? primary : new Color(1f, 1f, 1f, 0.15f);
                t.knob.anchoredPosition = new Vector2(v ? 21f : 3f, -3f);
            }
            if (_computer != null)
            {
                var pc = HarleyComputer.Instance;
                _computer.sub.text = pc == null || !pc.Available ? "your own AI · off"
                    : pc.Connected ? (string.IsNullOrEmpty(pc.ComputerName) ? "connected" : pc.ComputerName) + (pc.Busy ? " · working…" : "")
                    : "not reachable";
            }
            if (_forget != null) _forget.sub.text = Time.unscaledTime < _forgetArmedUntil ? "tap again to forget" : "clear her memory";

            // Usage meter (like Claude's)
            var plan = HarleyPlan.Instance;
            bool server = plan != null && plan.Label.Length > 0;
            foreach (var u in _usage)
            {
                u.root.SetActive(true);
                u.bar.SetActive(server);
                if (!server)
                {
                    u.pill.text = "Dev";
                    u.fill.anchorMax = new Vector2(0f, 1f);
                    u.text.text = "using your own AI keys · no daily limit";
                    continue;
                }
                u.pill.text = plan.Plan == "pro" ? "Pro" : "Free";
                float f = Mathf.Clamp01(plan.Percent / 100f);
                u.fill.anchorMax = new Vector2(f, 1f);
                u.fillImage.color = plan.Percent >= 90 ? new Color(0.97f, 0.44f, 0.44f) : plan.Percent >= 70 ? new Color(0.98f, 0.75f, 0.14f) : primary;
                u.text.text = plan.Known ? $"{plan.Percent}% today · resets in {plan.ResetsIn}" : "…";
            }
        }

        static System.Reflection.MethodInfo _ovrGet;
        static object _ovrStart;
        static bool _ovrTried;

        /// <summary>OVRInput Start button: the left controller menu button OR the left-hand menu gesture.</summary>
        static bool HandMenuGesture()
        {
            if (!_ovrTried)
            {
                _ovrTried = true;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("OVRInput");
                    if (t == null) continue;
                    var button = t.GetNestedType("Button");
                    if (button == null) break;
                    _ovrStart = System.Enum.Parse(button, "Start");
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "Get" && ps.Length == 2 && ps[0].ParameterType == button) { _ovrGet = m; break; }
                    }
                    break;
                }
            }
            if (_ovrGet == null) return false;
            try
            {
                var ctrl = _ovrGet.GetParameters()[1];
                return (bool)_ovrGet.Invoke(null, new[] { _ovrStart, ctrl.HasDefaultValue ? ctrl.DefaultValue : System.Enum.Parse(ctrl.ParameterType, "Active") });
            }
            catch { return false; }
        }

        /// <summary>Left controller menu button toggles the menu (runs on the always-active watcher).</summary>
        public void PollMenuButton()
        {
            InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            bool down = false;
            if (left.isValid) left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out down);
            down |= HandMenuGesture(); // hands: look at your left palm and pinch (the Meta menu gesture)
            if (down && !_menuWasDown) Toggle();
            _menuWasDown = down;
        }
    }
}
