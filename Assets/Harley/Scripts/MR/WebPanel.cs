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
        public int pageWidth = 1280, pageHeight = 720;
        [Tooltip("Bigger = larger text on pages.")]
        public int pageDpi = 200;
        [Tooltip("Thumbstick scroll speed, page pixels per second.")]
        public float scrollSpeed = 1400f;

        const float ToolbarPx = 52f;

        public string Title => _title;
        public string Url => _url;
        /// <summary>What the page looks like right now (upside down: row 0 is the top). Null until the first frame.</summary>
        public Texture2D PageTexture => _frames > 0 ? _tex : null;
        public float LastUsed { get; private set; }

        RawImage _page;
        TextMeshProUGUI _titleLabel, _placeholder;
        Texture2D _tex;
        int _frames, _lastFrame = -1;
        string _title = "", _url = "";
        float _nextInfo;
        bool _touching;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _web;
        IntPtr _bufferAddress = IntPtr.Zero;
        AndroidJavaObject _buffer;
#endif

        /// <summary>Opens a new browser window in front of the player.</summary>
        public static WebPanel Create(string url, Transform head)
        {
            var go = new GameObject("Harley Browser");
            var p = go.AddComponent<WebPanel>();
            p.startUrl = url;
            p.head = head;
            p.size = new Vector2(0.8f, 0.45f + ToolbarPx / PixelsPerMeter);
            p.EnsureBuilt();
            p.ShowInFront(0.95f, 0.05f, Windows.Count <= 1 ? 0f : 0.25f * (Windows.Count - 1));
            return p;
        }

        protected override void OnEnable() { base.OnEnable(); if (!Windows.Contains(this)) Windows.Add(this); }
        protected override void OnDisable() { base.OnDisable(); Windows.Remove(this); }

        protected override void Build()
        {
            var c = Content;
            float w = c.rect.width;

            // Toolbar
            float x = 10f, y = 8f, h = 36f;
            PlaceTL(AddButton(c, "Back", Back, null, 18).rect, x, y, 70, h); x += 76;
            PlaceTL(AddButton(c, "Reload", Reload, null, 18).rect, x, y, 80, h); x += 86;
            PlaceTL(AddButton(c, "Google", () => Load("https://www.google.com"), null, 18).rect, x, y, 84, h); x += 90;
            PlaceTL(AddButton(c, "YouTube", () => Load("https://m.youtube.com"), null, 18).rect, x, y, 92, h); x += 98;

            float right = w - 10f;
            right -= 44; PlaceTL(AddButton(c, "X", Close, new Color(0.45f, 0.12f, 0.2f, 1f), 18).rect, right, y, 44, h); right -= 6;
            right -= 54; PlaceTL(AddButton(c, "Go", PressEnter, null, 18).rect, right, y, 54, h); right -= 6;
            right -= 70; PlaceTL(AddButton(c, "Type", TypeText, primary, 18).rect, right, y, 70, h); right -= 10;

            _titleLabel = Label(c, "", 16, muted, TextAlignmentOptions.Left);
            _titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            PlaceTL(_titleLabel.rectTransform, x + 4, y, Mathf.Max(40, right - x - 4), h);

            // Page
            var pageGO = new GameObject("Page", typeof(RectTransform), typeof(RawImage));
            pageGO.transform.SetParent(c, false);
            _page = pageGO.GetComponent<RawImage>();
            _page.raycastTarget = false;
            _page.color = Color.white;
            _page.uvRect = new Rect(0, 1, 1, -1); // Android rows start at the top
            Place(_page.rectTransform, Vector2.zero, Vector2.one, new Vector2(0, 0), new Vector2(0, -ToolbarPx));

            _placeholder = Label(_page.rectTransform, "Loading...", 26, new Color(0.3f, 0.3f, 0.35f), TextAlignmentOptions.Center);
            Place(_placeholder.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _page.color = new Color(0.94f, 0.93f, 0.97f);

            _tex = new Texture2D(pageWidth, pageHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            CreateWebView();
            LastUsed = Time.unscaledTime;
        }

        void CreateWebView()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _web = new AndroidJavaObject("com.harley.web.HarleyWebView", activity, pageWidth, pageHeight, pageDpi, startUrl);
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
            if (frame != _lastFrame && _bufferAddress != IntPtr.Zero)
            {
                _lastFrame = frame;
                _tex.LoadRawTextureData(_bufferAddress, pageWidth * pageHeight * 4);
                _tex.Apply(false);
                if (_frames++ == 0)
                {
                    _page.texture = _tex;
                    _page.color = Color.white;
                    if (_placeholder) _placeholder.gameObject.SetActive(false);
                }
            }
            if (Time.unscaledTime >= _nextInfo)
            {
                _nextInfo = Time.unscaledTime + 0.5f;
                _title = _web.Call<string>("getTitle") ?? "";
                _url = _web.Call<string>("getUrl") ?? "";
                bool loading = _web.Call<bool>("isLoading");
                if (_titleLabel) _titleLabel.text = (loading ? "Loading...  " : "") + (_title.Length > 0 ? _title : _url);
            }
#else
            if (_titleLabel) _titleLabel.text = _title;
#endif
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

        void Back()
        {
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
        }
    }
}
