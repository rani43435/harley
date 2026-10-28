using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Things to do with Harley in your room (or in Harley's world). Pick a tool in the menu,
    /// then point with a hand or controller at empty space and pull the trigger / pinch:
    ///  Draw  - paint glowing lines in the air
    ///  Add   - drop things into the room: a ball, a box, a plant, a star, a gift (they fall onto your real table)
    ///  Break - smash the things you added, or crack your walls and furniture (just for fun, it's virtual)
    ///  Cover - throw a sheet over a piece of real furniture to hide it (point again to uncover)
    ///  Fetch - throw a ball and Harley flies after it and brings it back
    /// Harley notices and reacts.
    /// </summary>
    public class HarleyTools : MonoBehaviour
    {
        public enum Tool { None, Draw, Add, Break, Cover, Fetch }

        public static HarleyTools Instance { get; private set; }

        public CompanionBrain brain;
        public Material glowTemplate, litTemplate;
        public Tool Current { get; private set; } = Tool.None;
        public string AddItemName => Items[_item];

        static readonly string[] Items = { "Ball", "Box", "Plant", "Star", "Gift" };
        static readonly Color[] Inks =
        {
            new Color(0.62f, 0.45f, 1f), new Color(1f, 0.45f, 0.75f), new Color(0.35f, 0.85f, 1f),
            new Color(1f, 0.85f, 0.35f), new Color(1f, 1f, 1f)
        };

        int _item, _ink;
        readonly bool[] _was = new bool[2];
        LineRenderer _stroke;
        readonly List<GameObject> _drawings = new List<GameObject>();
        readonly List<GameObject> _added = new List<GameObject>();
        readonly Dictionary<CompanionRoom.Thing, GameObject> _covers = new Dictionary<CompanionRoom.Thing, GameObject>();
        Material _ink0, _sheet, _crack;
        float _strokeStart;
        int _strokes;

        // fetch
        enum Fetch { Idle, Holding, Flying, Going, Carrying }
        Fetch _fetch;
        Rigidbody _ball;
        int _ballHand = 1;
        readonly Queue<(float t, Vector3 p)> _handTrail = new Queue<(float, Vector3)>();
        float _fetchTimer;

        void Awake() { Instance = this; }

        void Start()
        {
            if (brain == null) brain = FindAnyObjectByType<CompanionBrain>();
            _ink0 = glowTemplate != null ? new Material(glowTemplate) : new Material(Shader.Find("Sprites/Default"));
        }

        public void SetTool(Tool t)
        {
            if (Current == Tool.Fetch && t != Tool.Fetch) EndFetch();
            Current = Current == t ? Tool.None : t; // pressing the active tool again turns it off
            HarleyPointer.ShowFreeRay = Current != Tool.None;
            if (Current == Tool.Fetch) StartFetch();
        }

        public void NextItem() => _item = (_item + 1) % Items.Length;
        public void NextInk() => _ink = (_ink + 1) % Inks.Length;
        public Color InkColor => Inks[_ink];

        public void ClearAll()
        {
            foreach (var g in _drawings) if (g) Destroy(g);
            foreach (var g in _added) if (g) Destroy(g);
            foreach (var kv in _covers) if (kv.Value) Destroy(kv.Value);
            _drawings.Clear(); _added.Clear(); _covers.Clear();
            EndFetch();
        }

        void Update()
        {
            if (Current == Tool.Fetch) { UpdateFetch(); return; }
            if (Current == Tool.None) return;

            // Right hand first, else left
            int id = HarleyPointer.Free[1].valid ? 1 : HarleyPointer.Free[0].valid ? 0 : -1;
            if (id < 0) { EndStroke(); _was[0] = _was[1] = false; return; }
            var ray = HarleyPointer.Free[id];
            bool down = ray.pressed && !_was[id];
            bool up = !ray.pressed && _was[id];
            _was[id] = ray.pressed;
            _was[1 - id] = HarleyPointer.Free[1 - id].pressed;

            switch (Current)
            {
                case Tool.Draw:
                    if (ray.pressed) Draw(ray);
                    if (up) EndStroke();
                    break;
                case Tool.Add:   if (down) Add(ray); break;
                case Tool.Break: if (down) Break(ray); break;
                case Tool.Cover: if (down) Cover(ray); break;
            }
        }

        // ---------- draw ----------

        void Draw(HarleyPointer.FreeRay ray)
        {
            Vector3 tip = ray.origin + ray.direction * (ray.isHand ? 0.12f : 0.18f);
            if (_stroke == null)
            {
                var go = new GameObject("Drawing");
                _stroke = go.AddComponent<LineRenderer>();
                _stroke.sharedMaterial = Ink(Inks[_ink]);
                _stroke.useWorldSpace = true;
                _stroke.widthMultiplier = 0.008f;
                _stroke.numCapVertices = 6;
                _stroke.numCornerVertices = 4;
                _stroke.positionCount = 0;
                _stroke.startColor = _stroke.endColor = Inks[_ink];
                _stroke.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _drawings.Add(go);
                _strokeStart = Time.time;
            }
            int n = _stroke.positionCount;
            if (n == 0 || Vector3.Distance(_stroke.GetPosition(n - 1), tip) > 0.004f)
            {
                _stroke.positionCount = n + 1;
                _stroke.SetPosition(n, tip);
            }
        }

        void EndStroke()
        {
            if (_stroke == null) return;
            if (_stroke.positionCount > 20 && Time.time - _strokeStart > 0.8f && ++_strokes % 3 == 1)
                brain?.React("The player is drawing in the air with glowing light. React to what they drew if you can guess it, playfully.", Emotion.Curious);
            _stroke = null;
        }

        Material Ink(Color c)
        {
            var m = new Material(_ink0);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c * 0.4f);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 1.8f); }
            return m;
        }

        // ---------- add ----------

        void Add(HarleyPointer.FreeRay ray)
        {
            Vector3 spot;
            if (Physics.Raycast(ray.origin, ray.direction, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                spot = hit.point + hit.normal * 0.15f;
            else
                spot = ray.origin + ray.direction * 0.7f;

            GameObject go = MakeItem(Items[_item], spot);
            _added.Add(go);
            if (_added.Count > 40) { if (_added[0]) Destroy(_added[0]); _added.RemoveAt(0); }
            brain?.sounds?.Play(Sfx.Pop, Random.Range(0.9f, 1.2f), 0.8f);
            if (_added.Count % 4 == 1)
                brain?.React($"The player just added a virtual {Items[_item].ToLowerInvariant()} to the room. Say something short and fun about it.", Emotion.Excited);
        }

        GameObject MakeItem(string kind, Vector3 at)
        {
            var root = new GameObject("Added " + kind);
            root.transform.position = at;
            var rb = root.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            root.AddComponent<Breakable>();

            switch (kind)
            {
                case "Ball":
                    Shape(PrimitiveType.Sphere, root.transform, Vector3.zero, Vector3.one * 0.12f, Lit(new Color(1f, 0.45f, 0.6f)));
                    var sc = root.AddComponent<SphereCollider>(); sc.radius = 0.06f;
                    sc.material = new PhysicsMaterial { bounciness = 0.75f, bounceCombine = PhysicsMaterialCombine.Maximum, dynamicFriction = 0.3f };
                    rb.mass = 0.2f;
                    break;
                case "Box":
                    Shape(PrimitiveType.Cube, root.transform, Vector3.zero, Vector3.one * 0.18f, Lit(new Color(0.85f, 0.68f, 0.48f)));
                    Shape(PrimitiveType.Cube, root.transform, Vector3.zero, new Vector3(0.185f, 0.03f, 0.185f), Lit(new Color(0.55f, 0.4f, 0.28f)));
                    root.AddComponent<BoxCollider>().size = Vector3.one * 0.18f;
                    rb.mass = 1f;
                    break;
                case "Plant":
                    Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0, -0.05f, 0), new Vector3(0.12f, 0.06f, 0.12f), Lit(new Color(0.8f, 0.5f, 0.38f)));
                    Shape(PrimitiveType.Sphere, root.transform, new Vector3(0, 0.07f, 0), new Vector3(0.16f, 0.14f, 0.16f), Lit(new Color(0.36f, 0.66f, 0.45f)));
                    Shape(PrimitiveType.Sphere, root.transform, new Vector3(0.04f, 0.13f, 0.02f), Vector3.one * 0.07f, Glow(new Color(1f, 0.6f, 0.85f), 1.2f));
                    var cap = root.AddComponent<CapsuleCollider>(); cap.radius = 0.07f; cap.height = 0.26f; cap.center = new Vector3(0, 0.02f, 0);
                    rb.mass = 0.8f;
                    break;
                case "Star":
                    for (int i = 0; i < 3; i++)
                        Shape(PrimitiveType.Cube, root.transform, Vector3.zero, new Vector3(0.05f, 0.18f, 0.05f), Glow(new Color(1f, 0.85f, 0.35f), 2f))
                            .transform.localRotation = Quaternion.Euler(0, 0, i * 60f);
                    root.AddComponent<SphereCollider>().radius = 0.08f;
                    rb.useGravity = false; // stars float
                    rb.linearDamping = 2f;
                    rb.angularVelocity = new Vector3(0, 1.5f, 0);
                    rb.angularDamping = 0f;
                    break;
                default: // Gift
                    Shape(PrimitiveType.Cube, root.transform, Vector3.zero, Vector3.one * 0.16f, Lit(new Color(0.55f, 0.4f, 0.95f)));
                    Shape(PrimitiveType.Cube, root.transform, Vector3.zero, new Vector3(0.165f, 0.165f, 0.035f), Lit(new Color(1f, 0.85f, 0.4f)));
                    Shape(PrimitiveType.Cube, root.transform, Vector3.zero, new Vector3(0.035f, 0.165f, 0.165f), Lit(new Color(1f, 0.85f, 0.4f)));
                    root.AddComponent<BoxCollider>().size = Vector3.one * 0.16f;
                    rb.mass = 0.6f;
                    break;
            }
            // Pop in
            root.transform.localScale = Vector3.one * 0.01f;
            root.AddComponent<PopIn>();
            return root;
        }

        // ---------- break ----------

        void Break(HarleyPointer.FreeRay ray)
        {
            if (!Physics.Raycast(ray.origin, ray.direction, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)) return;

            var b = hit.collider.GetComponentInParent<Breakable>();
            if (b != null)
            {
                Shatter(b.gameObject, hit.point);
                _added.Remove(b.gameObject);
                brain?.React("The player just smashed one of the virtual things in the room into pieces.", Emotion.Surprised);
                return;
            }

            // A real wall / table / floor: a crack and a puff of debris (only virtual!)
            Crack(hit.point, hit.normal);
            if (Random.value < 0.4f) brain?.React("The player is 'breaking' the real walls and furniture with your virtual crack effect. Tease them a bit.", Emotion.Surprised);
        }

        void Shatter(GameObject go, Vector3 at)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            Material mat = renderers.Length > 0 ? renderers[0].sharedMaterial : Lit(Color.white);
            Bounds bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position, Vector3.one * 0.15f);
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            Destroy(go);
            Debris(bounds.center, bounds.size.magnitude * 0.18f, 14, mat, 3f);
            brain?.sounds?.Play(Sfx.Thud, Random.Range(1.2f, 1.6f), 1f);
        }

        void Crack(Vector3 at, Vector3 normal)
        {
            if (_crack == null) _crack = CrackMaterial();
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "Crack";
            q.transform.position = at + normal * 0.004f;
            q.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            q.transform.localScale = Vector3.one * Random.Range(0.3f, 0.55f);
            q.GetComponent<Renderer>().sharedMaterial = _crack;
            _drawings.Add(q);
            Debris(at + normal * 0.03f, 0.03f, 10, Lit(new Color(0.75f, 0.73f, 0.72f)), 2.5f, normal);
            brain?.sounds?.Play(Sfx.Thud, Random.Range(0.7f, 0.9f), 1f);
        }

        void Debris(Vector3 center, float size, int count, Material mat, float life, Vector3? push = null)
        {
            for (int i = 0; i < count; i++)
            {
                var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                piece.name = "Piece";
                piece.transform.position = center + Random.insideUnitSphere * size;
                piece.transform.rotation = Random.rotation;
                piece.transform.localScale = Vector3.one * Random.Range(0.25f, 0.6f) * Mathf.Max(0.02f, size);
                piece.GetComponent<Renderer>().sharedMaterial = mat;
                var rb = piece.AddComponent<Rigidbody>();
                rb.mass = 0.05f;
                Vector3 dir = (Random.insideUnitSphere + (push ?? Vector3.up) * 0.8f).normalized;
                rb.linearVelocity = dir * Random.Range(1f, 2.6f);
                rb.angularVelocity = Random.insideUnitSphere * 12f;
                Destroy(piece, life + Random.value);
            }
        }

        // ---------- cover (hide real furniture) ----------

        void Cover(HarleyPointer.FreeRay ray)
        {
            var room = brain != null ? brain.room : null;
            if (room == null || !Physics.Raycast(ray.origin, ray.direction, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)) return;

            CompanionRoom.Thing best = null;
            float bestD = 1.2f;
            foreach (var t in room.Things)
            {
                if (!t.isVolume) continue;
                float d = Vector3.Distance(hit.point, t.center) - t.size.magnitude * 0.5f;
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best == null) { brain?.voice?.ShowStatus("(point at a piece of furniture)", 2f); return; }

            if (_covers.TryGetValue(best, out GameObject old))
            {
                if (old) Destroy(old);
                _covers.Remove(best);
                brain?.sounds?.Play(Sfx.ChirpUp, 1f, 0.8f);
                return;
            }

            if (_sheet == null) _sheet = Lit(new Color(0.78f, 0.72f, 0.95f));
            var go = new GameObject("Cover " + best.name);
            Vector3 size = best.size + Vector3.one * 0.06f;
            Vector3 center = best.center;
            var body = Shape(PrimitiveType.Cube, go.transform, Vector3.zero, size, _sheet);
            body.transform.position = center;
            body.transform.rotation = best.anchor != null ? Quaternion.Euler(0, best.anchor.eulerAngles.y, 0) : Quaternion.identity;
            // soft rounded top, like a sheet thrown over it
            var top = Shape(PrimitiveType.Sphere, go.transform, Vector3.zero, new Vector3(size.x * 1.02f, Mathf.Min(size.x, size.z) * 0.35f, size.z * 1.02f), _sheet);
            top.transform.position = center + Vector3.up * size.y * 0.5f;
            top.transform.rotation = body.transform.rotation;
            go.AddComponent<PopIn>();
            _covers[best] = go;
            brain?.sounds?.Play(Sfx.Whee, 1f, 0.8f);
            brain?.React($"The player just covered their real {best.kind} with a magic sheet so it's hidden.", Emotion.Happy);
        }

        // ---------- fetch ----------

        void StartFetch()
        {
            _fetch = Fetch.Holding;
            if (_ball == null)
            {
                var go = MakeItem("Ball", Vector3.zero);
                go.name = "Fetch Ball";
                _ball = go.GetComponent<Rigidbody>();
            }
            _ball.isKinematic = true;
            brain?.React("The player wants to play fetch with a ball: they will throw it and you fly to get it. Get excited!", Emotion.Excited);
        }

        void EndFetch()
        {
            _fetch = Fetch.Idle;
            if (_ball) Destroy(_ball.gameObject);
            _ball = null;
        }

        void UpdateFetch()
        {
            if (_ball == null) { StartFetch(); return; }
            var mover = brain != null ? brain.movement as CompanionMovement : null;

            switch (_fetch)
            {
                case Fetch.Holding:
                {
                    // Ball sits in front of the hand; pull and hold the trigger / pinch, release to throw.
                    int id = HarleyPointer.Free[_ballHand].valid ? _ballHand : 1 - _ballHand;
                    var ray = HarleyPointer.Free[id];
                    if (!ray.valid) return;
                    _ballHand = id;
                    Vector3 p = ray.origin + ray.direction * 0.1f;
                    _ball.transform.position = p;
                    _handTrail.Enqueue((Time.time, p));
                    while (_handTrail.Count > 0 && Time.time - _handTrail.Peek().t > 0.1f) _handTrail.Dequeue();

                    if (ray.pressed) _was[id] = true;
                    else if (_was[id])
                    {
                        _was[id] = false;
                        Vector3 v = Vector3.zero;
                        if (_handTrail.Count >= 2) { var first = _handTrail.Peek(); float span = Time.time - first.t; if (span > 0.01f) v = (p - first.p) / span; }
                        if (v.magnitude < 0.8f) v = ray.direction * 3f; // a gentle toss if the hand didn't move
                        _ball.isKinematic = false;
                        _ball.linearVelocity = v * 1.3f;
                        _fetch = Fetch.Flying;
                        _fetchTimer = Time.time;
                        brain?.sounds?.Play(Sfx.Whee, 1.2f, 0.8f);
                    }
                    break;
                }
                case Fetch.Flying:
                    if (_ball.linearVelocity.magnitude < 0.25f || Time.time - _fetchTimer > 2.5f)
                    {
                        _fetch = Fetch.Going;
                        _fetchTimer = Time.time;
                        brain?.visual?.FlashEmotion(Emotion.Excited, 3f);
                        brain?.sounds?.Play(Sfx.ChirpUp, 1.2f, 0.8f);
                    }
                    break;
                case Fetch.Going:
                    if (mover == null) { _fetch = Fetch.Holding; _ball.isKinematic = true; break; }
                    mover.FlyTo(_ball.position + Vector3.up * 0.05f, 2f, _ball.position);
                    if (Vector3.Distance(brain.transform.position, _ball.position) < 0.32f || Time.time - _fetchTimer > 8f)
                    {
                        _fetch = Fetch.Carrying;
                        _fetchTimer = Time.time;
                        _ball.isKinematic = true;
                        brain?.sounds?.Play(Sfx.Pop, 1.3f, 0.8f);
                    }
                    break;
                case Fetch.Carrying:
                {
                    if (mover == null || mover.head == null) break;
                    Transform hand = brain.visual != null ? brain.visual.handR : brain.transform;
                    _ball.transform.position = hand.position + hand.forward * 0.03f;
                    Vector3 f = mover.head.forward; f.y = 0; f = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
                    Vector3 drop = mover.head.position + f * 0.45f + Vector3.down * 0.25f;
                    mover.FlyTo(drop, 2f, mover.head.position);
                    if (Vector3.Distance(brain.transform.position, drop) < 0.12f || Time.time - _fetchTimer > 8f)
                    {
                        _fetch = Fetch.Holding; // back in your hand: throw again
                        brain?.visual?.Perform(CompanionAction.Spin);
                        brain?.React("You just brought the ball back to the player in a game of fetch. Cheer, ask them to throw again.", Emotion.Happy);
                    }
                    break;
                }
            }
        }

        // ---------- helpers ----------

        Material Lit(Color c)
        {
            var m = litTemplate != null ? new Material(litTemplate) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            return m;
        }

        Material Glow(Color c, float k)
        {
            var m = glowTemplate != null ? new Material(glowTemplate) : Lit(c);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c * 0.4f);
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * k); }
            return m;
        }

        static GameObject Shape(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>A crack drawn into a texture once: dark jagged lines from the centre, transparent elsewhere.</summary>
        static Material CrackMaterial()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[S * S];
            var rnd = new System.Random(3);
            void Dot(int x, int y, byte a)
            {
                if (x < 0 || y < 0 || x >= S || y >= S) return;
                int i = y * S + x;
                if (px[i].a < a) px[i] = new Color32(25, 22, 30, a);
            }
            void Branch(float x, float y, float ang, float len, int depth)
            {
                for (int step = 0; step < len; step++)
                {
                    ang += (float)(rnd.NextDouble() - 0.5) * 0.5f;
                    x += Mathf.Cos(ang); y += Mathf.Sin(ang);
                    byte a = (byte)(230 - step * 120 / Mathf.Max(1, (int)len));
                    Dot((int)x, (int)y, a); Dot((int)x + 1, (int)y, (byte)(a / 2)); Dot((int)x, (int)y + 1, (byte)(a / 2));
                    if (depth < 3 && rnd.NextDouble() < 0.04) Branch(x, y, ang + (float)(rnd.NextDouble() - 0.5) * 1.8f, len * 0.5f, depth + 1);
                }
            }
            for (int i = 0; i < 7; i++) Branch(S / 2f, S / 2f, i / 7f * Mathf.PI * 2f, 100f + (float)rnd.NextDouble() * 20f, 0);
            tex.SetPixels32(px);
            tex.Apply(true, true);
            var m = new Material(Shader.Find("Sprites/Default"));
            m.mainTexture = tex;
            return m;
        }

        /// <summary>Marks things the player added: they can be smashed.</summary>
        public class Breakable : MonoBehaviour { }

        /// <summary>Elastic grow-in when something appears.</summary>
        class PopIn : MonoBehaviour
        {
            float _t;
            void Update()
            {
                _t += Time.deltaTime / 0.45f;
                float k = Mathf.Clamp01(_t);
                float s = k >= 1f ? 1f : 1f - Mathf.Cos(k * Mathf.PI * 3f) * Mathf.Exp(-6f * k);
                transform.localScale = Vector3.one * Mathf.Max(0.01f, s);
                if (_t >= 1f) Destroy(this);
            }
        }
    }
}
