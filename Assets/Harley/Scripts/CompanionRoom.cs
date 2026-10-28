using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Room awareness from the headset's room scan (Meta MR Utility Kit).
    /// Harley knows where the couch, tables, bed, screen, windows, lamps and plants
    /// are, can talk about them and fly to them ("Harley, go sit on the couch").
    ///
    /// MRUK is read through reflection, so this compiles and runs without MRUK
    /// installed (Harley then just doesn't know the room layout).
    /// Tested against MRUK v207: MRUK.Instance, GetCurrentRoom(), MRUKRoom.Anchors,
    /// MRUKAnchor.Label / VolumeBounds / PlaneRect.
    /// </summary>
    public class CompanionRoom : MonoBehaviour
    {
        public class Thing
        {
            public string name;      // unique, e.g. "table", "table 2"
            public string kind;      // label in plain words, e.g. "table"
            public Vector3 center;   // world
            public Vector3 top;      // world, top of a volume (or center for planes)
            public Vector3 normal;   // planes: facing direction; volumes: up
            public Vector3 size;     // world-ish extents
            public bool isVolume;
            public Transform anchor;
        }

        [Tooltip("Seconds between room re-reads.")]
        public float refreshSeconds = 5f;

        public bool HasRoom => _things.Count > 0;
        public float? FloorY { get; private set; }
        public IReadOnlyList<Thing> Things => _things;
        /// <summary>The room's floor (MRUK): its anchor and its rectangle in the anchor's plane. Null until scanned.</summary>
        public Transform FloorAnchor { get; private set; }
        public Rect FloorRect { get; private set; }
        /// <summary>The room's ceiling, same idea.</summary>
        /// <summary>Wall planes from the room scan (anchor + rectangle in its local XY).</summary>
        public readonly List<(Transform anchor, Rect rect)> Walls = new List<(Transform, Rect)>();
        public Transform CeilingAnchor { get; private set; }
        public Rect CeilingRect { get; private set; }

        readonly List<Thing> _things = new List<Thing>();

        // reflection cache
        bool _resolved, _available;
        PropertyInfo _instance, _anchors, _label, _volume, _plane;
        MethodInfo _currentRoom;

        static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            { "TABLE", "table" }, { "COUCH", "couch" }, { "BED", "bed" }, { "STORAGE", "shelf" },
            { "SCREEN", "screen" }, { "LAMP", "lamp" }, { "PLANT", "plant" }, { "WALL_ART", "picture on the wall" },
            { "WINDOW_FRAME", "window" }, { "DOOR_FRAME", "door" }, { "OTHER", "object" }
        };

        IEnumerator Start()
        {
            while (true)
            {
                Refresh();
                yield return new WaitForSeconds(HasRoom ? refreshSeconds : 2f);
            }
        }

        void Resolve()
        {
            _resolved = true;
            Type mruk = FindType("Meta.XR.MRUtilityKit.MRUK");
            Type room = FindType("Meta.XR.MRUtilityKit.MRUKRoom");
            Type anchor = FindType("Meta.XR.MRUtilityKit.MRUKAnchor");
            if (mruk == null || room == null || anchor == null) return;
            const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
            _instance = mruk.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _currentRoom = mruk.GetMethod("GetCurrentRoom", inst, null, Type.EmptyTypes, null);
            _anchors = room.GetProperty("Anchors", inst);
            _label = anchor.GetProperty("Label", inst);
            _volume = anchor.GetProperty("VolumeBounds", inst);
            _plane = anchor.GetProperty("PlaneRect", inst);
            _available = _instance != null && _currentRoom != null && _anchors != null && _label != null;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        public void Refresh()
        {
            if (!_resolved) Resolve();
            if (!_available) return;

            object mruk, room;
            try
            {
                mruk = _instance.GetValue(null);
                if (mruk == null) return;
                room = _currentRoom.Invoke(mruk, null);
                if (room == null) return;
            }
            catch { return; }

            if (!(_anchors.GetValue(room) is IList anchors)) return;

            _things.Clear();
            Walls.Clear();
            var counts = new Dictionary<string, int>();
            float? floor = null;

            foreach (var a in anchors)
            {
                if (!(a is Component comp) || comp == null) continue;
                string labels = (_label.GetValue(a) ?? "").ToString(); // flags enum -> "TABLE" or "TABLE, OTHER"
                string first = labels.Split(',')[0].Trim();

                if (first == "FLOOR" || first == "CEILING")
                {
                    object fr = _plane != null ? _plane.GetValue(a) : null;
                    Rect rect = fr is Rect r0 ? r0 : new Rect(-2f, -2f, 4f, 4f);
                    if (first == "FLOOR") { floor = comp.transform.position.y; FloorAnchor = comp.transform; FloorRect = rect; }
                    else { CeilingAnchor = comp.transform; CeilingRect = rect; }
                    continue;
                }
                if (first == "WALL_FACE" || first == "INVISIBLE_WALL_FACE")
                {
                    object wr = _plane != null ? _plane.GetValue(a) : null;
                    if (wr is Rect wrect) Walls.Add((comp.transform, wrect));
                    continue;
                }
                if (!Kinds.TryGetValue(first, out string kind)) continue; // ceiling, global mesh...

                var thing = new Thing { kind = kind, anchor = comp.transform };
                Transform tr = comp.transform;

                object vol = _volume != null ? _volume.GetValue(a) : null; // boxed Bounds or null
                if (vol is Bounds b)
                {
                    thing.isVolume = true;
                    WorldBox(tr, b, out Vector3 min, out Vector3 max);
                    thing.center = (min + max) * 0.5f;
                    thing.size = max - min;
                    thing.top = new Vector3(thing.center.x, max.y, thing.center.z);
                    thing.normal = Vector3.up;
                }
                else
                {
                    object rect = _plane != null ? _plane.GetValue(a) : null;
                    Rect r = rect is Rect rr ? rr : new Rect(-0.25f, -0.25f, 0.5f, 0.5f);
                    thing.center = tr.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));
                    thing.size = new Vector3(r.width, r.height, 0.02f);
                    thing.top = thing.center;
                    thing.normal = tr.forward; // MRUK planes face along their forward axis
                }

                counts.TryGetValue(kind, out int n);
                counts[kind] = ++n;
                thing.name = n == 1 ? kind : kind + " " + n;
                _things.Add(thing);
            }

            if (floor.HasValue) FloorY = floor;
        }

        static void WorldBox(Transform tr, Bounds b, out Vector3 min, out Vector3 max)
        {
            min = Vector3.positiveInfinity;
            max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 w = tr.TransformPoint(c);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
        }

        /// <summary>Finds a thing by the name Claude used ("couch", "table 2", "the window").</summary>
        public bool TryFind(string name, Vector3 near, out Thing thing)
        {
            thing = null;
            if (string.IsNullOrWhiteSpace(name) || _things.Count == 0) return false;
            string q = name.Trim().ToLowerInvariant();
            if (q.StartsWith("the ", StringComparison.Ordinal)) q = q.Substring(4);

            foreach (var t in _things) if (t.name == q) { thing = t; return true; }

            // Same kind: pick the closest one.
            float best = float.MaxValue;
            foreach (var t in _things)
            {
                if (!(q.Contains(t.kind) || t.kind.Contains(q))) continue;
                float d = Vector3.Distance(near, t.center);
                if (d < best) { best = d; thing = t; }
            }
            return thing != null;
        }

        /// <summary>Best surface to sit on near the player (table, couch, bed, shelf top).</summary>
        public bool TryFindSeat(Transform head, float floorY, out Vector3 spot)
        {
            spot = default;
            float best = float.MaxValue;
            bool found = false;
            Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            foreach (var t in _things)
            {
                if (!t.isVolume) continue;
                float h = t.top.y - floorY;
                if (h < 0.3f || h > 1.3f) continue;
                Vector3 to = t.top - head.position;
                float dist = to.magnitude;
                if (dist > 4f) continue;
                float angle = Vector3.Angle(f, Vector3.ProjectOnPlane(to, Vector3.up));
                float score = dist * 0.4f + angle * 0.01f;
                if (score < best) { best = score; spot = t.top; found = true; }
            }
            return found;
        }

        /// <summary>One line per object, with distance and direction from the player, for the prompt.</summary>
        public string DescribeForPrompt(Transform head, int max = 12)
        {
            if (_things.Count == 0 || head == null) return "";
            var sorted = new List<Thing>(_things);
            sorted.Sort((a, b) => Vector3.Distance(head.position, a.center).CompareTo(Vector3.Distance(head.position, b.center)));

            var sb = new StringBuilder();
            int n = 0;
            foreach (var t in sorted)
            {
                if (n++ >= max) break;
                Vector3 to = t.center - head.position;
                float dist = new Vector2(to.x, to.z).magnitude;
                sb.Append("- ").Append(t.name).Append(": ")
                  .Append(dist.ToString("0.0", CultureInfo.InvariantCulture)).Append(" m ")
                  .Append(Direction(head, to)).Append('\n');
            }
            return sb.ToString();
        }

        static string Direction(Transform head, Vector3 to)
        {
            Vector3 f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 d = Vector3.ProjectOnPlane(to, Vector3.up);
            if (d.sqrMagnitude < 0.0001f || f.sqrMagnitude < 0.0001f) return "right here";
            float a = Vector3.SignedAngle(f, d.normalized, Vector3.up); // + = right
            float abs = Mathf.Abs(a);
            string side = a > 0 ? "right" : "left";
            if (abs < 25f) return "in front of them";
            if (abs < 70f) return "front-" + side;
            if (abs < 115f) return "to their " + side;
            if (abs < 155f) return "behind-" + side;
            return "behind them";
        }
    }
}
