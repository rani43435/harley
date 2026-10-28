using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Room or World. "Room" = mixed reality (your real room through passthrough).
    /// "World" = fully immersive: Harley's own place, a quiet dusk island with a lake,
    /// soft hills, trees, glowing crystals, floating lanterns and stars.
    /// Everything is made from simple shapes at runtime (nothing to import).
    /// Your real room's invisible walls stay, so Harley never flies through them.
    /// </summary>
    public class HarleyWorld : MonoBehaviour
    {
        public static HarleyWorld Instance { get; private set; }
        public static bool Immersive => Instance != null && Instance._immersive;

        [Tooltip("Lit material to copy for the world (the builder uses Harley's white).")]
        public Material litTemplate;
        [Tooltip("Glowing material to copy (the builder uses Harley's purple).")]
        public Material glowTemplate;
        public Transform head;

        const string Pref = "harley.world";
        bool _immersive;
        GameObject _world;
        Camera _cam;
        CameraClearFlags _clearFlags;
        Color _clearColor;
        readonly List<MonoBehaviour> _passthrough = new List<MonoBehaviour>();
        float _floorY;

        void Awake() { Instance = this; }

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (PlayerPrefs.GetInt(Pref, 0) == 1) Invoke(nameof(GoWorld), 0.5f);
        }

        void GoWorld() => SetImmersive(true);

        public void Toggle() => SetImmersive(!_immersive);

        public void SetImmersive(bool on)
        {
            if (on == _immersive) return;
            _immersive = on;
            PlayerPrefs.SetInt(Pref, on ? 1 : 0);
            PlayerPrefs.Save();

            if (_cam == null) { _cam = Camera.main; if (_cam) { _clearFlags = _cam.clearFlags; _clearColor = _cam.backgroundColor; } }

            // Passthrough off in the world (saves battery too), back on in the room.
            if (_passthrough.Count == 0)
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (mb != null && mb.GetType().Name == "OVRPassthroughLayer") _passthrough.Add(mb);
            foreach (var pt in _passthrough) if (pt) pt.enabled = !on;

            if (on)
            {
                if (_world == null) Build();
                _world.SetActive(true);
                if (_cam) { _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = new Color(0.08f, 0.06f, 0.2f, 1f); }
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.42f, 0.36f, 0.55f);
            }
            else
            {
                if (_world) _world.SetActive(false);
                if (_cam) { _cam.clearFlags = _clearFlags; _cam.backgroundColor = _clearColor; }
            }
        }

        // ---------- building the world ----------

        void Build()
        {
            _world = new GameObject("Harley World");
            Transform w = _world.transform;
            var mover = FindAnyObjectByType<CompanionMovement>();
            _floorY = mover != null ? mover.floorY : 0f;
            Vector3 c = head != null ? new Vector3(head.position.x, _floorY, head.position.z) : new Vector3(0, _floorY, 0);
            var rnd = new System.Random(7);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);

            // Sky
            var sky = Prim(PrimitiveType.Sphere, "Sky", w, c, Vector3.one * 900f, null);
            var skyShader = Shader.Find("Harley/Sky");
            if (skyShader != null) sky.GetComponent<Renderer>().sharedMaterial = new Material(skyShader);

            // Ground island, a lake, a beach ring
            var grass = Lit(new Color(0.36f, 0.52f, 0.42f), 0.15f);
            var sand = Lit(new Color(0.86f, 0.78f, 0.66f), 0.2f);
            var water = Lit(new Color(0.23f, 0.33f, 0.62f), 0.92f);
            var rock = Lit(new Color(0.42f, 0.4f, 0.5f), 0.2f);
            var bark = Lit(new Color(0.42f, 0.3f, 0.26f), 0.1f);
            var leaf = Lit(new Color(0.55f, 0.42f, 0.72f), 0.25f);   // lilac trees
            var leaf2 = Lit(new Color(0.34f, 0.58f, 0.5f), 0.25f);
            var mountain = Lit(new Color(0.3f, 0.26f, 0.44f), 0.1f);
            var crystal = Glow(new Color(0.62f, 0.45f, 1f), 1.6f);
            var lantern = Glow(new Color(1f, 0.78f, 0.5f), 2.2f);

            var ocean = Prim(PrimitiveType.Cylinder, "Ocean", w, c + Vector3.down * 0.12f, new Vector3(600f, 0.05f, 600f), water);
            Prim(PrimitiveType.Cylinder, "Beach", w, c + Vector3.down * 0.05f, new Vector3(46f, 0.05f, 46f), sand);
            var ground = Prim(PrimitiveType.Cylinder, "Ground", w, c + Vector3.down * 0.026f, new Vector3(42f, 0.05f, 42f), grass);
            ground.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 1f); // things you add can land on it
            Prim(PrimitiveType.Cylinder, "Lake", w, c + new Vector3(-7f, -0.02f, 9f), new Vector3(9f, 0.03f, 6f), water);

            // Soft hills at the edge, mountains far away
            for (int i = 0; i < 14; i++)
            {
                float a = i / 14f * Mathf.PI * 2f + R(-0.1f, 0.1f);
                float d = R(14f, 19f);
                Prim(PrimitiveType.Sphere, "Hill", w, c + new Vector3(Mathf.Cos(a) * d, -R(1.5f, 2.5f), Mathf.Sin(a) * d),
                     new Vector3(R(7f, 11f), R(4f, 6f), R(7f, 11f)), i % 3 == 0 ? leaf2 : grass);
            }
            for (int i = 0; i < 9; i++)
            {
                float a = i / 9f * Mathf.PI * 2f + 0.3f;
                float d = R(120f, 170f);
                var m = Prim(PrimitiveType.Capsule, "Mountain", w, c + new Vector3(Mathf.Cos(a) * d, R(-10f, 0f), Mathf.Sin(a) * d),
                             new Vector3(R(40f, 70f), R(30f, 55f), R(40f, 70f)), mountain);
                m.transform.rotation = Quaternion.Euler(0f, R(0f, 360f), 0f);
            }

            // Trees: a ring of them, not too close
            for (int i = 0; i < 22; i++)
            {
                float a = R(0f, Mathf.PI * 2f);
                float d = R(5.5f, 12f);
                Vector3 p = c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(c.x - 7f, c.z + 9f)) < 6f) continue; // not in the lake
                float hgt = R(1.6f, 3.2f);
                Prim(PrimitiveType.Cylinder, "Trunk", w, p + Vector3.up * hgt * 0.5f, new Vector3(0.16f, hgt * 0.5f, 0.16f), bark);
                var crown = i % 3 == 0 ? leaf2 : leaf;
                Prim(PrimitiveType.Sphere, "Crown", w, p + Vector3.up * (hgt + 0.3f), Vector3.one * R(1.4f, 2.1f), crown);
                Prim(PrimitiveType.Sphere, "Crown", w, p + new Vector3(R(-0.5f, 0.5f), hgt - 0.1f, R(-0.5f, 0.5f)), Vector3.one * R(1f, 1.4f), crown);
            }

            // Rocks and glowing crystals
            for (int i = 0; i < 16; i++)
            {
                float a = R(0f, Mathf.PI * 2f), d = R(3f, 13f);
                Vector3 p = c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                var r = Prim(PrimitiveType.Sphere, "Rock", w, p, new Vector3(R(0.4f, 1.1f), R(0.25f, 0.6f), R(0.4f, 1f)), rock);
                r.transform.rotation = Quaternion.Euler(R(-10, 10), R(0, 360), R(-10, 10));
                if (i % 2 == 0)
                    for (int k = 0; k < 3; k++)
                    {
                        var cr = Prim(PrimitiveType.Capsule, "Crystal", w, p + new Vector3(R(-0.3f, 0.3f), R(0.2f, 0.4f), R(-0.3f, 0.3f)),
                                      new Vector3(0.1f, R(0.25f, 0.45f), 0.1f), crystal);
                        cr.transform.rotation = Quaternion.Euler(R(-25, 25), R(0, 360), R(-25, 25));
                    }
            }

            // Floating lanterns that drift slowly
            var lanterns = new GameObject("Lanterns").transform;
            lanterns.SetParent(w, false);
            for (int i = 0; i < 18; i++)
            {
                float a = R(0f, Mathf.PI * 2f), d = R(3f, 16f);
                Prim(PrimitiveType.Sphere, "Lantern", lanterns, c + new Vector3(Mathf.Cos(a) * d, R(2.5f, 7f), Mathf.Sin(a) * d), Vector3.one * R(0.15f, 0.3f), lantern);
            }
            lanterns.gameObject.AddComponent<Drift>();

            // Warm evening light
            var lightGO = new GameObject("World Sun");
            lightGO.transform.SetParent(w, false);
            var sun = lightGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.78f, 0.7f);
            sun.intensity = 0.9f;
            sun.shadows = LightShadows.None;
            lightGO.transform.rotation = Quaternion.LookRotation(new Vector3(-0.3f, -0.4f, -0.9f));
        }

        Material Lit(Color col, float smooth)
        {
            var m = litTemplate != null ? new Material(litTemplate) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            return m;
        }

        Material Glow(Color col, float k)
        {
            var m = glowTemplate != null ? new Material(glowTemplate) : Lit(col, 0.5f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col * 0.3f);
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", col * k); }
            return m;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            if (mat) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        /// <summary>Slow bobbing for the lanterns.</summary>
        class Drift : MonoBehaviour
        {
            Vector3[] _base;
            void Start()
            {
                _base = new Vector3[transform.childCount];
                for (int i = 0; i < _base.Length; i++) _base[i] = transform.GetChild(i).position;
            }
            void Update()
            {
                float t = Time.time;
                for (int i = 0; i < _base.Length; i++)
                    transform.GetChild(i).position = _base[i] + new Vector3(Mathf.Sin(t * 0.2f + i) * 0.3f, Mathf.Sin(t * 0.45f + i * 1.7f) * 0.25f, Mathf.Cos(t * 0.17f + i) * 0.3f);
            }
        }
    }
}
