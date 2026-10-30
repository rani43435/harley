using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Language lessons with your own room (inspired by Meta's open-source Spatial Lingo):
    /// Harley flies to a real thing she spotted (cup, laptop, chair...) or a piece of furniture, marks it
    /// with a "?" and asks what it's called in the language you're learning. Say it out loud; she tells
    /// you if it's right, says it properly (with der/die/das in German) and the word stays floating on
    /// the object. Then she flies to the next one.
    /// </summary>
    public class HarleyLanguage : MonoBehaviour
    {
        public static HarleyLanguage Instance { get; private set; }
        public static readonly string[] Languages = { "German", "English", "Spanish", "French", "Arabic", "Italian", "Turkish" };

        public CompanionBrain brain;
        public bool Active { get; private set; }
        public string Language { get; private set; } = "German";
        public string CurrentThing { get; private set; } = "";
        public int Learned => _done.Count;

        class Target { public string name; public Vector3 pos; }
        Target _current;
        readonly HashSet<string> _done = new HashSet<string>();
        readonly List<GameObject> _labels = new List<GameObject>();
        GameObject _question;
        float _nextAt = -1f;

        void Awake()
        {
            Instance = this;
            if (!brain) brain = GetComponent<CompanionBrain>();
            Language = PlayerPrefs.GetString("harley.learn.lang", "German");
        }

        public string Label => Active ? "Learn: " + Language : "Learn";

        /// <summary>Menu "Learn" button: starts / stops the lesson.</summary>
        public void MenuPress()
        {
            if (Active) End(); else Begin(Language);
        }

        /// <summary>Menu: pick the language (switches a running lesson too).</summary>
        public void SetLanguage(string language)
        {
            if (Active) { Begin(language); return; }
            Language = language;
            PlayerPrefs.SetString("harley.learn.lang", Language);
        }

        /// <summary>Menu language button: the next language, round and round (switches a running lesson too).</summary>
        public void NextLanguage()
        {
            int i = System.Array.IndexOf(Languages, Language);
            string next = Languages[(i + 1 + Languages.Length) % Languages.Length];
            if (Active) Begin(next);
            else { Language = next; PlayerPrefs.SetString("harley.learn.lang", Language); }
        }

        public void Begin(string language)
        {
            if (!string.IsNullOrWhiteSpace(language))
            {
                foreach (var l in Languages)
                    if (language.ToLowerInvariant().Contains(l.ToLowerInvariant()) || l.ToLowerInvariant().StartsWith(language.ToLowerInvariant().Trim()))
                    { Language = l; break; }
                if (language.Contains("ألماني") || language.Contains("الماني")) Language = "German";
                if (language.Contains("انجليزي") || language.Contains("إنجليزي")) Language = "English";
                if (language.Contains("اسباني") || language.Contains("إسباني")) Language = "Spanish";
                if (language.Contains("فرنسي")) Language = "French";
            }
            PlayerPrefs.SetString("harley.learn.lang", Language);
            bool was = Active;
            Active = true;
            ClearQuestion();
            if (was && _current != null) { _current = null; CurrentThing = ""; _nextAt = Time.time + 3.5f; }
            if (was) brain?.LessonEvent($"The player switched the lesson to {Language}. Say so in one short, fun sentence.");
            else brain?.LessonEvent($"The player started a {Language} lesson with you. Say hi as their {Language} teacher in one short, fun sentence; you'll fly to things in their room and ask what they're called.");
            _nextAt = Time.time + 3.5f;
        }

        public void End(bool quiet = false)
        {
            if (!Active) return;
            Active = false;
            ClearQuestion();
            _current = null;
            CurrentThing = "";
            if (!quiet) brain?.LessonEvent($"The {Language} lesson is over ({_done.Count} words learned). Say a short, proud goodbye to the lesson.");
        }

        public void ClearWords()
        {
            foreach (var g in _labels) if (g) Destroy(g);
            _labels.Clear();
            _done.Clear();
        }

        /// <summary>The brain says the player got it (or was told): show the word on the thing, then move on.</summary>
        public void Answered(string word)
        {
            if (!Active || _current == null) return;
            ClearQuestion();
            if (!string.IsNullOrWhiteSpace(word))
            {
                var l = MakeLabel(word.Trim(), _current.pos + Vector3.up * 0.12f, new Color(0.75f, 0.62f, 1f));
                _labels.Add(l);
                while (_labels.Count > 14) { if (_labels[0]) Destroy(_labels[0]); _labels.RemoveAt(0); }
            }
            _done.Add(_current.name);
            _current = null;
            CurrentThing = "";
            _nextAt = Time.time + 5f;
        }

        void Update()
        {
            if (!Active || _current != null || _nextAt < 0f || Time.time < _nextAt) return;
            if (brain != null && brain.voice != null && (brain.voice.IsSpeaking || brain.voice.IsListening)) { _nextAt = Time.time + 1f; return; }
            _nextAt = -1f;
            Next();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var g in _labels) if (g) Face(g.transform, cam.transform);
            if (_question) Face(_question.transform, cam.transform);
        }

        static void Face(Transform t, Transform head)
        {
            Vector3 d = t.position - head.position; d.y = 0;
            if (d.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
        }

        void Next()
        {
            var t = Pick();
            if (t == null)
            {
                brain?.LessonEvent("You couldn't find anything new in the room to teach right now. Ask the player to look around slowly or move to another spot, playfully.");
                _nextAt = Time.time + 20f;
                return;
            }
            _current = t;
            CurrentThing = t.name;
            ClearQuestion();
            _question = MakeLabel("?", t.pos + Vector3.up * 0.14f, Color.white);
            if (brain != null && brain.movement is CompanionMovement m)
            {
                Vector3 toHead = m.head != null ? (m.head.position - t.pos) : Vector3.back;
                toHead.y = 0;
                Vector3 hover = t.pos + Vector3.up * 0.22f + (toHead.sqrMagnitude > 0.001f ? toHead.normalized * 0.2f : Vector3.zero);
                m.FlyTo(hover, 12f, t.pos);
            }
            brain?.LessonEvent($"[lesson] You just flew to the player's real {t.name}. Ask them what it is called in {Language}, short and playful. Don't say the answer yet.");
        }

        Target Pick()
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            var options = new List<Target>();
            var sight = HarleyObjectSight.Instance;
            if (sight != null)
                foreach (var s in sight.Objects)
                    if (s.count >= 2 && !_done.Contains(s.label)) options.Add(new Target { name = s.label, pos = s.position });
            var room = brain != null ? brain.room : null;
            if (room != null)
                foreach (var th in room.Things)
                    if (!_done.Contains(th.kind)) options.Add(new Target { name = th.kind, pos = th.isVolume ? th.top : th.center });
            if (options.Count == 0) return null;
            // Nearby things first, with a little surprise
            options.Sort((a, b) => Score(a, head).CompareTo(Score(b, head)));
            return options[Random.Range(0, Mathf.Min(3, options.Count))];
        }

        static float Score(Target t, Transform head) => head == null ? 0 : Vector3.Distance(t.pos, head.position);

        void ClearQuestion()
        {
            if (_question) Destroy(_question);
            _question = null;
        }

        static GameObject MakeLabel(string text, Vector3 at, Color color)
        {
            var go = new GameObject("Word " + text);
            go.transform.position = at;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = 0.55f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = new Color32(30, 15, 60, 255);
            tmp.rectTransform.sizeDelta = new Vector2(0.6f, 0.15f);
            return go;
        }

        /// <summary>For the prompt while a lesson is on.</summary>
        public string DescribeForPrompt()
        {
            if (!Active) return "";
            return "\nLANGUAGE LESSON MODE: you are the player's playful " + Language + " teacher, using real things in their room. " +
                   (string.IsNullOrEmpty(CurrentThing) ? "You're picking the next thing. " : "Right now you're hovering at their " + CurrentThing + " and asked what it's called in " + Language + ". ") +
                   "When they answer: if it's right (small mistakes are fine), praise them, say the word clearly" + (Language == "German" ? " with its article (der/die/das) and the plural" : "") +
                   " and one tiny example sentence, then use action next_word with target = the correct " + Language + " word" + (Language == "German" ? " with its article" : "") + ". " +
                   "If it's wrong or they don't know: give one gentle hint; on the second try tell them the answer and use next_word with the word. " +
                   "Talk in the player's own language, but say the " + Language + " words clearly. If they want to stop, use action stop_lesson. " +
                   "Words learned so far: " + _done.Count + ".";
        }
    }
}
