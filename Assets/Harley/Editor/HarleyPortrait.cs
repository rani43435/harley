using System.IO;
using UnityEditor;
using UnityEngine;

namespace Harley.EditorTools
{
    /// <summary>Harley > Take Portrait: renders Harley from a few angles into HarleyPortrait_*.png next to the project.</summary>
    public static class HarleyPortrait
    {
        [MenuItem("Harley/Take Portrait", false, 60)]
        public static void Take()
        {
            var brain = Object.FindAnyObjectByType<CompanionBrain>();
            if (brain == null) { EditorUtility.DisplayDialog("Harley", "No Harley in the scene.", "OK"); return; }
            Transform h = brain.transform;
            var rig = h.GetComponentInChildren<RobotRig>();
            if (rig != null) rig.PoseNow();

            var go = new GameObject("Portrait Camera");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.1f, 0.18f, 1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            var lightGO = new GameObject("Portrait Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;

            var rt = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var tex = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
            Vector3 center = h.position + Vector3.down * 0.1f;
            float[] angles = { 0f, 35f, 90f };
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (float a in angles)
            {
                Vector3 fwd = h.rotation * (Quaternion.Euler(0, a, 0) * Vector3.forward);
                cam.transform.position = center + fwd * 1.15f + Vector3.up * 0.05f;
                cam.transform.LookAt(center);
                lightGO.transform.rotation = Quaternion.LookRotation(-fwd + Vector3.down * 0.6f + h.right * 0.4f);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(dir, $"HarleyPortrait_{(int)a}.png"), tex.EncodeToPNG());
            }
            cam.targetTexture = null;
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(lightGO);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log("[Harley] Portraits saved in " + dir);
        }
    }
}
