using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;

namespace Harley.EditorTools
{
    /// <summary>
    /// One click builds Harley in the open scene, with materials, config and every reference wired.
    ///  Harley > Create Window App Scene      normal app window next to your other apps, sees your screen
    ///  Harley > Create Mixed Reality Harley  flies around your room (needs the Meta XR / MRUK setup)
    /// </summary>
    public static class CompanionBuilder
    {
        const string RootDir = "Assets/Harley";
        const string MatDir = RootDir + "/Materials";
        const string ConfigPath = RootDir + "/CompanionConfig.asset";
        const string ModelPath = RootDir + "/Models/Harley.fbx";

        static readonly Color White = new Color(0.957f, 0.945f, 1f);     // #F4F1FF
        static readonly Color Purple = new Color(0.357f, 0.129f, 0.714f); // #5B21B6 deep royal purple
        static readonly Color Lavender = new Color(0.769f, 0.710f, 0.992f); // #C4B5FD
        static readonly Color Cheek = new Color(0.96f, 0.62f, 0.85f);
        static readonly Color Bubble = new Color(0.12f, 0.06f, 0.2f, 0.82f);
        static readonly Color WindowBackground = new Color(0.078f, 0.063f, 0.110f); // #14101C

        /// <summary>Everything the shared body builder makes.</summary>
        class Parts
        {
            public GameObject root, body;
            public CompanionVisual visual;
            public CompanionSounds sounds;
            public TextMeshPro subtitle;
            public Renderer subtitleBg;
        }

        // ---------- window app ----------

        [MenuItem("Harley/Advanced/Create Window App Scene (old 2D version)", false, 100)]
        public static void CreateWindowApp()
        {
            if (FindByTypeName("OVRCameraRig") != null || FindByTypeName("OVRManager") != null)
            {
                if (!EditorUtility.DisplayDialog("Harley", "This scene has a VR camera rig. The window app needs a normal (non-VR) project and scene. Continue anyway?", "Continue", "Cancel"))
                    return;
            }

            CompanionConfig config = GetOrCreateConfig();

            // Camera: looks at Harley from the front, dark window background.
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGO = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGO.tag = "MainCamera";
                Undo.RegisterCreatedObjectUndo(camGO, "Create Camera");
                cam = camGO.GetComponent<Camera>();
            }
            cam.transform.position = new Vector3(0f, 0.035f, 0.62f);
            cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.03f, 0f) - cam.transform.position, Vector3.up);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = WindowBackground;
            cam.fieldOfView = 35f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            if (cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();

            Parts p = BuildBody(Vector3.zero);
            GameObject root = p.root;

            var claude = root.AddComponent<ClaudeClient>();
            var eyes = root.AddComponent<ScreenEyes>();
            eyes.maxSize = 1024; eyes.jpegQuality = 80;
            var memory = root.AddComponent<CompanionMemory>();
            var tools = root.AddComponent<CompanionTools>();
            var motion = root.AddComponent<PanelMotion>();
            var input = root.AddComponent<PanelInput>();
            var voice = root.AddComponent<CompanionVoice>();
            var brain = root.AddComponent<CompanionBrain>();
            var controls = root.AddComponent<PanelControls>();

            motion.cam = cam;
            motion.head = cam.transform;
            motion.bodyRadius = 0.1f;
            input.cam = cam;
            input.body = p.body.transform;
            input.bodyRadius = 0.1f;
            input.motion = motion;
            voice.head = cam.transform;

            WireCommon(p, config, claude, eyes, memory, tools, motion, input, voice, brain, null);
            brain.mode = CompanionMode.Window;
            controls.brain = brain;
            controls.voice = voice;
            controls.input = input;
            controls.screen = eyes;

            EnsureLight();

            // Turns on the window-app manifest changes (screen sharing service, window size) for Android builds.
            if (!File.Exists(HarleyMarker))
            {
                File.WriteAllText(HarleyMarker, "Harley window app build. Delete this file to build the mixed reality version without screen-sharing entries.\n");
                AssetDatabase.ImportAsset(HarleyMarker);
            }

            Finish(root);
            EditorUtility.DisplayDialog("Harley",
                "Harley's window app scene is ready.\n\n" +
                KeyNote(config) + "\n" +
                "• Build for Android with XR turned OFF (Project Settings > XR Plug-in Management > Android), so Quest opens Harley as a normal window.\n" +
                "• The first time, Quest asks to allow screen capture: tap Start.\n" +
                VoiceNote(), "OK");
        }

        // ---------- mixed reality ----------

        [MenuItem("Harley/Advanced/Add Mixed Reality Harley Only", false, 101)]
        static void CreateMixedRealityMenu() => CreateMixedReality(true);

        /// <summary>Harley in 3D in your room, plus the pointer, the floating menu and the wrist button.</summary>
        public static void CreateMixedReality(bool showDialog)
        {
            CompanionConfig config = GetOrCreateConfig();
            Camera cam = Camera.main;
            Vector3 pos = cam != null
                ? cam.transform.position + Flat(cam.transform.forward) * 0.9f + Vector3.down * 0.2f
                : new Vector3(0f, 1.3f, 0.9f);
            if (pos.y < 0.6f) pos.y = 1.2f; // the rig's camera sits on the floor in the Editor; on the headset it's at eye height

            Parts p = BuildBody(pos, true);
            GameObject root = p.root;

            var claude = root.AddComponent<ClaudeClient>();
            var eyes = root.AddComponent<MixedEyes>();
            eyes.maxSize = 512; eyes.jpegQuality = 70;
            eyes.pageMaxSize = 1024;
            var room = root.AddComponent<CompanionRoom>();
            var memory = root.AddComponent<CompanionMemory>();
            var tools = root.AddComponent<CompanionTools>();
            var movement = root.AddComponent<CompanionMovement>();
            var voice = root.AddComponent<CompanionVoice>();
            var interaction = root.AddComponent<CompanionInteraction>();
            var brain = root.AddComponent<CompanionBrain>();

            interaction.body = p.body.transform;
            interaction.bodyRadius = 0.08f;
            interaction.movement = movement;
            movement.bodyRadius = 0.08f;
            movement.standHeight = 0.2f;
            movement.room = room;

            WireCommon(p, config, claude, eyes, memory, tools, movement, interaction, voice, brain, room);
            brain.mode = CompanionMode.MixedReality;
            eyes.passthroughCameraAccess = FindByTypeName("PassthroughCameraAccess");
            var wit = root.AddComponent<WitSpeech>();
            wit.config = config;
            voice.wit = wit;

            // Floating UI: pointer for hands/controllers, the settings menu, the wrist button
            var ui = new GameObject("Harley UI");
            Undo.RegisterCreatedObjectUndo(ui, "Create Harley UI");
            var pointer = ui.AddComponent<HarleyPointer>();
            pointer.lineMaterial = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/Harley_Glow.mat");
            var menuGO = new GameObject("Harley Menu");
            menuGO.transform.SetParent(ui.transform, false);
            var menu = menuGO.AddComponent<HarleyMenu>();
            menu.brain = brain;
            var wristGO = new GameObject("Harley Wrist Button");
            wristGO.transform.SetParent(ui.transform, false);
            var wrist = wristGO.AddComponent<WristButton>();
            wrist.menu = menu;
            // Harley's world (immersive mode) and the play tools
            var white = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/Harley_White.mat");
            var purpleMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/Harley_Purple.mat");
            var world = ui.AddComponent<HarleyWorld>();
            world.litTemplate = white;
            world.glowTemplate = purpleMat;
            ui.AddComponent<HarleyGrab>();
            var pocket = root.AddComponent<HarleyPocket>();
            pocket.brain = brain;
            pocket.litTemplate = white;
            pocket.glowTemplate = purpleMat;
            world.room = room;
            var play = ui.AddComponent<HarleyTools>();
            play.brain = brain;
            play.litTemplate = white;
            play.glowTemplate = purpleMat;
            foreach (var c in ui.GetComponentsInChildren<Component>(true)) EditorUtility.SetDirty(c);

            EnsureLight();
            Finish(root);
            if (!showDialog) return;

            string camNote = eyes.passthroughCameraAccess != null
                ? "• Passthrough camera connected."
                : "• No Passthrough Camera Access yet: add the building block, then drag it into PassthroughEyes.";
            string roomNote = FindByTypeName("MRUK") != null
                ? "• Room scan (MRUK) found: Harley will know your furniture."
                : "• No MRUK in the scene: add the MR Utility Kit block so Harley knows your room.";
            EditorUtility.DisplayDialog("Harley", "Harley is in your room.\n\n" + KeyNote(config) + "\n" + camNote + "\n" + roomNote + "\n" + VoiceNote(config), "OK");
        }

        [MenuItem("Harley/Select Config (API key, city)", false, 20)]
        public static void SelectConfig()
        {
            Selection.activeObject = GetOrCreateConfig();
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        [MenuItem("Harley/Open Memory Folder", false, 40)]
        public static void OpenMemoryFolder() => EditorUtility.RevealInFinder(Application.persistentDataPath);

        const string HarleyMarker = "Assets/Harley/Editor/WindowAppBuild.txt";

        // ---------- shared ----------

        static Parts BuildBody(Vector3 position, bool robot = false)
        {
            EnsureFolder(RootDir);
            EnsureFolder(MatDir);

            Material white = LitMaterial("Harley_White", White, 0.15f, 0.8f);
            // Dark base + purple emission = solid glowing purple that doesn't wash out to pink/white.
            Material purple = LitMaterial("Harley_Purple", Purple, 1.0f, 0.75f, Purple * 0.2f);
            Material cheek = LitMaterial("Harley_Cheek", Cheek, 0.5f, 0.4f);
            Material glow = SpriteMaterial("Harley_Glow", Color.white, null);
            Material bubble = SpriteMaterial("Harley_Bubble", Bubble, null);
            Material sparkle = SpriteMaterial("Harley_Sparkle", Color.white, SparkleTexture());

            var p = new Parts();
            p.root = new GameObject("Harley");
            Undo.RegisterCreatedObjectUndo(p.root, "Create Harley");
            p.root.transform.position = position;

            var visualGO = new GameObject("Visual");
            visualGO.transform.SetParent(p.root.transform, false);
            p.visual = visualGO.AddComponent<CompanionVisual>();

            if (robot)
            {
                p.visual.haloHeight = 0.1f;
                p.visual.haloRadius = 0.058f;
                p.visual.gestureScale = 1.9f;
                p.visual.listenHandOffset = new Vector3(0.02f, 0.2f, 0.0f);
            }

            var squash = new GameObject("SquashRoot").transform;
            squash.SetParent(visualGO.transform, false);

            GameObject eyeL, eyeR, mouth, cheekL, cheekR, handL, handR;
            var model = robot ? AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) : null;
            if (model != null)
            {
                BuildFromModel(p, model, visualGO.transform, squash, white, purple, cheek,
                               out eyeL, out eyeR, out mouth, out cheekL, out cheekR, out handL, out handR);
            }
            else
            {
                // Robot: a smaller, slightly wider head on a body. Window app: just the round head.
                Transform headT = squash;
                if (robot)
                {
                    headT = new GameObject("Head").transform;
                    headT.SetParent(squash, false);
                    headT.localScale = new Vector3(0.82f, 0.76f, 0.78f);
                }
                var squashT = squash;
                squash = headT;
                p.body = Prim("Body", squash, Vector3.zero, Vector3.one * 0.2f, white);
                p.sounds = p.body.AddComponent<CompanionSounds>(); // sounds come from the body
                eyeL = Prim("EyeL", squash, new Vector3(-0.038f, 0.016f, 0.088f), new Vector3(0.032f, 0.05f, 0.014f), purple);
                eyeR = Prim("EyeR", squash, new Vector3(0.038f, 0.016f, 0.088f), new Vector3(0.032f, 0.05f, 0.014f), purple);
                mouth = Prim("Mouth", squash, new Vector3(0f, -0.027f, 0.094f), new Vector3(0.018f, 0.006f, 0.008f), purple);
                cheekL = Prim("CheekL", squash, new Vector3(-0.061f, -0.014f, 0.077f), new Vector3(0.024f, 0.012f, 0.008f), cheek);
                cheekR = Prim("CheekR", squash, new Vector3(0.061f, -0.014f, 0.077f), new Vector3(0.024f, 0.012f, 0.008f), cheek);
                squash = squashT;
                Vector3 handPosL = robot ? new Vector3(-0.1f, -0.215f, 0.025f) : new Vector3(-0.128f, -0.03f, 0.02f);
                handL = Prim("HandL", visualGO.transform, handPosL, robot ? new Vector3(0.03f, 0.034f, 0.026f) : Vector3.one * 0.034f, white);
                handR = Prim("HandR", visualGO.transform, new Vector3(-handPosL.x, handPosL.y, handPosL.z), robot ? new Vector3(0.03f, 0.034f, 0.026f) : Vector3.one * 0.034f, white);
                if (robot) BuildRobotBody(p, visualGO.transform, white, purple, handL.transform, handR.transform);
            }

            // halo
            var haloGO = new GameObject("Halo");
            haloGO.transform.SetParent(visualGO.transform, false);
            haloGO.transform.localPosition = new Vector3(0f, p.visual.haloHeight, 0f);
            var halo = haloGO.AddComponent<LineRenderer>();
            halo.sharedMaterial = glow;
            halo.widthMultiplier = 0.007f;
            halo.useWorldSpace = false;
            halo.loop = true;
            halo.alignment = LineAlignment.View;
            halo.shadowCastingMode = ShadowCastingMode.Off;
            halo.receiveShadows = false;
            halo.startColor = halo.endColor = Purple;
            const int segs = 48;
            halo.positionCount = segs;
            for (int i = 0; i < segs; i++)
            {
                float a = i / (float)segs * Mathf.PI * 2f;
                halo.SetPosition(i, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * p.visual.haloRadius);
            }

            // trail
            var trailGO = new GameObject("Trail");
            trailGO.transform.SetParent(visualGO.transform, false);
            trailGO.transform.localPosition = new Vector3(0f, robot ? -0.3f : -0.06f, 0f);
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

            // sparkles (on the root, so they don't shrink with the pop-in)
            var sparkles = BuildSparkles(p.root.transform, sparkle);

            // subtitle bubble
            var subGO = new GameObject("Subtitle");
            subGO.transform.SetParent(p.root.transform, false);
            subGO.transform.localPosition = new Vector3(0f, 0.19f, 0f);
            p.subtitle = subGO.AddComponent<TextMeshPro>();
            p.subtitle.text = "";
            p.subtitle.fontSize = 0.22f;
            p.subtitle.alignment = TextAlignmentOptions.Bottom;
#pragma warning disable 0618
            p.subtitle.enableWordWrapping = true;
#pragma warning restore 0618
            p.subtitle.rectTransform.pivot = new Vector2(0.5f, 0f);
            p.subtitle.rectTransform.sizeDelta = new Vector2(0.45f, 0.25f);
            p.subtitle.color = Color.white;
            var tmpRenderer = subGO.GetComponent<MeshRenderer>();
            if (tmpRenderer) tmpRenderer.sortingOrder = 1;

            var bg = Prim("SubtitleBg", p.root.transform, new Vector3(0f, 0.22f, 0f), new Vector3(0.3f, 0.06f, 1f), bubble, PrimitiveType.Quad);
            bg.GetComponent<MeshRenderer>().sortingOrder = 0;
            p.subtitleBg = bg.GetComponent<Renderer>();

            var v = p.visual;
            v.squashRoot = squash;
            v.eyeL = eyeL.transform;
            v.eyeR = eyeR.transform;
            v.mouth = mouth.transform;
            v.cheekL = cheekL.GetComponent<Renderer>();
            v.cheekR = cheekR.GetComponent<Renderer>();
            v.handL = handL.transform;
            v.handR = handR.transform;
            v.halo = halo;
            v.trail = trail;
            v.sparkles = sparkles;
            v.glowRenderers = new Renderer[] { eyeL.GetComponent<Renderer>(), eyeR.GetComponent<Renderer>(), mouth.GetComponent<Renderer>() };
            return p;
        }

        /// <summary>
        /// Harley's Blender model (Assets/Harley/Models/Harley.fbx): head parts go under the squash root so the
        /// face animates as before, the body and the one-piece arms stay below, with hand targets at the arm tips.
        /// </summary>
        static void BuildFromModel(Parts p, GameObject model, Transform visual, Transform squash, Material white, Material purple, Material cheek,
                                   out GameObject eyeL, out GameObject eyeR, out GameObject mouth, out GameObject cheekL, out GameObject cheekR,
                                   out GameObject handL, out GameObject handR)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = "Model";
            inst.transform.SetParent(visual, false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;

            Transform Find(string n)
            {
                foreach (var t in inst.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
                return null;
            }
            Transform head = Find("Head"), eL = Find("EyeL"), eR = Find("EyeR");
            // The face must look along +Z: turn the model around if the exporter flipped it
            if (eL != null && head != null && visual.InverseTransformPoint(eL.position).z < visual.InverseTransformPoint(head.position).z)
                inst.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // Our materials (shared with the rest of Harley), matched by name
            Material joint = LitMaterial("Harley_Joint", new Color(0.81f, 0.78f, 0.9f), 0.05f, 0.6f);
            Material shine = LitMaterial("Harley_Highlight", Color.white, 1.2f, 0.9f);
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string n = mats[i] != null ? mats[i].name : "";
                    mats[i] = n.Contains("Purple") ? purple : n.Contains("Cheek") ? cheek : n.Contains("Joint") ? joint : n.Contains("Highlight") ? shine : white;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            // Face parts animate with the squash root
            foreach (string n in new[] { "Head", "EyeL", "EyeR", "Mouth", "CheekL", "CheekR" })
            {
                var t = Find(n);
                if (t != null) t.SetParent(squash, true);
            }
            p.body = head != null ? head.gameObject : inst;
            p.sounds = p.body.AddComponent<CompanionSounds>();
            eyeL = eL != null ? eL.gameObject : new GameObject("EyeL");
            eyeR = eR != null ? eR.gameObject : new GameObject("EyeR");
            mouth = Find("Mouth") != null ? Find("Mouth").gameObject : new GameObject("Mouth");
            cheekL = Find("CheekL") != null ? Find("CheekL").gameObject : new GameObject("CheekL");
            cheekR = Find("CheekR") != null ? Find("CheekR").gameObject : new GameObject("CheekR");

            // Hand targets at the far end of each arm
            GameObject Target(string name, Transform arm)
            {
                var go = new GameObject(name);
                go.transform.SetParent(visual, false);
                Vector3 tip = arm != null ? arm.position + Vector3.down * 0.1f : visual.position;
                var mf = arm != null ? arm.GetComponent<MeshFilter>() : null;
                if (mf != null && mf.sharedMesh != null)
                {
                    float best = -1f;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        Vector3 w = arm.TransformPoint(v);
                        float d = (w - arm.position).sqrMagnitude;
                        if (d > best) { best = d; tip = w; }
                    }
                }
                go.transform.position = tip;
                return go;
            }
            Transform armL = Find("ArmL"), armR = Find("ArmR");
            handL = Target("HandL", armL);
            handR = Target("HandR", armR);

            var rig = visual.gameObject.AddComponent<HarleyBodyRig>();
            rig.visual = p.visual;
            rig.left = new HarleyBodyRig.Arm { arm = armL, target = handL.transform };
            rig.right = new HarleyBodyRig.Arm { arm = armR, target = handR.transform };
            var core = Find("Core");
            rig.core = core != null ? core.GetComponent<Renderer>() : null;
            var glows = new System.Collections.Generic.List<Renderer>();
            foreach (string n in new[] { "HoverRing", "HoverGlow" }) { var t = Find(n); if (t && t.GetComponent<Renderer>()) glows.Add(t.GetComponent<Renderer>()); }
            rig.hoverGlow = glows.ToArray();
        }

        /// <summary>
        /// Harley's robot body: neck, chest with a glowing core, hips, jointed arms and short legs.
        /// An original design: soft white shell, purple joints and light, the halo and face of Harley.
        /// </summary>
        static void BuildRobotBody(Parts p, Transform visual, Material white, Material purple, Transform handL, Transform handR)
        {
            Material joint = LitMaterial("Harley_Joint", new Color(0.82f, 0.8f, 0.9f), 0.05f, 0.6f);
            var bodyT = new GameObject("Torso").transform;
            bodyT.SetParent(visual, false);

            Prim("Neck", bodyT, new Vector3(0f, -0.085f, 0f), new Vector3(0.035f, 0.016f, 0.035f), joint, PrimitiveType.Cylinder);
            Prim("Chest", bodyT, new Vector3(0f, -0.145f, 0f), new Vector3(0.125f, 0.105f, 0.09f), white);
            Prim("Collar", bodyT, new Vector3(0f, -0.103f, 0f), new Vector3(0.09f, 0.006f, 0.065f), purple, PrimitiveType.Cylinder);
            var core = Prim("Core", bodyT, new Vector3(0f, -0.14f, 0.043f), new Vector3(0.026f, 0.026f, 0.01f), purple);
            Prim("Waist", bodyT, new Vector3(0f, -0.2f, 0f), new Vector3(0.06f, 0.012f, 0.05f), joint, PrimitiveType.Cylinder);
            Prim("Hips", bodyT, new Vector3(0f, -0.225f, 0f), new Vector3(0.09f, 0.055f, 0.07f), white);

            var rig = visual.gameObject.AddComponent<RobotRig>();
            rig.visual = p.visual;
            rig.body = bodyT;
            rig.core = core.GetComponent<Renderer>();

            RobotRig.Arm Arm(string side, float x, Transform hand)
            {
                var a = new RobotRig.Arm();
                a.shoulder = Prim("Shoulder" + side, bodyT, new Vector3(x * 0.068f, -0.118f, 0f), Vector3.one * 0.03f, purple).transform;
                a.upper = Prim("UpperArm" + side, visual, Vector3.zero, Vector3.one * 0.02f, white, PrimitiveType.Capsule).transform;
                a.lower = Prim("Forearm" + side, visual, Vector3.zero, Vector3.one * 0.02f, white, PrimitiveType.Capsule).transform;
                a.hand = hand;
                a.upperLength = 0.072f; a.lowerLength = 0.072f;
                return a;
            }
            rig.left = Arm("L", -1f, handL);
            rig.right = Arm("R", 1f, handR);
            rig.left.elbowHint = new Vector3(-1f, -0.3f, -1f);

            RobotRig.Leg Leg(string side, float x)
            {
                var l = new RobotRig.Leg();
                l.hip = Prim("Hip" + side, bodyT, new Vector3(x * 0.03f, -0.245f, 0f), Vector3.one * 0.026f, purple).transform;
                l.thigh = Prim("Thigh" + side, visual, Vector3.zero, Vector3.one * 0.02f, white, PrimitiveType.Capsule).transform;
                l.shin = Prim("Shin" + side, visual, Vector3.zero, Vector3.one * 0.02f, white, PrimitiveType.Capsule).transform;
                l.foot = Prim("Foot" + side, visual, Vector3.zero, new Vector3(0.026f, 0.014f, 0.04f), white).transform;
                l.thighLength = 0.06f; l.shinLength = 0.06f;
                return l;
            }
            rig.legL = Leg("L", -1f);
            rig.legR = Leg("R", 1f);
            rig.limbWidth = 0.024f;
        }

        static void WireCommon(Parts p, CompanionConfig config, ClaudeClient claude, CompanionSight eyes, CompanionMemory memory,
                               CompanionTools tools, CompanionMotion motion, CompanionInput input, CompanionVoice voice,
                               CompanionBrain brain, CompanionRoom room)
        {
            p.visual.movement = motion;
            p.visual.voice = voice;

            voice.config = config;
            voice.sounds = p.sounds;
            voice.subtitle = p.subtitle;
            voice.subtitleBackground = p.subtitleBg;
            p.sounds.config = config;
            memory.config = config;
            tools.config = config;
            tools.claude = claude;
            claude.config = config;

            brain.config = config;
            brain.claude = claude;
            brain.eyes = eyes;
            brain.movement = motion;
            brain.visual = p.visual;
            brain.voice = voice;
            brain.interaction = input;
            brain.memory = memory;
            brain.room = room;
            brain.sounds = p.sounds;
            brain.tools = tools;
        }

        static void Finish(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true)) EditorUtility.SetDirty(c);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;
        }

        static string KeyNote(CompanionConfig config) =>
            !config.HasBrainKey
                ? "• Paste your Claude API key (and your city for weather): Harley > Select Config."
                : "• API key is set.";

        static string VoiceNote(CompanionConfig config = null) =>
            config != null && !string.IsNullOrWhiteSpace(config.witToken)
                ? "• Wit token set: Harley listens and talks out loud."
                : "• To talk out loud, paste a free wit.ai Client Access Token into Wit Token (Harley > Select Config). Typing works without it.";

        static ParticleSystem BuildSparkles(Transform parent, Material mat)
        {
            var go = new GameObject("Sparkles");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.12f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.008f, 0.022f);
            main.startColor = Lavender;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.03f; // drift up a little
            main.maxParticles = 300;

            var emission = ps.emission;
            emission.enabled = false; // emitted by script

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.1f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = fade;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        static Texture2D SparkleTexture()
        {
            string path = MatDir + "/Harley_Sparkle.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "Harley_Sparkle", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    a = a * a * (3f - 2f * a);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        static GameObject Prim(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, PrimitiveType type = PrimitiveType.Sphere)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col) Object.DestroyImmediate(col); // no colliders: room raycasts must not hit Harley
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
            bool isNew = m == null;
            if (isNew)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Standard");
                m = new Material(sh) { name = name };
            }
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
            if (isNew) AssetDatabase.CreateAsset(m, path); else EditorUtility.SetDirty(m);
            return m;
        }

        static Material SpriteMaterial(string name, Color color, Texture2D tex)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            // Sprites/Default: unlit, alpha blended, uses vertex colours, works in URP.
            m = new Material(Shader.Find("Sprites/Default")) { name = name };
            m.SetColor("_Color", color);
            if (tex != null) m.mainTexture = tex;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static CompanionConfig GetOrCreateConfig()
        {
            EnsureFolder(RootDir);
            var c = AssetDatabase.LoadAssetAtPath<CompanionConfig>(ConfigPath);
            if (c != null) return c;
            c = ScriptableObject.CreateInstance<CompanionConfig>();
            AssetDatabase.CreateAsset(c, ConfigPath);
            AssetDatabase.SaveAssets();
            return c;
        }

        static MonoBehaviour FindByTypeName(string typeName)
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (mb != null && mb.GetType().Name == typeName) return mb;
            return null;
        }

        static void EnsureLight()
        {
            if (Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 0) return;
            var lightGO = new GameObject("Harley Light");
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
