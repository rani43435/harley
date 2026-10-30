using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Free or Pro, as the Harley server sees this headset (only when a server is set in the config).
    /// Free: Groq with a small daily allowance. Pro: Claude Haiku, big allowance.
    /// </summary>
    public class HarleyPlan : MonoBehaviour
    {
        public static HarleyPlan Instance { get; private set; }
        public static bool IsPro => Instance != null && Instance.Plan == "pro";

        public CompanionConfig config;
        public string Plan { get; private set; } = "free";
        public bool Known { get; private set; }
        readonly Dictionary<string, int> _used = new Dictionary<string, int>(), _limits = new Dictionary<string, int>();
        float _next;

        void Awake() { Instance = this; }

        void Update()
        {
            if (!HarleyServer.On(config) || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 120f;
            StartCoroutine(Refresh());
        }

        /// <summary>Ask the server again soon (after an answer, after a purchase).</summary>
        public void RefreshSoon(float seconds = 2f) => _next = Mathf.Min(_next, Time.unscaledTime + seconds);

        IEnumerator Refresh()
        {
            using (var req = UnityWebRequest.Get(config.serverUrl.Trim().TrimEnd('/') + "/me"))
            {
                HarleyServer.AuthGroq(req, config); // same app key + headset id
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;
                if (!(MiniJson.Parse(req.downloadHandler.text) is Dictionary<string, object> d)) yield break;
                Plan = d.Str("plan") ?? "free";
                Copy(d.ObjAt("used"), _used);
                Copy(d.ObjAt("limits"), _limits);
                Known = true;
            }
        }

        static void Copy(Dictionary<string, object> src, Dictionary<string, int> dst)
        {
            dst.Clear();
            if (src == null) return;
            foreach (var kv in src) dst[kv.Key] = System.Convert.ToInt32(kv.Value);
        }

        /// <summary>For the menu: "Pro" or "Free · 12/30 answers today".</summary>
        public string Label
        {
            get
            {
                if (!HarleyServer.On(config)) return "";
                if (!Known) return "…";
                if (Plan == "pro") return "Pro";
                _used.TryGetValue("chat", out int u); _limits.TryGetValue("chat", out int l);
                return $"Free · {u}/{l} answers today";
            }
        }
    }
}
