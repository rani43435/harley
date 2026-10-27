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
        public bool CanListen => HasToken;
        public bool CanSpeak => HasToken && config.witVoiceOutput;
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

        IEnumerator SpeakRoutine(string text, int id)
        {
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
