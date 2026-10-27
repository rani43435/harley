using System;
using System.Reflection;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Mixed reality version: Harley sees through the Quest passthrough camera
    /// (MRUK PassthroughCameraAccess). The camera sees the real room only, not
    /// virtual content.
    ///
    /// The camera component is used through reflection on purpose, so this
    /// script compiles whatever MRUK version (or none) is installed.
    /// </summary>
    public class PassthroughEyes : CompanionSight
    {
        [Tooltip("Drag the PassthroughCameraAccess component here (Meta MRUK). The builder finds it for you.")]
        public MonoBehaviour passthroughCameraAccess;

        PropertyInfo _isPlaying;
        MethodInfo _getTexture;
        Type _cachedType;

        public override string Kind => "camera";
        public override bool CanSee => SourceTexture() != null;

        void Reset() { maxSize = 512; jpegQuality = 70; }

        protected Texture SourceTexture()
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

        public override string CaptureJpegBase64() => EncodeTexture(SourceTexture());
    }
}
