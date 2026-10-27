using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace AICompanion
{
    /// <summary>
    /// Voice in / voice out, without hard-wiring any speech package.
    ///
    /// Speech-to-text: wire Meta Voice SDK "Dictation" in the Inspector:
    ///   On Start Listening  -> AppDictationExperience.Activate()
    ///   On Stop Listening   -> AppDictationExperience.Deactivate()
    ///   Dictation "On Full Transcription"    -> CompanionVoice.ReceiveTranscript(string)
    ///   Dictation "On Partial Transcription" -> CompanionVoice.ReceivePartialTranscript(string)  (optional)
    ///
    /// Text-to-speech: wire Meta Voice SDK "TTS":
    ///   On Speak -> TTSSpeaker.Speak(string)
    ///   TTSSpeaker "On Playback Start"    -> CompanionVoice.OnTtsStarted()   (optional, exact timing)
    ///   TTSSpeaker "On Playback Complete" -> CompanionVoice.OnTtsFinished()  (optional)
    ///   Drag the TTSSpeaker's AudioSource into Tts Audio for a real lip-sync.
    ///
    /// Nothing wired? It still works with subtitles only (text bubble above the head).
    /// </summary>
    public class CompanionVoice : MonoBehaviour
    {
        [Serializable] public class StringEvent : UnityEvent<string> { }

        [Header("Speech to text")]
        public UnityEvent onStartListening = new UnityEvent();
        public UnityEvent onStopListening = new UnityEvent();
        [Tooltip("Stop listening if nothing was heard after this many seconds.")]
        public float listenTimeout = 9f;

        [Header("Text to speech")]
        public StringEvent onSpeak = new StringEvent();
        public UnityEvent onStopSpeaking = new UnityEvent();
        [Tooltip("Optional: the AudioSource the TTS plays through, for mouth movement that follows the voice.")]
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

        float _listenStarted;
        float _speakEndsAt, _speakStarted, _speakDuration;
        float _subtitleHideAt;
        float _subtitleAlpha;
        bool _ttsReportsTiming;
        bool _dots;
        int _lastDots;
        readonly float[] _samples = new float[256];
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        Color _bgColor = new Color(0.12f, 0.06f, 0.2f, 0.8f);

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if (subtitleBackground != null && subtitleBackground.sharedMaterial != null && subtitleBackground.sharedMaterial.HasProperty(ColorId))
                _bgColor = subtitleBackground.sharedMaterial.GetColor(ColorId);
            SetSubtitleAlpha(0f);
        }

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
        }

        // ---------- listening ----------

        public void StartListening()
        {
            if (IsListening) return;
            if (IsSpeaking) StopSpeaking();
            IsListening = true;
            _listenStarted = Time.time;
            ShowText("...", partialColor, false);
            _dots = true;
            onStartListening?.Invoke();
            ListeningChanged?.Invoke(true);
        }

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            onStopListening?.Invoke();
            ListeningChanged?.Invoke(false);
            _subtitleHideAt = Time.time + 0.6f;
        }

        /// <summary>Wire the dictation's "full transcription" event here.</summary>
        public void ReceiveTranscript(string text)
        {
            bool was = IsListening;
            IsListening = false;
            if (was) { onStopListening?.Invoke(); ListeningChanged?.Invoke(false); }
            if (string.IsNullOrWhiteSpace(text)) { _subtitleHideAt = Time.time + 0.5f; return; }
            ShowText(text.Trim(), partialColor, false);
            _subtitleHideAt = Time.time + 1.5f;
            Transcribed?.Invoke(text.Trim());
        }

        /// <summary>Optional: live words while you are still talking.</summary>
        public void ReceivePartialTranscript(string text)
        {
            if (!IsListening || string.IsNullOrWhiteSpace(text)) return;
            _listenStarted = Time.time; // still hearing you, don't time out
            ShowText(text.Trim() + "...", partialColor, false);
        }

        /// <summary>For testing in the Editor or from a UI field: pretend the player said this.</summary>
        public void SimulatePlayerSaid(string text) => ReceiveTranscript(text);

        // ---------- speaking ----------

        public void Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (IsListening) StopListening();
            text = text.Trim();

            _speakDuration = EstimateDuration(text);
            _speakStarted = Time.time;
            _speakEndsAt = Time.time + _speakDuration;
            _ttsReportsTiming = false;
            IsSpeaking = true;
            ShowText(text, subtitleColor, true);
            onSpeak?.Invoke(text);
        }

        public void StopSpeaking()
        {
            if (!IsSpeaking) return;
            IsSpeaking = false;
            onStopSpeaking?.Invoke();
            _subtitleHideAt = Time.time + 0.5f;
        }

        /// <summary>Optional: wire TTS "playback start" for exact timing.</summary>
        public void OnTtsStarted()
        {
            _ttsReportsTiming = true;
            IsSpeaking = true;
            _speakStarted = Time.time;
            _speakEndsAt = Time.time + 30f; // safety cap until we hear "finished"
        }

        /// <summary>Optional: wire TTS "playback complete".</summary>
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

            if (IsListening && t - _listenStarted > listenTimeout) StopListening();

            if (IsSpeaking && t >= _speakEndsAt)
            {
                IsSpeaking = false;
                _subtitleHideAt = t + subtitleHoldSeconds;
            }

            // Mouth
            float target = 0f;
            if (IsSpeaking)
            {
                if (ttsAudio != null && ttsAudio.isPlaying)
                {
                    ttsAudio.GetOutputData(_samples, 0);
                    float sum = 0f;
                    for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
                    target = Mathf.Clamp01(Mathf.Sqrt(sum / _samples.Length) * 10f);
                }
                else
                {
                    // Syllable-ish rhythm when there is no audio to follow.
                    float syl = 0.5f + 0.5f * Mathf.Sin(t * 17f);
                    float wobble = Mathf.PerlinNoise(t * 3f, 0.37f);
                    target = syl * (0.45f + 0.55f * wobble);
                }
            }
            TalkAmount = Mathf.Lerp(TalkAmount, target, 1f - Mathf.Exp(-25f * Time.deltaTime));

            // Typewriter subtitles in sync with speech
            if (subtitle != null && IsSpeaking && !_ttsReportsTiming)
            {
                float p = Mathf.Clamp01((t - _speakStarted) / Mathf.Max(0.1f, _speakDuration * 0.9f));
                subtitle.maxVisibleCharacters = Mathf.CeilToInt(p * subtitle.textInfo.characterCount);
            }
            else if (subtitle != null && IsSpeaking && _ttsReportsTiming)
            {
                // Real TTS playing: reveal at a normal speaking pace.
                float p = Mathf.Clamp01((t - _speakStarted) / Mathf.Max(0.1f, _speakDuration));
                subtitle.maxVisibleCharacters = Mathf.CeilToInt(p * subtitle.textInfo.characterCount);
            }

            // Fade out
            bool show = IsSpeaking || IsListening || t < _subtitleHideAt;
            _subtitleAlpha = Mathf.MoveTowards(_subtitleAlpha, show ? 1f : 0f, Time.deltaTime * 3f);
            SetSubtitleAlpha(_subtitleAlpha);

            // Listening dots animation until the first words arrive
            if (_dots && !IsListening) _dots = false;
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
            // Face the player.
            Transform st = subtitle.transform;
            Vector3 away = st.position - head.position;
            if (away.sqrMagnitude > 0.0001f) st.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

            // Size the background to the text.
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
