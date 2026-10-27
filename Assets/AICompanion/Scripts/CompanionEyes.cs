using System;
using System.Reflection;
using UnityEngine;

namespace AICompanion
{
    /// <summary>
    /// Lets the companion "see what you see": grabs a frame from the Quest
    /// passthrough camera (MRUK PassthroughCameraAccess) and turns it into a
    /// small JPEG for Claude.
    ///
    /// The camera component is used through reflection on purpose, so this
    /// script compiles whatever MRUK version (or none) is installed.
    /// In the Editor, set Editor Test Image to try vision without a headset.
    /// </summary>
    public class CompanionEyes : MonoBehaviour
    {
        [Tooltip("Drag the PassthroughCameraAccess component here (Meta MRUK v81+). The builder finds it for you.")]
        public MonoBehaviour passthroughCameraAccess;

        [Tooltip("Used when there is no headset camera (Editor / Link). Any photo of a room works.")]
        public Texture editorTestImage;

        [Tooltip("Longest edge sent to Claude. 512 is cheap, fast and enough to recognise things.")]
        public int maxSize = 512;
        [Range(30, 95)] public int jpegQuality = 70;

        RenderTexture _rt;
        Texture2D _readback;
        PropertyInfo _isPlaying;
        MethodInfo _getTexture;
        Type _cachedType;

        public bool CanSee => SourceTexture() != null;

        Texture SourceTexture()
        {
            if (passthroughCameraAccess != null && passthroughCameraAccess.isActiveAndEnabled)
            {
                var t = passthroughCameraAccess.GetType();
                if (t != _cachedType)
                {
                    _cachedType = t;
                    _isPlaying = t.GetProperty("IsPlaying", BindingFlags.Public | BindingFlags.Instance);
                    _getTexture = t.GetMethod("GetTexture", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }

                bool playing = true;
                if (_isPlaying != null)
                {
                    try { playing = (bool)_isPlaying.GetValue(passthroughCameraAccess); }
                    catch { playing = false; }
                }

                if (playing && _getTexture != null)
                {
                    Texture tex = null;
                    try { tex = _getTexture.Invoke(passthroughCameraAccess, null) as Texture; }
                    catch { /* camera permission not granted yet */ }
                    if (tex != null) return tex;
                }
            }
            return editorTestImage;
        }

        /// <summary>Returns a base64 JPEG of the current view, or null if there is nothing to see.</summary>
        public string CaptureJpegBase64()
        {
            var src = SourceTexture();
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

        void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_readback != null) Destroy(_readback);
        }
    }
}
