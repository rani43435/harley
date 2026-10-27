using System;
using UnityEngine;

namespace AICompanion
{
    public enum Emotion { Neutral, Happy, Excited, Curious, Surprised, Sad, Sleepy, Love }

    public enum CompanionAction { Stay, Follow, ComeClose, Wander, LookAtThing, Sit, Hide, Spin, Dance, Wave }

    /// <summary>What Claude sends back every time (matches the JSON schema in ClaudeClient).</summary>
    [Serializable]
    public class CompanionReply
    {
        public string say;
        public string emotion;
        public string action;

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
                default: return CompanionAction.Stay;
            }
        }

        /// <summary>Parses Claude's text. Falls back to "just say the text" if it is not JSON.</summary>
        public static CompanionReply Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new CompanionReply { say = "", emotion = "neutral", action = "stay" };
            int a = text.IndexOf('{');
            int b = text.LastIndexOf('}');
            if (a >= 0 && b > a)
            {
                try
                {
                    var r = JsonUtility.FromJson<CompanionReply>(text.Substring(a, b - a + 1));
                    if (r != null) { r.say = r.say ?? ""; return r; }
                }
                catch { /* fall through */ }
            }
            return new CompanionReply { say = text.Trim(), emotion = "neutral", action = "stay" };
        }
    }
}
