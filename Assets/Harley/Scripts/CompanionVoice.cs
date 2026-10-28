using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Harley
{
    /// <summary>
    /// Voice in / voice out.
    ///
    /// Meta Voice SDK is connected automatically: put an App Dictation Experience
    /// and a TTS Speaker in the scene and Harley finds them at start
    /// (Assets > Create > Voice SDK > Add App Dictation Experience to Scene,
    ///  Assets > Create > Voice SDK > TTS > Add Default TTS Setup).
    ///
    /// Prefer manual wiring? Use the UnityEvents below; anything wired there
    /// turns the automatic connection off for that half.
    ///
    /// No text-to-speech? Harley talks in a cute babble voice (CompanionSounds)
    /// with subtitles in a bubble above its head.
    /// </summary>
    public class CompanionVoice : MonoBehaviour
    {
        [Serializable] public class StringEvent : UnityEvent<string> { }

        public CompanionConfig config;
        public CompanionSounds sounds;
        [Tooltip("Find Meta Voice SDK dictation and TTS in the scene and connect them.")]
        public bool autoConnectVoiceSDK = true;
        [Tooltip("Free voice with a wit.ai token (set Wit Token in the config). Added automatically.")]
        public WitSpeech wit;

        [Header("Speech to text (manual wiring, optional)")]
        public UnityEvent onStartListening = new UnityEvent();
        public UnityEvent onStopListening = new UnityEvent();
        [Tooltip("Stop listening if nothing was heard after this many seconds.")]
        public float listenTimeout = 9f;

        [Header("Text to speech (manual wiring, optional)")]
        public StringEvent onSpeak = new StringEvent();
        public UnityEvent onStopSpeaking = new UnityEvent();
        [Tooltip("The AudioSource the TTS plays through, for a mouth that follows the voice. Found automatically with Voice SDK.")]
        public AudioSource ttsAudio;

        [Header("Subtitles")]
        public TMP_Text subtitle;
        public Renderer subtitleBackground;
        public Transform head;
        public float subtitleHoldSeconds = 2.5f;
        public Color subtitleColor = Color.white;
        public Color partialColor = new Color(0.77f, 0.71f, 0.99f);

        public event Action<string> Transcribed;
        public event Action<bool> ListeningChanged;

        public bool IsListening { get; private set; }
        public bool IsSpeaking { get; private set; }
        /// <summary>0..1 how open the mouth should be right now.</summary>
        public float TalkAmount { get; private set; }
        /// <summary>True when a real text-to-speech voice is connected.</summary>
        public bool HasTts => _autoTts || (onSpeak != null && onSpeak.GetPersistentEventCount() > 0) || WitSpeaks;
        public bool HasDictation => _autoDictation || (onStartListening != null && onStartListening.GetPersistentEventCount() > 0) || WitListens;
        bool WitListens => wit != null && wit.CanListen && !_autoDictation;
        bool WitSpeaks => wit != null && wit.CanSpeak && !_autoTts;
        /// <summary>Microphone level 0..1 while listening (Wit only).</summary>
        public float MicLevel => wit != null && wit.IsRecording ? wit.Level : 0f;

        float _listenStarted, _micStoppedAt, _listenEndedAt = -999f;
        float _speakEndsAt, _speakStarted, _speakDuration;
        float _subtitleHideAt;
        float _subtitleAlpha;
        bool _ttsReportsTiming, _babbling;
        bool _dots;
        int _lastDots;
        readonly float[] _samples = new float[256];
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        Color _bgColor = new Color(0.12f, 0.06f, 0.2f, 0.8f);

        // Voice SDK, found by reflection (no compile-time dependency)
        MonoBehaviour _dictation, _tts;
        MethodInfo _dictActivate, _dictDeactivate, _ttsSpeak, _ttsStop;
        bool _autoDictation, _autoTts;

        void Awake()
        {
            // Unassigned fields can be Unity "fake null" objects in the Editor: make them real null so ?. is safe.
            if (!sounds) sounds = null;
            if (!ttsAudio) ttsAudio = null;
            if (!subtitle) subtitle = null;
            if (!subtitleBackground) subtitleBackground = null;
            if (!head) head = null;
            _mpb = new MaterialPropertyBlock();
            if (subtitleBackground != null && subtitleBackground.sharedMaterial != null && subtitleBackground.sharedMaterial.HasProperty(ColorId))
                _bgColor = subtitleBackground.sharedMaterial.GetColor(ColorId);
            SetSubtitleAlpha(0f);
        }

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (autoConnectVoiceSDK) AutoConnect();
            ConnectWit();
            ConnectNative();
#if UNITY_ANDROID && !UNITY_EDITOR
            // Speech-to-text needs the microphone: ask once, up front.
            if (HasDictation && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
        }

        // ---------- Wit (free voice, no SDK) ----------

        void ConnectWit()
        {
            if (!wit) wit = GetComponent<WitSpeech>();
            if (!wit) wit = gameObject.AddComponent<WitSpeech>();
            wit.config = config;
            wit.Transcribed += OnWitTranscribed;
            wit.Failed += OnWitFailed;
            wit.SpeechStarted += () => { if (_witSpeaking) OnTtsStarted(); };
            wit.SpeechFinished += () => { if (_witSpeaking) { _witSpeaking = false; OnTtsFinished(); } };
            if (WitSpeaks && ttsAudio == null) ttsAudio = wit.output;
            if (wit.HasToken) Debug.Log("[Harley] Voice: Wit.ai connected (talk " + (WitListens ? "on" : "off") + ", speech " + (WitSpeaks ? "on" : "off") + ").");
        }

        bool _witSpeaking;

        // ---------- the headset's own voice (free, offline) ----------

        NativeVoice _native;
        bool _nativeSpeaking;
        bool NativeSpeaks => _native != null && _native.Ready && (config == null || config.deviceVoice);

        void ConnectNative()
        {
            _native = GetComponent<NativeVoice>();
            if (!_native) _native = gameObject.AddComponent<NativeVoice>();
            if (config != null) { _native.pitch = config.deviceVoicePitch; _native.rate = config.deviceVoiceRate; }
        }

        void OnWitTranscribed(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (IsListening) StopListening();
                ShowStatus("(I didn't catch that)", 2f);
                return;
            }
            _listenEndedAt = Time.time; // fresh: the transcript is for this Talk
            ReceiveTranscript(text);
        }

        void OnWitFailed(string why)
        {
            if (IsListening) { IsListening = false; ListeningChanged?.Invoke(false); }
            if (_witSpeaking)
            {
                // No voice: fall back to the babble voice so Harley still "talks".
                _witSpeaking = false;
                if (sounds != null && IsSpeaking && subtitle != null)
                {
                    _speakDuration = Mathf.Max(0.8f, sounds.Babble(subtitle.text, Emotion.Neutral) + 0.15f);
                    _babbling = true;
                    _speakStarted = Time.time;
                    _speakEndsAt = Time.time + _speakDuration;
                    _ttsReportsTiming = false;
                    return;
                }
            }
            ShowStatus("(" + why + ")", 3f);
        }

        // ---------- Voice SDK auto-connect ----------

        void AutoConnect()
        {
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                if (_dictation == null && n == "AppDictationExperience") _dictation = mb;
                else if (_tts == null && n == "TTSSpeaker") _tts = mb;
            }

            const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
            try
            {
                if (_dictation != null && onStartListening.GetPersistentEventCount() == 0)
                {
                    Type t = _dictation.GetType();
                    _dictActivate = t.GetMethod("Activate", inst, null, Type.EmptyTypes, null);
                    _dictDeactivate = t.GetMethod("Deactivate", inst, null, Type.EmptyTypes, null);
                    object events = Prop(_dictation, "DictationEvents");
                    if (events != null)
                    {
                        if (Prop(events, "OnFullTranscription") is UnityEvent<string> full) full.AddListener(ReceiveTranscript);
                        if (Prop(events, "OnPartialTranscription") is UnityEvent<string> part) part.AddListener(ReceivePartialTranscript);
                        if (Prop(events, "OnStoppedListening") is UnityEvent stopped) stopped.AddListener(() => _micStoppedAt = Time.time);
                    }
                    _autoDictation = _dictActivate != null;
                }

                if (_tts != null && onSpeak.GetPersistentEventCount() == 0)
                {
                    Type t = _tts.GetType();
                    _ttsSpeak = t.GetMethod("Speak", inst, null, new[] { typeof(string) }, null);
                    _ttsStop = t.GetMethod("Stop", inst, null, Type.EmptyTypes, null);
                    if (ttsAudio == null && Prop(_tts, "AudioSource") is AudioSource src) ttsAudio = src;
                    object events = Prop(_tts, "Events");
                    if (events != null)
                    {
                        if (Prop(events, "OnPlaybackQueueBegin") is UnityEvent begin) begin.AddListener(OnTtsStarted);
                        if (Prop(events, "OnPlaybackQueueComplete") is UnityEvent done) done.AddListener(OnTtsFinished);
                    }
                    _autoTts = _ttsSpeak != null;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Harley] Could not connect Voice SDK automatically, wire it by hand: " + e.Message);
            }

            Debug.Log($"[Harley] Voice: dictation {(HasDictation ? "connected" : "not found")}, " +
                      $"TTS {(HasTts ? "connected" : "not found, using babble voice")}.");
        }

        static object Prop(object target, string name)
        {
            if (target == null) return null;
            foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != name || p.GetIndexParameters().Length > 0) continue;
                try { return p.GetValue(target); } catch { return null; }
            }
            return null;
        }

        void InvokeSafe(MethodInfo m, object target, params object[] args)
        {
            if (m == null || target == null) return;
            try { m.Invoke(target, args); }
            catch (Exception e) { Debug.LogWarning("[Harley] Voice SDK call failed: " + (e.InnerException ?? e).Message); }
        }

        // ---------- listening ----------

        public void StartListening()
        {
            if (IsListening) return;
            if (IsSpeaking) StopSpeaking();
            IsListening = true;
            _listenStarted = Time.time;
            _micStoppedAt = 0f;
            ShowText("...", partialColor, false);
            _dots = true;
            sounds?.Play(Sfx.Ding, 1f, 0.6f);
            if (_autoDictation) InvokeSafe(_dictActivate, _dictation);
            else if (WitListens) wit.StartRecording();
            onStartListening?.Invoke();
            ListeningChanged?.Invoke(true);
        }

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            _listenEndedAt = Time.time;
            if (_autoDictation) InvokeSafe(_dictDeactivate, _dictation);
            if (wit != null && wit.IsRecording) { ShowText("...", partialColor, false); _dots = true; wit.StopAndTranscribe(); }
            onStopListening?.Invoke();
            ListeningChanged?.Invoke(false);
            _subtitleHideAt = Time.time + (wit != null && (wit.IsTranscribing || _dots) ? 6f : 0.6f);
        }

        /// <summary>Dictation's "full transcription" lands here.</summary>
        public void ReceiveTranscript(string text)
        {
            // Only while listening, or just after (the final words can arrive a moment after the mic closes).
            bool was = IsListening;
            if (!was && Time.time - _listenEndedAt > 8f) return;

            IsListening = false;
            _listenEndedAt = Time.time;
            if (_autoDictation) InvokeSafe(_dictDeactivate, _dictation); // one sentence per Talk: don't keep the mic open
            if (was) { onStopListening?.Invoke(); ListeningChanged?.Invoke(false); }
            Deliver(text);
        }

        void Deliver(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { _subtitleHideAt = Time.time + 0.5f; return; }
            ShowText(text.Trim(), partialColor, false);
            _subtitleHideAt = Time.time + 1.5f;
            Transcribed?.Invoke(text.Trim());
        }

        /// <summary>Live words while you are still talking.</summary>
        public void ReceivePartialTranscript(string text)
        {
            if (!IsListening || string.IsNullOrWhiteSpace(text)) return;
            _listenStarted = Time.time; // still hearing you, don't time out
            ShowText(text.Trim() + "...", partialColor, false);
        }

        /// <summary>For testing in the Editor or from a UI field: pretend the player said this.</summary>
        public void SimulatePlayerSaid(string text) => Deliver(text);

        // ---------- speaking ----------

        public void Say(string text, Emotion emotion = Emotion.Neutral)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (IsListening) StopListening();
            text = text.Trim();

            _speakDuration = EstimateDuration(text);
            _ttsReportsTiming = false;
            _babbling = false;
            _nativeSpeaking = false;

            bool preferBabble = config != null && config.babble && config.babbleEvenWithTTS;
            if (HasTts && !preferBabble)
            {
                if (_autoTts) InvokeSafe(_ttsSpeak, _tts, text);
                else if (WitSpeaks) { _witSpeaking = true; wit.Speak(text); _speakDuration += 4f; } // + time to fetch the audio
                onSpeak?.Invoke(text);
            }
            else if (!preferBabble && NativeSpeaks && _native.Speak(text, NativeVoice.Guess(text, config != null ? config.witLanguage : "en")))
            {
                _nativeSpeaking = true;
                _speakDuration += 1.5f; // ends early when the device says it's done
                onSpeak?.Invoke(text);
            }
            else if (sounds != null && (config == null || config.babble))
            {
                _speakDuration = Mathf.Max(0.8f, sounds.Babble(text, emotion) + 0.15f);
                _babbling = true;
            }

            _speakStarted = Time.time;
            _speakEndsAt = Time.time + _speakDuration;
            IsSpeaking = true;
            ShowText(text, subtitleColor, true);
        }

        public void StopSpeaking()
        {
            if (!IsSpeaking) return;
            IsSpeaking = false;
            if (_babbling) sounds?.StopBabble();
            if (_autoTts) InvokeSafe(_ttsStop, _tts);
            if (_witSpeaking) { _witSpeaking = false; wit.StopSpeaking(); }
            if (_nativeSpeaking) { _nativeSpeaking = false; _native.Stop(); }
            onStopSpeaking?.Invoke();
            _subtitleHideAt = Time.time + 0.5f;
        }

        /// <summary>TTS started playing (auto-wired with Voice SDK).</summary>
        public void OnTtsStarted()
        {
            _ttsReportsTiming = true;
            IsSpeaking = true;
            _speakStarted = Time.time;
            _speakEndsAt = Time.time + 30f; // safety cap until we hear "finished"
        }

        /// <summary>TTS finished playing (auto-wired with Voice SDK).</summary>
        public void OnTtsFinished()
        {
            IsSpeaking = false;
            _speakEndsAt = Time.time;
            if (subtitle != null) subtitle.maxVisibleCharacters = 99999;
            _subtitleHideAt = Time.time + subtitleHoldSeconds;
        }

        /// <summary>Short text above the head that is not spoken (errors, status).</summary>
        public void ShowStatus(string text, float seconds = 3f)
        {
            ShowText(text, partialColor, false);
            _subtitleHideAt = Time.time + seconds;
        }

        // ---------- per frame ----------

        void Update()
        {
            float t = Time.time;

            float timeout = wit != null && wit.IsRecording ? wit.maxSeconds + 1f : listenTimeout;
            if (IsListening && t - _listenStarted > timeout) StopListening();
            if (IsListening && wit != null && !wit.IsRecording && wit.IsTranscribing && !_autoDictation)
            {   // Wit finished recording on its own (you stopped talking): wait for the words
                IsListening = false; _listenEndedAt = t; ListeningChanged?.Invoke(false);
                ShowText("...", partialColor, false); _dots = true; _subtitleHideAt = t + 6f;
            }
            if (IsListening && _micStoppedAt > 0f && t - _micStoppedAt > 2.5f) StopListening(); // mic closed, nothing came

            // Device voice finished (it reports while it's talking; give it a moment to start)
            if (_nativeSpeaking && IsSpeaking && t - _speakStarted > 0.8f && !_native.IsSpeaking)
                _speakEndsAt = t;

            if (IsSpeaking && t >= _speakEndsAt)
            {
                IsSpeaking = false;
                _babbling = false;
                _nativeSpeaking = false;
                _subtitleHideAt = t + subtitleHoldSeconds;
            }

            // Mouth
            float target = 0f;
            if (IsSpeaking)
            {
                if (_babbling && sounds != null)
                {
                    target = sounds.BabbleLevel;
                }
                else if (ttsAudio != null && ttsAudio.isPlaying)
                {
                    ttsAudio.GetOutputData(_samples, 0);
                    float sum = 0f;
                    for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
                    target = Mathf.Clamp01(Mathf.Sqrt(sum / _samples.Length) * 10f);
                }
                else
                {
                    float syl = 0.5f + 0.5f * Mathf.Sin(t * 17f);
                    float wobble = Mathf.PerlinNoise(t * 3f, 0.37f);
                    target = syl * (0.45f + 0.55f * wobble);
                }
            }
            TalkAmount = Mathf.Lerp(TalkAmount, target, 1f - Mathf.Exp(-25f * Time.deltaTime));

            // Typewriter subtitles in sync with speech
            if (subtitle != null && IsSpeaking)
            {
                float dur = _ttsReportsTiming ? EstimateDuration(subtitle.text) : _speakDuration * 0.9f;
                float p = Mathf.Clamp01((t - _speakStarted) / Mathf.Max(0.1f, dur));
                subtitle.maxVisibleCharacters = Mathf.CeilToInt(p * subtitle.textInfo.characterCount);
            }

            // Fade out
            bool show = IsSpeaking || IsListening || t < _subtitleHideAt;
            _subtitleAlpha = Mathf.MoveTowards(_subtitleAlpha, show ? 1f : 0f, Time.deltaTime * 3f);
            SetSubtitleAlpha(_subtitleAlpha);

            // Listening dots until the first words arrive
            if (_dots && !IsListening && !(wit != null && wit.IsTranscribing)) _dots = false;
            if (_dots && subtitle != null)
            {
                int dots = 1 + (int)(t * 3f) % 3;
                if (dots != _lastDots)
                {
                    _lastDots = dots;
                    subtitle.text = new string('.', dots);
                }
            }
        }

        void LateUpdate()
        {
            if (subtitle == null || head == null) return;
            Transform st = subtitle.transform;
            Vector3 away = st.position - head.position;
            if (away.sqrMagnitude > 0.0001f) st.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

            if (subtitleBackground != null && _subtitleAlpha > 0.01f)
            {
                Bounds b = subtitle.textBounds;
                Transform bg = subtitleBackground.transform;
                bg.rotation = st.rotation;
                bg.position = st.TransformPoint(b.center) + st.forward * 0.002f;
                bg.localScale = new Vector3(b.size.x + 0.03f, b.size.y + 0.02f, 1f);
            }
        }

        // ---------- helpers ----------

        void ShowText(string text, Color color, bool typewriter)
        {
            _dots = false;
            if (subtitle == null) return;
            subtitle.text = text;
            subtitle.color = new Color(color.r, color.g, color.b, subtitle.color.a);
            subtitle.maxVisibleCharacters = typewriter ? 0 : 99999;
            subtitle.ForceMeshUpdate();
            _subtitleHideAt = Time.time + subtitleHoldSeconds;
        }

        void SetSubtitleAlpha(float a)
        {
            if (subtitle != null)
            {
                subtitle.alpha = a;
                subtitle.enabled = a > 0.001f;
            }
            if (subtitleBackground != null)
            {
                Color c = _bgColor; c.a *= a;
                subtitleBackground.GetPropertyBlock(_mpb);
                _mpb.SetColor(ColorId, c);
                _mpb.SetColor(BaseColorId, c);
                subtitleBackground.SetPropertyBlock(_mpb);
                subtitleBackground.enabled = a > 0.001f;
            }
        }

        static float EstimateDuration(string text)
        {
            int words = text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return Mathf.Clamp(words / 2.6f + 0.6f, 1.2f, 15f);
        }
    }
}
