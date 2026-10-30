using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Where Harley's AI calls go. With a server address in the config, every Groq / Gemini call goes through
    /// Harley's own server (Tools/harley-server): the real API keys live there, never in the app, and each
    /// headset gets a daily allowance. Without one, the app talks to Groq / Gemini directly with the keys
    /// in the config (fine for your own headset while developing).
    /// </summary>
    public static class HarleyServer
    {
        public static bool On(CompanionConfig c) => c != null && !string.IsNullOrWhiteSpace(c.serverUrl);
        static string Base(CompanionConfig c) => c.serverUrl.Trim().TrimEnd('/');

        public static string Groq(CompanionConfig c) => On(c) ? Base(c) + "/groq/" : "https://api.groq.com/openai/v1/";
        public static string Gemini(CompanionConfig c) => On(c) ? Base(c) + "/gemini/" : "https://generativelanguage.googleapis.com/v1beta/";

        public static string Claude(CompanionConfig c) => On(c) ? Base(c) + "/claude/messages" : "https://api.anthropic.com/v1/messages";

        public static void AuthClaude(UnityWebRequest req, CompanionConfig c)
        {
            if (On(c)) Identify(req, c);
            else req.SetRequestHeader("x-api-key", (c.apiKey ?? "").Trim());
            req.SetRequestHeader("anthropic-version", "2023-06-01");
        }

        public static void AuthGroq(UnityWebRequest req, CompanionConfig c)
        {
            if (On(c)) Identify(req, c);
            else req.SetRequestHeader("Authorization", "Bearer " + (c.groqApiKey ?? "").Trim());
        }

        public static void AuthGemini(UnityWebRequest req, CompanionConfig c)
        {
            if (On(c)) Identify(req, c);
            else req.SetRequestHeader("x-goog-api-key", (c.geminiApiKey ?? "").Trim());
        }

        static string _device;
        /// <summary>A stable, anonymous id for this headset (for the daily allowance).</summary>
        public static string Device
        {
            get
            {
                if (_device != null) return _device;
                _device = PlayerPrefs.GetString("harley.device", "");
                if (_device.Length < 16)
                {
                    _device = System.Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString("harley.device", _device);
                    PlayerPrefs.Save();
                }
                return _device;
            }
        }

        static void Identify(UnityWebRequest req, CompanionConfig c)
        {
            req.SetRequestHeader("x-harley-key", (c.serverAppKey ?? "").Trim());
            req.SetRequestHeader("x-harley-device", Device);
        }
    }
}
