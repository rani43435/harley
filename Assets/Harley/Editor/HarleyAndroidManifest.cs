#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace Harley.EditorTools
{
    /// <summary>
    /// Window app builds only: adds what screen sharing needs to the generated
    /// AndroidManifest.xml (foreground service + permission activity + permissions)
    /// and gives Harley's window a default size on Quest.
    ///
    /// It runs when Assets/Harley/Editor/WindowAppBuild.txt exists. The menu
    /// "Harley > Create Window App Scene" creates that file for you.
    /// </summary>
    public class HarleyAndroidManifest : IPostGenerateGradleAndroidProject
    {
        public const string MarkerPath = "Assets/Harley/Editor/WindowAppBuild.txt";
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 10;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (!File.Exists(MarkerPath)) return;

            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath)) { Debug.LogWarning("[Harley] AndroidManifest.xml not found at " + manifestPath); return; }

            var doc = new XmlDocument();
            doc.Load(manifestPath);
            XmlElement manifest = doc.DocumentElement;
            if (manifest == null) return;
            var app = manifest["application"];
            if (app == null) { app = doc.CreateElement("application"); manifest.AppendChild(app); }

            AddPermission(doc, manifest, "android.permission.FOREGROUND_SERVICE");
            AddPermission(doc, manifest, "android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION");
            AddPermission(doc, manifest, "android.permission.POST_NOTIFICATIONS");

            if (!HasChild(app, "activity", "com.harley.capture.HarleyCapturePermissionActivity"))
            {
                var act = doc.CreateElement("activity");
                SetAttr(doc, act, "name", "com.harley.capture.HarleyCapturePermissionActivity");
                SetAttr(doc, act, "exported", "false");
                SetAttr(doc, act, "excludeFromRecents", "true");
                SetAttr(doc, act, "theme", "@android:style/Theme.Translucent.NoTitleBar");
                app.AppendChild(act);
            }

            if (!HasChild(app, "service", "com.harley.capture.HarleyCaptureService"))
            {
                var svc = doc.CreateElement("service");
                SetAttr(doc, svc, "name", "com.harley.capture.HarleyCaptureService");
                SetAttr(doc, svc, "exported", "false");
                SetAttr(doc, svc, "foregroundServiceType", "mediaProjection");
                app.AppendChild(svc);
            }

            // Default window size on Quest (Harley is a small, tall window).
            foreach (XmlNode node in app.ChildNodes)
            {
                if (!(node is XmlElement activity) || activity.Name != "activity" || !IsLauncher(activity)) continue;
                // A fixed orientation makes Quest ignore the window size: let the window decide.
                if (activity.HasAttribute("screenOrientation", AndroidNs)) activity.RemoveAttribute("screenOrientation", AndroidNs);
                SetAttr(doc, activity, "resizeableActivity", "true");
                if (activity["layout"] == null)
                {
                    var layout = doc.CreateElement("layout");
                    SetAttr(doc, layout, "defaultWidth", "560dp");
                    SetAttr(doc, layout, "defaultHeight", "720dp");
                    activity.AppendChild(layout);
                }
            }

            if (doc.OuterXml.Contains("com.oculus.intent.category.VR"))
                Debug.LogWarning("[Harley] This build is an immersive VR app. For the window app, turn off XR for Android " +
                                 "(Project Settings > XR Plug-in Management > Android).");

            var settings = new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true };
            using (var w = XmlWriter.Create(manifestPath, settings)) doc.Save(w);
            Debug.Log("[Harley] Window app manifest ready (screen sharing + window size).");
        }

        static bool IsLauncher(XmlElement activity)
        {
            foreach (XmlNode f in activity.GetElementsByTagName("category"))
                if (f is XmlElement e && e.GetAttribute("name", AndroidNs) == "android.intent.category.LAUNCHER") return true;
            return false;
        }

        static bool HasChild(XmlElement parent, string tag, string name)
        {
            foreach (XmlNode n in parent.ChildNodes)
                if (n is XmlElement e && e.Name == tag && e.GetAttribute("name", AndroidNs) == name) return true;
            return false;
        }

        static void AddPermission(XmlDocument doc, XmlElement manifest, string permission)
        {
            foreach (XmlNode n in manifest.ChildNodes)
                if (n is XmlElement e && e.Name == "uses-permission" && e.GetAttribute("name", AndroidNs) == permission) return;
            var p = doc.CreateElement("uses-permission");
            SetAttr(doc, p, "name", permission);
            manifest.InsertBefore(p, manifest["application"]);
        }

        static void SetAttr(XmlDocument doc, XmlElement e, string name, string value)
        {
            if (e.HasAttribute(name, AndroidNs)) { e.SetAttribute(name, AndroidNs, value); return; }
            var a = doc.CreateAttribute("android", name, AndroidNs);
            a.Value = value;
            e.Attributes.Append(a);
        }
    }
}
#endif
