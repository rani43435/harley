using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Watch together: while a video plays in a window, Harley follows along like a friend on the couch.
    /// She knows what it is (title, channel, how far in, the captions when they're on) and every so often
    /// takes a look at the picture; she only says something when there's something worth saying, and she
    /// remembers what she already said so it feels like one continuous watch, not separate glances.
    /// </summary>
    public class HarleyWatch : MonoBehaviour
    {
        public static HarleyWatch Instance { get; private set; }

        public CompanionBrain brain;
        public bool On { get; private set; }
        [Tooltip("Seconds between looks at the video.")]
        public float interval = 40f;

        string _title = "";
        readonly List<string> _notes = new List<string>(); // what she said while watching
        float _next;
        bool _asked;

        void Awake()
        {
            Instance = this;
            if (!brain) brain = GetComponent<CompanionBrain>();
        }

        public void Set(bool on)
        {
            if (on == On) return;
            On = on;
            _notes.Clear();
            _title = "";
            _next = Time.time + 6f;
            var w = Window();
            string what = w != null && w.Media != null && w.Media.title.Length > 0 ? " (\"" + w.Media.title + "\")" : "";
            brain?.LessonEvent(on
                ? "[event] The player wants to watch the video" + what + " together with you. Say you're in, in one short excited sentence, then mostly watch quietly."
                : "[event] The player stopped watching together. One short line, like a friend getting up from the couch.");
        }

        public void Toggle() => Set(!On);

        /// <summary>The window with the video: the one you look at, else any that plays.</summary>
        WebPanel Window()
        {
            var cam = Camera.main;
            WebPanel best = null; float bestScore = -2f;
            foreach (var w in WebPanel.Windows)
            {
                if (w == null || w.Minimized) continue;
                w.WantMedia();
                float score = cam != null ? Vector3.Dot(cam.transform.forward, (w.transform.position - cam.transform.position).normalized) : 0f;
                if (w.Media != null && w.Media.playing) score += 1f;
                if (score > bestScore) { bestScore = score; best = w; }
            }
            return best;
        }

        void Update()
        {
            if (!On || brain == null) return;
            var w = Window();
            if (w == null) return;

            // Remember what she said about the video
            if (_asked && !string.IsNullOrWhiteSpace(brain.LastSay) && (_notes.Count == 0 || _notes[_notes.Count - 1] != brain.LastSay))
            {
                _notes.Add(brain.LastSay);
                while (_notes.Count > 5) _notes.RemoveAt(0);
                _asked = false;
            }

            var m = w.Media;
            if (m != null && m.title.Length > 0 && m.title != _title)
            {
                bool first = _title.Length == 0;
                _title = m.title;
                _notes.Clear();
                if (!first) { _next = Time.time + 8f; return; } // a new video: let it start
            }
            if (Time.time < _next) return;
            if (m == null || !m.playing) { _next = Time.time + 5f; return; } // paused: wait
            if (brain.voice != null && (brain.voice.IsSpeaking || brain.voice.IsListening)) { _next = Time.time + 4f; return; }

            _next = Time.time + interval;
            _asked = brain.WatchLook(Describe(m));
        }

        string Describe(WebPanel.MediaInfo m)
        {
            string time = m.duration > 0 ? $" ({Clock(m.time)} of {Clock(m.duration)})" : "";
            string s = "[event optional] You're watching a video together with the player, side by side: \"" + m.title + "\"" +
                       (m.artist.Length > 0 ? " by " + m.artist : "") + time + ". The picture is the video right now.";
            if (m.captions.Length > 0) s += " What's being said: \"" + m.captions + "\".";
            if (_notes.Count > 0) s += " What you already said while watching: " + string.Join(" / ", _notes) + ".";
            s += " React like a friend on the couch: one short, natural sentence only if something is funny, surprising, beautiful or worth a comment " +
                 "(don't repeat yourself, don't describe the picture, don't ask a question every time). If nothing's worth it, return an empty say.";
            return s;
        }

        static string Clock(int s) => s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

        /// <summary>For the prompt.</summary>
        public string DescribeForPrompt()
        {
            if (!On) return "";
            return "\nWATCHING TOGETHER: you and the player are watching a video" + (_title.Length > 0 ? " (\"" + _title + "\")" : "") +
                   ". Keep comments rare and short; when they talk to you about it, answer like you've been watching the whole time.";
        }
    }
}
