using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    public enum Sfx { Pop, Boing, ChirpUp, ChirpDown, Ding, Blip, Whee, Thud, Purr }

    /// <summary>
    /// Harley's sounds, all synthesized at startup (no audio files needed):
    /// a cute babble voice (Animal-Crossing style) while it talks, and little
    /// reaction sounds for poke, pet, grab, throw, listening and thinking.
    /// Sounds are spatial, so they come from where Harley is in your room.
    /// </summary>
    public class CompanionSounds : MonoBehaviour
    {
        public CompanionConfig config;
        [Tooltip("Optional. Created automatically if empty.")]
        public AudioSource sfxSource;
        [Tooltip("Optional. Created automatically if empty.")]
        public AudioSource voiceSource;

        /// <summary>0..1 loudness of the babble right now (drives the mouth).</summary>
        public float BabbleLevel { get; private set; }
        public bool IsBabbling => _queue.Count > 0 || Time.time < _lastSyllableEnds;

        const int Rate = 44100;
        AudioClip[] _syllables;
        readonly Dictionary<Sfx, AudioClip> _sfx = new Dictionary<Sfx, AudioClip>();

        struct Syllable { public float at; public float pitch; public int clip; }
        readonly Queue<Syllable> _queue = new Queue<Syllable>();
        float _lastSyllableEnds;
        float _env;

        float Volume => config != null ? config.soundVolume : 0.7f;

        void Awake()
        {
            if (sfxSource == null) sfxSource = MakeSource("Sfx");
            if (voiceSource == null) voiceSource = MakeSource("Babble");
            BuildClips();
        }

        AudioSource MakeSource(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;          // 3D: comes from Harley
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 0.5f;
            s.maxDistance = 12f;
            s.dopplerLevel = 0f;
            return s;
        }

        // ---------- public API ----------

        public void Play(Sfx s, float pitch = 1f, float volume = 1f)
        {
            if (!_sfx.TryGetValue(s, out var clip) || sfxSource == null) return;
            sfxSource.pitch = pitch;
            sfxSource.PlayOneShot(clip, Volume * volume);
        }

        /// <summary>Queues babble for a sentence. Returns how long it will take (seconds).</summary>
        public float Babble(string text, Emotion emotion)
        {
            StopBabble();
            if (string.IsNullOrWhiteSpace(text)) return 0f;

            float basePitch = emotion switch
            {
                Emotion.Excited => 1.25f,
                Emotion.Surprised => 1.3f,
                Emotion.Happy => 1.12f,
                Emotion.Curious => 1.15f,
                Emotion.Love => 1.05f,
                Emotion.Sad => 0.85f,
                Emotion.Sleepy => 0.8f,
                _ => 1f
            };
            float step = emotion == Emotion.Sleepy || emotion == Emotion.Sad ? 0.13f : emotion == Emotion.Excited ? 0.095f : 0.11f;

            float t = Time.time + 0.05f;
            string[] words = text.Split(' ');
            for (int w = 0; w < words.Length; w++)
            {
                string word = words[w];
                int letters = 0;
                foreach (char c in word) if (char.IsLetterOrDigit(c)) letters++;
                if (letters > 0)
                {
                    int count = Mathf.Clamp(Mathf.RoundToInt(letters / 2.6f), 1, 4);
                    for (int i = 0; i < count; i++)
                    {
                        float p = basePitch * Random.Range(0.9f, 1.12f);
                        bool lastOfQuestion = (word.EndsWith("?") || word.EndsWith("\u061F")) && i == count - 1;
                        if (lastOfQuestion) p *= 1.25f;
                        _queue.Enqueue(new Syllable { at = t, pitch = p, clip = Random.Range(0, _syllables.Length) });
                        t += step;
                    }
                }
                char end = word.Length > 0 ? word[word.Length - 1] : ' ';
                if (end == '.' || end == '!' || end == '?' || end == '\u061F') t += 0.28f;          // incl. Arabic ?
                else if (end == ',' || end == ';' || end == ':' || end == '\u060C') t += 0.16f;     // incl. Arabic comma
                else t += 0.04f;
            }
            _lastSyllableEnds = t;
            return t - Time.time;
        }

        public void StopBabble()
        {
            _queue.Clear();
            _lastSyllableEnds = 0f;
        }

        void Update()
        {
            while (_queue.Count > 0 && Time.time >= _queue.Peek().at)
            {
                var s = _queue.Dequeue();
                if (voiceSource != null)
                {
                    voiceSource.pitch = s.pitch;
                    voiceSource.PlayOneShot(_syllables[s.clip], Volume * 0.8f);
                }
                _env = 1f;
            }
            _env = Mathf.MoveTowards(_env, 0f, Time.deltaTime / 0.1f);
            BabbleLevel = _env;
        }

        // ---------- synthesis ----------

        void BuildClips()
        {
            // Five vowel colours, each as harmonic weights over a 620 Hz voice.
            float[][] vowels =
            {
                new[] { 1f, 0.7f, 0.5f, 0.3f },   // a
                new[] { 1f, 0.35f, 0.6f, 0.2f },  // e
                new[] { 1f, 0.2f, 0.15f, 0.45f }, // i
                new[] { 1f, 0.8f, 0.25f, 0.1f },  // o
                new[] { 1f, 0.5f, 0.1f, 0.05f },  // u
            };
            _syllables = new AudioClip[vowels.Length * 2];
            for (int v = 0; v < vowels.Length; v++)
                for (int k = 0; k < 2; k++)
                    _syllables[v * 2 + k] = MakeSyllable("syl" + v + k, vowels[v], 600f + k * 45f);

            _sfx[Sfx.Pop] = Sweep("pop", 0.14f, 320f, 980f, 18f, 0.9f);
            _sfx[Sfx.Boing] = Boing();
            _sfx[Sfx.ChirpUp] = TwoBlips("chirpUp", 760f, 1180f);
            _sfx[Sfx.ChirpDown] = Sweep("chirpDown", 0.3f, 900f, 430f, 7f, 0.7f);
            _sfx[Sfx.Ding] = TwoBlips("ding", 880f, 1320f);
            _sfx[Sfx.Blip] = Sweep("blip", 0.06f, 1250f, 1150f, 40f, 0.35f);
            _sfx[Sfx.Whee] = Sweep("whee", 0.5f, 380f, 1600f, 3f, 0.75f, vibrato: 9f);
            _sfx[Sfx.Thud] = Thud();
            _sfx[Sfx.Purr] = Purr();
        }

        static AudioClip MakeSyllable(string name, float[] harmonics, float f0)
        {
            int n = (int)(Rate * 0.085f);
            var data = new float[n];
            float norm = 0f;
            foreach (var h in harmonics) norm += h;
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float u = i / (float)n;
                float f = f0 * (1f - 0.06f * u) * (1f + 0.01f * Mathf.Sin(2f * Mathf.PI * 30f * t));
                phase += 2.0 * Mathf.PI * f / Rate;
                float s = 0f;
                for (int h = 0; h < harmonics.Length; h++) s += harmonics[h] * Mathf.Sin((float)(phase * (h + 1)));
                float env = Mathf.Min(1f, t / 0.006f) * Mathf.Pow(1f - u, 1.5f);
                data[i] = s / norm * env * 0.9f;
            }
            return Clip(name, data);
        }

        static AudioClip Sweep(string name, float len, float f1, float f2, float decay, float amp, float vibrato = 0f)
        {
            int n = (int)(Rate * len);
            var data = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float u = i / (float)n;
                float f = Mathf.Lerp(f1, f2, u * u * (3f - 2f * u));
                if (vibrato > 0f) f *= 1f + 0.04f * Mathf.Sin(2f * Mathf.PI * vibrato * t);
                phase += 2.0 * Mathf.PI * f / Rate;
                float env = Mathf.Min(1f, t / 0.005f) * Mathf.Exp(-decay * t) * (1f - u);
                data[i] = (Mathf.Sin((float)phase) * 0.8f + Mathf.Sin((float)phase * 2f) * 0.2f) * env * amp;
            }
            return Clip(name, data);
        }

        static AudioClip TwoBlips(string name, float fa, float fb)
        {
            int half = (int)(Rate * 0.11f);
            var data = new float[half * 2];
            for (int i = 0; i < data.Length; i++)
            {
                bool second = i >= half;
                int j = second ? i - half : i;
                float t = j / (float)Rate;
                float f = second ? fb : fa;
                float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-22f * t);
                data[i] = Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.7f;
            }
            return Clip(name, data);
        }

        static AudioClip Boing()
        {
            int n = (int)(Rate * 0.38f);
            var data = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float f = 430f + 150f * Mathf.Sin(2f * Mathf.PI * 17f * t) * Mathf.Exp(-5f * t);
                phase += 2.0 * Mathf.PI * f / Rate;
                float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-7f * t);
                data[i] = Mathf.Sin((float)phase) * env * 0.85f;
            }
            return Clip("boing", data);
        }

        static AudioClip Thud()
        {
            int n = (int)(Rate * 0.18f);
            var data = new float[n];
            var rng = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Exp(-28f * t);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-80f * t) * 0.3f;
                data[i] = (Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(160f, 90f, t / 0.18f) * t) * env + noise) * 0.9f;
            }
            return Clip("thud", data);
        }

        static AudioClip Purr()
        {
            int n = (int)(Rate * 0.9f);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float u = i / (float)n;
                float am = 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * 24f * t);
                float env = Mathf.Min(1f, t / 0.08f) * (1f - u);
                data[i] = (Mathf.Sin(2f * Mathf.PI * 170f * t) * 0.7f + Mathf.Sin(2f * Mathf.PI * 340f * t) * 0.3f) * am * env * 0.5f;
            }
            return Clip("purr", data);
        }

        static AudioClip Clip(string name, float[] data)
        {
            var c = AudioClip.Create("harley_" + name, data.Length, 1, Rate, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
