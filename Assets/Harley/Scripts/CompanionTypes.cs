using System;
using System.Text;
using UnityEngine;

namespace Harley
{
    public enum Emotion { Neutral, Happy, Excited, Curious, Surprised, Sad, Sleepy, Love }

    public enum CompanionAction { Stay, Follow, ComeClose, Wander, LookAtThing, Sit, Hide, Spin, Dance, Wave, GoTo }

    /// <summary>What Claude sends back every time (matches the JSON schema in ClaudeClient).</summary>
    [Serializable]
    public class CompanionReply
    {
        public string say;
        public string emotion;
        public string action;
        /// <summary>For go_to: the name of a room object from the room list, e.g. "couch" or "table 2".</summary>
        public string target;
        /// <summary>A short new fact about the player worth keeping between sessions, or empty.</summary>
        public string remember;
        /// <summary>True only when the player asked Harley to forget what it knows about them.</summary>
        public bool forget;

        public Emotion GetEmotion()
        {
            switch ((emotion ?? "").Trim().ToLowerInvariant())
            {
                case "happy": return Emotion.Happy;
                case "excited": return Emotion.Excited;
                case "curious": return Emotion.Curious;
                case "surprised": return Emotion.Surprised;
                case "sad": return Emotion.Sad;
                case "sleepy": return Emotion.Sleepy;
                case "love": return Emotion.Love;
                default: return Emotion.Neutral;
            }
        }

        public CompanionAction GetAction()
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "follow": return CompanionAction.Follow;
                case "come_close": return CompanionAction.ComeClose;
                case "wander": return CompanionAction.Wander;
                case "look_at_thing": return CompanionAction.LookAtThing;
                case "sit": return CompanionAction.Sit;
                case "hide": return CompanionAction.Hide;
                case "spin": return CompanionAction.Spin;
                case "dance": return CompanionAction.Dance;
                case "wave": return CompanionAction.Wave;
                case "go_to": return CompanionAction.GoTo;
                default: return CompanionAction.Stay;
            }
        }

        /// <summary>Parses Claude's text. Falls back to "just say the text" if it is not JSON.</summary>
        public static CompanionReply Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Empty();
            int a = text.IndexOf('{');
            int b = text.LastIndexOf('}');
            if (a >= 0 && b > a)
            {
                try
                {
                    var r = JsonUtility.FromJson<CompanionReply>(text.Substring(a, b - a + 1));
                    if (r != null)
                    {
                        r.say = r.say ?? "";
                        r.target = r.target ?? "";
                        r.remember = r.remember ?? "";
                        return r;
                    }
                }
                catch { /* fall through */ }
            }

            // Looks like JSON but isn't complete (e.g. cut off at max_tokens): rescue "say" instead of speaking raw JSON.
            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var rescued = Empty();
                rescued.say = ExtractString(text, "say");
                return rescued;
            }

            var plain = Empty();
            plain.say = text.Trim();
            return plain;
        }

        static string ExtractString(string json, string key)
        {
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return "";
            int colon = json.IndexOf(':', k);
            if (colon < 0) return "";
            int q = json.IndexOf('"', colon + 1);
            if (q < 0) return "";
            var sb = new StringBuilder();
            for (int i = q + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    sb.Append(n == 'n' ? ' ' : n == 't' ? ' ' : n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static CompanionReply Empty() =>
            new CompanionReply { say = "", emotion = "neutral", action = "stay", target = "", remember = "", forget = false };
    }
}
