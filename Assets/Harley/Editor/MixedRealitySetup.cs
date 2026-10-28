using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Harley.EditorTools
{
    /// <summary>
    /// Harley > Set Up Mixed Reality (one click)
    /// Turns the open project and scene into Harley's mixed reality app:
    ///  1. Meta project settings (passthrough, camera access, hands, system keyboard) + Meta's Project Setup fixes
    ///  2. Building blocks: Camera Rig, Passthrough, Hand Tracking, Passthrough Camera Access, MR Utility Kit, Effect Mesh
    ///  3. Harley in 3D, the pointer for hands/controllers, the floating menu and the wrist button
    /// Safe to run again: it skips what is already there. If Unity reloads in the middle, just run it again.
    /// Meta XR Core SDK, MR Utility Kit and the OpenXR plugin must be installed (Packages/manifest.json).
    /// </summary>
    public static class MixedRealitySetup
    {
        const string CoreBlocks = "Packages/com.meta.xr.sdk.core/Editor/BuildingBlocks/BlockData/";
        const string MrukBlocks = "Packages/com.meta.xr.mrutilitykit/Editor/BuildingBlocks/";

        static readonly string[] Blocks =
        {
            CoreBlocks + "CameraRig/CameraRigBB.asset",
            CoreBlocks + "Passthrough/Passthrough.asset",
            CoreBlocks + "HandTracking/HandTracking.asset",
            CoreBlocks + "PassthroughCameraAccess/PassthroughCameraAccess.asset",
            MrukBlocks + "MRUtilityKit/MRUtilityKit.asset",
            MrukBlocks + "EffectMesh/EffectMesh.asset",
        };

        static bool _running;

        [MenuItem("Harley/Set Up Mixed Reality (one click)", false, -10)]
        public static async void SetUp()
        {
            if (_running) return;
            if (FindType("OVRCameraRig") == null || FindType("Meta.XR.MRUtilityKit.MRUK") == null)
            {
                EditorUtility.DisplayDialog("Harley",
                    "Meta XR SDK isn't installed yet.\n\nWait until Unity finishes importing the packages (Meta XR Core SDK, MR Utility Kit), then run this again.",
                    "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Harley",
                    "Set up Harley's mixed reality app in this scene?\n\nHarley flies around your real room in 3D, with a floating menu and browser windows.\n" +
                    "The old window-app Harley in this scene is removed.", "Set up", "Cancel"))
                return;

            _running = true;
            var log = new List<string>();
            try
            {
                Progress("Removing the window app version", 0.05f);
                RemoveWindowApp(log);

                Progress("Player settings", 0.1f);
                PlayerSettingsForQuest(log);

                Progress("Meta project settings", 0.15f);
                MetaProjectConfig(log);

                int i = 0;
                foreach (string path in Blocks)
                {
                    Progress("Adding building block: " + System.IO.Path.GetFileNameWithoutExtension(path), 0.35f + 0.08f * i++);
                    await InstallBlock(path, log);
                }

                Progress("OVR Manager", 0.85f);
                ConfigureRig(log);

                Progress("Harley", 0.92f);
                RemoveOldMrHarley();
                CompanionBuilder.CreateMixedReality(false);
                log.Add("Harley, the pointer, the menu and the wrist button are in the scene.");

                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                log.Add("Stopped: " + e.Message + "\nRun Harley > Set Up Mixed Reality again.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _running = false;
            }

            var config = AssetDatabase.LoadAssetAtPath<CompanionConfig>("Assets/Harley/CompanionConfig.asset");
            string keys = config == null || !config.HasBrainKey ? "\n• Paste your AI key: Harley > Select Config (Gemini is free: aistudio.google.com)." : "";
            string wit = config == null || string.IsNullOrWhiteSpace(config.witToken) ? "\n• To talk out loud: make a free app on wit.ai and paste its Client Access Token into Wit Token (Harley > Select Config)." : "";
            EditorUtility.DisplayDialog("Harley", "Done.\n\n" + string.Join("\n", log.Select(l => "• " + l)) + keys + wit +
                "\n\nLast step: in the Project Setup Tool window that opens now, press Fix All (Android tab). Then File > Build And Run with the headset connected.", "OK");
            OpenProjectSetupTool();
        }

        static void Progress(string what, float p) => EditorUtility.DisplayProgressBar("Harley mixed reality setup", what, p);

        // ---------- steps ----------

        static void RemoveWindowApp(List<string> log)
        {
            const string marker = "Assets/Harley/Editor/WindowAppBuild.txt";
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(marker) != null || System.IO.File.Exists(marker))
            {
                AssetDatabase.DeleteAsset(marker);
                log.Add("Window app build settings removed.");
            }

            var roots = new HashSet<GameObject>();
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                if (mb is PanelControls || mb is PanelMotion || mb is PanelInput || mb is ScreenEyes) roots.Add(mb.transform.root.gameObject);
            }
            foreach (var go in roots) Undo.DestroyObjectImmediate(go);
            if (roots.Count > 0) log.Add("Old window-app Harley removed.");
        }

        static void RemoveOldMrHarley()
        {
            foreach (var b in Object.FindObjectsByType<CompanionBrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (b != null) Undo.DestroyObjectImmediate(b.transform.root.gameObject);
            foreach (var t in new[] { typeof(HarleyPointer), typeof(HarleyMenu), typeof(WristButton) })
            foreach (var o in Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (o is Component c && c != null) Undo.DestroyObjectImmediate(c.gameObject);
        }

        static void PlayerSettingsForQuest(List<string> log)
        {
            PlayerSettings.allowUnsafeCode = true;
            PlayerSettings.Android.forceInternetPermission = true;
            if ((int)PlayerSettings.Android.minSdkVersion < 32) PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            log.Add("Android: internet on, API 32+, IL2CPP, ARM64.");
        }

        static void MetaProjectConfig(List<string> log)
        {
            Type t = FindType("OVRProjectConfig");
            if (t == null) { log.Add("Meta project config not found (skipped)."); return; }
            object cfg = t.GetProperty("CachedProjectConfig", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (cfg == null) { log.Add("Meta project config not found (skipped)."); return; }

            SetEnum(cfg, "handTrackingSupport", "ControllersAndHands");
            SetField(cfg, "requiresSystemKeyboard", true);
            SetField(cfg, "insightPassthroughEnabled", true);
            SetField(cfg, "isPassthroughCameraAccessEnabled", true);
            SetEnum(cfg, "sceneSupport", "Supported");
            SetEnum(cfg, "boundaryVisibilitySupport", "Supported"); // lets Harley turn the boundary off in passthrough
            SetField(cfg, "focusAware", true);

            t.GetMethod("CommitProjectConfig", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { cfg });
            log.Add("Meta: hands + controllers, passthrough, camera access, room scan, system keyboard.");
        }

        static void OpenProjectSetupTool()
        {
            // The Meta menu moved between SDK versions: open the settings page directly (no red "menu not found" error)
            SettingsService.OpenProjectSettings("Project/Meta XR");
        }

        static async Task FixAll(List<string> log)
        {
            Type t = FindType("OVRProjectSetup");
            MethodInfo fix = t?.GetMethod("FixAllAsync", BindingFlags.Public | BindingFlags.Static);
            if (fix == null) { log?.Add("Meta Project Setup Tool not found: run Meta > Tools > Project Setup Tool > Fix All."); return; }
            try
            {
                if (fix.Invoke(null, new object[] { BuildTargetGroup.Android }) is Task task) await task;
                log?.Add("Meta Project Setup Tool: fixes applied.");
            }
            catch (Exception e)
            {
                log?.Add("Project Setup Tool: " + (e.InnerException ?? e).Message + " (open Meta > Tools > Project Setup Tool and press Fix All).");
            }
        }

        static async Task InstallBlock(string path, List<string> log)
        {
            var block = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (block == null) { log.Add($"Block {name} not found (package version?)."); return; }

            string id = block.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block) as string;
            if (!string.IsNullOrEmpty(id) && BlockInScene(id)) return; // already there

            MethodInfo install = block.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "InstallWithDependencies" && m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(GameObject));
            if (install == null) { log.Add($"Can't add {name} automatically: add it in Meta > Tools > Building Blocks."); return; }

            try
            {
                if (install.Invoke(block, new object[] { null }) is Task task)
                {
                    if (await Task.WhenAny(task, Task.Delay(60000)) != task) { log.Add($"{name}: took too long, add it in Meta > Tools > Building Blocks."); return; }
                    await task;
                }
                string blockName = block.GetType().GetProperty("BlockName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(block) as string;
                log.Add("Added " + (blockName ?? name) + ".");
            }
            catch (Exception e)
            {
                string msg = (e.InnerException ?? e).Message;
                if (msg.Contains("already present")) return;
                log.Add($"{name}: {msg}");
            }
        }

        static bool BlockInScene(string id)
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != "BuildingBlock") continue;
                if (mb.GetType().GetProperty("BlockId")?.GetValue(mb) as string == id) return true;
            }
            return false;
        }

        static void ConfigureRig(List<string> log)
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                if (n == "OVRManager")
                {
                    var so = new SerializedObject(mb);
                    SetBool(so, "isInsightPassthroughEnabled", true);
                    SetBool(so, "requestScenePermissionOnStartup", true);
                    SetBool(so, "requestPassthroughCameraAccessPermissionOnStartup", true);
                    SetBool(so, "requestRecordAudioPermissionOnStartup", true);
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(mb);
                }
                else if (n == "EffectMesh")
                {
                    // Invisible walls and furniture: only for Harley to bounce off and sit on.
                    var so = new SerializedObject(mb);
                    SetBool(so, "Colliders", true);
                    SetBool(so, "hideMesh", true);
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(mb);
                }
            }

            // A camera must clear to transparent so passthrough shows through.
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (cam.name == "Main Camera" && cam.transform.parent == null && FindType("OVRCameraRig") != null &&
                    Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 1)
                {
                    Undo.DestroyObjectImmediate(cam.gameObject); // the old flat camera
                    continue;
                }
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            log.Add("Passthrough background, permissions (camera, room, microphone), invisible room colliders.");
        }

        // ---------- helpers ----------

        static void SetBool(SerializedObject so, string field, bool v)
        {
            var p = so.FindProperty(field);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v;
        }

        static void SetField(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(target, value);
        }

        static void SetEnum(object target, string field, string value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null || !f.FieldType.IsEnum) return;
            try { f.SetValue(target, Enum.Parse(f.FieldType, value)); } catch { }
        }

        static Type FindType(string fullOrShortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullOrShortName, false);
                if (t != null) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in types)
                    if (t.Name == fullOrShortName) return t;
            }
            return null;
        }
    }
}
