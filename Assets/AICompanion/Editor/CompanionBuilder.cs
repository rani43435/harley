using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;

namespace AICompanion.EditorTools
{
    /// <summary>
    /// One click: builds the white & purple companion in the open scene,
    /// creates its materials and config, and wires every reference.
    /// Menu: AI Companion > Create Companion In Scene
    /// </summary>
    public static class CompanionBuilder
    {
        const string RootDir = "Assets/AICompanion";
        const string MatDir = RootDir + "/Materials";
        const string ConfigPath = RootDir + "/CompanionConfig.asset";

        static readonly Color White = new Color(0.957f, 0.945f, 1f);    // #F4F1FF
        static readonly Color Purple = new Color(0.545f, 0.361f, 0.965f); // #8B5CF6
        static readonly Color Cheek = new Color(0.96f, 0.62f, 0.85f);
        static readonly Color Bubble = new Color(0.12f, 0.06f, 0.2f, 0.82f);

        [MenuItem("AI Companion/Create Companion In Scene", false, 0)]
        public static void Create()
        {
            EnsureFolder(RootDir);
            EnsureFolder(MatDir);

            Material white = LitMaterial("Companion_White", White, 0.15f, 0.8f);
            // Dark base + purple emission = solid glowing purple that doesn't wash out to pink/white.
            Material purple = LitMaterial("Companion_Purple", Purple, 1.0f, 0.75f, Purple * 0.2f);
            Material cheek = LitMaterial("Companion_Cheek", Cheek, 0.5f, 0.4f);
            Material glow = SpriteMaterial("Companion_Glow", Color.white);
            Material bubble = SpriteMaterial("Companion_Bubble", Bubble);
            CompanionConfig config = GetOrCreateConfig();

            // ---- root + brain parts ----
            var root = new GameObject("AI Companion");
            Undo.RegisterCreatedObjectUndo(root, "Create AI Companion");
            Camera cam = Camera.main;
            root.transform.position = cam != null
                ? cam.transform.position + Flat(cam.transform.forward) * 0.9f + Vector3.down * 0.2f
                : new Vector3(0f, 1.3f, 0.9f);

            var claude = root.AddComponent<ClaudeClient>();
            var eyes = root.AddComponent<CompanionEyes>();
            var movement = root.AddComponent<CompanionMovement>();
            var voice = root.AddComponent<CompanionVoice>();
            var interaction = root.AddComponent<CompanionInteraction>();
            var brain = root.AddComponent<CompanionBrain>();

            // ---- body ----
            var visualGO = new GameObject("Visual");
            visualGO.transform.SetParent(root.transform, false);
            var visual = visualGO.AddComponent<CompanionVisual>();

            var squash = new GameObject("SquashRoot").transform;
            squash.SetParent(visualGO.transform, false);

            var body = Prim("Body", squash, Vector3.zero, Vector3.one * 0.2f, white);
            var eyeL = Prim("EyeL", squash, new Vector3(-0.038f, 0.016f, 0.088f), new Vector3(0.032f, 0.05f, 0.014f), purple);
            var eyeR = Prim("EyeR", squash, new Vector3(0.038f, 0.016f, 0.088f), new Vector3(0.032f, 0.05f, 0.014f), purple);
            var mouth = Prim("Mouth", squash, new Vector3(0f, -0.027f, 0.094f), new Vector3(0.018f, 0.006f, 0.008f), purple);
            var cheekL = Prim("CheekL", squash, new Vector3(-0.061f, -0.014f, 0.077f), new Vector3(0.024f, 0.012f, 0.008f), cheek);
            var cheekR = Prim("CheekR", squash, new Vector3(0.061f, -0.014f, 0.077f), new Vector3(0.024f, 0.012f, 0.008f), cheek);
            var handL = Prim("HandL", visualGO.transform, new Vector3(-0.128f, -0.03f, 0.02f), Vector3.one * 0.034f, white);
            var handR = Prim("HandR", visualGO.transform, new Vector3(0.128f, -0.03f, 0.02f), Vector3.one * 0.034f, white);

            // ---- halo ----
            var haloGO = new GameObject("Halo");
            haloGO.transform.SetParent(visualGO.transform, false);
            var halo = haloGO.AddComponent<LineRenderer>();
            halo.sharedMaterial = glow;
            halo.widthMultiplier = 0.007f;
            halo.useWorldSpace = false;
            halo.loop = true;
            halo.alignment = LineAlignment.View;
            halo.shadowCastingMode = ShadowCastingMode.Off;
            halo.receiveShadows = false;
            halo.startColor = halo.endColor = Purple;
            haloGO.transform.localPosition = new Vector3(0f, visual.haloHeight, 0f);
            const int segs = 48;
            halo.positionCount = segs;
            for (int i = 0; i < segs; i++)
            {
                float a = i / (float)segs * Mathf.PI * 2f;
                halo.SetPosition(i, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * visual.haloRadius);
            }

            // ---- trail ----
            var trailGO = new GameObject("Trail");
            trailGO.transform.SetParent(visualGO.transform, false);
            trailGO.transform.localPosition = new Vector3(0f, -0.06f, 0f);
            var trail = trailGO.AddComponent<TrailRenderer>();
            trail.sharedMaterial = glow;
            trail.time = 0.35f;
            trail.minVertexDistance = 0.01f;
            trail.widthCurve = AnimationCurve.Linear(0f, 0.03f, 1f, 0f);
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Purple, 0f), new GradientColorKey(White, 1f) },
                      new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = g;
            trail.emitting = false;

            // ---- subtitle bubble ----
            var subGO = new GameObject("Subtitle");
            subGO.transform.SetParent(root.transform, false);
            subGO.transform.localPosition = new Vector3(0f, 0.19f, 0f);
            var tmp = subGO.AddComponent<TextMeshPro>();
            tmp.text = "";
            tmp.fontSize = 0.22f;
            tmp.alignment = TextAlignmentOptions.Bottom;
#pragma warning disable 0618
            tmp.enableWordWrapping = true;
#pragma warning restore 0618
            tmp.rectTransform.pivot = new Vector2(0.5f, 0f);
            tmp.rectTransform.sizeDelta = new Vector2(0.45f, 0.25f);
            tmp.color = Color.white;
            var tmpRenderer = subGO.GetComponent<MeshRenderer>();
            if (tmpRenderer) tmpRenderer.sortingOrder = 1;

            var bg = Prim("SubtitleBg", root.transform, new Vector3(0f, 0.22f, 0f), new Vector3(0.3f, 0.06f, 1f), bubble, PrimitiveType.Quad);
            bg.GetComponent<MeshRenderer>().sortingOrder = 0;

            // ---- wire everything ----
            visual.squashRoot = squash;
            visual.eyeL = eyeL.transform;
            visual.eyeR = eyeR.transform;
            visual.mouth = mouth.transform;
            visual.cheekL = cheekL.GetComponent<Renderer>();
            visual.cheekR = cheekR.GetComponent<Renderer>();
            visual.handL = handL.transform;
            visual.handR = handR.transform;
            visual.halo = halo;
            visual.trail = trail;
            visual.glowRenderers = new Renderer[] { eyeL.GetComponent<Renderer>(), eyeR.GetComponent<Renderer>(), mouth.GetComponent<Renderer>() };
            visual.movement = movement;
            visual.voice = voice;

            voice.subtitle = tmp;
            voice.subtitleBackground = bg.GetComponent<Renderer>();

            interaction.body = body.transform;
            interaction.bodyRadius = 0.1f;
            movement.bodyRadius = 0.1f;

            claude.config = config;
            brain.config = config;
            brain.claude = claude;
            brain.eyes = eyes;
            brain.movement = movement;
            brain.visual = visual;
            brain.voice = voice;
            brain.interaction = interaction;

            eyes.passthroughCameraAccess = FindPassthroughCamera();

            EnsureLight();

            foreach (var c in root.GetComponentsInChildren<Component>(true)) EditorUtility.SetDirty(c);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;

            string camNote = eyes.passthroughCameraAccess != null
                ? "Passthrough camera found and connected."
                : "No PassthroughCameraAccess in the scene yet. Add it (see README) and drag it into CompanionEyes.";
            string keyNote = string.IsNullOrWhiteSpace(config.apiKey)
                ? "Paste your Claude API key into Assets/AICompanion/CompanionConfig."
                : "API key is set.";
            EditorUtility.DisplayDialog("AI Companion", "Companion created.\n\n" + keyNote + "\n" + camNote, "OK");
        }

        [MenuItem("AI Companion/Select Config (API key)", false, 20)]
        public static void SelectConfig()
        {
            EnsureFolder(RootDir);
            Selection.activeObject = GetOrCreateConfig();
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        // ---------- helpers ----------

        static GameObject Prim(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, PrimitiveType type = PrimitiveType.Sphere)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col) Object.DestroyImmediate(col); // no colliders: room raycasts must not hit the companion
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        static Material LitMaterial(string name, Color color, float emission, float smoothness, Color? baseOverride = null)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;

            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            m = new Material(sh) { name = name };
            Color albedo = baseOverride ?? color;
            albedo.a = 1f;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", albedo);
            if (m.HasProperty("_Color")) m.SetColor("_Color", albedo);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (emission > 0f && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material SpriteMaterial(string name, Color color)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            // Sprites/Default: unlit, alpha blended, uses vertex colours, works in URP.
            m = new Material(Shader.Find("Sprites/Default")) { name = name };
            m.SetColor("_Color", color);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static CompanionConfig GetOrCreateConfig()
        {
            var c = AssetDatabase.LoadAssetAtPath<CompanionConfig>(ConfigPath);
            if (c != null) return c;
            c = ScriptableObject.CreateInstance<CompanionConfig>();
            AssetDatabase.CreateAsset(c, ConfigPath);
            AssetDatabase.SaveAssets();
            return c;
        }

        static MonoBehaviour FindPassthroughCamera()
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (mb != null && mb.GetType().Name == "PassthroughCameraAccess") return mb;
            return null;
        }

        static void EnsureLight()
        {
            if (Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 0) return;
            var lightGO = new GameObject("Companion Light");
            Undo.RegisterCreatedObjectUndo(lightGO, "Create Light");
            var l = lightGO.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            l.shadows = LightShadows.None;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
        }
    }
}
