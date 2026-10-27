using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>
    /// Sharper text and web pages on Quest: renders the eyes at a higher resolution and
    /// smooths edges (MSAA). Runs by itself when the app starts.
    /// </summary>
    public static class HarleyQuality
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // 1.0 = the headset's default. Harley's scene is light, so it can afford ~1.35x more pixels per eye.
            XRSettings.eyeTextureResolutionScale = 1.35f;
            QualitySettings.antiAliasing = 4;
            var urp = GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.msaaSampleCount = 4;
                urp.renderScale = 1f;
            }
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
#endif
        }
    }
}
