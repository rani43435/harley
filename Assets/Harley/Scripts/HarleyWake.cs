using System;
using System.Collections;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// "Hi Harley" / "Hey Harley": keeps a quiet ear open. When you say something short, it checks
    /// the words (Gemini or Wit); if it starts with Harley's name, Harley starts listening like the
    /// Talk button. "Hi Harley, open YouTube" in one go works too.
    /// It pauses while you're already talking to Harley, while she speaks, and in the background.
    /// </summary>
    public class HarleyWake : MonoBehaviour
    {
        public CompanionConfig config;
        public CompanionVoice voice;
        public WitSpeech wit;
        public CompanionVisual visual;
        public CompanionSounds sounds;

        [Tooltip("Microphone level that counts as speech.")]
        public float threshold = 0.022f;
        [Tooltip("Pause after speech that ends a phrase.")]
        public float endSilence = 0.55f;
        public float minPhrase = 0.35f, maxPhrase = 6f;
        [Tooltip("Never check more often than this (seconds).")]
        public float minGap = 2.5f;

        const int Rate = 16000, ClipSeconds = 12;
        static readonly string[] Names = { "harley", "harly", "harlie", "hardly", "charlie", "هارلي", "هارلى", "هارلئ" };

        AudioClip _clip;
        string _mic;
        int _readPos;
        bool _inSpeech;
        int _speechStart, _lastLoud;
        float _lastCheck = -99f;
        bool _checking;
        readonly float[] _frame = new float[Rate / 10]; // 100 ms

        public bool Enabled => config != null && config.wakeWord && wit != null && wit.CanListen;

        void Start()
        {
            if (!voice) voice = GetComponentInChildren<CompanionVoice>();
            if (!wit && voice) wit = voice.GetComponent<WitSpeech>();
            if (!visual) visual = GetComponentInChildren<CompanionVisual>();
            if (!sounds) sounds = GetComponentInChildren<CompanionSounds>();
            if (!config && voice) config = voice.config;
        }

        bool Busy => voice == null || voice.IsListening || voice.IsSpeaking || (wit != null && (wit.IsRecording || wit.IsTranscribing)) || _checking;

        void Update()
        {
            bool want = Enabled && !Busy && Application.isFocused && HasMicPermission();
            if (want && _clip == null) Open();
            else if (!want && _clip != null && !_checking) Close();
            if (_clip == null) return;
            Listen();
        }

        static bool HasMicPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
#else
            return true;
#endif
        }

        void Open()
        {
            if (Microphone.devices.Length == 0) return;
            _mic = Microphone.devices[0];
            _clip = Microphone.Start(_mic, true, ClipSeconds, Rate);
            _readPos = 0;
            _inSpeech = false;
        }

        void Close()
        {
            // Talk may have taken over the same microphone: don't stop its recording
            bool talkRecording = wit != null && wit.IsRecording;
            if (_mic != null && !talkRecording && Microphone.IsRecording(_mic)) Microphone.End(_mic);
            _clip = null;
            _inSpeech = false;
        }

        void OnDisable() => Close();
        void OnApplicationPause(bool paused) { if (paused) Close(); }

        void Listen()
        {
            int pos = Microphone.GetPosition(_mic);
            int total = _clip.samples;
            int available = (pos - _readPos + total) % total;
            while (available >= _frame.Length)
            {
                _clip.GetData(_frame, _readPos);
                float sum = 0f;
                for (int i = 0; i < _frame.Length; i++) sum += _frame[i] * _frame[i];
                float rms = Mathf.Sqrt(sum / _frame.Length);
                int frameStart = _readPos;
                _readPos = (_readPos + _frame.Length) % total;
                available -= _frame.Length;

                if (rms > threshold)
                {
                    if (!_inSpeech) { _inSpeech = true; _speechStart = (frameStart - Rate / 5 + total) % total; } // keep 200 ms before
                    _lastLoud = _readPos;
                }
                else if (_inSpeech)
                {
                    float quiet = ((_readPos - _lastLoud + total) % total) / (float)Rate;
                    if (quiet >= endSilence) { _inSpeech = false; Phrase(_speechStart, _readPos, total); }
                }
                if (_inSpeech && ((_readPos - _speechStart + total) % total) / (float)Rate > maxPhrase)
                {
                    _inSpeech = false; // too long to be "Hi Harley ...": ignore (TV, other people talking)
                }
            }
        }

        void Phrase(int from, int to, int total)
        {
            int len = (to - from + total) % total;
            float seconds = len / (float)Rate;
            if (seconds < minPhrase || seconds > maxPhrase) return;
            if (Time.unscaledTime - _lastCheck < minGap) return;
            if (!UnderDailyLimit()) return;
            _lastCheck = Time.unscaledTime;

            var samples = new float[len];
            if (from + len <= total) _clip.GetData(samples, from);
            else
            {
                var a = new float[total - from];
                var b = new float[len - a.Length];
                _clip.GetData(a, from);
                _clip.GetData(b, 0);
                a.CopyTo(samples, 0);
                b.CopyTo(samples, a.Length);
            }
            var pcm = new byte[len * 2];
            for (int i = 0; i < len; i++)
            {
                short s = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                pcm[i * 2] = (byte)(s & 0xff);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            StartCoroutine(Check(pcm));
        }

        IEnumerator Check(byte[] pcm)
        {
            _checking = true;
            string text = null;
            yield return wit.TranscribeOnce(pcm, t => text = t);
            _checking = false;
            if (string.IsNullOrWhiteSpace(text)) yield break;

            if (!Woke(text, out string rest)) yield break;
            Debug.Log("[Harley] Wake word: \"" + text + "\"");
            Close(); // free the microphone for Talk
            sounds?.Play(Sfx.Ding, 1.2f, 0.7f);
            visual?.FlashEmotion(Emotion.Happy, 1.5f);
            if (rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
                voice.SimulatePlayerSaid(rest);   // "Hi Harley, open YouTube": straight to it
            else
                voice.StartListening();           // "Hi Harley": now listening, like Talk
        }

        /// <summary>The phrase starts with (or is) Harley's name, maybe after hi / hey / hallo / يا.</summary>
        static bool Woke(string text, out string rest)
        {
            rest = "";
            string t = text.Trim().ToLowerInvariant();
            int best = -1, nameLen = 0;
            foreach (var n in Names)
            {
                int i = t.IndexOf(n, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) { best = i; nameLen = n.Length; }
            }
            if (best < 0 || best > 14) return false; // the name has to come first ("hey harley", "يا هارلي")
            rest = t.Substring(best + nameLen).Trim(' ', ',', '.', '!', '?', '،', '؟');
            return true;
        }

        bool UnderDailyLimit()
        {
            string day = DateTime.Now.ToString("yyyyMMdd");
            if (PlayerPrefs.GetString("harley.wake.day", "") != day) { PlayerPrefs.SetString("harley.wake.day", day); PlayerPrefs.SetInt("harley.wake.n", 0); }
            int n = PlayerPrefs.GetInt("harley.wake.n", 0);
            int max = config != null ? config.wakeChecksPerDay : 120;
            if (n >= max) return false;
            PlayerPrefs.SetInt("harley.wake.n", n + 1);
            return true;
        }
    }
}
