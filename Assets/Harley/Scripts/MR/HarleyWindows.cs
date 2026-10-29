using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Arranges app windows like Quest's home: up to three side by side in a gentle curve
    /// (middle, right, left) at fixed spots, plus a dock bar underneath with every open app.
    /// A minimized app disappears into the dock but keeps running (music and video keep playing).
    /// </summary>
    public static class HarleyWindows
    {
        const int Slots = 3;
        const float Radius = 1.2f, SlotAngle = 52f;
        static readonly int[] FillOrder = { 0, 1, 2 };            // middle, right, left
        static readonly float[] SlotYaw = { 0f, SlotAngle, -SlotAngle };
        static readonly WebPanel[] _slots = new WebPanel[Slots];
        static readonly Dictionary<WebPanel, int> _lastSlot = new Dictionary<WebPanel, int>();

        static Vector3 _anchor;
        static float _anchorYaw;
        static bool _hasAnchor;

        public static Vector3 Anchor => _anchor;
        public static float AnchorYaw => _anchorYaw;

        /// <summary>Puts a window in a free spot (its old one if it had one). If all three are taken, the least used one is minimized.</summary>
        public static void Place(WebPanel w, Transform head)
        {
            if (w == null) return;
            if (head == null && Camera.main != null) head = Camera.main.transform;
            CleanUp();
            if (!_hasAnchor || VisibleCount() == 0) SetAnchor(head);

            int slot = -1;
            if (_lastSlot.TryGetValue(w, out int prev) && (_slots[prev] == null || _slots[prev] == w)) slot = prev;
            if (slot < 0) foreach (int s in FillOrder) if (_slots[s] == null || _slots[s] == w) { slot = s; break; }
            if (slot < 0)
            {
                // Full: send the least recently used window to the dock
                int lru = 0;
                for (int s = 1; s < Slots; s++) if (_slots[s].LastUsed < _slots[lru].LastUsed) lru = s;
                _slots[lru].Minimize();
                slot = lru;
            }
            for (int s = 0; s < Slots; s++) if (_slots[s] == w) _slots[s] = null;
            _slots[slot] = w;
            _lastSlot[w] = slot;
            Pose(w, slot);
            HarleyDock.Refresh(head);
        }

        /// <summary>The window left its spot (minimized or closed).</summary>
        public static void Release(WebPanel w)
        {
            for (int s = 0; s < Slots; s++) if (_slots[s] == w) _slots[s] = null;
            HarleyDock.Refresh(null);
        }

        public static void Forget(WebPanel w)
        {
            Release(w);
            _lastSlot.Remove(w);
        }

        /// <summary>Brings every visible window back around you, where you're looking now.</summary>
        public static void Recenter(Transform head)
        {
            CleanUp();
            SetAnchor(head);
            for (int s = 0; s < Slots; s++) if (_slots[s] != null) Pose(_slots[s], s);
            HarleyDock.Refresh(head, true);
        }

        static void SetAnchor(Transform head)
        {
            if (head == null) return;
            Vector3 f = head.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.001f) f = Vector3.forward;
            _anchorYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _anchor = head.position;
            _hasAnchor = true;
        }

        static void Pose(WebPanel w, int slot)
        {
            Quaternion rot = Quaternion.Euler(0f, _anchorYaw + SlotYaw[slot], 0f);
            Vector3 dir = rot * Vector3.forward;
            w.transform.position = _anchor + dir * Radius + Vector3.down * 0.06f;
            w.transform.rotation = Quaternion.LookRotation(dir, Vector3.up); // faces away from you, like every panel
        }

        static int VisibleCount()
        {
            int n = 0;
            foreach (var s in _slots) if (s != null && !s.Minimized) n++;
            return n;
        }

        static void CleanUp()
        {
            for (int s = 0; s < Slots; s++) if (_slots[s] == null || _slots[s].Minimized) _slots[s] = null;
        }
    }

    /// <summary>The bar under your windows: Apps, every open app (tap to show / hide), bring windows here, and Harley's menu.</summary>
    public class HarleyDock : SpatialPanel
    {
        static HarleyDock _instance;
        const float Btn = 58f, Gap = 10f, Pad = 14f;
        const int MaxApps = 8;

        readonly List<PanelButton> _apps = new List<PanelButton>();
        readonly List<Transform> _dots = new List<Transform>();
        PanelButton _appsBtn, _recenterBtn, _harleyBtn;
        TextMeshProUGUI _clock;
        readonly List<WebPanel> _shown = new List<WebPanel>();
        float _nextClock;

        /// <summary>Shows / updates the dock (hidden when no app is open).</summary>
        public static void Refresh(Transform head, bool move = false)
        {
            int open = 0;
            foreach (var w in WebPanel.Windows) if (w != null) open++;
            if (open == 0) { if (_instance) _instance.gameObject.SetActive(false); return; }
            if (_instance == null)
            {
                var go = new GameObject("Harley Dock");
                _instance = go.AddComponent<HarleyDock>();
                _instance.head = head != null ? head : (Camera.main ? Camera.main.transform : null);
                _instance.EnsureBuilt();
                move = true;
            }
            bool wasHidden = !_instance.gameObject.activeSelf;
            _instance.gameObject.SetActive(true);
            if (move || wasHidden) _instance.PlaceUnderWindows();
            _instance.Relayout();
        }

        protected override void Awake()
        {
            base.Awake();
            barHeight = 0.022f; // grab bar underneath: move the dock wherever you like
            size = new Vector2(Width(MaxApps) / PixelsPerMeter, (Btn + Pad * 2) / PixelsPerMeter);
        }

        static float Width(int apps) => Pad * 2 + (apps + 3) * Btn + (apps + 2) * Gap + 90f + (apps > 0 ? Gap * 2 : 0);

        protected override void Build()
        {
            var c = Content;
            background = new Color(0.09f, 0.09f, 0.11f, 0.92f);
            if (c.GetComponent<UnityEngine.UI.Image>()) c.GetComponent<UnityEngine.UI.Image>().color = background;

            _appsBtn = AddButton(c, "Apps", () => LauncherPanel.Show(head), null, 15);
            for (int i = 0; i < MaxApps; i++)
            {
                int k = i;
                var b = AddButton(c, "", () => TapApp(k), null, 26);
                b.label.fontStyle = FontStyles.Bold;
                _apps.Add(b);
                var dot = Box(b.rect, "Dot", Color.white);
                Place(dot.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-9, -9), new Vector2(9, -5));
                _dots.Add(dot.transform);
            }
            _recenterBtn = AddButton(c, "Here", () => HarleyWindows.Recenter(head), null, 15);
            _harleyBtn = AddButton(c, "H", OpenMenu, primary, 26);
            _clock = Label(c, "", 18, muted, TextAlignmentOptions.Center);
        }

        void PlaceUnderWindows()
        {
            Quaternion rot = Quaternion.Euler(0f, HarleyWindows.AnchorYaw, 0f);
            Vector3 f = rot * Vector3.forward;
            Vector3 a = HarleyWindows.Anchor;
            if (a == Vector3.zero && head != null) { a = head.position; f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized; }
            transform.position = a + f * 0.78f + Vector3.down * 0.5f;
            // Tilted toward you, like a keyboard tray
            Vector3 away = transform.position - a;
            transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        void Relayout()
        {
            _shown.Clear();
            foreach (var w in WebPanel.Windows) if (w != null && _shown.Count < MaxApps) _shown.Add(w);
            int n = _shown.Count;
            float wpx = Width(n);
            Root.sizeDelta = new Vector2(wpx, Root.sizeDelta.y);
            size = new Vector2(wpx / PixelsPerMeter, size.y);

            float x = Pad;
            PlaceTL(_appsBtn.rect, x, Pad, Btn * 1.2f, Btn); x += Btn * 1.2f + Gap;
            if (n > 0) x += Gap;
            for (int i = 0; i < MaxApps; i++)
            {
                var b = _apps[i];
                bool on = i < n;
                b.rect.gameObject.SetActive(on);
                if (!on) continue;
                var w = _shown[i];
                var app = w.App;
                b.baseColor = app != null ? app.color : new Color(0.3f, 0.3f, 0.36f, 1f);
                if (w.Minimized) b.baseColor = Color.Lerp(b.baseColor, new Color(0.15f, 0.15f, 0.18f, 1f), 0.55f);
                b.label.text = app != null ? app.letter : "W";
                _dots[i].gameObject.SetActive(!w.Minimized);
                PlaceTL(b.rect, x, Pad, Btn, Btn);
                x += Btn + Gap;
            }
            if (n > 0) x += Gap;
            PlaceTL(_recenterBtn.rect, x, Pad, Btn * 1.1f, Btn); x += Btn * 1.1f + Gap;
            PlaceTL(_harleyBtn.rect, x, Pad, Btn, Btn); x += Btn + Gap;
            PlaceTL(_clock.rectTransform, x, Pad, 80f, Btn);
        }

        void TapApp(int i)
        {
            if (i >= _shown.Count || _shown[i] == null) return;
            var w = _shown[i];
            if (w.Minimized) w.Restore();
            else w.Minimize();
        }

        void OpenMenu()
        {
            var menu = FindAnyObjectByType<HarleyMenu>(FindObjectsInactive.Include);
            if (menu != null) menu.Toggle();
        }

        protected override void Update()
        {
            base.Update();
            if (Time.unscaledTime >= _nextClock)
            {
                _nextClock = Time.unscaledTime + 5f;
                if (_clock) _clock.text = System.DateTime.Now.ToString("HH:mm");
            }
        }
    }
}

namespace Harley
{
    /// <summary>
    /// B on the right controller (or Y on the left) = "Here": all your windows and the dock come to where you
    /// are and look now. Handy after walking to another spot in the room.
    /// </summary>
    public class HarleyHereButton : MonoBehaviour
    {
        bool _was;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var go = new GameObject("Harley Here Button");
            DontDestroyOnLoad(go);
            go.AddComponent<HarleyHereButton>();
        }

        void Update()
        {
            bool down = false;
            var right = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            var left = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            if (right.isValid && right.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool b) && b) down = true;
            if (left.isValid && left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool y) && y) down = true;
            if (down && !_was && WebPanel.Windows.Count > 0 && Camera.main != null)
                HarleyWindows.Recenter(Camera.main.transform);
            _was = down;
        }
    }
}
