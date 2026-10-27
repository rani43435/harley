using System;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// What Harley can see. Two kinds:
    ///  - PassthroughEyes: the headset's front camera (mixed reality version)
    ///  - ScreenEyes: everything you see in the headset, apps included (window app version)
    /// </summary>
    public abstract class CompanionSight : MonoBehaviour
    {
        [Tooltip("Longest edge of the picture sent to Claude. Bigger = can read smaller text, costs a bit more.")]
        public int maxSize = 768;
        [Range(30, 95)] public int jpegQuality = 75;
        [Tooltip("Used when there is no real picture (Editor). Any screenshot or room photo works.")]
        public Texture editorTestImage;

        /// <summary>"camera" or "screen", used in Harley's prompt.</summary>
        public abstract string Kind { get; }
        public abstract bool CanSee { get; }
        /// <summary>Base64 JPEG of what Harley sees right now, or null.</summary>
        public abstract string CaptureJpegBase64();

        RenderTexture _rt;
        Texture2D _readback;

        /// <summary>Downscales any texture and encodes it as base64 JPEG.</summary>
        protected string EncodeTexture(Texture src)
        {
            if (src == null || src.width <= 0 || src.height <= 0) return null;

            float scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(src.width, src.height));
            int w = Mathf.Max(16, Mathf.RoundToInt(src.width * scale));
            int h = Mathf.Max(16, Mathf.RoundToInt(src.height * scale));

            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                if (_readback != null) Destroy(_readback);
                _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                _readback = new Texture2D(w, h, TextureFormat.RGB24, false);
            }

            Graphics.Blit(src, _rt);
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _readback.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _readback.Apply(false);
            RenderTexture.active = prev;

            return Convert.ToBase64String(_readback.EncodeToJPG(jpegQuality));
        }

        protected virtual void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_readback != null) Destroy(_readback);
        }
    }
}
