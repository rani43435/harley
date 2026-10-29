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
        /// <summary>Room = mixed reality. Blend = your real room (floor, furniture) with Harley's world all around,
        /// through the walls and ceiling, like a home with open views. World = fully immersive.</summary>
        public enum Mode { Room = 0, Blend = 1, World = 2 }

        public static HarleyWorld Instance { get; private set; }
        public static bool Immersive => Instance != null && Instance.Current != Mode.Room; // a 360 view or Harley's world: the real room is hidden
        public static bool InHarleysWorld => Instance != null && Instance.Current == Mode.World;
        public static bool Blended => Instance != null && Instance.Current == Mode.Blend;
        public Mode Current { get; private set; } = Mode.Room;

        [Tooltip("Lit material to copy for the world (the builder uses Harley's white).")]
        public Material litTemplate;
        [Tooltip("Glowing material to copy (the builder uses Harley's purple).")]
        public Material glowTemplate;
        public Transform head;
        public CompanionRoom room;

        const string Pref = "harley.world";
        GameObject _world, _windows;
        Camera _cam;
        CameraClearFlags _clearFlags;
        Color _clearColor;
        readonly List<MonoBehaviour> _passthrough = new List<MonoBehaviour>();
        float _floorY;
        float _nextWindowsRefresh;

        void Awake() { Instance = this; }

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (room == null) room = FindAnyObjectByType<CompanionRoom>();
            int saved = PlayerPrefs.GetInt(Pref, 0);
            if (saved != 0) StartCoroutine(Later((Mode)saved));
            SuppressBoundary(true); // walk around the whole room: no boundary while you can see it
        }

        /// <summary>
        /// Turns the Guardian boundary off while passthrough shows your real room (Quest allows that only with passthrough on),
        /// and back on in the full virtual world, where you can't see where you're walking.
        /// </summary>
        static void SuppressBoundary(bool suppress)
        {
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                if (mb == null || mb.GetType().Name != "OVRManager") continue;
                var f = mb.GetType().GetField("shouldBoundaryVisibilityBeSuppressed");
                if (f != null) f.SetValue(mb, suppress);
                return;
            }
        }

        System.Collections.IEnumerator Later(Mode m) { yield return new WaitForSeconds(1.5f); SetMode(m); }

        /// <summary>Room -> Room + world -> World -> Room.</summary>
        // ---------- 360 views for "Room + world" (Resources/Views, Poly Haven, CC0) ----------

        static readonly (string file, string name)[] ViewNames =
        {
            ("umhlanga_sunrise", "Beach"), ("lilienstein", "Meadow"), ("immenstadter_horn", "Alps"),
            ("misty_pines", "Forest"), ("shanghai_bund", "City night"),
        };
        readonly List<(Texture2D tex, string name)> _views = new List<(Texture2D, string)>();
        bool _viewsLoaded;
        public int ViewIndex { get; private set; }
        GameObject _pano;
        Material _panoMat;

        void LoadViews()
        {
            if (_viewsLoaded) return;
            _viewsLoaded = true;
            foreach (var (file, name) in ViewNames)
            {
                var tex = Resources.Load<Texture2D>("Views/" + file);
                if (tex != null) _views.Add((tex, name));
            }
            ViewIndex = Mathf.Clamp(PlayerPrefs.GetInt(Pref + ".view", 0), 0, Mathf.Max(0, _views.Count - 1));
        }

        /// <summary>The name shown on the menu button.</summary>
        public string CurrentLabel
        {
            get
            {
                if (Current != Mode.Blend) return Label(Current);
                LoadViews();
                return _views.Count > 0 ? _views[ViewIndex].name : "360 view";
            }
        }

        /// <summary>My room -> each 360 view (fully virtual) -> Harley's world -> My room.</summary>
        public void Toggle()
        {
            LoadViews();
            if (Current == Mode.Room) { ViewIndex = 0; SetMode(Mode.Blend); }
            else if (Current == Mode.Blend && ViewIndex + 1 < _views.Count) { ViewIndex++; ShowView(); }
            else SetMode((Mode)(((int)Current + 1) % 3));
            PlayerPrefs.SetInt(Pref + ".view", ViewIndex);
        }

        /// <summary>Shows a 360 view through the walls (blend). Without views, the 3D world is used instead.</summary>
        void ShowView()
        {
            LoadViews();
            bool on = Current == Mode.Blend && _views.Count > 0;
            if (!on) { if (_pano) _pano.SetActive(false); return; }
            if (_pano == null)
            {
                var shader = Shader.Find("Harley/Panorama");
                if (shader == null) return;
                _panoMat = new Material(shader);
                _pano = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _pano.name = "360 View";
                Destroy(_pano.GetComponent<Collider>());
                _pano.transform.localScale = Vector3.one * 400f;
                var r = _pano.GetComponent<Renderer>();
                r.sharedMaterial = _panoMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            _panoMat.SetTexture("_MainTex", _views[ViewIndex].tex);
            if (_panoMat.HasProperty("_StencilComp")) _panoMat.SetFloat("_StencilComp", 8f); // everywhere: fully virtual
            _panoMat.SetFloat("_Exposure", _views[ViewIndex].name == "City night" ? 1.1f : 1f);
            _pano.SetActive(true);
        }

        void LateUpdate()
        {
            // The view is "infinitely far": it moves with your head, only turning stays put
            if (_pano != null && _pano.activeSelf && head != null) _pano.transform.position = head.position;
        }

        public void SetImmersive(bool on) => SetMode(on ? Mode.World : Mode.Room);

        public static string Label(Mode m) => m == Mode.Room ? "My room" : m == Mode.Blend ? "360 view" : "Harley's world";

        public void SetMode(Mode m)
        {
            if (m == Current) return;
            Current = m;
            PlayerPrefs.SetInt(Pref, (int)m);
            PlayerPrefs.Save();

            if (_cam == null) { _cam = Camera.main; if (_cam) { _clearFlags = _cam.clearFlags; _clearColor = _cam.backgroundColor; } }

            // Passthrough only in your room; the 360 views and Harley's world are fully virtual (saves battery too)
            if (_passthrough.Count == 0)
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                    if (mb != null && mb.GetType().Name == "OVRPassthroughLayer") _passthrough.Add(mb);
            foreach (var pt in _passthrough) if (pt) pt.enabled = m == Mode.Room;
            SuppressBoundary(m == Mode.Room); // in virtual views the boundary keeps you safe

            LoadViews();
            bool panoBlend = m == Mode.Blend && _views.Count > 0;
            if ((m == Mode.World || (m == Mode.Blend && !panoBlend)) && _world == null) Build();
            if (_world) _world.SetActive(m == Mode.World || (m == Mode.Blend && !panoBlend));
            ShowView();
            if (_cam)
            {
                if (m != Mode.Room) { _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = new Color(0.08f, 0.06f, 0.2f, 1f); }
                else { _cam.clearFlags = _clearFlags; _cam.backgroundColor = _clearColor; }
            }
            if (m != Mode.Room)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.42f, 0.36f, 0.55f);
            }
            BuildWindows();
        }

        void Update()
        {
            // The room scan can arrive late or change: keep the wall portals in step with it
            if (false && Current == Mode.Blend && Time.unscaledTime >= _nextWindowsRefresh)
            {
                _nextWindowsRefresh = Time.unscaledTime + 3f;
                int walls = room != null ? room.Walls.Count : 0;
                if (_windows == null || walls != _portalWalls) BuildWindows();
            }
        }

        int _portalWalls = -1;
        readonly List<Material> _worldMats = new List<Material>();
        Material _skyMat;
        readonly List<GameObject> _insideRoom = new List<GameObject>();

        /// <summary>
        /// Blend ("Room + world"): your walls turn into views onto Harley's world. Each wall gets an invisible
        /// portal that marks its pixels, and the world is drawn only there. The floor, furniture, ceiling and
        /// everything else stay your real room.
        /// </summary>
        void BuildWindows()
        {
            if (_windows) Destroy(_windows);
            _windows = null;
            bool blend = false; // the wall-portal blend is retired: views are fully virtual now
            ApplyStencil(blend);
            if (!blend) { ShowInsideRoom(true); return; }

            var shader = Shader.Find("Harley/Wall Portal");
            if (shader == null) return;
            var mat = new Material(shader);
            _windows = new GameObject("Wall Portals");
            _portalWalls = room != null ? room.Walls.Count : 0;

            Vector3 roomMin = Vector3.positiveInfinity, roomMax = Vector3.negativeInfinity;
            if (room != null && room.Walls.Count > 0)
            {
                foreach (var (anchor, r) in room.Walls)
                {
                    if (anchor == null) continue;
                    var q = Quad(_windows.transform, mat);
                    q.name = "Wall Portal";
                    q.transform.SetPositionAndRotation(anchor.TransformPoint(new Vector3(r.center.x, r.center.y, 0f)), anchor.rotation);
                    q.transform.localScale = new Vector3(r.width + 0.04f, r.height + 0.04f, 1f); // a hair bigger: no seams in corners
                    foreach (var corner in new[] { new Vector3(r.xMin, r.yMin, 0), new Vector3(r.xMax, r.yMax, 0) })
                    {
                        Vector3 w = anchor.TransformPoint(corner);
                        roomMin = Vector3.Min(roomMin, w); roomMax = Vector3.Max(roomMax, w);
                    }
                }
            }
            else
            {
                // No room scan: a round "wall" 2.5 m around you, 3 m high
                Vector3 c = head != null ? new Vector3(head.position.x, _floorY, head.position.z) : new Vector3(0, _floorY, 0);
                const int n = 24; const float rad = 2.5f, hgt = 3f;
                float seg = 2f * rad * Mathf.Tan(Mathf.PI / n) + 0.02f;
                for (int i = 0; i < n; i++)
                {
                    float a = (i + 0.5f) / n * Mathf.PI * 2f;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    var q = Quad(_windows.transform, mat);
                    q.transform.SetPositionAndRotation(c + dir * rad + Vector3.up * hgt * 0.5f, Quaternion.LookRotation(dir, Vector3.up));
                    q.transform.localScale = new Vector3(seg, hgt, 1f);
                }
                roomMin = c - new Vector3(rad, 0, rad); roomMax = c + new Vector3(rad, hgt, rad);
            }

            // Your real furniture stays in front of the views: invisible boxes that only write depth, drawn before the portals
            var occShader = Shader.Find("Harley/Passthrough Window");
            if (occShader != null && room != null)
            {
                var occ = new Material(occShader) { renderQueue = 1900 };
                foreach (var thing in room.Things)
                {
                    if (!thing.isVolume) continue;
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(box.GetComponent<Collider>());
                    box.name = "Keep real " + thing.name;
                    box.transform.SetParent(_windows.transform, false);
                    box.transform.position = thing.center;
                    box.transform.rotation = thing.anchor != null ? Quaternion.Euler(0f, thing.anchor.eulerAngles.y, 0f) : Quaternion.identity;
                    box.transform.localScale = thing.size + Vector3.one * 0.03f;
                    var br = box.GetComponent<Renderer>();
                    br.sharedMaterial = occ;
                    br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }

            // World things that would stand inside your room are hidden (they'd look painted onto the walls)
            _insideRoom.Clear();
            if (_world != null && roomMin.x < roomMax.x)
            {
                foreach (Transform child in _world.transform)
                {
                    if (child.name == "Sky" || child.name == "Ocean" || child.name == "Beach" || child.name == "Ground") continue;
                    Vector3 p = child.position;
                    if (p.x > roomMin.x - 0.6f && p.x < roomMax.x + 0.6f && p.z > roomMin.z - 0.6f && p.z < roomMax.z + 0.6f)
                        _insideRoom.Add(child.gameObject);
                }
                var lanterns = _world.transform.Find("Lanterns");
                if (lanterns != null)
                    foreach (Transform l in lanterns)
                    {
                        Vector3 p = l.position;
                        if (p.x > roomMin.x - 0.6f && p.x < roomMax.x + 0.6f && p.z > roomMin.z - 0.6f && p.z < roomMax.z + 0.6f)
                            _insideRoom.Add(l.gameObject);
                    }
            }
            ShowInsideRoom(false);
        }

        void ShowInsideRoom(bool show)
        {
            foreach (var go in _insideRoom) if (go) go.SetActive(show);
        }

        /// <summary>World drawn everywhere (full world) or only through the wall portals (blend).</summary>
        void ApplyStencil(bool blend)
        {
            float comp = blend ? 3f : 8f; // Equal : Always
            foreach (var m in _worldMats) if (m && m.HasProperty("_StencilComp")) m.SetFloat("_StencilComp", comp);
            if (_skyMat)
            {
                if (_skyMat.HasProperty("_StencilComp")) _skyMat.SetFloat("_StencilComp", comp);
                _skyMat.renderQueue = blend ? 2450 : 1000; // after the portals in the blend
            }
        }

        static GameObject Quad(Transform parent, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return q;
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
            if (skyShader != null) { _skyMat = new Material(skyShader); sky.GetComponent<Renderer>().sharedMaterial = _skyMat; }

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

            // Trees, bushes and grass: Quaternius' low-poly nature models (Resources/Nature, CC0) if present
            var trees = LoadNature("Tree");
            var bushes = LoadNature("Bush");
            var grasses = LoadNature("Grass");
            for (int i = 0; i < 26; i++)
            {
                float a = R(0f, Mathf.PI * 2f);
                float d = R(5.5f, 13f);
                Vector3 p = c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(c.x - 7f, c.z + 9f)) < 6f) continue; // not in the lake
                float hgt = R(1.6f, 3.2f);
                if (trees.Count > 0)
                {
                    Place(trees[rnd.Next(trees.Count)], w, p, R(3f, 5.5f), R(0f, 360f), i % 3 == 0 ? leaf2 : null);
                    continue;
                }
                Prim(PrimitiveType.Cylinder, "Trunk", w, p + Vector3.up * hgt * 0.5f, new Vector3(0.16f, hgt * 0.5f, 0.16f), bark);
                var crown = i % 3 == 0 ? leaf2 : leaf;
                Prim(PrimitiveType.Sphere, "Crown", w, p + Vector3.up * (hgt + 0.3f), Vector3.one * R(1.4f, 2.1f), crown);
                Prim(PrimitiveType.Sphere, "Crown", w, p + new Vector3(R(-0.5f, 0.5f), hgt - 0.1f, R(-0.5f, 0.5f)), Vector3.one * R(1f, 1.4f), crown);
            }
            for (int i = 0; bushes.Count > 0 && i < 30; i++)
            {
                float a = R(0f, Mathf.PI * 2f), d = R(3f, 14f);
                Place(bushes[rnd.Next(bushes.Count)], w, c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d), R(0.5f, 1.1f), R(0f, 360f), null);
            }
            for (int i = 0; grasses.Count > 0 && i < 70; i++)
            {
                float a = R(0f, Mathf.PI * 2f), d = R(2.2f, 15f);
                Place(grasses[rnd.Next(grasses.Count)], w, c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d), R(0.2f, 0.45f), R(0f, 360f), null);
            }

            // Rocks and glowing crystals
            var rocks = LoadNature("Rock");
            for (int i = 0; i < 16; i++)
            {
                float a = R(0f, Mathf.PI * 2f), d = R(3f, 13f);
                Vector3 p = c + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                if (rocks.Count > 0) Place(rocks[rnd.Next(rocks.Count)], w, p, R(0.35f, 0.9f), R(0f, 360f), null);
                else
                {
                    var r = Prim(PrimitiveType.Sphere, "Rock", w, p, new Vector3(R(0.4f, 1.1f), R(0.25f, 0.6f), R(0.4f, 1f)), rock);
                    r.transform.rotation = Quaternion.Euler(R(-10, 10), R(0, 360), R(-10, 10));
                }
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

        static List<GameObject> LoadNature(string prefix)
        {
            var list = new List<GameObject>();
            foreach (var go in Resources.LoadAll<GameObject>("Nature"))
                if (go.name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) list.Add(go);
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        readonly Dictionary<Material, Material> _natureMats = new Dictionary<Material, Material>();

        /// <summary>Puts a model on the ground at a given height (it's scaled to fit), in Harley's world colours and lighting.</summary>
        void Place(GameObject prefab, Transform parent, Vector3 groundPos, float height, float yaw, Material tint)
        {
            var go = Instantiate(prefab, parent);
            go.name = prefab.name;
            foreach (var col in go.GetComponentsInChildren<Collider>()) Destroy(col);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * go.transform.rotation;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) { go.transform.position = groundPos; return; }
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float s = height / Mathf.Max(0.01f, b.size.y);
            go.transform.localScale *= s;
            b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            go.transform.position += groundPos - new Vector3(b.center.x, b.min.y, b.center.z);

            // Same simple, stencil-aware lighting as the rest of the world
            var ws = Shader.Find("Harley/World");
            foreach (var r in rs)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (ws == null) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    if (!_natureMats.TryGetValue(src, out var m))
                    {
                        Color c = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : src.HasProperty("_Color") ? src.color : Color.white;
                        // Leaves lean to Harley's lilac a little, so the island keeps its dusk look
                        if (c.g > c.r && c.g > c.b) c = Color.Lerp(c, new Color(0.55f, 0.45f, 0.75f), 0.18f);
                        m = new Material(ws);
                        m.SetColor("_BaseColor", c);
                        m.SetFloat("_Shine", 0.15f);
                        _worldMats.Add(m);
                        _natureMats[src] = m;
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
        }

        Material Lit(Color col, float smooth)
        {
            var ws = Shader.Find("Harley/World");
            if (ws != null)
            {
                var hm = new Material(ws);
                hm.SetColor("_BaseColor", col);
                hm.SetFloat("_Shine", smooth);
                _worldMats.Add(hm);
                return hm;
            }
            var m = litTemplate != null ? new Material(litTemplate) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            return m;
        }

        Material Glow(Color col, float k)
        {
            var ws = Shader.Find("Harley/World");
            if (ws != null)
            {
                var hm = new Material(ws);
                hm.SetColor("_BaseColor", col * 0.3f);
                hm.SetColor("_EmissionColor", col * Mathf.Min(k, 1.2f));
                _worldMats.Add(hm);
                return hm;
            }
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
