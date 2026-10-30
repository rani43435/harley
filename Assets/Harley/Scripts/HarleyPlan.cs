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
        /// <summary>How much of today's allowance is used, 0-100 (like Claude's usage meter).</summary>
        public int Percent { get; private set; }
        float _resetsAt;
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
                Percent = d.TryGetValue("percent", out var p) ? System.Convert.ToInt32(p) : 0;
                float resets = d.TryGetValue("resets_in", out var r) ? System.Convert.ToSingle(r) : 0f;
                _resetsAt = Time.unscaledTime + resets;
                Known = true;
            }
        }

        /// <summary>When today's allowance comes back, e.g. "4h 20m".</summary>
        public string ResetsIn
        {
            get
            {
                int s = Mathf.Max(0, Mathf.RoundToInt(_resetsAt - Time.unscaledTime));
                return s >= 3600 ? $"{s / 3600}h {s % 3600 / 60}m" : $"{Mathf.Max(1, s / 60)}m";
            }
        }

        /// <summary>For the menu: a little usage bar, like Claude's: "Free ▰▰▰▱▱▱▱▱▱▱ 32% · resets in 4h 20m".</summary>
        public string Label
        {
            get
            {
                if (!HarleyServer.On(config)) return "";
                if (!Known) return "…";
                int filled = Mathf.Clamp(Mathf.RoundToInt(Percent / 10f), 0, 10);
                string bar = new string('▰', filled) + new string('▱', 10 - filled);
                string color = Percent >= 90 ? "#F87171" : Percent >= 70 ? "#FBBF24" : "#C4B1FB";
                return $"{(Plan == "pro" ? "Pro" : "Free")} <color={color}>{bar}</color> {Percent}% · resets in {ResetsIn}";
            }
        }
    }
}
