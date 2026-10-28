using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley's eyes in mixed reality: the passthrough camera for your real room,
    /// and the browser windows (YouTube, web pages) floating in it.
    /// Whatever you are looking at is what Harley sees: look at a browser window and it
    /// sees the page; look at the room and it sees the room.
    /// </summary>
    public class MixedEyes : PassthroughEyes
    {
        public Transform head;
        [Tooltip("Longest edge of a browser picture (pages need more pixels to read text).")]
        public int pageMaxSize = 1024;
        [Tooltip("A window counts as looked at if your gaze is this close to it (degrees).")]
        public float lookAngle = 22f;

        string _kind = "camera", _note = "";
        int _cameraMaxSize = -1;

        public override string Kind => _kind;
        public override string ViewNote => _note;
        public override bool CanSee => base.CanSee || LookedAtPanel() != null;

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
        }

        /// <summary>The browser window the player is looking at, if any.</summary>
        public WebPanel LookedAtPanel()
        {
            if (head == null) { if (Camera.main == null) return null; head = Camera.main.transform; }
            WebPanel best = null;
            float bestAngle = lookAngle;
            foreach (var p in WebPanel.Windows)
            {
                if (p == null || p.PageTexture == null || p.Minimized) continue;
                if (p.Raycast(head.position, head.forward, out _)) return p;
                float a = Vector3.Angle(head.forward, p.transform.position - head.position);
                if (a < bestAngle) { bestAngle = a; best = p; }
            }
            return best;
        }

        public override string CaptureJpegBase64()
        {
            if (_cameraMaxSize < 0) _cameraMaxSize = maxSize;
            WebPanel panel = LookedAtPanel();
            if (panel != null)
            {
                _kind = "browser";
                string title = string.IsNullOrEmpty(panel.Title) ? "" : $"\"{panel.Title}\" ";
                _note = $"They are looking at a browser window floating in their room, showing {title}({panel.Url}).";
                maxSize = pageMaxSize;
                string jpg = EncodeTexture(panel.PageTexture, true);
                maxSize = _cameraMaxSize;
                return jpg;
            }

            _kind = "camera";
            int open = WebPanel.Windows.Count;
            if (HarleyWorld.Immersive) { _note = ""; return null; } // in Harley's world the real room isn't visible
            _note = open > 0 ? $"They also have {open} browser window{(open > 1 ? "s" : "")} open in the room, but they are not looking at it right now." : "";
            maxSize = _cameraMaxSize;
            return base.CaptureJpegBase64();
        }
    }
}
