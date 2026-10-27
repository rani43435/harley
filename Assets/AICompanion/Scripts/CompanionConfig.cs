using UnityEngine;

namespace AICompanion
{
    /// <summary>
    /// All the knobs for the companion in one asset.
    /// The asset holds your API key, so it is git-ignored (see .gitignore).
    /// </summary>
    [CreateAssetMenu(fileName = "CompanionConfig", menuName = "AI Companion/Config")]
    public class CompanionConfig : ScriptableObject
    {
        [Header("Claude API  (never commit this asset to git)")]
        public string apiKey = "";
        [Tooltip("claude-haiku-5-5 is the fastest, which matters for a talking character. claude-sonnet-5-5 is smarter but slower.")]
        public string model = "claude-haiku-5-5";
        [Tooltip("Upper bound for one reply. Replies are 1-2 short sentences, so this is plenty.")]
        public int maxTokens = 400;
        [Tooltip("Seconds before a request is given up.")]
        public int requestTimeout = 30;

        [Header("Character")]
        public string companionName = "Nova";
        [TextArea(2, 6)]
        public string personality =
            "Playful, curious and warm. A little cheeky but never mean. " +
            "Loves noticing small details in the player's room and asking about them.";
        [Tooltip("Language to use until the player speaks. After that Nova answers in whatever language the player uses.")]
        public string defaultLanguage = "English";

        [Header("Memory")]
        [Tooltip("How many back-and-forth exchanges Nova remembers in one session.")]
        public int historyTurns = 8;

        [Header("Proactive behaviour")]
        [Tooltip("Nova sometimes looks at what you are looking at and comments on it on its own.")]
        public bool proactiveComments = true;
        public float proactiveMinSeconds = 60f;
        public float proactiveMaxSeconds = 120f;

        [Header("Reactions")]
        [Tooltip("Min seconds between Claude calls caused by pokes, waves and petting. Reactions in between are local and free.")]
        public float eventCallCooldown = 20f;
    }
}
