using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley notices things in your room on her own, on the headset itself (no internet, no limits):
    /// a small YOLO model (from Meta's open-source Spatial Lingo, MIT) looks at the passthrough camera
    /// every second or two and finds ~80 kinds of everyday objects (cup, laptop, phone, chair, bottle,
    /// book, tv, plant, person...). Each one gets a spot in the room by casting the camera ray into
    /// the room scan, so Harley can fly to "the cup", say what's around, or teach you its name.
    /// </summary>
    public class HarleyObjectSight : MonoBehaviour
    {
        public static HarleyObjectSight Instance { get; private set; }

        [Tooltip("The camera component (MRUK PassthroughCameraAccess). Found automatically.")]
        public MonoBehaviour passthroughCameraAccess;
        [Tooltip("Seconds between looks.")]
        public float interval = 1.5f;
        [Tooltip("Model layers run per frame (more = faster results, more load).")]
        public int layersPerFrame = 12;
        [Tooltip("Things not seen again for this long are forgotten.")]
        public float forgetAfter = 180f;
        public LayerMask roomLayers = ~0;

        public class Seen
        {
            public string label;
            public Vector3 position;
            public float lastSeen;
            public int count;
            public bool onSurface; // the ray hit the room scan (else a guess along the ray)
        }

        public IReadOnlyList<Seen> Objects => _seen;
        public bool Running => _worker != null;

        const int Size = 640;
        readonly List<Seen> _seen = new List<Seen>();
        Worker _worker;
        string[] _labels;
        Tensor<float> _input;
        IEnumerator<int> _schedule;
        System.Collections.IEnumerator _scheduleRaw;
        bool _busy;
        float _nextLook;
        Pose _pose;
        MethodInfo _getTexture, _getPose, _toRay;
        PropertyInfo _isPlaying;

        static readonly HashSet<string> Skip = new HashSet<string> { "person" }; // you, mostly

        void Awake() { Instance = this; }

        void Start()
        {
            try
            {
                var model = Resources.Load<ModelAsset>("Vision/yolo");
                var classes = Resources.Load<TextAsset>("Vision/classes");
                if (model == null || classes == null) { Debug.Log("[Harley] Object sight: no model in Resources/Vision, off."); enabled = false; return; }
                _labels = classes.text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                _worker = new Worker(ModelLoader.Load(model), BackendType.GPUCompute);
                _input = new Tensor<float>(new TensorShape(1, 3, Size, Size));
                Debug.Log("[Harley] Object sight ready (" + _labels.Length + " kinds of things).");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Harley] Object sight could not start: " + e.Message);
                enabled = false;
            }
        }

        void OnDestroy()
        {
            _worker?.Dispose();
            _input?.Dispose();
        }

        void Update()
        {
            if (_worker == null) return;
            if (_scheduleRaw != null)
            {
                for (int i = 0; i < layersPerFrame; i++)
                    if (!_scheduleRaw.MoveNext()) { _scheduleRaw = null; ReadResults(); break; }
                return;
            }
            if (_busy || Time.unscaledTime < _nextLook || HarleyWorld.Immersive) return;
            _nextLook = Time.unscaledTime + interval;
            Look();
            Forget();
        }

        // ---------- looking ----------

        void Look()
        {
            if (!FindCamera()) return;
            Texture tex = null;
            try
            {
                if (_isPlaying != null && !(bool)_isPlaying.GetValue(passthroughCameraAccess)) return;
                tex = _getTexture?.Invoke(passthroughCameraAccess, null) as Texture;
                if (_getPose != null) _pose = (Pose)_getPose.Invoke(passthroughCameraAccess, null);
            }
            catch { return; }
            if (tex == null) return;

            _busy = true;
            TextureConverter.ToTensor(tex, _input, default);
            _worker.SetInput(0, _input);
            _scheduleRaw = _worker.ScheduleIterable();
        }

        async void ReadResults()
        {
            try
            {
                var boxesOut = _worker.PeekOutput(0) as Tensor<float>;
                var idsOut = _worker.PeekOutput(1) as Tensor<int>;
                if (boxesOut == null || idsOut == null || boxesOut.shape[0] == 0) { _busy = false; return; }
                using var boxes = await boxesOut.ReadbackAndCloneAsync();
                using var ids = await idsOut.ReadbackAndCloneAsync();
                int n = Mathf.Min(Mathf.Min(boxes.shape[0], ids.shape[0]), 40);
                float now = Time.unscaledTime;
                for (int i = 0; i < n; i++)
                {
                    int id = ids[i];
                    if (id < 0 || id >= _labels.Length) continue;
                    string label = _labels[id].Trim();
                    if (Skip.Contains(label)) continue;
                    float cx = boxes[i, 0] / Size, cy = boxes[i, 1] / Size;
                    Place(label, new Vector2(cx, 1f - cy), now);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Harley] Object sight: " + e.Message); }
            _busy = false;
        }

        void Place(string label, Vector2 viewport, float now)
        {
            Ray ray;
            try { ray = (Ray)_toRay.Invoke(passthroughCameraAccess, new object[] { viewport, (Pose?)_pose }); }
            catch { return; }
            bool onSurface = Physics.Raycast(ray, out RaycastHit hit, 5f, roomLayers, QueryTriggerInteraction.Ignore);
            Vector3 p = onSurface ? hit.point : ray.origin + ray.direction * 1.4f;

            // Same thing seen again nearby: smooth its spot
            Seen best = null; float bestD = 0.45f;
            foreach (var s in _seen)
            {
                if (s.label != label) continue;
                float d = Vector3.Distance(s.position, p);
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) { best = new Seen { label = label, position = p }; _seen.Add(best); }
            else best.position = Vector3.Lerp(best.position, p, onSurface ? 0.35f : 0.1f);
            best.onSurface |= onSurface;
            best.lastSeen = now;
            best.count++;
        }

        void Forget()
        {
            float now = Time.unscaledTime;
            _seen.RemoveAll(s => now - s.lastSeen > forgetAfter || (s.count < 2 && now - s.lastSeen > 20f)); // one-off blips go fast
        }

        bool FindCamera()
        {
            if (passthroughCameraAccess == null)
            {
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
                    if (mb != null && mb.GetType().Name == "PassthroughCameraAccess") { passthroughCameraAccess = mb; break; }
                if (passthroughCameraAccess == null) return false;
            }
            if (_getTexture == null)
            {
                var t = passthroughCameraAccess.GetType();
                _getTexture = t.GetMethod("GetTexture", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _getPose = t.GetMethod("GetCameraPose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _toRay = t.GetMethod("ViewportPointToRay", BindingFlags.Public | BindingFlags.Instance);
                _isPlaying = t.GetProperty("IsPlaying", BindingFlags.Public | BindingFlags.Instance);
            }
            return passthroughCameraAccess.isActiveAndEnabled && _getTexture != null && _toRay != null;
        }

        // ---------- for Harley ----------

        /// <summary>Things noticed lately, nearest first, for the prompt.</summary>
        public string DescribeForPrompt(Transform head, int max = 10)
        {
            if (head == null || _seen.Count == 0) return "";
            float now = Time.unscaledTime;
            var list = new List<Seen>(_seen.FindAll(s => s.count >= 2));
            if (list.Count == 0) return "";
            list.Sort((a, b) => Vector3.Distance(a.position, head.position).CompareTo(Vector3.Distance(b.position, head.position)));
            var sb = new StringBuilder();
            Vector3 f = head.forward; f.y = 0; f = f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
            for (int i = 0; i < list.Count && i < max; i++)
            {
                var s = list[i];
                Vector3 d = s.position - head.position;
                float dist = new Vector2(d.x, d.z).magnitude;
                float ang = Vector3.SignedAngle(f, new Vector3(d.x, 0, d.z), Vector3.up);
                string dir = Mathf.Abs(ang) < 30 ? "ahead" : Mathf.Abs(ang) > 140 ? "behind you" : ang > 0 ? "to the right" : "to the left";
                string when = now - s.lastSeen < 10 ? "" : " (seen a little while ago)";
                sb.Append("- ").Append(s.label).Append(", ").Append(dist.ToString("0.0")).Append(" m ").Append(dir).Append(when).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The nearest seen thing whose name matches ("the cup", "my laptop", "bottle").</summary>
        public bool TryFind(string name, Vector3 near, out Seen found)
        {
            found = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.ToLowerInvariant();
            float best = float.MaxValue;
            foreach (var s in _seen)
            {
                if (!n.Contains(s.label.ToLowerInvariant())) continue;
                float d = Vector3.Distance(s.position, near);
                if (d < best) { best = d; found = s; }
            }
            return found != null;
        }
    }
}
