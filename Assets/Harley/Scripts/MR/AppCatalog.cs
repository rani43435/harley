using UnityEngine;

namespace Harley
{
    /// <summary>
    /// The "apps" on Harley's launcher. Each one opens its website in an app-style window
    /// (no address bar, its own name and tile), with the browser identity that site works best with.
    /// The tiles are Harley's own simple designs, not the services' logos.
    /// </summary>
    public static class AppCatalog
    {
        public enum Identity { Phone = 0, Desktop = 1, Tv = 2 }

        public class App
        {
            public string name, url, letter, note;
            public Color color;
            public Identity identity;
            /// <summary>Video / music is DRM-protected: it will show black or refuse to play inside Harley.</summary>
            public bool protectedMedia;
        }

        public static readonly App[] All =
        {
            new App { name = "YouTube",   url = "https://www.youtube.com/tv",  letter = "Y", color = C(0.86f, 0.25f, 0.27f), identity = Identity.Tv },
            new App { name = "Instagram", url = "https://www.instagram.com/",  letter = "I", color = C(0.78f, 0.32f, 0.62f), identity = Identity.Phone },
            new App { name = "HBO Max",   url = "https://play.hbomax.com/",    letter = "H", color = C(0.36f, 0.28f, 0.78f), identity = Identity.Desktop, protectedMedia = true,
                      note = "Shows and films are protected, so they won't play here. Use the HBO Max app for watching." },
            new App { name = "Spotify",   url = "https://open.spotify.com/",   letter = "S", color = C(0.20f, 0.66f, 0.42f), identity = Identity.Desktop, protectedMedia = true,
                      note = "Spotify's player is protected and may not play here." },
            new App { name = "Netflix",   url = "https://www.netflix.com/",    letter = "N", color = C(0.62f, 0.14f, 0.18f), identity = Identity.Desktop, protectedMedia = true,
                      note = "Netflix is protected, so videos won't play here." },
            new App { name = "Twitch",    url = "https://m.twitch.tv/",        letter = "T", color = C(0.47f, 0.32f, 0.86f), identity = Identity.Phone },
            new App { name = "WhatsApp",  url = "https://web.whatsapp.com/",   letter = "W", color = C(0.22f, 0.62f, 0.47f), identity = Identity.Desktop },
            new App { name = "Google",    url = "https://www.google.com/",     letter = "G", color = C(0.27f, 0.47f, 0.86f), identity = Identity.Phone },
        };

        static Color C(float r, float g, float b) => new Color(r, g, b, 1f);
    }
}
