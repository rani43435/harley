using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Harley
{
    /// <summary>
    /// A floating browser window (YouTube, Google, any site) that lives inside Harley's
    /// mixed-reality app, so Harley can see exactly what is on it.
    ///  - Point and click / pinch, or touch it with a finger. Drag to scroll, or use the thumbstick.
    ///  - Type: opens the Quest keyboard and types into the selected field. Go = Enter.
    ///  - Grab the bar underneath to move it.
    /// The page is a real Android WebView (Plugins/Android/HarleyWebView.java) drawn into a texture.
    /// </summary>
    public class WebPanel : SpatialPanel
    {
        public static readonly List<WebPanel> Windows = new List<WebPanel>();

        public string startUrl = "https://www.google.com";
        [Tooltip("Page resolution in pixels (16:9).")]
        public int pageWidth = 1920, pageHeight = 1080;
        [Tooltip("Bigger = larger text on pages. 300 at 1920 px lays pages out like a 1024 px wide screen, drawn with 1.9x the pixels.")]
        public int pageDpi = 300;
        [Tooltip("Show the page through a Quest compositor layer: drawn by the headset itself at full sharpness, like Meta's own windows.")]
        public bool sharpLayer = true;
        [Tooltip("Thumbstick scroll speed, page pixels per second.")]
        public float scrollSpeed = 1400f;

        const float ToolbarPx = 52f;

        public string Title => _title;
        public string Url => _url;
        /// <summary>What the page looks like right now (upside down: row 0 is the top). Null until the first frame.</summary>
        public Texture PageTexture => _frames > 0 ? (Texture)_tex : null;
        /// <summary>YouTube TV (youtube.com/tv) is open: arrows / OK / Back drive it.</summary>
        public bool TvMode => (_url ?? "").Contains("youtube.com/tv");
        public float LastUsed { get; set; }

        RawImage _page;
        TextMeshProUGUI _titleLabel, _placeholder;
        Texture2D _tex;
        int _frames, _lastFrame = -1;
        string _title = "", _url = "";
        float _nextInfo;
        bool _touching;
        RenderTexture _sharp;            // the page with mipmaps: no shimmer or pixel crawl at a distance
        GameObject _tvKeys;
        float _nextStickKey;
        bool _wasTv;
        int _focusSeq;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _web;
        IntPtr _bufferAddress = IntPtr.Zero;
        AndroidJavaObject _buffer;
#endif

        /// <summary>The app this window shows (null = plain browser).</summary>
        public AppCatalog.App App { get; private set; }

        /// <summary>Opens a new browser window in front of the player.</summary>
        public static WebPanel Create(string url, Transform head) => Open(url, null, head);

        /// <summary>Opens an app (from the launcher) as its own app-style window.</summary>
        public static WebPanel Create(AppCatalog.App app, Transform head)
        {
            // One window per app: bring it back instead of opening a second one.
            foreach (var w in Windows)
                if (w != null && w.App == app) { if (w.Minimized) w.Restore(); w.LastUsed = Time.unscaledTime; return w; }
            return Open(app.url, app, head);
        }

        static WebPanel Open(string url, AppCatalog.App app, Transform head)
        {
            var go = new GameObject(app != null ? app.name : "Harley Browser");
            var p = go.AddComponent<WebPanel>();
            p.startUrl = url;
            p.App = app;
            p.head = head;
            p.size = new Vector2(1.0f, 0.5625f + ToolbarPx / PixelsPerMeter);
            p.resizable = true;
            p.EnsureBuilt();
            HarleyWindows.Place(p, head); // side by side, like Quest's home
            return p;
        }

        protected override void OnEnable() { base.OnEnable(); if (!Windows.Contains(this)) Windows.Add(this); }
        protected override void OnDisable() { base.OnDisable(); Windows.Remove(this); HarleyWindows.Forget(this); }

        // ---------- minimize to the dock ----------

        /// <summary>Hidden in the dock, still running (sound keeps playing).</summary>
        public bool Minimized { get; private set; }
        public override bool Interactable => base.Interactable && !Minimized;

        public void Minimize()
        {
            if (Minimized) return;
            Minimized = true;
            if (Root) Root.gameObject.SetActive(false);
            if (_overlay) _overlay.gameObject.SetActive(false);
            HarleyWindows.Release(this);
        }

        public void Restore()
        {
            LastUsed = Time.unscaledTime;
            if (!Minimized) return;
            Minimized = false;
            if (Root) Root.gameObject.SetActive(true);
            if (_overlay) _overlay.gameObject.SetActive(true);
            HarleyWindows.Place(this, head);
        }

        protected override void Build()
        {
            var c = Content;
            float w = c.rect.width;
            float y = 9f, h = 36f;

            // Left: back / reload / home (apps)
            float x = 12f;
            PlaceTL(AddButton(c, "Back", Back, ghost, 17).rect, x, y, 64, h); x += 68;
            PlaceTL(AddButton(c, "Reload", Reload, ghost, 17).rect, x, y, 72, h); x += 76;
            PlaceTL(AddButton(c, "Apps", () => LauncherPanel.Show(head), ghost, 17).rect, x, y, 62, h); x += 70;

            // Right: type / enter / close
            float right = w - 12f;
            right -= 40; PlaceTL(AddButton(c, "X", Close, new Color(0.62f, 0.18f, 0.26f, 0.9f), 17).rect, right, y, 40, h); right -= 6;
            right -= 40; PlaceTL(AddButton(c, "_", Minimize, ghost, 20).rect, right, y, 40, h); right -= 6;
            right -= 56; PlaceTL(AddButton(c, "Enter", PressEnter, ghost, 16).rect, right, y, 56, h); right -= 6;
            right -= 62; PlaceTL(AddButton(c, "Type", TypeText, primary, 17).rect, right, y, 62, h); right -= 10;

            // Middle: an address pill (app name, or the page title) - tap it to go somewhere else
            float pillW = Mathf.Max(80f, right - x - 4f);
            var pill = AddButton(c, "", AskAddress, new Color(1f, 1f, 1f, 0.07f), 16);
            _pill = pill.rect.gameObject;
            PlaceTL(pill.rect, x, y, pillW, h);
            if (App != null)
            {
                var tile = Box(pill.rect, "Tile", App.color);
                PlaceTL(tile.rectTransform, 8, 6, 24, 24);
                var letter = Label(tile.rectTransform, App.letter, 15, Color.white, TextAlignmentOptions.Center);
                letter.fontStyle = FontStyles.Bold;
                Place(letter.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            }
            _titleLabel = pill.label;
            _titleLabel.alignment = TextAlignmentOptions.Center;
            _titleLabel.color = muted;
            _titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            Place(_titleLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(App != null ? 40 : 14, 0), new Vector2(-14, 0));

            // YouTube TV remote (shown only on youtube.com/tv, over the address pill): thumbstick or these buttons
            _tvKeys = new GameObject("TV Keys", typeof(RectTransform));
            _tvKeys.transform.SetParent(c, false);
            var tk = (RectTransform)_tvKeys.transform;
            Place(tk, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            float kw = 46f, total = kw * 4 + 60 + 4 * 6;
            float kx = x + Mathf.Max(0f, (pillW - total) * 0.5f);
            foreach (var (label, code) in new[] { ("<", KeyLeft), ("^", KeyUp), ("v", KeyDown), (">", KeyRight) })
            { int k = code; PlaceTL(AddButton(tk, label, () => Key(k), ghost, 22).rect, kx, y, kw, h); kx += kw + 6; }
            PlaceTL(AddButton(tk, "OK", () => Key(KeyEnter), primary, 17).rect, kx, y, 60, h);
            _tvKeys.SetActive(false);

            // Page, with rounded-feel inset
            var pageGO = new GameObject("Page", typeof(RectTransform), typeof(RawImage));
            pageGO.transform.SetParent(c, false);
            _page = pageGO.GetComponent<RawImage>();
            _page.raycastTarget = false;
            _page.uvRect = new Rect(0, 0, 1, 1); // the page is turned the right way up when it's copied in
            Place(_page.rectTransform, Vector2.zero, Vector2.one, new Vector2(0, 0), new Vector2(0, -ToolbarPx));

            string loadingText = App != null ? App.name : "Loading...";
            _placeholder = Label(_page.rectTransform, loadingText, 34, new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Center);
            Place(_placeholder.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _page.color = App != null ? Color.Lerp(App.color, Color.black, 0.55f) : new Color(0.16f, 0.16f, 0.19f);

            // A short heads-up on apps whose videos can't play here
            if (App != null && App.protectedMedia && !string.IsNullOrEmpty(App.note))
            {
                _note = Label(c, App.note, 15, new Color(1f, 0.85f, 0.6f), TextAlignmentOptions.Center);
                _note.textWrappingMode = TextWrappingModes.Normal;
                var bg = Box(c, "Note", new Color(0.1f, 0.08f, 0.04f, 0.9f));
                bg.transform.SetSiblingIndex(_note.transform.GetSiblingIndex());
                Place(bg.rectTransform, new Vector2(0.12f, 0), new Vector2(0.88f, 0), new Vector2(0, 12), new Vector2(0, 62));
                Place(_note.rectTransform, new Vector2(0.12f, 0), new Vector2(0.88f, 0), new Vector2(12, 12), new Vector2(-12, 62));
                _noteBg = bg.gameObject;
                _noteUntil = Time.unscaledTime + 9f;
            }

            _tex = new Texture2D(pageWidth, pageHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _sharp = new RenderTexture(pageWidth, pageHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                useMipMap = true, autoGenerateMips = true, filterMode = FilterMode.Trilinear, anisoLevel = 8,
                wrapMode = TextureWrapMode.Clamp, name = "Harley Page"
            };
            _sharp.Create();
            CreateWebView();
            LastUsed = Time.unscaledTime;
        }

        static readonly Color ghost = new Color(1f, 1f, 1f, 0.06f);
        TextMeshProUGUI _note;
        GameObject _pill;
        GameObject _noteBg;
        float _noteUntil;

        void AskAddress()
        {
            HarleyKeyboard.Open("", "Search or type a web address", t =>
            {
                if (!string.IsNullOrWhiteSpace(t)) Load(t.Trim());
            });
        }

        void CreateWebView()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _web = new AndroidJavaObject("com.harley.web.HarleyWebView", activity, pageWidth, pageHeight, pageDpi, startUrl,
                        App != null && App.identity == AppCatalog.Identity.Desktop ? 1 : 0);
                }
                _buffer = _web.Call<AndroidJavaObject>("getBuffer");
                unsafe { _bufferAddress = (IntPtr)AndroidJNI.GetDirectBufferAddress(_buffer.GetRawObject()); }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Harley] Browser could not start: " + e.Message);
                if (_placeholder) _placeholder.text = "The browser could not start.";
            }
#else
            _placeholder.text = "The browser runs on the headset.\n(" + startUrl + ")";
            _url = startUrl;
            _title = "Browser preview";
#endif
        }

        protected override void Update()
        {
            base.Update();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_web == null) return;
            int frame = _web.Call<int>("getFrameCount");
            if (!Minimized && frame != _lastFrame && _bufferAddress != IntPtr.Zero)
            {
                _lastFrame = frame;
                _tex.LoadRawTextureData(_bufferAddress, pageWidth * pageHeight * 4);
                _tex.Apply(false);
                Graphics.Blit(_tex, _sharp, new Vector2(1f, -1f), new Vector2(0f, 1f)); // right way up + mipmaps
                if (_frames++ == 0)
                {
                    _page.texture = _sharp;
                    _page.color = Color.white;
                    if (_placeholder) _placeholder.gameObject.SetActive(false);
                    if (sharpLayer) MakeSharpLayer();
                }
            }
            if (Time.unscaledTime >= _nextInfo)
            {
                _nextInfo = Time.unscaledTime + 0.5f;
                _title = _web.Call<string>("getTitle") ?? "";
                _url = _web.Call<string>("getUrl") ?? "";
                bool loading = _web.Call<bool>("isLoading");
                string shown = App != null ? App.name : _title.Length > 0 ? _title : _url;
                if (_titleLabel) _titleLabel.text = (loading ? "Loading...  " : "") + shown;
            }
            // A text box on the page got focus (search, chat, login): open Harley's keyboard for it
            int focus = _web.Call<int>("getFocusSeq");
            if (focus != _focusSeq)
            {
                _focusSeq = focus;
                if (!Minimized && !TvMode && !HarleyKeyboard.IsOpen && Time.unscaledTime - LastUsed < 3f)
                {
                    string current = _web.Call<string>("getFocusValue") ?? "";
                    HarleyKeyboard.Open(current, App != null ? App.name : "Type here", text =>
                    {
                        _web?.Call("setText", text ?? "");
                        _web?.Call("pressEnter");
                    });
                }
            }
            if (_noteBg != null && Time.unscaledTime > _noteUntil) { Destroy(_noteBg); Destroy(_note.gameObject); _noteBg = null; }
            if (TvMode != _wasTv)
            {
                _wasTv = TvMode;
                if (_tvKeys) _tvKeys.SetActive(_wasTv);
                if (_pill) _pill.SetActive(!_wasTv);
            }
#else
            if (_titleLabel) _titleLabel.text = _title;
#endif
        }

        // ---------- compositor layer (sharp like system windows) ----------

        OVROverlay _overlay;
        static Material _hole;

        void MakeSharpLayer()
        {
            try
            {
                var shader = Shader.Find("Harley/Underlay Hole");
                if (shader == null) return;
                if (_hole == null) _hole = new Material(shader);

                var go = new GameObject("Sharp Page");
                go.transform.SetParent(transform, false);
                Rect r = _page.rectTransform.rect;
                go.transform.position = _page.rectTransform.TransformPoint(r.center);
                go.transform.rotation = transform.rotation;
                go.transform.localScale = new Vector3(r.width / PixelsPerMeter, r.height / PixelsPerMeter, 1f);

                _overlay = go.AddComponent<OVROverlay>();
                _overlay.currentOverlayType = OVROverlay.OverlayType.Underlay;
                _overlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
                _overlay.isDynamic = true;
                _overlay.noDepthBufferTesting = true;
                _overlay.useEfficientSupersample = true;
                _overlay.compositionDepth = 0;
                _overlay.textures = new Texture[] { _sharp, null };

                // (Passthrough is the backmost underlay, so the page sits in front of it.)

                _page.material = _hole; // see-through hole where the layer shows
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Harley] Sharp layer not available, using the normal page: " + e.Message);
                if (_overlay) Destroy(_overlay.gameObject);
                _overlay = null;
                _page.material = null;
            }
        }

        void LateUpdate()
        {
            // The compositor can't show the layer (e.g. not on a headset): fall back to the normal picture.
            if (_overlay != null && !Minimized && Time.frameCount % 30 == 0 && _frames > 60 && !_overlay.isOverlayVisible && _page.material == _hole)
            {
                Debug.LogWarning("[Harley] Sharp layer not visible, using the normal page.");
                _page.material = null;
                _overlay.enabled = false;
            }
        }

        // ---------- touching the page ----------

        bool ToPage(PanelPointer p, out float px, out float py)
        {
            Vector3 lp = _page.rectTransform.InverseTransformPoint(p.world);
            Rect r = _page.rectTransform.rect;
            float nx = (lp.x - r.xMin) / r.width, ny = (lp.y - r.yMin) / r.height;
            px = Mathf.Clamp01(nx) * pageWidth;
            py = (1f - Mathf.Clamp01(ny)) * pageHeight;
            return nx >= 0f && nx <= 1f && ny >= 0f && ny <= 1f;
        }

        protected override void ContentDown(PanelPointer p)
        {
            if (!ToPage(p, out float x, out float y)) return;
            _touching = true;
            LastUsed = Time.unscaledTime;
            Touch(0, x, y);
        }

        protected override void ContentDrag(PanelPointer p)
        {
            if (!_touching) return;
            ToPage(p, out float x, out float y);
            Touch(2, x, y);
        }

        protected override void ContentUp(PanelPointer p)
        {
            if (!_touching) return;
            _touching = false;
            ToPage(p, out float x, out float y);
            Touch(1, x, y);
        }

        public override void PointerScroll(PanelPointer p, Vector2 amount)
        {
            LastUsed = Time.unscaledTime;
            if (TvMode)
            {
                // Thumbstick = TV remote arrows, one press per flick (repeats while held)
                if (Time.unscaledTime < _nextStickKey) return;
                _nextStickKey = Time.unscaledTime + 0.28f;
                if (Mathf.Abs(amount.x) > Mathf.Abs(amount.y)) Key(amount.x > 0 ? KeyRight : KeyLeft);
                else Key(amount.y > 0 ? KeyUp : KeyDown);
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("scroll", 0f, -amount.y * scrollSpeed * Time.unscaledDeltaTime);
#endif
        }

        void Touch(int action, float x, float y)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("touch", action, x, y);
#endif
        }

        // ---------- toolbar ----------

        public void Load(string url)
        {
            LastUsed = Time.unscaledTime;
            if (!url.Contains("://")) url = url.Contains(".") && !url.Contains(" ")
                ? "https://" + url
                : "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
            startUrl = url;
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("loadUrl", url);
#else
            _url = url;
            if (_placeholder) _placeholder.text = "The browser runs on the headset.\n(" + url + ")";
#endif
        }

        const string YouTubeTv = "https://www.youtube.com/tv";
        // Android key codes
        const int KeyUp = 19, KeyDown = 20, KeyLeft = 21, KeyRight = 22, KeyEnter = 66, KeyEscape = 111;

        public void Key(int code)
        {
            LastUsed = Time.unscaledTime;
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("key", code);
#endif
        }

        void Back()
        {
            if (TvMode) { Key(KeyEscape); return; } // YouTube TV's own back
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("goBack");
#endif
        }

        void Reload()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("reload");
#endif
        }

        void TypeText()
        {
            HarleyKeyboard.Open("", "Type here", text =>
            {
                if (string.IsNullOrEmpty(text)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
                _web?.Call("typeText", text);
#endif
            });
        }

        void PressEnter()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("pressEnter");
#endif
        }

        public void Close() => Destroy(gameObject);

        void OnApplicationPause(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _web?.Call("setPaused", paused);
#endif
        }

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _web?.Call("destroy"); } catch { }
            _buffer?.Dispose();
            _web?.Dispose();
            _web = null;
#endif
            if (_tex) Destroy(_tex);
            if (_sharp) { _sharp.Release(); Destroy(_sharp); }
        }
    }
}
