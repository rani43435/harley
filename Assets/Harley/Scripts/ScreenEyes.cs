using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Window app version: Harley sees everything you see in the headset
    /// (YouTube, the browser, other app windows, the room around them), using
    /// Android screen capture (MediaProjection) through the HarleyCapture plugin
    /// in Plugins/Android.
    ///
    /// The first time, Quest asks you to allow screen capture. While it runs,
    /// a notification says Harley can see your screen. Only one picture is sent
    /// to Claude per message, when you talk to Harley or it glances on its own.
    /// </summary>
    public class ScreenEyes : CompanionSight
    {
        [Tooltip("Ask for screen sharing as soon as the app opens.")]
        public bool startOnLaunch = true;

        const string PluginClass = "com.harley.capture.HarleyCapture";

        public override string Kind => "screen";

        public override bool CanSee
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return IsSharing;
#else
                return editorTestImage != null;
#endif
            }
        }

        /// <summary>True while screen capture is running.</summary>
        public bool IsSharing
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { using (var c = new AndroidJavaClass(PluginClass)) return c.CallStatic<bool>("isRunning"); }
                catch { return false; }
#else
                return editorTestImage != null;
#endif
            }
        }

        /// <summary>Why the last attempt failed ("declined", ...), or empty.</summary>
        public string LastError
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { using (var c = new AndroidJavaClass(PluginClass)) return c.CallStatic<string>("getLastError") ?? ""; }
                catch (System.Exception e) { return e.Message; }
#else
                return "";
#endif
            }
        }

        void Reset() { maxSize = 1024; jpegQuality = 80; }

        void Start()
        {
            if (startOnLaunch) StartSharing();
        }

        /// <summary>Shows Quest's "allow screen capture" prompt and starts sharing.</summary>
        public void StartSharing()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsSharing) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(PluginClass))
                    c.CallStatic("requestStart", activity);
            }
            catch (System.Exception e) { Debug.LogWarning("[Harley] Screen sharing failed to start: " + e.Message); }
#else
            Debug.Log("[Harley] Screen sharing only runs on the headset. In the Editor, Harley uses Editor Test Image.");
#endif
        }

        public void StopSharing()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var c = new AndroidJavaClass(PluginClass))
                    c.CallStatic("stop", activity);
            }
            catch (System.Exception e) { Debug.LogWarning("[Harley] " + e.Message); }
#endif
        }

        public override string CaptureJpegBase64()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var c = new AndroidJavaClass(PluginClass))
                    return c.CallStatic<string>("grabJpegBase64", maxSize, jpegQuality);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Harley] Screen grab failed: " + e.Message);
                return null;
            }
#else
            return EncodeTexture(editorTestImage);
#endif
        }
    }
}
