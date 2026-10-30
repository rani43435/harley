using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Real voice for Harley with one free Wit.ai token and no extra SDK:
    ///  - Listen: records the microphone, stops by itself when you stop talking,
    ///    and turns it into text with Wit's dictation API.
    ///  - Speak: turns Harley's reply into speech with Wit's text-to-speech API.
    ///
    /// Setup: wit.ai > New App (pick the language you speak) > Management > Settings >
    /// copy the "Client Access Token" into the config (Wit Token).
    /// </summary>
    public class WitSpeech : MonoBehaviour
    {
        const string Api = "https://api.wit.ai";
        const string ApiVersion = "20240304";
        const int Rate = 16000;

        public CompanionConfig config;
        [Tooltip("Plays Harley's voice. Created on this object if empty.")]
        public AudioSource output;

        [Header("Listening")]
        [Tooltip("Stop recording after this much quiet once you started talking.")]
        public float silenceSeconds = 1.1f;
        [Tooltip("Give up if you say nothing for this long.")]
        public float noSpeechSeconds = 7f;
        public float maxSeconds = 20f;
        [Tooltip("Microphone level that counts as talking.")]
        public float speechThreshold = 0.018f;

        public bool HasToken => config != null && !string.IsNullOrWhiteSpace(config.witToken);
        /// <summary>No Wit token, but a Gemini key: Gemini does the listening (and the voice, while it works).</summary>
        /// <summary>No Wit token: Groq (Whisper + Orpheus) does the hearing and the voice, Gemini as the backup.</summary>
        public bool UsesGroq => !HasToken && GroqClient.Has(config);
        bool HasGeminiKey => config != null && !string.IsNullOrWhiteSpace(config.geminiApiKey);
        public bool UsesGemini => !HasToken && !UsesGroq && HasGeminiKey;
        bool CloudVoice => !HasToken && (UsesGroq || HasGeminiKey);
        public bool CanListen => HasToken || UsesGroq || UsesGemini;
        bool PiperReady => config != null && config.offlineVoice && PiperVoice.Instance != null && PiperVoice.Instance.Ready;
        public bool CanSpeak => config != null && config.witVoiceOutput &&
                                (PiperReady || HasToken || (CloudVoice && config.geminiVoiceOutput && Time.unscaledTime >= _cloudVoiceRestAt));
        // When every free voice is used up, rest a while (Harley babbles meanwhile) and then try again: it never stays silent for good
        float _cloudVoiceRestAt, _groqVoiceRestAt, _geminiVoiceRestAt;
        public bool IsRecording { get; private set; }
        public bool IsTranscribing { get; private set; }
        /// <summary>0..1 microphone level while recording.</summary>
        public float Level { get; private set; }

        public event Action<string> Transcribed;   // "" = heard nothing
        public event Action<string> Failed;
        public event Action SpeechStarted, SpeechFinished;

        AudioClip _clip;
        string _mic;
        float _startedAt, _lastLoudAt;
        bool _heard;
        readonly float[] _probe = new float[512];
        string _voice;
        bool _voiceLookupDone;
        int _speakId;

        void Awake()
        {
            if (!GetComponent<PiperVoice>()) gameObject.AddComponent<PiperVoice>();
            if (!output)
            {
                output = gameObject.AddComponent<AudioSource>();
                output.playOnAwake = false;
                output.spatialBlend = 0.85f;
                output.minDistance = 0.4f;
                output.maxDistance = 6f;
            }
        }

        // ---------- listening ----------

        public void StartRecording()
        {
            if (IsRecording) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                Failed?.Invoke("mic permission");
                return;
            }
#endif
            if (Microphone.devices.Length == 0) { Failed?.Invoke("no microphone"); return; }
            _mic = Microphone.devices[0];
            _clip = Microphone.Start(_mic, false, Mathf.CeilToInt(maxSeconds) + 1, Rate);
            IsRecording = true;
            _heard = false;
            _startedAt = _lastLoudAt = Time.unscaledTime;
        }

        /// <summary>Stop now and send what was recorded.</summary>
        public void StopAndTranscribe()
        {
            if (!IsRecording) return;
            int end = Microphone.GetPosition(_mic);
            Microphone.End(_mic);
            IsRecording = false;
            Level = 0f;

            if (!_heard || end < Rate / 3 || _clip == null) { Transcribed?.Invoke(""); return; }

            var samples = new float[end * _clip.channels];
            _clip.GetData(samples, 0);
            var pcm = new byte[end * 2];
            for (int i = 0; i < end; i++)
            {
                short s = (short)Mathf.Clamp(samples[i * _clip.channels] * 32767f, -32768f, 32767f);
                pcm[i * 2] = (byte)(s & 0xff);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            StartCoroutine(Dictate(pcm));
        }

        public void Cancel()
        {
            if (!IsRecording) return;
            Microphone.End(_mic);
            IsRecording = false;
            Level = 0f;
        }

        void Update()
        {
            if (!IsRecording || _clip == null) return;
            int pos = Microphone.GetPosition(_mic);
            float now = Time.unscaledTime;
            if (pos > _probe.Length)
            {
                _clip.GetData(_probe, pos - _probe.Length);
                float sum = 0f;
                for (int i = 0; i < _probe.Length; i++) sum += _probe[i] * _probe[i];
                float rms = Mathf.Sqrt(sum / _probe.Length);
                Level = Mathf.Clamp01(rms * 12f);
                if (rms > speechThreshold) { _heard = true; _lastLoudAt = now; }
            }

            if (_heard && now - _lastLoudAt > silenceSeconds) StopAndTranscribe();
            else if (!_heard && now - _startedAt > noSpeechSeconds) StopAndTranscribe();
            else if (now - _startedAt > maxSeconds) StopAndTranscribe();
        }

        IEnumerator Dictate(byte[] pcm)
        {
            if (UsesGroq || UsesGemini) { yield return DictateCloud(pcm); yield break; }
            IsTranscribing = true;
            using (var req = new UnityWebRequest($"{Api}/dictation?v={ApiVersion}", UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(pcm);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", "Bearer " + config.witToken.Trim());
                req.SetRequestHeader("Content-Type", "audio/raw;encoding=signed-integer;bits=16;rate=16000;endian=little");
                req.timeout = 25;
                yield return req.SendWebRequest();
                IsTranscribing = false;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Failed?.Invoke(WitError(req));
                    yield break;
                }
                Transcribed?.Invoke(ParseDictation(req.downloadHandler.text));
            }
        }

        /// <summary>Turns a short recording (16 kHz mono 16-bit) into text with Wit or Gemini, without touching the Talk state.</summary>
        public IEnumerator TranscribeOnce(byte[] pcm, Action<string> done)
        {
            string text = null;
            if (UsesGroq)
            {
                yield return GroqClient.Transcribe(config, pcm, s => text = s, e => text = null);
                if (text == null && HasGeminiKey) yield return GeminiClient.Transcribe(config, pcm, s => text = s, e => text = null);
            }
            else if (UsesGemini)
            {
                yield return GeminiClient.Transcribe(config, pcm, s => text = s, e => text = null);
            }
            else if (HasToken)
            {
                using (var req = new UnityWebRequest($"{Api}/dictation?v={ApiVersion}", UnityWebRequest.kHttpVerbPOST))
                {
                    req.uploadHandler = new UploadHandlerRaw(pcm);
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Authorization", "Bearer " + config.witToken.Trim());
                    req.SetRequestHeader("Content-Type", "audio/raw;encoding=signed-integer;bits=16;rate=16000;endian=little");
                    req.timeout = 15;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) text = ParseDictation(req.downloadHandler.text);
                }
            }
            done(text);
        }

        IEnumerator DictateCloud(byte[] pcm)
        {
            IsTranscribing = true;
            string text = null, error = null;
            if (UsesGroq)
            {
                yield return GroqClient.Transcribe(config, pcm, s => text = s, e => error = e);
                if (error != null && HasGeminiKey)
                {
                    Debug.LogWarning("[Harley] Groq hearing failed, trying Gemini: " + error);
                    error = null;
                    yield return GeminiClient.Transcribe(config, pcm, s => text = s, e => error = e);
                }
            }
            else yield return GeminiClient.Transcribe(config, pcm, s => text = s, e => error = e);
            IsTranscribing = false;
            if (error != null)
            {
                Debug.LogWarning("[Harley] Gemini hearing failed: " + error);
                Failed?.Invoke(error.Contains("HTTP 429") ? "too many requests, try again" : error.Contains("HTTP 0") ? "no internet" : "I couldn't hear you, try again");
                yield break;
            }
            Transcribed?.Invoke(text ?? "");
        }

        /// <summary>Wit streams several JSON objects back to back; join the final transcriptions.</summary>
        static string ParseDictation(string body)
        {
            var finals = new List<string>();
            string last = "";
            foreach (string chunk in SplitObjects(body))
            {
                object parsed;
                try { parsed = MiniJson.Parse(chunk); } catch { continue; }
                if (!(parsed is Dictionary<string, object> o)) continue;
                string text = o.Str("text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                last = text;
                bool final = (o.TryGetValue("is_final", out object f) && f is bool b && b) || o.Str("type") == "FINAL_TRANSCRIPTION";
                if (final) finals.Add(text.Trim());
            }
            return finals.Count > 0 ? string.Join(" ", finals) : last.Trim();
        }

        static IEnumerable<string> SplitObjects(string s)
        {
            int depth = 0, start = -1;
            bool inStr = false, esc = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{') { if (depth++ == 0) start = i; }
                else if (c == '}' && depth > 0 && --depth == 0 && start >= 0) yield return s.Substring(start, i - start + 1);
            }
        }

        // ---------- speaking ----------

        public bool IsSpeaking => output != null && output.isPlaying;

        public void Speak(string text)
        {
            if (!CanSpeak || string.IsNullOrWhiteSpace(text)) return;
            StartCoroutine(SpeakRoutine(text.Trim(), ++_speakId));
        }

        public void StopSpeaking()
        {
            _speakId++;
            if (output != null && output.isPlaying) { output.Stop(); SpeechFinished?.Invoke(); }
        }

        /// <summary>How long to leave a voice alone after it failed: the server's retry-after (daily limit = hours), else a few minutes.</summary>
        static float RestFor(string err)
        {
            if (string.IsNullOrEmpty(err)) return 60f;
            int at = err.IndexOf("retry-after=", System.StringComparison.Ordinal);
            if (at >= 0 && float.TryParse(err.Substring(at + 12).Split(' ', '\n')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sec))
                return Mathf.Clamp(sec, 30f, 6 * 3600f);
            return err.Contains("429") ? 1800f : 60f;
        }

        IEnumerator SpeakRoutine(string text, int id)
        {
            if (PiperReady)
            {
                AudioClip local = null;
                yield return PiperVoice.Instance.Speak(text, c => local = c);
                if (id != _speakId) yield break;
                if (local != null)
                {
                    output.clip = local;
                    output.volume = Mathf.Clamp01(config.voiceVolume);
                    output.Play();
                    SpeechStarted?.Invoke();
                    while (id == _speakId && output.isPlaying) yield return null;
                    if (id == _speakId) SpeechFinished?.Invoke();
                    yield break;
                }
                // else: fall through to the cloud voices
            }
            if (CloudVoice)
            {
                AudioClip clip = null; string err = null;
                float now = Time.unscaledTime;
                if (UsesGroq && now >= _groqVoiceRestAt)
                {
                    yield return GroqClient.Speak(config, text, c => clip = c, e => err = e);
                    if (clip == null) { _groqVoiceRestAt = Time.unscaledTime + RestFor(err); Debug.LogWarning("[Harley] Groq voice: " + err); }
                }
                if (clip == null && HasGeminiKey && Time.unscaledTime >= _geminiVoiceRestAt && id == _speakId)
                {
                    yield return GeminiClient.Speak(config, text, c => clip = c, e => err = e);
                    if (clip == null) { _geminiVoiceRestAt = Time.unscaledTime + (err != null && err.Contains("no Gemini voice") ? 99999f : 600f); Debug.LogWarning("[Harley] Gemini voice: " + err); }
                }
                if (id != _speakId) yield break;
                if (clip == null)
                {
                    _cloudVoiceRestAt = Mathf.Min(_groqVoiceRestAt, _geminiVoiceRestAt);
                    Failed?.Invoke("voice: " + err);
                    yield break;
                }
                output.clip = clip;
                output.volume = Mathf.Clamp01(config.voiceVolume);
                output.Play();
                SpeechStarted?.Invoke();
                while (id == _speakId && output.isPlaying) yield return null;
                if (id == _speakId) SpeechFinished?.Invoke();
                yield break;
            }
            if (!_voiceLookupDone) yield return FindVoice();
            if (id != _speakId) yield break;
            if (text.Length > 280) text = text.Substring(0, 280);

            var body = new Dictionary<string, object> { { "q", text } };
            if (!string.IsNullOrEmpty(_voice)) body["voice"] = _voice;

            using (var req = new UnityWebRequest($"{Api}/synthesize?v={ApiVersion}", UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MiniJson.Serialize(body)));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", "Bearer " + config.witToken.Trim());
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "audio/wav");
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (id != _speakId) yield break;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Failed?.Invoke(WitError(req));
                    yield break;
                }
                AudioClip clip = WavToClip(req.downloadHandler.data);
                if (clip == null) { Failed?.Invoke("could not read the voice audio"); yield break; }

                output.clip = clip;
                output.volume = config != null ? Mathf.Clamp01(config.voiceVolume) : 1f;
                output.Play();
                SpeechStarted?.Invoke();
                while (id == _speakId && output.isPlaying) yield return null;
                if (id == _speakId) SpeechFinished?.Invoke();
            }
        }

        /// <summary>Picks the configured voice, or the first voice Wit offers for your app's language.</summary>
        IEnumerator FindVoice()
        {
            _voiceLookupDone = true;
            if (!string.IsNullOrWhiteSpace(config.witVoice)) { _voice = config.witVoice.Trim(); yield break; }
            using (var req = UnityWebRequest.Get($"{Api}/voices?v={ApiVersion}"))
            {
                req.SetRequestHeader("Authorization", "Bearer " + config.witToken.Trim());
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;
                object parsed = null;
                try { parsed = MiniJson.Parse(req.downloadHandler.text); } catch { }
                if (!(parsed is Dictionary<string, object> locales)) yield break;

                string lang = (config.witLanguage ?? "").Trim().ToLowerInvariant();
                string fallback = null;
                foreach (var kv in locales)
                {
                    if (!(kv.Value is List<object> voices) || voices.Count == 0) continue;
                    string first = (voices[0] as Dictionary<string, object>)?.Str("name");
                    if (string.IsNullOrEmpty(first)) continue;
                    if (fallback == null) fallback = first;
                    if (lang.Length > 0 && kv.Key.ToLowerInvariant().StartsWith(lang)) { _voice = first; yield break; }
                }
                _voice = fallback;
            }
        }

        static string WitError(UnityWebRequest req)
        {
            if (req.responseCode == 400 || req.responseCode == 401 || req.responseCode == 403) return "Wit token is wrong";
            if (req.responseCode == 0) return "no internet";
            return "Wit HTTP " + req.responseCode;
        }

        static AudioClip WavToClip(byte[] wav)
        {
            if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return null;
            int channels = 1, rate = 16000, bits = 16, pos = 12;
            int dataStart = -1, dataLen = 0;
            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(wav, pos + 10);
                    rate = BitConverter.ToInt32(wav, pos + 12);
                    bits = BitConverter.ToInt16(wav, pos + 22);
                }
                else if (id == "data")
                {
                    dataStart = pos + 8;
                    // Streaming WAVs can report a huge or zero size: use what actually arrived.
                    dataLen = size <= 0 || dataStart + size > wav.Length ? wav.Length - dataStart : size;
                    break;
                }
                pos += 8 + Math.Max(0, size) + (size & 1);
            }
            if (dataStart < 0 || bits != 16 || channels < 1) return null;

            int frames = dataLen / 2 / channels;
            if (frames <= 0) return null;
            var data = new float[frames * channels];
            for (int i = 0; i < data.Length; i++) data[i] = BitConverter.ToInt16(wav, dataStart + i * 2) / 32768f;
            var clip = AudioClip.Create("Harley voice", frames, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
