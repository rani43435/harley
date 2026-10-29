using System;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Lets Harley run the screens for you: "open YouTube", "put on some lofi on YouTube",
    /// "search Google for...", "open wikipedia.org", "close Instagram", "close everything".
    /// target formats (from the brain): "YouTube", "YouTube: lofi music", "Google: weather Münster",
    /// "wikipedia.org", a full URL, or anything else = a Google search.
    /// </summary>
    public static class HarleyScreens
    {
        /// <summary>Opens what target asks for. Returns a short description for logs, or null if nothing was opened.</summary>
        public static WebPanel Open(string target, Transform head)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            target = target.Trim();

            string appName = target, query = "";
            int colon = target.IndexOf(':');
            if (colon > 0 && !target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                appName = target.Substring(0, colon).Trim();
                query = target.Substring(colon + 1).Trim();
            }

            AppCatalog.App app = Find(appName);
            if (app != null)
            {
                var w = WebPanel.Create(app, head);
                if (query.Length > 0) w.Load(SearchUrl(app, query));
                return w;
            }

            // A web address
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return WebPanel.Create(target, head);
            if (target.IndexOf(' ') < 0 && target.Contains("."))
                return WebPanel.Create("https://" + target, head);

            // Anything else: search for it
            return WebPanel.Create("https://www.google.com/search?q=" + Uri.EscapeDataString(target), head);
        }

        /// <summary>Closes the named app (or every window with "all" / empty). Returns how many closed.</summary>
        public static int Close(string target)
        {
            int n = 0;
            bool all = string.IsNullOrWhiteSpace(target) || Words(target, "all", "everything", "alles", "كل");
            var app = all ? null : Find(target);
            for (int i = WebPanel.Windows.Count - 1; i >= 0; i--)
            {
                var w = WebPanel.Windows[i];
                if (w == null) continue;
                bool match = all
                    || (app != null && w.App == app)
                    || (app == null && (w.Title ?? "").IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match) { w.Close(); n++; }
            }
            // Nothing matched a name: close the one you're looking at
            if (n == 0 && !all && WebPanel.Windows.Count > 0 && Camera.main != null)
            {
                WebPanel best = null; float bestAng = 35f;
                foreach (var w in WebPanel.Windows)
                {
                    if (w == null) continue;
                    float a = Vector3.Angle(Camera.main.transform.forward, w.transform.position - Camera.main.transform.position);
                    if (a < bestAng) { bestAng = a; best = w; }
                }
                if (best != null) { best.Close(); n = 1; }
            }
            return n;
        }

        /// <summary>For the prompt: the apps Harley can open and what's open now.</summary>
        public static string DescribeForPrompt()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < AppCatalog.All.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(AppCatalog.All[i].name);
                if (AppCatalog.All[i].protectedMedia) sb.Append(" (its videos/music can't play here)");
            }
            sb.Append(". Open now: ");
            int open = 0;
            foreach (var w in WebPanel.Windows)
            {
                if (w == null) continue;
                if (open++ > 0) sb.Append(", ");
                sb.Append(w.App != null ? w.App.name : (string.IsNullOrEmpty(w.Title) ? "a browser window" : w.Title));
            }
            if (open == 0) sb.Append("nothing");
            return sb.ToString();
        }

        static AppCatalog.App Find(string name)
        {
            string n = Norm(name);
            if (n.Length == 0) return null;
            foreach (var a in AppCatalog.All)
            {
                string an = Norm(a.name);
                if (n == an || n.Contains(an) || (an.StartsWith(n) && n.Length >= 3)) return a;
            }
            // A few spoken / other-language names
            if (Words(name, "يوتيوب", "يوتوب", "yt")) return Get("YouTube");
            if (Words(name, "انستا", "انستغرام", "انستجرام", "insta")) return Get("Instagram");
            if (Words(name, "سبوتيفاي", "سبوتي")) return Get("Spotify");
            if (Words(name, "نتفلكس", "نتفليكس")) return Get("Netflix");
            if (Words(name, "واتساب", "واتس")) return Get("WhatsApp");
            if (Words(name, "map", "خرائط", "خريطة", "ماب", "karte")) return Get("Maps");
            if (Words(name, "جوجل", "غوغل", "google")) return Get("Google");
            if (Words(name, "hbo", "اتش بي او")) return Get("HBO Max");
            if (Words(name, "كلاود", "claude")) return Get("Claude");
            return null;
        }

        static AppCatalog.App Get(string name) => Array.Find(AppCatalog.All, a => a.name == name);

        static string SearchUrl(AppCatalog.App app, string q)
        {
            string e = Uri.EscapeDataString(q);
            switch (app.name)
            {
                case "YouTube": return "https://www.youtube.com/tv#/search?q=" + e;
                case "Google": return "https://www.google.com/search?q=" + e;
                case "Maps": return "https://www.google.com/maps/search/" + e;
                case "Spotify": return "https://open.spotify.com/search/" + e;
                case "Twitch": return "https://m.twitch.tv/search?term=" + e;
                case "Instagram": return "https://www.instagram.com/explore/search/keyword/?q=" + e;
                default: return app.url;
            }
        }

        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        static bool Words(string s, params string[] words)
        {
            string l = (s ?? "").ToLowerInvariant();
            foreach (var w in words) if (l.Contains(w)) return true;
            return false;
        }
    }
}
