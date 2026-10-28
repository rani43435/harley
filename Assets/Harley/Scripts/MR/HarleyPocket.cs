using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley's magic pocket (in its glowing chest). Ask for something - "give me a VR headset",
    /// "do you have headphones?" - and Harley pulls it out, flies over and holds it out to you.
    /// Pinch or grip to take it. Drop it near Harley to give it back, or ask Harley to put it away.
    /// Hold glasses / a headset / headphones up to your face and let go to "put them on".
    /// The items are Harley's own simple, unbranded models.
    /// </summary>
    public class HarleyPocket : MonoBehaviour
    {
        public CompanionBrain brain;
        public Material litTemplate, glowTemplate;
        [Tooltip("Where things come out of (the chest core).")]
        public Transform pocket;

        class Item
        {
            public string name;
            public string[] words;   // what people call it, in a few languages
            public bool wearable;
        }

        static readonly Item[] Catalog =
        {
            new Item { name = "VR headset", wearable = true, words = new[] { "vr", "headset", "quest", "meta quest", "oculus", "نظارة vr", "نظارة الواقع", "كويست" } },
            new Item { name = "smart glasses", wearable = true, words = new[] { "glasses", "sunglasses", "ray-ban", "rayban", "ray ban", "smart glasses", "نظارة", "نضارة", "brille" } },
            new Item { name = "headphones", wearable = true, words = new[] { "headphones", "headphone", "earphones", "headset audio", "airpods", "سماعات", "سماعة", "kopfhörer" } },
            new Item { name = "phone", words = new[] { "phone", "iphone", "mobile", "smartphone", "تلفون", "موبايل", "جوال", "handy" } },
            new Item { name = "bass guitar", words = new[] { "bass", "guitar", "باص", "جيتار", "غيتار", "gitarre" } },
            new Item { name = "camera", words = new[] { "camera", "كاميرا", "kamera" } },
            new Item { name = "cup of tea", words = new[] { "tea", "cup", "coffee", "mug", "شاي", "قهوة", "كاسة", "كوب", "tee", "kaffee" } },
            new Item { name = "book", words = new[] { "book", "كتاب", "buch" } },
            new Item { name = "flower", words = new[] { "flower", "rose", "وردة", "زهرة", "blume" } },
            new Item { name = "umbrella", words = new[] { "umbrella", "شمسية", "مظلة", "regenschirm" } },
            new Item { name = "ball", words = new[] { "ball", "طابة", "كورة", "كرة" } },
            new Item { name = "star", words = new[] { "star", "نجمة", "stern" } },
        };

        readonly List<GameObject> _out = new List<GameObject>();
        GameObject _offering;
        float _offerUntil;

        public string DescribeForPrompt()
        {
            var names = new List<string>();
            foreach (var i in Catalog) names.Add(i.name);
            return string.Join(", ", names);
        }

        void Start()
        {
            if (brain == null) brain = GetComponent<CompanionBrain>();
            if (pocket == null)
            {
                var body = GetComponentInChildren<HarleyBodyRig>();
                var rig = GetComponentInChildren<RobotRig>();
                pocket = body != null && body.core != null ? body.core.transform
                       : rig != null && rig.core != null ? rig.core.transform : transform;
            }
        }

        // ---------- give ----------

        public void Give(string what)
        {
            Item item = Find(what);
            if (item == null) { brain?.voice?.ShowStatus("(not in my pocket)", 2f); return; }
            if (_offering != null) PutAway(_offering);

            GameObject go = Build(item);
            go.transform.position = pocket.position;
            go.transform.localScale = Vector3.one * 0.01f;
            _out.Add(go);
            _offering = go;
            _offerUntil = Time.time + 15f;

            var g = go.GetComponent<Grabbable>();
            g.Grabbed += () => { if (_offering == go) EndOffer(); };
            g.Released += v => OnReleased(go, item);

            brain?.movement?.Perform(CompanionAction.ComeClose);
            brain?.sounds?.Play(Sfx.Pop, 1.1f, 0.9f);
            if (brain?.visual != null) brain.visual.Burst(14, brain.visual.lavender);
            StartCoroutine(Grow(go));
        }

        IEnumerator Grow(GameObject go)
        {
            for (float t = 0f; t < 1f && go != null; t += Time.deltaTime / 0.5f)
            {
                float k = 1f - Mathf.Cos(t * Mathf.PI * 2.5f) * Mathf.Exp(-5f * t);
                go.transform.localScale = Vector3.one * Mathf.Max(0.01f, k);
                yield return null;
            }
            if (go) go.transform.localScale = Vector3.one;
        }

        void Update()
        {
            if (_offering == null) return;
            var mover = brain != null ? brain.movement : null;
            Transform head = mover != null ? mover.head : null;
            if (head == null) return;

            // Hold it out toward you, a bit below the face, until you take it
            Vector3 toYou = head.position - transform.position;
            toYou.y = 0f;
            toYou = toYou.sqrMagnitude > 0.001f ? toYou.normalized : transform.forward;
            Vector3 hold = transform.position + toYou * 0.24f + Vector3.down * 0.06f;
            var g = _offering.GetComponent<Grabbable>();
            if (g != null && !g.IsHeld)
            {
                _offering.transform.position = Vector3.Lerp(_offering.transform.position, hold, 1f - Mathf.Exp(-8f * Time.deltaTime));
                _offering.transform.rotation = Quaternion.Slerp(_offering.transform.rotation, Quaternion.LookRotation(-toYou, Vector3.up), 1f - Mathf.Exp(-6f * Time.deltaTime));
            }
            if (brain.visual != null) brain.visual.reachTargetR = _offering.transform;

            if (Time.time > _offerUntil)
            {
                var put = _offering;
                EndOffer();
                PutAway(put);
            }
        }

        void EndOffer()
        {
            _offering = null;
            if (brain?.visual != null) brain.visual.reachTargetR = null;
        }

        void OnReleased(GameObject go, Item item)
        {
            if (go == null || brain == null) return;
            Transform head = brain.movement != null ? brain.movement.head : null;

            // Back to Harley: dropped right next to it
            if (Vector3.Distance(go.transform.position, transform.position) < 0.26f)
            {
                PutAway(go);
                brain.React($"The player gave the {item.name} back to you, you put it back in your pocket.", Emotion.Happy);
                return;
            }
            // "Put on": a wearable let go right at the face
            if (item.wearable && head != null && Vector3.Distance(go.transform.position, head.position) < 0.17f)
            {
                _out.Remove(go);
                Destroy(go);
                brain.voice?.ShowStatus("(wearing the " + item.name + ")", 2.5f);
                brain.React($"The player just put on the {item.name} you gave them. Compliment them on how it looks, playfully.", Emotion.Love);
            }
        }

        // ---------- take back ----------

        public void TakeBack(string what)
        {
            Item only = string.IsNullOrWhiteSpace(what) ? null : Find(what);
            foreach (var go in _out.ToArray())
            {
                if (go == null) continue;
                if (only != null && !go.name.EndsWith(only.name)) continue;
                PutAway(go);
            }
            brain?.movement?.Perform(CompanionAction.ComeClose);
        }

        void PutAway(GameObject go)
        {
            if (go == null) return;
            if (go == _offering) EndOffer();
            _out.Remove(go);
            var g = go.GetComponent<Grabbable>();
            if (g != null) g.enabled = false; // can't be grabbed while flying home
            var rb = go.GetComponent<Rigidbody>();
            if (rb) rb.isKinematic = true;
            StartCoroutine(FlyHome(go));
        }

        IEnumerator FlyHome(GameObject go)
        {
            Vector3 from = go.transform.position;
            Vector3 scale = go.transform.localScale;
            for (float t = 0f; t < 1f && go != null; t += Time.deltaTime / 0.6f)
            {
                float e = t * t * (3f - 2f * t);
                go.transform.position = Vector3.Lerp(from, pocket.position, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * 0.15f;
                go.transform.localScale = scale * (1f - e * 0.95f);
                yield return null;
            }
            if (go) Destroy(go);
            brain?.sounds?.Play(Sfx.Purr, 1.2f, 0.7f);
        }

        // ---------- finding ----------

        static Item Find(string what)
        {
            if (string.IsNullOrWhiteSpace(what)) return null;
            string w = what.Trim().ToLowerInvariant();
            foreach (var i in Catalog) if (i.name == w) return i;
            foreach (var i in Catalog)
                foreach (var word in i.words)
                    if (w.Contains(word) || word.Contains(w)) return i;
            return null;
        }

        // ---------- the items (simple, unbranded shapes) ----------

        GameObject Build(Item item)
        {
            var root = new GameObject("Pocket " + item.name);
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            root.AddComponent<Grabbable>().reach = 0.13f;
            root.AddComponent<HarleyTools.Breakable>();
            Transform t = root.transform;

            Material dark = Lit(new Color(0.16f, 0.16f, 0.19f)), white = Lit(new Color(0.93f, 0.93f, 0.95f));
            switch (item.name)
            {
                case "VR headset":
                    P(PrimitiveType.Cube, t, new Vector3(0, 0, 0), new Vector3(0.19f, 0.1f, 0.075f), white);
                    P(PrimitiveType.Cube, t, new Vector3(0, 0, -0.04f), new Vector3(0.18f, 0.085f, 0.012f), dark);         // face plate
                    P(PrimitiveType.Sphere, t, new Vector3(-0.06f, 0.02f, 0.039f), new Vector3(0.016f, 0.016f, 0.004f), dark); // front sensors
                    P(PrimitiveType.Sphere, t, new Vector3(0.06f, 0.02f, 0.039f), new Vector3(0.016f, 0.016f, 0.004f), dark);
                    P(PrimitiveType.Cube, t, new Vector3(-0.1f, 0.01f, -0.07f), new Vector3(0.012f, 0.025f, 0.12f), dark);  // strap
                    P(PrimitiveType.Cube, t, new Vector3(0.1f, 0.01f, -0.07f), new Vector3(0.012f, 0.025f, 0.12f), dark);
                    Box(root, new Vector3(0.19f, 0.1f, 0.09f), 0.5f);
                    break;
                case "smart glasses":
                    foreach (float x in new[] { -0.034f, 0.034f })
                    {
                        P(PrimitiveType.Cylinder, t, new Vector3(x, 0, 0), new Vector3(0.05f, 0.004f, 0.042f), dark).transform.localRotation = Quaternion.Euler(90, 0, 0);
                        P(PrimitiveType.Cylinder, t, new Vector3(x, 0, -0.001f), new Vector3(0.044f, 0.0045f, 0.036f), Lit(new Color(0.25f, 0.22f, 0.32f), 0.95f)).transform.localRotation = Quaternion.Euler(90, 0, 0);
                    }
                    P(PrimitiveType.Cube, t, new Vector3(0, 0.008f, 0), new Vector3(0.022f, 0.006f, 0.006f), dark);
                    P(PrimitiveType.Cube, t, new Vector3(-0.064f, 0.008f, -0.06f), new Vector3(0.005f, 0.008f, 0.12f), dark);
                    P(PrimitiveType.Cube, t, new Vector3(0.064f, 0.008f, -0.06f), new Vector3(0.005f, 0.008f, 0.12f), dark);
                    Box(root, new Vector3(0.14f, 0.05f, 0.04f), 0.05f);
                    break;
                case "headphones":
                    for (int i = 0; i <= 8; i++)
                    {
                        float a = Mathf.Lerp(-80f, 80f, i / 8f) * Mathf.Deg2Rad;
                        P(PrimitiveType.Cube, t, new Vector3(Mathf.Sin(a) * 0.085f, Mathf.Cos(a) * 0.085f, 0), new Vector3(0.024f, 0.012f, 0.03f), dark)
                            .transform.localRotation = Quaternion.Euler(0, 0, -a * Mathf.Rad2Deg);
                    }
                    foreach (float x in new[] { -0.09f, 0.09f })
                    {
                        P(PrimitiveType.Cylinder, t, new Vector3(x, -0.01f, 0), new Vector3(0.065f, 0.018f, 0.065f), dark).transform.localRotation = Quaternion.Euler(0, 0, 90);
                        P(PrimitiveType.Cylinder, t, new Vector3(x * 0.88f, -0.01f, 0), new Vector3(0.055f, 0.01f, 0.055f), Lit(new Color(0.55f, 0.36f, 0.97f))).transform.localRotation = Quaternion.Euler(0, 0, 90);
                    }
                    Box(root, new Vector3(0.2f, 0.18f, 0.07f), 0.25f);
                    break;
                case "phone":
                    P(PrimitiveType.Cube, t, Vector3.zero, new Vector3(0.074f, 0.152f, 0.008f), dark);
                    P(PrimitiveType.Cube, t, new Vector3(0, 0, -0.0045f), new Vector3(0.068f, 0.144f, 0.001f), Glow(new Color(0.4f, 0.3f, 0.75f), 0.6f));
                    Box(root, new Vector3(0.074f, 0.152f, 0.01f), 0.18f);
                    break;
                case "bass guitar":
                    var body = Lit(new Color(0.75f, 0.16f, 0.2f));
                    P(PrimitiveType.Sphere, t, new Vector3(0, -0.11f, 0), new Vector3(0.19f, 0.22f, 0.04f), body);
                    P(PrimitiveType.Sphere, t, new Vector3(0.02f, -0.02f, 0), new Vector3(0.14f, 0.13f, 0.038f), body);
                    P(PrimitiveType.Cube, t, new Vector3(0, 0.16f, 0), new Vector3(0.03f, 0.36f, 0.018f), Lit(new Color(0.5f, 0.34f, 0.22f)));
                    P(PrimitiveType.Cube, t, new Vector3(0, 0.36f, 0), new Vector3(0.045f, 0.06f, 0.016f), dark);
                    P(PrimitiveType.Cube, t, new Vector3(0, -0.1f, -0.022f), new Vector3(0.07f, 0.04f, 0.004f), white); // pickguard
                    Box(root, new Vector3(0.2f, 0.6f, 0.05f), 2.5f).center = new Vector3(0, 0.08f, 0);
                    break;
                case "camera":
                    P(PrimitiveType.Cube, t, Vector3.zero, new Vector3(0.12f, 0.075f, 0.05f), dark);
                    P(PrimitiveType.Cylinder, t, new Vector3(0, 0, 0.04f), new Vector3(0.055f, 0.025f, 0.055f), Lit(new Color(0.3f, 0.3f, 0.33f))).transform.localRotation = Quaternion.Euler(90, 0, 0);
                    P(PrimitiveType.Cube, t, new Vector3(0.04f, 0.042f, 0), new Vector3(0.02f, 0.01f, 0.02f), white);
                    Box(root, new Vector3(0.12f, 0.08f, 0.09f), 0.4f);
                    break;
                case "cup of tea":
                    P(PrimitiveType.Cylinder, t, Vector3.zero, new Vector3(0.075f, 0.045f, 0.075f), white);
                    P(PrimitiveType.Cylinder, t, new Vector3(0, 0.04f, 0), new Vector3(0.066f, 0.004f, 0.066f), Lit(new Color(0.62f, 0.38f, 0.18f), 0.9f));
                    P(PrimitiveType.Sphere, t, new Vector3(0.045f, 0.005f, 0), new Vector3(0.012f, 0.045f, 0.03f), white);
                    Box(root, new Vector3(0.1f, 0.09f, 0.08f), 0.3f);
                    break;
                case "book":
                    P(PrimitiveType.Cube, t, Vector3.zero, new Vector3(0.15f, 0.21f, 0.03f), Lit(new Color(0.36f, 0.28f, 0.7f)));
                    P(PrimitiveType.Cube, t, new Vector3(0.004f, 0, 0), new Vector3(0.144f, 0.204f, 0.032f), Lit(new Color(0.96f, 0.94f, 0.88f)));
                    Box(root, new Vector3(0.15f, 0.21f, 0.034f), 0.4f);
                    break;
                case "flower":
                    P(PrimitiveType.Cylinder, t, new Vector3(0, -0.08f, 0), new Vector3(0.008f, 0.09f, 0.008f), Lit(new Color(0.3f, 0.6f, 0.36f)));
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i / 6f * Mathf.PI * 2f;
                        P(PrimitiveType.Sphere, t, new Vector3(Mathf.Cos(a) * 0.025f, 0.02f, Mathf.Sin(a) * 0.025f), new Vector3(0.035f, 0.012f, 0.035f), Lit(new Color(1f, 0.55f, 0.75f)));
                    }
                    P(PrimitiveType.Sphere, t, new Vector3(0, 0.024f, 0), Vector3.one * 0.022f, Glow(new Color(1f, 0.85f, 0.4f), 0.8f));
                    Box(root, new Vector3(0.08f, 0.22f, 0.08f), 0.05f).center = new Vector3(0, -0.06f, 0);
                    break;
                case "umbrella":
                    P(PrimitiveType.Sphere, t, new Vector3(0, 0.2f, 0), new Vector3(0.5f, 0.14f, 0.5f), Lit(new Color(0.55f, 0.36f, 0.97f)));
                    P(PrimitiveType.Cylinder, t, new Vector3(0, 0, 0), new Vector3(0.012f, 0.22f, 0.012f), dark);
                    Box(root, new Vector3(0.5f, 0.5f, 0.5f), 0.4f).center = new Vector3(0, 0.05f, 0);
                    break;
                case "ball":
                    P(PrimitiveType.Sphere, t, Vector3.zero, Vector3.one * 0.12f, Lit(new Color(1f, 0.45f, 0.6f)));
                    var sc = root.AddComponent<SphereCollider>(); sc.radius = 0.06f;
                    sc.material = new PhysicsMaterial { bounciness = 0.75f, bounceCombine = PhysicsMaterialCombine.Maximum };
                    rb.mass = 0.2f;
                    break;
                default: // star
                    for (int i = 0; i < 3; i++)
                        P(PrimitiveType.Cube, t, Vector3.zero, new Vector3(0.04f, 0.15f, 0.04f), Glow(new Color(1f, 0.85f, 0.35f), 2f))
                            .transform.localRotation = Quaternion.Euler(0, 0, i * 60f);
                    root.AddComponent<SphereCollider>().radius = 0.07f;
                    rb.useGravity = false;
                    break;
            }
            return root;
        }

        static BoxCollider Box(GameObject root, Vector3 size, float mass)
        {
            var b = root.AddComponent<BoxCollider>();
            b.size = size;
            root.GetComponent<Rigidbody>().mass = mass;
            return b;
        }

        static GameObject P(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        readonly Dictionary<Color, Material> _mats = new Dictionary<Color, Material>();

        Material Lit(Color c, float smooth = 0.5f)
        {
            if (_mats.TryGetValue(c, out var cached)) return cached;
            var m = litTemplate != null ? new Material(litTemplate) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            _mats[c] = m;
            return m;
        }

        Material Glow(Color c, float k)
        {
            var m = glowTemplate != null ? new Material(glowTemplate) : Lit(c);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c * 0.4f);
            if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * k); }
            return m;
        }
    }
}
