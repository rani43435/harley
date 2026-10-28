using UnityEngine;

namespace Harley
{
    public enum AiProvider { Claude, Gemini }

    /// <summary>
    /// All of Harley's knobs in one asset.
    /// The asset holds your API key, so it is git-ignored (see .gitignore).
    /// </summary>
    [CreateAssetMenu(fileName = "CompanionConfig", menuName = "Harley/Config")]
    public class CompanionConfig : ScriptableObject
    {
        [Tooltip("Which AI is Harley's brain. Gemini has a free tier (aistudio.google.com); Claude needs paid API credits.")]
        public AiProvider provider = AiProvider.Claude;

        /// <summary>True when the chosen AI has a key.</summary>
        public bool HasBrainKey => !string.IsNullOrWhiteSpace(provider == AiProvider.Gemini ? geminiApiKey : apiKey) &&
                                   (provider == AiProvider.Gemini || apiKey.Trim().StartsWith("sk-"));

        [Header("Gemini (free tier: aistudio.google.com > Get API key)")]
        public string geminiApiKey = "";
        [Tooltip("gemini-3.5-flash answers in ~2 s on the free tier. If it is busy or gone Harley tries other Flash models by itself.")]
        public string geminiModel = "gemini-3.5-flash";

        [Header("Claude API  (never commit this asset to git)")]
        public string apiKey = "";
        [Tooltip("claude-haiku-5-5 is the fastest, which matters for a talking character. claude-sonnet-5-5 is smarter but slower.")]
        public string model = "claude-haiku-5-5";
        [Tooltip("Upper bound for one reply. Replies are 1-2 short sentences, so this is plenty.")]
        public int maxTokens = 600;
        [Tooltip("Seconds before a request is given up.")]
        public int requestTimeout = 25;

        [Header("Voice (free, wit.ai)")]
        [Tooltip("wit.ai > New App (choose the language you speak) > Management > Settings > Client Access Token. Empty = type instead of talking, babble voice.")]
        public string witToken = "";
        [Tooltip("Harley talks out loud with Wit's voice. Off = cute babble voice with subtitles.")]
        public bool witVoiceOutput = true;
        [Tooltip("Language of your Wit app, used to pick a voice: en, ar, de...")]
        public string witLanguage = "en";
        [Tooltip("Optional exact Wit voice name. Empty = the first voice for the language above.")]
        public string witVoice = "";
        [Range(0f, 1f)] public float voiceVolume = 1f;

        [Header("Voice with Gemini (free, same key)")]
        [Tooltip("Without a Wit token, Gemini hears you (speech to text) and, if it can, gives Harley a real voice.")]
        public bool geminiVoiceOutput = true;
        [Tooltip("Gemini voice name, e.g. Leda, Kore, Puck, Aoede. Empty = Leda.")]
        public string geminiVoice = "Leda";

        [Header("Voice (headset's own, free and offline)")]
        [Tooltip("Without Wit, Harley talks with the headset's built-in text-to-speech (if it has one). Off = babble voice.")]
        public bool deviceVoice = true;
        [Range(0.5f, 2f)] public float deviceVoicePitch = 1.35f;
        [Range(0.5f, 2f)] public float deviceVoiceRate = 1.05f;

        [Header("Character")]
        public string companionName = "Harley";
        [TextArea(2, 6)]
        public string personality =
            "Playful, curious and warm. A little cheeky but never mean. " +
            "Loves noticing small details in what the player is looking at and asking about them.";
        [Tooltip("Language to use until the player speaks. After that Harley answers in whatever language the player uses.")]
        public string defaultLanguage = "English";

        [Header("Internet")]
        [Tooltip("Let Harley search the web for news, scores, prices and anything recent.")]
        public bool webSearch = true;
        [Tooltip("Claude's web search tool version.")]
        public string webSearchToolType = "web_search_20260318";
        [Tooltip("Model used for web searches.")]
        public string searchModel = "claude-haiku-5-5";
        [Range(1, 5)] public int maxSearchesPerQuestion = 3;
        [Tooltip("Live weather from Open-Meteo (free, no key).")]
        public bool weather = true;

        [Header("Where you live (weather, local news)")]
        [Tooltip("Optional. Empty = Harley guesses your city from your internet connection. City, optionally with region, e.g. \"Münster, Hessen\".")]
        public string homeCity = "";
        [Tooltip("Two-letter country code, e.g. DE.")]
        public string countryCode = "";
        [Tooltip("Optional exact position. Leave 0 to look up Home City.")]
        public float homeLatitude = 0f;
        public float homeLongitude = 0f;

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
