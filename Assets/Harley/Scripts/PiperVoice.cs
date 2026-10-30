using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Harley's own voice, on the headset: Piper neural voices run by sherpa-onnx (no internet, no limits, free).
    /// English: "kristin" (US, public-domain recordings). Arabic: "kareem" (Jordan).
    /// The voices ship in StreamingAssets/Piper and are copied to the app's storage on first launch
    /// (the speech engine needs real files). Generation runs on a worker thread.
    /// </summary>
    public class PiperVoice : MonoBehaviour
    {
        public static PiperVoice Instance { get; private set; }

        [Tooltip("Speaking speed (1 = normal, higher = faster).")]
        public float speed = 1.05f;
        [Tooltip("CPU threads for the voice.")]
        public int threads = 2;

        /// <summary>True once the voice files are in place and the engine can be used.</summary>
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }

        const string Folder = "Piper";
        const string Version = "1"; // bump when the voice files change

        class Voice { public string dir, model; public object tts; }
        readonly Dictionary<string, Voice> _voices = new Dictionary<string, Voice>
        {
            { "en", new Voice { dir = "en", model = "en_US-kristin-medium.onnx" } },
            { "ar", new Voice { dir = "ar", model = "ar_JO-kareem-medium.onnx" } },
        };
        readonly object _lock = new object();
        string _root;

        void Awake() { Instance = this; }

        IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _root = Path.Combine(Application.persistentDataPath, Folder);
            string marker = Path.Combine(_root, "ready-" + Version);
            if (!File.Exists(marker))
            {
                yield return CopyFromStreamingAssets();
                if (Failed) yield break;
                File.WriteAllText(marker, "ok");
            }
            Ready = true;
            Debug.Log("[Harley] Piper voice ready (offline).");
            // Warm up English in the background so the first sentence is quick
            new Thread(() => { try { lock (_lock) Get("en"); } catch (Exception e) { Debug.LogWarning("[Harley] Piper warm-up: " + e.Message); } }) { IsBackground = true }.Start();
#else
            Failed = true; // the native engine is only built into the Quest app
            yield break;
#endif
        }

        IEnumerator CopyFromStreamingAssets()
        {
            string src = Application.streamingAssetsPath + "/" + Folder + "/";
            string listText = null;
            using (var req = UnityWebRequest.Get(src + "files.txt"))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) listText = req.downloadHandler.text;
            }
            if (string.IsNullOrEmpty(listText)) { Failed = true; Debug.Log("[Harley] Piper voice files not in the app, using other voices."); yield break; }

            foreach (string raw in listText.Split('\n'))
            {
                string rel = raw.Trim();
                if (rel.Length == 0) continue;
                string dst = Path.Combine(_root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                using (var req = new UnityWebRequest(src + rel, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(dst) { removeFileOnAbort = true };
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { Failed = true; Debug.LogWarning("[Harley] Piper copy failed: " + rel + " " + req.error); yield break; }
                }
            }
        }

        /// <summary>"ar" for Arabic text, else "en".</summary>
        public static string LanguageOf(string text)
        {
            foreach (char ch in text) if (ch >= '؀' && ch <= 'ۿ') return "ar";
            return "en";
        }

        /// <summary>Makes the audio for text. done(clip) on the main thread; clip is null if it failed.</summary>
        public IEnumerator Speak(string text, Action<AudioClip> done)
        {
            if (!Ready || string.IsNullOrWhiteSpace(text)) { done(null); yield break; }
            string lang = LanguageOf(text);
            float[] samples = null; int rate = 0; string error = null;
            bool finished = false;
            var worker = new Thread(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        var tts = Get(lang);
                        using (var audio = new GeneratedAudio(tts, Clean(text), speed))
                        {
                            samples = audio.samples;
                            rate = audio.rate;
                        }
                    }
                }
                catch (Exception e) { error = e.Message; }
                finally { finished = true; }
            }) { IsBackground = true };
            worker.Start();
            while (!finished) yield return null;
            if (samples == null || samples.Length == 0 || rate <= 0) { Debug.LogWarning("[Harley] Piper: " + (error ?? "no audio")); done(null); yield break; }
            var clip = AudioClip.Create("Harley voice", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            done(clip);
        }

        static string Clean(string text)
        {
            // No emoji / markdown symbols read out loud
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsSurrogate(c) || c == '*' || c == '#' || c == '_' || c == '`') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- the engine (Quest only) ----------

#if UNITY_ANDROID && !UNITY_EDITOR
        SherpaOnnx.OfflineTts Get(string lang)
        {
            var v = _voices[lang];
            if (v.tts is SherpaOnnx.OfflineTts ready) return ready;
            var cfg = new SherpaOnnx.OfflineTtsConfig();
            cfg.Init();
            cfg.Model.Vits.Model = Path.Combine(_root, v.dir, v.model);
            cfg.Model.Vits.Tokens = Path.Combine(_root, v.dir, "tokens.txt");
            cfg.Model.Vits.DataDir = Path.Combine(_root, "espeak-ng-data");
            cfg.Model.NumThreads = Mathf.Clamp(threads, 1, 4);
            cfg.MaxNumSentences = 2;
            var tts = new SherpaOnnx.OfflineTts(cfg);
            v.tts = tts;
            return tts;
        }

        sealed class GeneratedAudio : IDisposable
        {
            public float[] samples; public int rate;
            SherpaOnnx.OfflineTtsGeneratedAudio _audio;
            public GeneratedAudio(SherpaOnnx.OfflineTts tts, string text, float speed)
            {
                _audio = tts.Generate(text, speed, 0);
                samples = _audio.Samples;
                rate = _audio.SampleRate;
            }
            public void Dispose() { _audio?.Dispose(); _audio = null; }
        }
#else
        object Get(string lang) => throw new NotSupportedException("Piper runs on the headset only");
        sealed class GeneratedAudio : IDisposable
        {
            public float[] samples; public int rate;
            public GeneratedAudio(object tts, string text, float speed) { throw new NotSupportedException(); }
            public void Dispose() { }
        }
#endif

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            lock (_lock)
                foreach (var v in _voices.Values)
                    if (v.tts is SherpaOnnx.OfflineTts t) { try { t.Dispose(); } catch { } v.tts = null; }
#endif
        }
    }
}
