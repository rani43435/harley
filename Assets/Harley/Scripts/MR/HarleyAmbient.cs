using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Harley
{
    /// <summary>
    /// Ambient light, like Ambilight on a TV: the colours at the edges of the window you're watching glow
    /// around it and onto the real wall behind it, and the room dims a little. Follows the picture live.
    /// </summary>
    public class HarleyAmbient : MonoBehaviour
    {
        public static HarleyAmbient Instance { get; private set; }

        public bool On { get; private set; }
        [Tooltip("How much the real room dims while it's on (passthrough brightness, 0 = not at all).")]
        public float roomDim = 0.35f;
        public float glowIntensity = 0.9f, wallIntensity = 0.55f;

        WebPanel _target;
        GameObject _glow, _wall;
        Material _glowMat, _wallMat;
        RenderTexture _small;
        readonly Color[] _edges = { Color.black, Color.black, Color.black, Color.black }; // L R T B
        readonly Color[] _shown = { Color.black, Color.black, Color.black, Color.black };
        bool _reading;
        float _nextSample, _nextWall, _fade;
        readonly List<MonoBehaviour> _passthrough = new List<MonoBehaviour>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Instance != null) return;
            var go = new GameObject("Harley Ambient Light");
            DontDestroyOnLoad(go);
            go.AddComponent<HarleyAmbient>();
        }

        void Awake() { Instance = this; }

        public void Set(bool on)
        {
            On = on;
            PlayerPrefs.SetInt("harley.ambient", on ? 1 : 0);
            if (!on) Dim(0f);
        }

        public void Toggle() => Set(!On);

        void Start() => On = PlayerPrefs.GetInt("harley.ambient", 0) == 1;

        void Update()
        {
            var target = On ? PickWindow() : null;
            _fade = Mathf.MoveTowards(_fade, target != null ? 1f : 0f, Time.unscaledDeltaTime * 1.5f);
            if (target != null) _target = target;

            if (_fade <= 0.001f)
            {
                if (_glow) _glow.SetActive(false);
                if (_wall) _wall.SetActive(false);
                if (!On) return;
                Dim(0f);
                return;
            }
            if (_target == null) return;
            Ensure();

            // Sample the picture's edges ~12 times a second
            var tex = _target.ShownTexture;
            if (tex != null && !_reading && Time.unscaledTime >= _nextSample && SystemInfo.supportsAsyncGPUReadback)
            {
                _nextSample = Time.unscaledTime + 0.08f;
                Graphics.Blit(tex, _small);
                _reading = true;
                AsyncGPUReadback.Request(_small, 0, TextureFormat.RGBA32, OnSample);
            }
            for (int i = 0; i < 4; i++) _shown[i] = Color.Lerp(_shown[i], _edges[i], 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f));

            // Glow right behind the window, the size of the window
            var w = _target.transform;
            _glow.SetActive(true);
            _glow.transform.SetPositionAndRotation(w.position + w.forward * 0.02f, w.rotation);
            Vector2 size = _target.size * w.lossyScale.x;
            _glow.transform.localScale = new Vector3(size.x * 1.7f, size.y * 1.9f, 1f);
            Apply(_glowMat, glowIntensity * _fade, new Vector2(1f / 1.7f, 1f / 1.9f), 5f);

            // And on the real wall behind it
            if (Time.unscaledTime >= _nextWall) { _nextWall = Time.unscaledTime + 0.5f; PlaceWall(w, size); }
            if (_wall.activeSelf) Apply(_wallMat, wallIntensity * _fade, new Vector2(0.35f, 0.4f), 3f);

            Dim(roomDim * _fade);
        }

        /// <summary>The window you're watching: playing, looked at most, not minimized.</summary>
        WebPanel PickWindow()
        {
            var cam = Camera.main;
            WebPanel best = null; float bestScore = -1f;
            foreach (var w in WebPanel.Windows)
            {
                if (w == null || w.Minimized || w.ShownTexture == null) continue;
                float score = cam != null ? Vector3.Dot(cam.transform.forward, (w.transform.position - cam.transform.position).normalized) : 0f;
                if (w == _target) score += 0.15f; // don't flicker between two windows
                if (score > bestScore) { bestScore = score; best = w; }
            }
            return bestScore > 0.5f ? best : null;
        }

        void OnSample(AsyncGPUReadbackRequest r)
        {
            _reading = false;
            if (r.hasError) return;
            var px = r.GetData<Color32>();
            const int W = 8, H = 4;
            Color l = default, rt = default, top = default, bottom = default;
            for (int y = 0; y < H; y++) { l += (Color)px[y * W] + (Color)px[y * W + 1]; rt += (Color)px[y * W + W - 1] + (Color)px[y * W + W - 2]; }
            for (int x = 0; x < W; x++) { bottom += px[x]; top += px[(H - 1) * W + x]; }
            _edges[0] = Boost(l / (H * 2)); _edges[1] = Boost(rt / (H * 2));
            _edges[2] = Boost(top / W); _edges[3] = Boost(bottom / W);
        }

        // Light looks better a bit more saturated and never quite black
        static Color Boost(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            return Color.HSVToRGB(h, Mathf.Clamp01(s * 1.35f), Mathf.Clamp(v * 1.2f, 0.06f, 1f));
        }

        void Apply(Material m, float intensity, Vector2 inner, float falloff)
        {
            m.SetColor("_ColorL", _shown[0]); m.SetColor("_ColorR", _shown[1]);
            m.SetColor("_ColorT", _shown[2]); m.SetColor("_ColorB", _shown[3]);
            m.SetVector("_Inner", inner);
            m.SetFloat("_Falloff", falloff);
            m.SetFloat("_Intensity", intensity);
        }

        void PlaceWall(Transform w, Vector2 size)
        {
            // Look straight back from the window into the room scan (walls have colliders)
            var hits = Physics.RaycastAll(w.position, w.forward, 4f, ~0, QueryTriggerInteraction.Ignore);
            float bestD = float.MaxValue; RaycastHit best = default; bool found = false;
            foreach (var h in hits)
            {
                if (h.distance < 0.15f || h.collider.GetComponentInParent<CompanionBrain>()) continue; // not Harley
                if (Mathf.Abs(Vector3.Dot(h.normal, Vector3.up)) > 0.5f) continue;                       // walls only
                if (h.distance < bestD) { bestD = h.distance; best = h; found = true; }
            }
            _wall.SetActive(found);
            if (!found) return;
            _wall.transform.SetPositionAndRotation(best.point + best.normal * 0.01f, Quaternion.LookRotation(-best.normal, Vector3.up));
            float grow = 1f + best.distance * 0.8f; // farther wall, bigger spill
            _wall.transform.localScale = new Vector3(size.x * 2.6f * grow, size.y * 2.6f * grow, 1f);
        }

        void Ensure()
        {
            if (_glow != null) return;
            var shader = Shader.Find("Harley/Ambient Glow");
            _glowMat = new Material(shader);
            _wallMat = new Material(shader);
            _glow = MakeQuad("Ambient Glow", _glowMat);
            _wall = MakeQuad("Ambient Wall", _wallMat);
            _small = new RenderTexture(8, 4, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };
        }

        GameObject MakeQuad(string name, Material m)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            q.SetActive(false);
            return q;
        }

        float _dimmed = -1f;
        /// <summary>Dims the real room (passthrough brightness) a little, like turning the lights down.</summary>
        void Dim(float amount)
        {
            if (Mathf.Abs(amount - _dimmed) < 0.01f) return;
            _dimmed = amount;
            if (_passthrough.Count == 0)
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                    if (mb != null && mb.GetType().Name == "OVRPassthroughLayer") _passthrough.Add(mb);
            foreach (var pt in _passthrough)
            {
                if (pt == null) continue;
                var m = pt.GetType().GetMethod("SetBrightnessContrastSaturation", new[] { typeof(float), typeof(float), typeof(float) });
                try { m?.Invoke(pt, new object[] { -amount, 0f, -amount * 0.3f }); } catch { }
            }
        }

        void OnDestroy()
        {
            Dim(0f);
            if (_small) _small.Release();
        }
    }
}
