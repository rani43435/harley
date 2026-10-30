using UnityEditor;
using UnityEngine;

namespace Harley.EditorTools
{
    /// <summary>The 360 views (Resources/Views) import big and sharp, wrapping around sideways.</summary>
    public class HarleyViewImport : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            string path = assetPath.Replace('\\', '/');
            if (path.Contains("/Harley/Resources/CuteHarley/"))
            {
                var t = (TextureImporter)assetImporter;
                t.maxTextureSize = 2048;
                t.mipmapEnabled = true;
                t.anisoLevel = 4;
                if (path.EndsWith("normal.png")) t.textureType = TextureImporterType.NormalMap;
                return;
            }
            if (path.Contains("/Harley/Resources/MenuIcons/"))
            {
                var t = (TextureImporter)assetImporter;
                t.textureType = TextureImporterType.Sprite;
                t.alphaIsTransparency = true;
                t.mipmapEnabled = true;
                t.maxTextureSize = 128;
                return;
            }
            if (!assetPath.Replace('\\', '/').Contains("/Harley/Resources/Views/")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = true;
            ti.maxTextureSize = 4096;
            ti.wrapModeU = TextureWrapMode.Repeat;
            ti.wrapModeV = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.npotScale = TextureImporterNPOTScale.None;
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 4096;
            android.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(android);
        }
    }
}
