using UnityEngine;

namespace Harley
{
    /// <summary>
    /// The headset's own text-to-speech (Android TextToSpeech): free, offline, no token.
    /// Used when Wit / the Voice SDK aren't set up. If the device has no speech engine
    /// (or not for that language), Ready / Speak say so and Harley uses the babble voice.
    /// </summary>
    public class NativeVoice : MonoBehaviour
    {
        public float pitch = 1.35f;
        public float rate = 1.05f;

        public bool Ready { get; private set; }
        public bool Failed { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _tts;
        volatile int _initStatus = int.MinValue;

        class InitListener : AndroidJavaProxy
        {
            readonly NativeVoice _owner;
            public InitListener(NativeVoice owner) : base("android.speech.tts.TextToSpeech$OnInitListener") { _owner = owner; }
            public void onInit(int status) { _owner._initStatus = status; }
        }

        void Start()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                    {
                        try { _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener(this)); }
                        catch (System.Exception e) { Debug.LogWarning("[Harley] No device voice: " + e.Message); _initStatus = -1; }
                    }));
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[Harley] No device voice: " + e.Message); Failed = true; }
        }

        void Update()
        {
            if (Ready || Failed || _initStatus == int.MinValue) return;
            if (_initStatus == 0 && _tts != null)
            {
                Ready = true;
                try { _tts.Call<int>("setPitch", pitch); _tts.Call<int>("setSpeechRate", rate); } catch { }
                Debug.Log("[Harley] Voice: device text-to-speech ready.");
            }
            else
            {
                Failed = true;
                Debug.Log("[Harley] Voice: this headset has no text-to-speech engine, using the babble voice.");
            }
        }

        string _lang = "";

        /// <summary>Speaks the text. False if the device can't speak this language.</summary>
        public bool Speak(string text, string language)
        {
            if (!Ready || string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                if (_lang != language)
                {
                    using (var locale = new AndroidJavaObject("java.util.Locale", language))
                    {
                        int res = _tts.Call<int>("setLanguage", locale);
                        if (res < 0) { Debug.Log("[Harley] Device voice has no '" + language + "'."); return false; }
                    }
                    _lang = language;
                }
                using (var s = new AndroidJavaObject("java.lang.String", text))
                using (var bundle = new AndroidJavaObject("android.os.Bundle"))
                    return _tts.Call<int>("speak", s, 0 /* QUEUE_FLUSH */, bundle, "harley") == 0;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Harley] Device voice failed: " + e.Message);
                return false;
            }
        }

        public bool IsSpeaking
        {
            get
            {
                if (!Ready) return false;
                try { return _tts.Call<bool>("isSpeaking"); } catch { return false; }
            }
        }

        public void Stop()
        {
            if (!Ready) return;
            try { _tts.Call<int>("stop"); } catch { }
        }

        void OnDestroy()
        {
            if (_tts == null) return;
            try { _tts.Call("shutdown"); } catch { }
            _tts.Dispose();
            _tts = null;
        }
#else
        void Start() { Failed = true; }
        public bool Speak(string text, string language) => false;
        public bool IsSpeaking => false;
        public void Stop() { }
#endif

        /// <summary>Very small language guess for the voice: Arabic script, German letters, else English.</summary>
        public static string Guess(string text, string fallback)
        {
            foreach (char c in text)
                if (c >= '؀' && c <= 'ۿ') return "ar";
            if (text.IndexOfAny(new[] { 'ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü' }) >= 0) return "de";
            return string.IsNullOrEmpty(fallback) ? "en" : fallback;
        }
    }
}
