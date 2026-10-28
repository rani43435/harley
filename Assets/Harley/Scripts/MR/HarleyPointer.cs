using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>
    /// Lets both hands and both controllers use Harley's floating panels:
    ///  - Controllers: point the ray, pull the trigger to click, hold to drag, thumbstick to scroll.
    ///  - Hands: point with an open hand, pinch thumb and index to click, pinch and move to drag.
    ///  - Or touch a panel directly with your index finger.
    ///
    /// Works with Meta's OVRCameraRig + OVRHand without a compile-time dependency
    /// (found by name / reflection), like the rest of Harley.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class HarleyPointer : MonoBehaviour
    {
        [Tooltip("Material for the ray and the cursor dot (the builder sets Harley_Glow).")]
        public Material lineMaterial;
        public Color rayColor = new Color(0.77f, 0.71f, 0.99f, 0.9f);
        public Color pressColor = new Color(0.55f, 0.36f, 0.97f, 1f);
        public float maxDistance = 6f;
        [Tooltip("Index finger closer than this to a panel = touching it.")]
        public float pokeDepth = 0.012f;

        public Transform head;

        class Side
        {
            public int id;
            public XRNode node;
            public string anchorName;
            public Transform anchor;     // controller anchor
            public MonoBehaviour hand;   // OVRHand
            public Transform indexTip;
            public LineRenderer line;
            public Transform dot;
            public PanelSurface hover, captured;
            public bool pressed, poking;
            public bool active;
        }

        readonly Side[] _sides = new Side[2];
        float _nextSearch;

        // OVRHand via reflection
        Type _ovrHandType;
        PropertyInfo _isTracked, _pointerValid, _pointerPose;
        MethodInfo _pinchStrength;
        object _indexFinger;

        /// <summary>True while any pointer is over a panel (so Harley's hands logic can ignore that pinch).</summary>
        public static bool AnyHover { get; private set; }

        /// <summary>A hand or controller ray that is not pointing at a panel: the play tools use these.</summary>
        public struct FreeRay
        {
            public bool valid, pressed, isHand;
            public Vector3 origin, direction;
        }
        /// <summary>[0] left, [1] right. Updated every frame.</summary>
        public static readonly FreeRay[] Free = new FreeRay[2];
        /// <summary>Set by the play tools while a tool is active, so the ray is drawn in empty space too.</summary>
        public static bool ShowFreeRay;
        public static Color FreeRayColor = new Color(0.77f, 0.71f, 0.99f, 0.6f);

        void Awake()
        {
            _sides[0] = new Side { id = 0, node = XRNode.LeftHand, anchorName = "leftcontrolleranchor" };
            _sides[1] = new Side { id = 1, node = XRNode.RightHand, anchorName = "rightcontrolleranchor" };
            foreach (var s in _sides) MakeVisuals(s);
        }

        void MakeVisuals(Side s)
        {
            Material mat = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
            var go = new GameObject(s.id == 0 ? "Pointer Left" : "Pointer Right");
            go.transform.SetParent(transform, false);
            s.line = go.AddComponent<LineRenderer>();
            s.line.sharedMaterial = mat;
            s.line.positionCount = 2;
            s.line.widthCurve = AnimationCurve.Linear(0f, 0.004f, 1f, 0.0015f);
            s.line.useWorldSpace = true;
            s.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s.line.receiveShadows = false;
            s.line.enabled = false;

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = "Cursor";
            dot.transform.SetParent(go.transform, false);
            dot.transform.localScale = Vector3.one * 0.008f;
            var r = dot.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s.dot = dot.transform;
            dot.SetActive(false);
        }

        void Update()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (Time.unscaledTime >= _nextSearch) Find();

            bool any = false;
            foreach (var s in _sides)
            {
                UpdateSide(s);
                any |= s.hover != null || s.captured != null;
            }
            AnyHover = any;
        }

        // ---------- per hand ----------

        void UpdateSide(Side s)
        {
            bool havePose = GetRay(s, out Vector3 origin, out Vector3 dir, out bool pressedNow, out Vector2 stick, out bool isHand);
            Free[s.id] = default;

            // Direct touch with the index finger beats the ray.
            if (isHand && s.indexTip != null && s.indexTip.gameObject.activeInHierarchy && !s.pressed)
            {
                if (TryPoke(s)) { HideVisuals(s); return; }
            }
            else if (s.poking) EndPoke(s);

            if (!havePose)
            {
                if (s.captured != null) { s.captured.PointerUp(Plane(s.captured, s, origin, dir)); s.captured = null; }
                SetHover(s, null);
                s.pressed = false;
                HideVisuals(s);
                return;
            }

            // Pressed panel keeps the pointer until release (drag).
            if (s.captured != null)
            {
                var h = Plane(s.captured, s, origin, dir);
                if (pressedNow) s.captured.PointerDrag(h);
                else { s.captured.PointerUp(h); s.captured = null; }
                s.pressed = pressedNow;
                Draw(s, origin, h.world, true);
                return;
            }

            PanelSurface best = null;
            PanelPointer bestHit = default;
            float bestDist = maxDistance;
            foreach (var p in PanelSurface.All)
            {
                if (p == null || !p.Interactable) continue;
                if (p.Raycast(origin, dir, out var hit) && hit.distance < bestDist)
                {
                    best = p; bestHit = hit; bestDist = hit.distance;
                }
            }
            bestHit.id = s.id;

            SetHover(s, best);
            if (best != null)
            {
                best.PointerHover(bestHit);
                if (stick.sqrMagnitude > 0.04f) best.PointerScroll(bestHit, stick);
                if (pressedNow && !s.pressed)
                {
                    s.captured = best;
                    best.PointerDown(bestHit);
                }
                Draw(s, origin, bestHit.world, pressedNow);
            }
            else
            {
                HideVisuals(s);
                // Not on a panel: free for the play tools
                Free[s.id] = new FreeRay { valid = true, pressed = pressedNow, isHand = isHand, origin = origin, direction = dir };
                if (ShowFreeRay) DrawFree(s, origin, dir, pressedNow);
            }

            s.pressed = pressedNow;
        }

        PanelPointer Plane(PanelSurface p, Side s, Vector3 origin, Vector3 dir)
        {
            if (!p.Raycast(origin, dir, out var hit, true))
                hit = new PanelPointer { origin = origin, direction = dir, world = origin + dir * 0.5f, local = new Vector2(9f, 9f), distance = 0.5f };
            hit.id = s.id;
            return hit;
        }

        void SetHover(Side s, PanelSurface p)
        {
            if (s.hover == p) return;
            if (s.hover != null) s.hover.PointerExit(s.id);
            s.hover = p;
        }

        // ---------- finger touch ----------

        bool TryPoke(Side s)
        {
            Vector3 tip = s.indexTip.position;
            foreach (var p in PanelSurface.All)
            {
                if (p == null || !p.Interactable) continue;
                Vector3 n = p.transform.forward;              // points away from the player
                float depth = Vector3.Dot(tip - p.transform.position, n); // >0 = pushed through
                Vector3 d = tip - p.transform.position;
                var local = new Vector2(Vector3.Dot(d, p.transform.right), Vector3.Dot(d, p.transform.up));
                bool inside = Mathf.Abs(local.x) <= p.size.x * 0.5f && Mathf.Abs(local.y) <= p.size.y * 0.5f;
                var ev = new PanelPointer { id = s.id, origin = tip - n * 0.1f, direction = n, world = tip, local = local, distance = 0.1f };

                if (s.poking && s.captured == p)
                {
                    if (depth < -pokeDepth * 2.5f || !inside) { p.PointerUp(ev); s.captured = null; s.poking = false; SetHover(s, null); return false; }
                    p.PointerDrag(ev);
                    return true;
                }
                if (!s.poking && inside && depth > -pokeDepth && depth < 0.05f)
                {
                    s.poking = true;
                    s.captured = p;
                    SetHover(s, p);
                    p.PointerHover(ev);
                    p.PointerDown(ev);
                    return true;
                }
            }
            if (s.poking) EndPoke(s);
            return false;
        }

        void EndPoke(Side s)
        {
            if (s.captured != null) s.captured.PointerUp(new PanelPointer { id = s.id, local = new Vector2(9f, 9f) });
            s.captured = null;
            s.poking = false;
            SetHover(s, null);
        }

        // ---------- input sources ----------

        bool GetRay(Side s, out Vector3 origin, out Vector3 dir, out bool pressed, out Vector2 stick, out bool isHand)
        {
            origin = Vector3.zero; dir = Vector3.forward; pressed = false; stick = Vector2.zero; isHand = false;

            // Tracked hand?
            if (s.hand != null && s.hand.isActiveAndEnabled && Bool(_isTracked, s.hand))
            {
                isHand = true;
                if (!Bool(_pointerValid, s.hand)) return false;
                var pose = _pointerPose?.GetValue(s.hand) as Transform;
                if (pose == null) return false;
                origin = pose.position;
                dir = pose.forward;
                float pinch = 0f;
                try { pinch = (float)_pinchStrength.Invoke(s.hand, new[] { _indexFinger }); } catch { }
                pressed = s.pressed ? pinch > 0.6f : pinch > 0.85f;
                return true;
            }

            // Controller?
            InputDevice dev = InputDevices.GetDeviceAtXRNode(s.node);
            if (s.anchor != null && s.anchor.gameObject.activeInHierarchy && dev.isValid)
            {
                origin = s.anchor.position;
                dir = s.anchor.forward;
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float trig))
                    pressed = s.pressed ? trig > 0.35f : trig > 0.6f;
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out stick);
                return true;
            }
            return false;
        }

        static bool Bool(PropertyInfo p, object o)
        {
            if (p == null || o == null) return false;
            try { return (bool)p.GetValue(o); } catch { return false; }
        }

        void Find()
        {
            _nextSearch = Time.unscaledTime + 2f;

            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != "OVRHand") continue;
                if (_ovrHandType == null) BindHand(mb.GetType());
                int side = SideOf(mb);
                if (side < 0 && _sides[0].hand == null) _sides[0].hand = mb;
                if (side > 0 && _sides[1].hand == null) _sides[1].hand = mb;
            }

            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                string n = t.name.ToLowerInvariant();
                foreach (var s in _sides)
                    if (s.anchor == null && n == s.anchorName) s.anchor = t;

                bool index = n.Contains("indextip") || n.Contains("index_tip") || n.Contains("index_finger_tip") || n.Contains("indexfingertip");
                if (!index) continue;
                int side = SideOfTransform(t);
                if (side < 0 && _sides[0].indexTip == null) _sides[0].indexTip = t;
                if (side > 0 && _sides[1].indexTip == null) _sides[1].indexTip = t;
            }
        }

        void BindHand(Type t)
        {
            _ovrHandType = t;
            const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
            _isTracked = t.GetProperty("IsTracked", inst);
            _pointerValid = t.GetProperty("IsPointerPoseValid", inst);
            _pointerPose = t.GetProperty("PointerPose", inst);
            _pinchStrength = t.GetMethod("GetFingerPinchStrength", inst);
            if (_pinchStrength != null)
            {
                Type finger = _pinchStrength.GetParameters()[0].ParameterType;
                try { _indexFinger = Enum.Parse(finger, "Index"); } catch { _pinchStrength = null; }
            }
        }

        int SideOf(MonoBehaviour hand)
        {
            // OVRHand.GetHand() says HandLeft / HandRight; fall back to the object names.
            try
            {
                var m = hand.GetType().GetMethod("GetHand", BindingFlags.Public | BindingFlags.Instance);
                string h = m?.Invoke(hand, null)?.ToString() ?? "";
                if (h.Contains("Left")) return -1;
                if (h.Contains("Right")) return 1;
            }
            catch { }
            return SideOfTransform(hand.transform);
        }

        static int SideOfTransform(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
            {
                string n = x.name.ToLowerInvariant();
                if (n.Contains("left") || n.StartsWith("l_", StringComparison.Ordinal) || n.EndsWith("_l", StringComparison.Ordinal)) return -1;
                if (n.Contains("right") || n.StartsWith("r_", StringComparison.Ordinal) || n.EndsWith("_r", StringComparison.Ordinal)) return 1;
            }
            return 0;
        }

        // ---------- visuals ----------

        void Draw(Side s, Vector3 from, Vector3 to, bool pressed)
        {
            Color c = pressed ? pressColor : rayColor;
            s.line.enabled = true;
            s.line.SetPosition(0, from + (to - from).normalized * 0.03f);
            s.line.SetPosition(1, to);
            s.line.startColor = new Color(c.r, c.g, c.b, 0.05f);
            s.line.endColor = c;
            s.dot.gameObject.SetActive(true);
            s.dot.position = to - (to - from).normalized * 0.002f;
            s.dot.localScale = Vector3.one * (pressed ? 0.006f : 0.009f);
        }

        void DrawFree(Side s, Vector3 from, Vector3 dir, bool pressed)
        {
            Vector3 to = from + dir * 0.6f;
            s.line.enabled = true;
            s.line.SetPosition(0, from + dir * 0.03f);
            s.line.SetPosition(1, to);
            Color c = pressed ? pressColor : FreeRayColor;
            s.line.startColor = new Color(c.r, c.g, c.b, 0.0f);
            s.line.endColor = c;
            s.dot.gameObject.SetActive(false);
        }

        void HideVisuals(Side s)
        {
            if (s.line) s.line.enabled = false;
            if (s.dot) s.dot.gameObject.SetActive(false);
        }
    }
}
