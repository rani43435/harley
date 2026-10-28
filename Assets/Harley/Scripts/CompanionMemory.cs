using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Long-term memory: small facts about the player that Harley keeps between
    /// sessions (name, likes, plans...). Saved as JSON on the headset only, in
    /// Application.persistentDataPath. Nothing leaves the device except as part
    /// of the prompt sent to Claude.
    /// </summary>
    public class CompanionMemory : MonoBehaviour
    {
        public CompanionConfig config;
        public string fileName = "harley_memory.json";

        [Serializable]
        class Data
        {
            public List<string> facts = new List<string>();
            public string firstMet = "";
            public string lastVisit = "";
            public int visits;
            public List<string> recentChat = new List<string>();
        }

        Data _data = new Data();
        string _previousVisit = "";
        List<string> _lastSessionChat = new List<string>();
        string Path => System.IO.Path.Combine(Application.persistentDataPath, fileName);

        public IReadOnlyList<string> Facts => _data.facts;
        public int Visits => _data.visits;

        void Awake()
        {
            Load();
            _previousVisit = _data.lastVisit;
            _lastSessionChat = new List<string>(_data.recentChat);
            string now = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(_data.firstMet)) _data.firstMet = now;
            _data.visits++;
            _data.lastVisit = now;
            Save();
        }

        /// <summary>Adds a fact (deduplicated). Returns true if it was new.</summary>
        public bool Remember(string fact)
        {
            if (config != null && !config.longTermMemory) return false;
            if (string.IsNullOrWhiteSpace(fact)) return false;
            fact = fact.Trim();
            if (fact.Length > 160) fact = fact.Substring(0, 160);
            foreach (var f in _data.facts)
                if (string.Equals(f, fact, StringComparison.OrdinalIgnoreCase)) return false;
            _data.facts.Add(fact);
            int max = config != null ? Mathf.Max(5, config.maxFacts) : 40;
            while (_data.facts.Count > max) _data.facts.RemoveAt(0);
            Save();
            return true;
        }

        /// <summary>Keeps the last few lines of conversation, so next time Harley knows what you talked about.</summary>
        public void NoteExchange(string player, string harley)
        {
            if (config != null && !config.longTermMemory) return;
            if (string.IsNullOrWhiteSpace(player) || player.StartsWith("[", StringComparison.Ordinal)) return;
            _data.recentChat.Add("Player: " + Clip(player));
            if (!string.IsNullOrWhiteSpace(harley)) _data.recentChat.Add("You: " + Clip(harley));
            while (_data.recentChat.Count > 16) _data.recentChat.RemoveAt(0);
            Save();
        }

        static string Clip(string s)
        {
            s = s.Replace('\n', ' ').Trim();
            return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
        }

        [ContextMenu("Forget Everything")]
        public void ForgetAll()
        {
            _data.facts.Clear();
            _data.recentChat.Clear();
            _lastSessionChat.Clear();
            Save();
        }

        /// <summary>Text block for the system prompt.</summary>
        public string DescribeForPrompt()
        {
            var sb = new StringBuilder();
            sb.Append("This is visit number ").Append(_data.visits).Append(" with this player.");
            if (DateTime.TryParse(_data.firstMet, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var first) && _data.visits > 1)
                sb.Append(" You first met ").Append(Ago(first)).Append('.');
            if (DateTime.TryParse(_previousVisit, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var prev))
                sb.Append(" Their previous visit was ").Append(Ago(prev)).Append('.');

            if (config != null && !config.longTermMemory)
                return sb.ToString();

            if (_data.facts.Count == 0)
            {
                sb.Append("\nYou don't know anything about the player yet.");
            }
            else
            {
                sb.Append("\nWhat you remember about the player from earlier:");
                foreach (var f in _data.facts) sb.Append("\n- ").Append(f);
            }
            if (_lastSessionChat.Count > 0)
            {
                sb.Append("\nThe end of your conversation last time (you can pick it up naturally if it fits, don't repeat it):");
                foreach (var line in _lastSessionChat) sb.Append("\n  ").Append(line);
            }
            return sb.ToString();
        }

        static string Ago(DateTime then)
        {
            TimeSpan d = DateTime.Now - then;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} minutes ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} hours ago";
            if (d.TotalDays < 2) return "yesterday";
            if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
            return then.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        }

        void Load()
        {
            try
            {
                if (File.Exists(Path))
                    _data = JsonUtility.FromJson<Data>(File.ReadAllText(Path)) ?? new Data();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Harley] Could not read memory, starting fresh: " + e.Message);
                _data = new Data();
            }
            if (_data.facts == null) _data.facts = new List<string>();
            if (_data.recentChat == null) _data.recentChat = new List<string>();
        }

        void Save()
        {
            try { File.WriteAllText(Path, JsonUtility.ToJson(_data, true)); }
            catch (Exception e) { Debug.LogWarning("[Harley] Could not save memory: " + e.Message); }
        }
    }
}
