using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>Something you can pick up with your hand (pinch) or controller (grip), and throw.</summary>
    public class Grabbable : MonoBehaviour
    {
        public static readonly List<Grabbable> All = new List<Grabbable>();
        [Tooltip("How close your hand must be to grab it (meters, from its centre).")]
        public float reach = 0.12f;
        public bool IsHeld { get; internal set; }
        /// <summary>Fired when you pick it up / let go (with the throw velocity).</summary>
        public event Action Grabbed;
        public event Action<Vector3> Released;
        internal void RaiseGrabbed() => Grabbed?.Invoke();
        internal void RaiseReleased(Vector3 v) => Released?.Invoke(v);
        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); IsHeld = false; }
    }

    /// <summary>
    /// Lets both hands and both controllers grab Grabbable things: pinch thumb + index
    /// (hand tracking) or squeeze grip (controller) near one, move it, let go to drop or throw.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class HarleyGrab : MonoBehaviour
    {
        public static HarleyGrab Instance { get; private set; }

        class Hand
        {
            public XRNode node;
            public Transform anchor, index, thumb;
            public bool was;
            public Grabbable held;
            public Vector3 offset;
            public Quaternion rotOffset;
            public readonly Queue<(float t, Vector3 p)> trail = new Queue<(float, Vector3)>();
        }

        readonly Hand[] _hands = { new Hand { node = XRNode.LeftHand }, new Hand { node = XRNode.RightHand } };
        float _nextFind;

        /// <summary>A grabbable thing is within reach of this point (Harley's own grab steps aside for it).</summary>
        public static bool NearAny(Vector3 point)
        {
            foreach (var g in Grabbable.All)
                if (g != null && Vector3.Distance(g.transform.position, point) < g.reach * (g.IsHeld ? 1.5f : 1f)) return true;
            return false;
        }

        /// <summary>True while that hand holds something (so other tools leave it alone).</summary>
        public bool Holding(int side) => _hands[side].held != null;

        void Awake() { Instance = this; }

        void Update()
        {
            if (Time.unscaledTime >= _nextFind) Find();
            for (int i = 0; i < 2; i++) UpdateHand(_hands[i]);
        }

        void UpdateHand(Hand h)
        {
            bool down = GrabDown(h, out Vector3 point, out Quaternion rot);
            if (!down)
            {
                if (h.held != null) Release(h, point);
                h.was = false;
                return;
            }

            h.trail.Enqueue((Time.time, point));
            while (h.trail.Count > 0 && Time.time - h.trail.Peek().t > 0.1f) h.trail.Dequeue();

            if (!h.was && h.held == null)
            {
                Grabbable best = null;
                float bestD = float.MaxValue;
                foreach (var g in Grabbable.All)
                {
                    if (g == null || g.IsHeld) continue;
                    float d = Vector3.Distance(g.transform.position, point);
                    if (d < g.reach && d < bestD) { bestD = d; best = g; }
                }
                if (best != null)
                {
                    h.held = best;
                    best.IsHeld = true;
                    h.offset = Quaternion.Inverse(rot) * (best.transform.position - point);
                    h.rotOffset = Quaternion.Inverse(rot) * best.transform.rotation;
                    var rb = best.GetComponent<Rigidbody>();
                    if (rb) rb.isKinematic = true;
                    best.RaiseGrabbed();
                }
            }
            h.was = true;

            if (h.held != null)
            {
                h.held.transform.position = point + rot * h.offset;
                h.held.transform.rotation = rot * h.rotOffset;
            }
        }

        void Release(Hand h, Vector3 point)
        {
            var g = h.held;
            h.held = null;
            if (g == null) return;
            g.IsHeld = false;
            Vector3 v = Vector3.zero;
            if (h.trail.Count >= 2)
            {
                var first = h.trail.Peek();
                float span = Time.time - first.t;
                if (span > 0.01f) v = (point - first.p) / span;
            }
            h.trail.Clear();
            var rb = g.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.isKinematic = false;
                rb.linearVelocity = v * 1.15f;
            }
            g.RaiseReleased(v);
        }

        bool GrabDown(Hand h, out Vector3 point, out Quaternion rot)
        {
            point = Vector3.zero;
            rot = Quaternion.identity;

            // Hand tracking: pinch thumb and index
            if (h.index != null && h.thumb != null && h.index.gameObject.activeInHierarchy)
            {
                float pinch = Vector3.Distance(h.index.position, h.thumb.position);
                point = (h.index.position + h.thumb.position) * 0.5f;
                rot = h.index.rotation;
                bool on = h.was ? pinch < 0.045f : pinch < 0.024f;
                // Only count it when the fingertips actually moved recently (a frozen, untracked hand keeps its last pose)
                if (on) return true;
            }

            // Controller: grip button
            var dev = InputDevices.GetDeviceAtXRNode(h.node);
            if (h.anchor != null && dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float grip))
            {
                point = h.anchor.position + h.anchor.forward * 0.04f;
                rot = h.anchor.rotation;
                return h.was ? grip > 0.35f : grip > 0.65f;
            }
            return false;
        }

        void Find()
        {
            _nextFind = Time.unscaledTime + 2f;
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                string n = t.name.ToLowerInvariant();
                int side = n.Contains("left") ? 0 : n.Contains("right") ? 1 : -1;
                if (n == "leftcontrolleranchor" && _hands[0].anchor == null) _hands[0].anchor = t;
                if (n == "rightcontrolleranchor" && _hands[1].anchor == null) _hands[1].anchor = t;
                bool index = n.Contains("indextip") || n.Contains("index_tip") || n.Contains("index_finger_tip") || n.Contains("indexfingertip");
                bool thumb = n.Contains("thumbtip") || n.Contains("thumb_tip") || n.Contains("thumb_finger_tip");
                if (!index && !thumb) continue;
                if (side < 0) side = SideOf(t);
                if (side < 0) continue;
                if (index && _hands[side].index == null) _hands[side].index = t;
                if (thumb && _hands[side].thumb == null) _hands[side].thumb = t;
            }
        }

        static int SideOf(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
            {
                string n = x.name.ToLowerInvariant();
                if (n.Contains("left") || n.StartsWith("l_", StringComparison.Ordinal) || n.EndsWith("_l", StringComparison.Ordinal)) return 0;
                if (n.Contains("right") || n.StartsWith("r_", StringComparison.Ordinal) || n.EndsWith("_r", StringComparison.Ordinal)) return 1;
            }
            return -1;
        }
    }
}
