using UnityEngine;

namespace Harley
{
    /// <summary>
    /// All of Harley's knobs in one asset.
    /// The asset holds your API key, so it is git-ignored (see .gitignore).
    /// </summary>
    [CreateAssetMenu(fileName = "CompanionConfig", menuName = "Harley/Config")]
    public class CompanionConfig : ScriptableObject
    {
        [Header("Claude API  (never commit this asset to git)")]
        public string apiKey = "";
        [Tooltip("claude-haiku-5-5 is the fastest, which matters for a talking character. claude-sonnet-5-5 is smarter but slower.")]
        public string model = "claude-haiku-5-5";
        [Tooltip("Upper bound for one reply. Replies are 1-2 short sentences, so this is plenty.")]
        public int maxTokens = 500;
        [Tooltip("Seconds before a request is given up.")]
        public int requestTimeout = 30;

        [Header("Character")]
        public string companionName = "Harley";
        [TextArea(2, 6)]
        public string personality =
            "Playful, curious and warm. A little cheeky but never mean. " +
            "Loves noticing small details in the player's room and asking about them.";
        [Tooltip("Language to use until the player speaks. After that Harley answers in whatever language the player uses.")]
        public string defaultLanguage = "English";

        [Header("Memory")]
        [Tooltip("How many back-and-forth exchanges Harley keeps in mind during one session.")]
        public int historyTurns = 8;
        [Tooltip("Harley remembers facts about you between sessions (saved on the headset only).")]
        public bool longTermMemory = true;
        [Tooltip("Max facts kept between sessions. Oldest are dropped first.")]
        public int maxFacts = 40;

        [Header("Proactive behaviour")]
        [Tooltip("Harley sometimes looks at what you are looking at and comments on it on its own.")]
        public bool proactiveComments = true;
        public float proactiveMinSeconds = 60f;
        public float proactiveMaxSeconds = 120f;

        [Header("Reactions")]
        [Tooltip("Min seconds between Claude calls caused by pokes, waves, petting and throws. Reactions in between are local and free.")]
        public float eventCallCooldown = 20f;

        [Header("Sound")]
        [Tooltip("Cute babble voice while Harley talks, used when no text-to-speech is connected (or always, if you prefer it).")]
        public bool babble = true;
        [Tooltip("Use the babble voice even when text-to-speech is connected.")]
        public bool babbleEvenWithTTS = false;
        [Range(0f, 1f)] public float soundVolume = 0.7f;
    }
}
