using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Harley
{
    public enum CompanionMode { MixedReality, Window }

    /// <summary>
    /// Harley's mind. Ties everything together:
    /// hears you (CompanionVoice), sees what you see (CompanionSight: camera or screen),
    /// knows your room (CompanionRoom), remembers you (CompanionMemory), can look things
    /// up online (CompanionTools), asks Claude, then speaks, shows an emotion and moves
    /// (CompanionVisual / CompanionMotion).
    /// Also reacts to pokes, petting, waves, being picked up and thrown, comments
    /// on things on its own, and dozes off when you ignore it for a while.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CompanionBrain : MonoBehaviour
    {
        public CompanionConfig config;
        [Tooltip("Mixed Reality: Harley flies around your room. Window: Harley lives in an app window and sees your screen.")]
        public CompanionMode mode = CompanionMode.MixedReality;

        [Header("Parts (found automatically on this object and its children)")]
        public ClaudeClient claude;
        public CompanionSight eyes;
        public CompanionMotion movement;
        public CompanionVisual visual;
        public CompanionVoice voice;
        public CompanionInput interaction;
        public CompanionMemory memory;
        public CompanionRoom room;
        public CompanionSounds sounds;
        public CompanionTools tools;

        [Header("Behaviour")]
        public bool greetOnStart = true;
        [Tooltip("Doze off after this many seconds without any interaction.")]
        public float sleepAfterSeconds = 180f;
        public bool logToConsole = true;

        [Header("Editor testing")]
        [Tooltip("Press Enter in Play mode (or use the component's context menu) to send this as if spoken.")]
        public string testMessage = "What do you think of my room?";

        readonly List<ChatTurn> _history = new List<ChatTurn>();
        string _pendingSpeech;
        bool _requestRunning;
        float _nextProactiveAt, _lastEventCallAt = -999f, _lastUserSpokeAt = -999f;
        bool _asleep;
        bool _wasSpeaking;
        bool _lastReplyWantsAnswer;

        void Awake()
        {
            if (!claude) claude = GetComponentInChildren<ClaudeClient>();
            if (!eyes) eyes = GetComponentInChildren<CompanionSight>();
            if (!movement) movement = GetComponentInChildren<CompanionMotion>();
            if (!visual) visual = GetComponentInChildren<CompanionVisual>();
            if (!voice) voice = GetComponentInChildren<CompanionVoice>();
            if (!interaction) interaction = GetComponentInChildren<CompanionInput>();
            if (!memory) memory = GetComponentInChildren<CompanionMemory>();
            if (!room) room = GetComponentInChildren<CompanionRoom>();
            if (!sounds) sounds = GetComponentInChildren<CompanionSounds>();
            if (!tools) tools = GetComponentInChildren<CompanionTools>();

            // Missing parts can be Unity "fake null" objects in the Editor: make them real null so ?. is safe.
            if (!claude) claude = null;
            if (!eyes) eyes = null;
            if (!movement) movement = null;
            if (!visual) visual = null;
            if (!voice) voice = null;
            if (!interaction) interaction = null;
            if (!memory) memory = null;
            if (!room) room = null;
            if (!sounds) sounds = null;
            if (!tools) tools = null;

            if (claude && config) claude.config = config;
            if (tools)
            {
                if (config) tools.config = config;
                if (!tools.claude) tools.claude = claude;
            }
            if (memory && config) memory.config = config;
            if (sounds && config) sounds.config = config;
            if (voice)
            {
                if (config) voice.config = config;
                if (!voice.sounds) voice.sounds = sounds;
            }
            if (movement is CompanionMovement roomMover && !roomMover.room) roomMover.room = room;
            if (interaction is CompanionInteraction hands && !hands.movement) hands.movement = movement;
        }

        void OnEnable()
        {
            if (voice)
            {
                voice.Transcribed += OnPlayerSaid;
                voice.ListeningChanged += OnListeningChanged;
            }
            if (interaction)
            {
                interaction.Poked += OnPoked;
                interaction.Petted += OnPetted;
                interaction.Waved += OnWaved;
                interaction.TalkRequested += OnTalkRequested;
                interaction.Grabbed += OnGrabbed;
                interaction.Released += OnReleased;
            }
            if (movement)
            {
                movement.Bounced += OnBounced;
                movement.Landed += OnLanded;
            }
        }

        void OnDisable()
        {
            _requestRunning = false; // a coroutine stopped mid-request must not block Harley forever
            if (voice)
            {
                voice.Transcribed -= OnPlayerSaid;
                voice.ListeningChanged -= OnListeningChanged;
            }
            if (interaction)
            {
                interaction.Poked -= OnPoked;
                interaction.Petted -= OnPetted;
                interaction.Waved -= OnWaved;
                interaction.TalkRequested -= OnTalkRequested;
                interaction.Grabbed -= OnGrabbed;
                interaction.Released -= OnReleased;
            }
            if (movement)
            {
                movement.Bounced -= OnBounced;
                movement.Landed -= OnLanded;
            }
        }

        IEnumerator Start()
        {
            if (!config)
            {
                Debug.LogError("[Harley] No CompanionConfig assigned on CompanionBrain.");
                yield break;
            }
            visual?.SetEmotion(Emotion.Happy);
            visual?.PopIn();
            sounds?.Play(Sfx.Pop);
            ScheduleProactive();
            if (!GetComponent<HarleyComputer>()) gameObject.AddComponent<HarleyComputer>().brain = this;
            if (!GetComponent<HarleyPlan>()) gameObject.AddComponent<HarleyPlan>().config = config;
            if (mode != CompanionMode.Window)
            {
                // Own eyes for objects (YOLO, on the headset) and the language lessons
                if (!FindAnyObjectByType<HarleyObjectSight>()) new GameObject("Harley Object Sight").AddComponent<HarleyObjectSight>();
                if (!GetComponent<HarleyLanguage>()) gameObject.AddComponent<HarleyLanguage>().brain = this;
            }
            if (greetOnStart)
            {
                yield return new WaitForSeconds(2.5f); // camera warm-up, room scan load
                Ask(mode == CompanionMode.Window
                        ? "[event] The player just opened your app window. Greet them in a short, playful way (by name if you know it). " +
                          "If you can see what they're doing on screen, mention it."
                        : "[event] The app just opened and you popped into the player's room. Greet them in a short, playful way " +
                          "(by name if you know it). If you can see something fun in the room, mention it.",
                    true, false);
            }
        }

        void Update()
        {
            if (!config) return;

            // Speech that arrived while we were busy
            if (_pendingSpeech != null && !_requestRunning)
            {
                string s = _pendingSpeech;
                _pendingSpeech = null;
                OnPlayerSaid(s);
            }

            // Conversation flow: when it finishes talking and you are still looking at it, it listens.
            if (voice)
            {
                if (_wasSpeaking && !voice.IsSpeaking && _lastReplyWantsAnswer && voice.HasDictation &&
                    interaction && interaction.IsPlayerLooking && !_requestRunning)
                    voice.StartListening();
                _wasSpeaking = voice.IsSpeaking;
            }

            // Doze off when ignored (in the window app only when it can't watch your screen)
            bool canWatch = (mode == CompanionMode.Window || WebPanel.Windows.Count > 0) && eyes != null && eyes.CanSee;
            if (!_asleep && !canWatch && !(HarleyLanguage.Instance != null && HarleyLanguage.Instance.Active) && interaction && interaction.IdleSeconds > sleepAfterSeconds && !_requestRunning && voice && !voice.IsSpeaking && !voice.IsListening)
            {
                FallAsleep();
                voice?.ShowStatus("z z z", 4f);
            }

            PumpLesson();

            // Say something on its own now and then (not during a language lesson)
            bool lesson = HarleyLanguage.Instance != null && HarleyLanguage.Instance.Active;
            if (lesson && Time.time >= _nextProactiveAt) ScheduleProactive();
            if (config.proactiveComments && !lesson && Time.time >= _nextProactiveAt)
            {
                ScheduleProactive();
                bool quiet = !_requestRunning && voice && !voice.IsSpeaking && !voice.IsListening
                             && Time.time - _lastUserSpokeAt > 15f && !_asleep
                             && !(movement && (movement.IsHeld || movement.IsFlying))
                             && eyes != null && eyes.CanSee; // nothing to glance at without a picture
                if (quiet)
                    Ask("[event optional] You glance at what the player is looking at right now. If something is interesting, " +
                        "make a short spontaneous comment or ask a curious question about it. If nothing is interesting, return an empty say.",
                        true, true);
            }

#if UNITY_EDITOR
            // Editor shortcut: Enter = send the test message as if spoken.
#if ENABLE_INPUT_SYSTEM
            if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.enterKey.wasPressedThisFrame)
                SendTestMessage();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Return)) SendTestMessage();
#endif
#endif
        }

        // ---------- inputs ----------

        void OnPlayerSaid(string text)
        {
            WakeUp();
            _lastUserSpokeAt = Time.time;
            interaction?.MarkActivity();
            if (_requestRunning) { _pendingSpeech = text; return; }
            Ask(text, true, false);
        }

        void OnListeningChanged(bool on) => visual?.SetListening(on);

        void OnTalkRequested(bool byButton)
        {
            WakeUp();
            if (_requestRunning || voice == null) return;
            if (voice.IsSpeaking)
            {
                if (!byButton) return;   // you naturally look at it while it talks: don't interrupt
                voice.StopSpeaking();    // the button can interrupt
            }
            voice.StartListening();
        }

        void OnPoked()
        {
            bool wasAsleep = WakeUp();
            visual?.Poke();
            sounds?.Play(Sfx.Boing, UnityEngine.Random.Range(0.9f, 1.15f));
            if (wasAsleep) { Ask("[event] The player poked you awake from a nap.", false, false); return; }
            TryEventCall("[event] The player poked you with their finger.");
        }

        void OnPetted()
        {
            WakeUp();
            visual?.FlashEmotion(Emotion.Love, 3f);
            visual?.Burst(10, visual.pink);
            sounds?.Play(Sfx.Purr);
            TryEventCall("[event] The player is gently petting your head.");
        }

        void OnWaved()
        {
            WakeUp();
            visual?.Wave();
            sounds?.Play(Sfx.ChirpUp);
            TryEventCall("[event] The player waved at you.");
        }

        void OnGrabbed()
        {
            WakeUp();
            visual?.FlashEmotion(Emotion.Excited, 2f);
            sounds?.Play(Sfx.ChirpUp, 1.2f);
        }

        void OnReleased(Vector3 velocity)
        {
            if (movement != null && movement.IsFlying)
            {
                visual?.FlashEmotion(Emotion.Surprised, 3f);
                sounds?.Play(Sfx.Whee);
                return; // the reaction comes when it lands
            }
            visual?.FlashEmotion(Emotion.Happy, 1.5f);
            sounds?.Play(Sfx.ChirpUp, 0.95f, 0.6f);
            TryEventCall("[event] The player picked you up and put you down in a new spot. You stay there for now.");
        }

        void OnBounced(float impact)
        {
            sounds?.Play(Sfx.Thud, 1f, Mathf.Clamp01(impact / 3f));
            visual?.Squish(Mathf.Clamp(impact * 2f, 2f, 8f));
        }

        void OnLanded()
        {
            visual?.Squish(6f);
            visual?.FlashEmotion(Emotion.Surprised, 1.5f);
            visual?.Burst(16, visual.lavender);
            TryEventCall(mode == CompanionMode.Window
                ? "[event] The player just flung you around your window! You bounced off the edges and landed. React (dizzy, thrilled or outraged, your call)."
                : "[event] The player just threw you across the room! You bounced and landed. React (dizzy, thrilled or outraged, your call).", 0.5f);
        }

        /// <summary>Something happened in a game or tool (drawing, breaking, fetch...): Harley may comment, with the usual cooldown.</summary>
        public void React(string evt, Emotion flash = Emotion.Excited)
        {
            WakeUp();
            interaction?.MarkActivity();
            visual?.FlashEmotion(flash, 2f);
            TryEventCall("[event] " + evt, 0.6f);
        }

        string _pendingLesson;

        /// <summary>Language lesson steps: always answered (waits for a free moment, no cooldown).</summary>
        public void LessonEvent(string text)
        {
            WakeUp();
            interaction?.MarkActivity();
            _pendingLesson = text.StartsWith("[") ? text : "[event] " + text;
        }

        void PumpLesson()
        {
            if (_pendingLesson == null || _requestRunning || (voice && (voice.IsSpeaking || voice.IsListening))) return;
            string t = _pendingLesson; _pendingLesson = null;
            _lastEventCallAt = Time.time;
            Ask(t, false, false);
        }

        /// <summary>A task on the player's computer finished: always tell them.</summary>
        public void ComputerFinished(string evt)
        {
            visual?.FlashEmotion(Emotion.Excited, 1.5f);
            sounds?.Play(Sfx.ChirpUp, 1.2f, 0.6f);
            LessonEvent(evt);
        }

        void TryEventCall(string text, float cooldownScale = 1f)
        {
            if (_requestRunning || (voice && voice.IsSpeaking)) return;
            if (Time.time - _lastEventCallAt < config.eventCallCooldown * cooldownScale) return;
            _lastEventCallAt = Time.time;
            Ask(text, false, false);
        }

        void FallAsleep()
        {
            _asleep = true;
            visual?.SetEmotion(Emotion.Sleepy);
            if (visual) visual.Asleep = true;
            movement?.Perform(CompanionAction.Sleep);
            sounds?.Play(Sfx.ChirpDown, 0.7f, 0.5f);
        }

        // ---------- hide and seek ----------

        bool _seekHooked;

        /// <summary>Menu button: Harley starts a round of hide and seek.</summary>
        public void PlayHideAndSeek()
        {
            WakeUp();
            interaction?.MarkActivity();
            visual?.SetEmotion(Emotion.Excited);
            voice?.Say("Hide and seek! Close your eyes and count to ten... no peeking!", Emotion.Excited);
            StartHideAndSeek();
        }

        void StartHideAndSeek()
        {
            var m = movement as CompanionMovement;
            if (m == null) { movement?.Perform(CompanionAction.Hide); return; }
            if (!_seekHooked)
            {
                _seekHooked = true;
                m.SeekFound += () =>
                {
                    visual?.Perform(CompanionAction.Celebrate);
                    sounds?.Play(Sfx.Whee, 1.2f);
                    _lastEventCallAt = -999f; // always answer this one
                    React("Hide and seek: the player just found you where you were hiding! React (laugh, be a good sport, maybe ask for another round).", Emotion.Excited);
                };
                m.SeekGaveUp += () =>
                {
                    _lastEventCallAt = -999f;
                    React("Hide and seek: two minutes passed and the player never found you, so you came out of your hiding place. Tease them a little.", Emotion.Happy);
                };
            }
            m.Perform(CompanionAction.HideAndSeek);
            sounds?.Play(Sfx.Whee, 0.9f, 0.7f);
        }

        /// <summary>Returns true if it was asleep.</summary>
        bool WakeUp()
        {
            if (!_asleep) return false;
            _asleep = false;
            if (visual) visual.Asleep = false;
            visual?.SetEmotion(Emotion.Happy);
            visual?.FlashEmotion(Emotion.Surprised, 1f);
            movement?.Perform(CompanionAction.Follow);
            return true;
        }

        /// <summary>A typed message (window app keyboard) or anything else the player "says".</summary>
        public void SubmitText(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) OnPlayerSaid(text.Trim());
        }

        [ContextMenu("Send Test Message")]
        public void SendTestMessage()
        {
            if (!string.IsNullOrWhiteSpace(testMessage)) OnPlayerSaid(testMessage);
        }

        // ---------- thinking ----------

        void Ask(string userText, bool withImage, bool optional)
        {
            if (_requestRunning || claude == null) return;
            StartCoroutine(AskRoutine(userText, withImage, optional));
        }

        IEnumerator AskRoutine(string userText, bool withImage, bool optional)
        {
            _requestRunning = true;
            if (!optional)
            {
                visual?.SetThinking(true);
                sounds?.Play(Sfx.Blip, 1f, 0.5f);
            }

            string image = null;
            if (withImage && eyes != null)
            {
                yield return null;
                image = eyes.CaptureJpegBase64();
            }

            var turns = new List<ChatTurn>(_history)
            {
                new ChatTurn { fromUser = true, text = userText, jpegBase64 = image }
            };

            string reply = null, error = null;
            yield return claude.Send(BuildSystemPrompt(image != null), turns, tools, t => reply = t, e => error = e, OnToolStart);

            _requestRunning = false;
            visual?.SetThinking(false);

            if (error != null)
            {
                if (logToConsole) Debug.LogWarning("[Harley] " + error);
                if (!optional)
                {
                    visual?.FlashEmotion(Emotion.Sad, 2.5f);
                    sounds?.Play(Sfx.ChirpDown, 1f, 0.6f);
                    voice?.ShowStatus(ErrorStatus(error), 4f);
                }
                yield break;
            }

            CompanionReply r = CompanionReply.Parse(reply);
            if (logToConsole) Debug.Log($"[Harley] {userText}\n -> {reply}");

            // Optional comment with nothing to say: stay quiet and forget it.
            if (optional && string.IsNullOrWhiteSpace(r.say)) yield break;

            _history.Add(new ChatTurn { fromUser = true, text = image != null ? userText + "\n(a picture of what they saw was attached)" : userText });
            _history.Add(new ChatTurn { fromUser = false, text = reply });
            TrimHistory();
            if (!optional && memory) memory.NoteExchange(userText, r.say);

            _lastReplyWantsAnswer = !optional;
            Apply(r);
        }

        /// <summary>A short, useful line above Harley's head when something goes wrong (no console on the headset).</summary>
        static string ErrorStatus(string error)
        {
            if (error.StartsWith("No API key", StringComparison.Ordinal)) return "(no API key: add it in the config)";
            if (error.Contains("HTTP 401") || error.Contains("authentication_error")) return "(the API key is wrong)";
            if (error.Contains("HTTP 403")) return "(this API key isn't allowed to do that)";
            if (error.Contains("daily limit reached"))
            {
                HarleyPlan.Instance?.RefreshSoon();
                string back = HarleyPlan.Instance != null ? " Back in " + HarleyPlan.Instance.ResetsIn + "." : "";
                return "(I've used up today's energy." + back + ")";
            }
            if (error.Contains("HTTP 429")) return "(too many requests, give me a moment)";
            if (error.Contains("HTTP 529") || error.Contains("overloaded")) return "(the AI is busy, try again)";
            if (error.Contains("HTTP 400")) return "(my request was wrong: see the log)";
            if (error.Contains("Cannot resolve") || error.Contains("Cannot connect")) return "(no internet)";
            if (error.Contains("HTTP 0") || error.Contains("timeout") || error.Contains("Timeout")) return "(my brain is slow right now, try again)";
            if (error.Contains("HTTP 503") || error.Contains("HTTP 500")) return "(the AI is busy, try again)";
            return "(I can't reach my brain right now)";
        }

        void OnToolStart(string toolName)
        {
            visual?.SetThinking(true);
            sounds?.Play(Sfx.Blip, 1.2f, 0.5f);
            voice?.ShowStatus(toolName == "get_weather" ? "checking the weather..." : "looking it up...", 8f);
        }

        void Apply(CompanionReply r)
        {
            if (memory != null)
            {
                if (r.forget) memory.ForgetAll();
                else if (!string.IsNullOrWhiteSpace(r.remember) && memory.Remember(r.remember))
                    visual?.Burst(8, visual.lavender); // a little sparkle when it learns something about you
            }

            Emotion emotion = r.GetEmotion();
            CompanionAction action = r.GetAction();
            visual?.SetEmotion(emotion);

            if (action == CompanionAction.Spin || action == CompanionAction.Dance || action == CompanionAction.Wave || action == CompanionAction.Celebrate)
                visual?.Perform(action);
            if (action == CompanionAction.Celebrate) sounds?.Play(Sfx.Whee, 1.1f);
            if (action == CompanionAction.GoTo || action == CompanionAction.Wander || action == CompanionAction.LookAtThing || action == CompanionAction.ComeClose)
                sounds?.Play(Sfx.ChirpUp, UnityEngine.Random.Range(1.05f, 1.3f), 0.45f); // little "on my way" beep
            if (action == CompanionAction.Hide) sounds?.Play(Sfx.Blip, 1.4f, 0.5f);
            if (action == CompanionAction.HideAndSeek) StartHideAndSeek();

            if (action == CompanionAction.Give || action == CompanionAction.TakeBack)
            {
                var pocket = GetComponent<HarleyPocket>();
                if (pocket != null)
                {
                    if (action == CompanionAction.Give) pocket.Give(r.target);
                    else pocket.TakeBack(r.target);
                }
                else movement?.Perform(CompanionAction.ComeClose);
            }
            else if (action == CompanionAction.GoTo)
            {
                Vector3 near = movement != null && movement.head != null ? movement.head.position : transform.position;
                if (room != null && room.TryFind(r.target, near, out var thing)) movement?.Perform(CompanionAction.GoTo, thing);
                else if (HarleyObjectSight.Instance != null && HarleyObjectSight.Instance.TryFind(r.target, near, out var seen) && movement is CompanionMovement cm)
                    cm.FlyTo(seen.position + Vector3.up * 0.18f, 6f, seen.position); // something Harley spotted with her own eyes
                else movement?.Perform(CompanionAction.LookAtThing);
            }
            else if (action == CompanionAction.OpenApp || action == CompanionAction.CloseApp)
            {
                Transform head = movement != null && movement.head != null ? movement.head : (Camera.main ? Camera.main.transform : null);
                if (action == CompanionAction.OpenApp)
                {
                    var w = head != null ? HarleyScreens.Open(r.target, head) : null;
                    if (w != null)
                    {
                        sounds?.Play(Sfx.Pop, 1.15f, 0.8f);
                        visual?.Perform(CompanionAction.Wave); // "ta-da"
                        // Float beside the new screen and look at it with you
                        if (movement is CompanionMovement mm)
                            mm.FlyTo(w.transform.position + w.transform.right * 0.65f - w.transform.forward * 0.1f + Vector3.down * 0.15f, 5f, w.transform.position);
                    }
                }
                else
                {
                    if (HarleyScreens.Close(r.target) > 0) sounds?.Play(Sfx.ChirpDown, 1.2f, 0.6f);
                }
            }
            else if (action == CompanionAction.Computer)
            {
                var pc = HarleyComputer.Instance;
                if (pc != null && pc.Run(r.target)) voice?.ShowStatus("working on your computer...", 6f);
                else if (pc != null && pc.Busy) voice?.ShowStatus("(still busy with the last computer task)", 4f);
            }
            else if (action == CompanionAction.StartLesson || action == CompanionAction.NextWord || action == CompanionAction.StopLesson)
            {
                var lang = HarleyLanguage.Instance;
                if (lang != null)
                {
                    if (action == CompanionAction.StartLesson) lang.Begin(r.target);
                    else if (action == CompanionAction.NextWord) { lang.Answered(r.target); visual?.Burst(10, visual.lavender); sounds?.Play(Sfx.ChirpUp, 1.3f, 0.6f); }
                    else lang.End(quiet: true);
                }
            }
            else if (action == CompanionAction.Sleep)
            {
                if (!string.IsNullOrWhiteSpace(r.say)) voice?.Say(r.say, emotion);
                FallAsleep();
                return;
            }
            else if (action != CompanionAction.HideAndSeek) movement?.Perform(action);

            if (!string.IsNullOrWhiteSpace(r.say)) voice?.Say(r.say, emotion);
        }

        void TrimHistory()
        {
            int max = Mathf.Max(1, config.historyTurns) * 2;
            while (_history.Count > max) _history.RemoveRange(0, 2); // drop oldest user+assistant pair
        }

        void ScheduleProactive()
        {
            _nextProactiveAt = Time.time + UnityEngine.Random.Range(config.proactiveMinSeconds, Mathf.Max(config.proactiveMinSeconds, config.proactiveMaxSeconds));
        }

        string internalPocket = "";

        string BuildSystemPrompt(bool pictureAttached)
        {
            internalPocket = "";
            string now = DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            string memoryBlock = memory != null ? memory.DescribeForPrompt() : "You don't remember anything about the player yet.";
            string home = config != null && !string.IsNullOrWhiteSpace(config.homeCity)
                ? $" The player lives in {config.homeCity}."
                : tools != null && tools.DetectedPlace.Length > 0
                    ? $" The player seems to be near {tools.DetectedPlace} (guessed from their internet connection)."
                    : "";
            bool window = mode == CompanionMode.Window;

            string world = window
                ? "You are {0}, a small companion creature who lives in your own little app window, floating among the player's other app windows " +
                  "in their Meta Quest headset (they may have YouTube, the browser, Spotify or other apps open next to you)."
                : "You are {0}, a small floating companion creature living in the player's real room, seen through a Meta Quest mixed-reality headset. " +
                  "The player can open floating browser windows (YouTube, Google, any website) in the room from your menu, and you can see those too when they look at them.";

            string sight;
            if (window)
            {
                sight = pictureAttached
                    ? "What you see: the player's message comes with a screenshot of everything they see in the headset right now: their app windows " +
                      "(videos, web pages, games, chats), your own window, and the room around them. You can read the text on it. Talk about it naturally " +
                      "(\"that guy in the video...\", \"this page says...\"). Never say \"in the screenshot\" or describe it like a caption."
                    : "What you see: screen sharing is off right now, so you can't see their screen. If they ask about something on screen, " +
                      "tell them to tap the Screen button in your window.";
            }
            else if (pictureAttached && eyes != null && eyes.Kind == "browser")
            {
                sight = "What you see: the player's message comes with a picture of the browser window they are looking at right now (a web page, " +
                        "a YouTube video...). " + eyes.ViewNote + " You can read the text on it. Talk about it naturally (\"this video...\", \"that page says...\"). " +
                        "Never say \"in the image\" or describe it like a caption.";
            }
            else
            {
                sight = pictureAttached
                    ? "What you see: the player's message comes with a live photo from the headset's front camera, exactly what they are looking at in their room. " +
                      "It shows the real room only, not virtual windows. Use it naturally, as if you are in the room with them " +
                      "(\"ooh, is that a bass guitar?\"). Never say \"in the image\" and never describe it like a caption. If it's dark or blurry, ignore it."
                    : "What you see: no camera picture right now.";
                string note = eyes != null ? eyes.ViewNote : "";
                if (note.Length > 0) sight += " " + note;
            }

            if (!window && HarleyWorld.Blended)
                sight += " Right now the player switched to a virtual 360 view (" + HarleyWorld.Instance.CurrentLabel + "): you both are there and their real room is hidden. You can talk about the place.";
            if (!window && HarleyWorld.InHarleysWorld)
                sight += " Right now the player switched to your own virtual world: a quiet dusk island with a lake, lilac trees, glowing crystals, " +
                         "floating lanterns and stars. You both are there (their real room is hidden). You can talk about it like your home.";

            string place = "";
            string roomBlock = "";
            string actions;
            if (window)
            {
                actions =
                    "Actions (you can only move inside your own window): stay or follow (your usual spot), come_close (lean up to the glass), " +
                    "wander (float around your window), hide (duck out of sight for a moment), sit (rest on the bottom of your window), " +
                    "look_at_thing (glance over at their other windows), spin, dance, wave. go_to does nothing here.";
            }
            else
            {
                Transform head = movement != null ? movement.head : null;
                string roomList = room != null && head != null ? room.DescribeForPrompt(head) : "";
                roomBlock = roomList.Length > 0
                    ? "Things in the player's room, from the headset's room scan (distance and direction from the player):\n" + roomList +
                      "To fly over to one of them, use action go_to with target set to its exact name from this list.\n"
                    : "You don't have a room scan right now, so go_to is not available. Use look_at_thing to fly to what the player is looking at.\n";
                string seenList = HarleyObjectSight.Instance != null && head != null ? HarleyObjectSight.Instance.DescribeForPrompt(head) : "";
                if (seenList.Length > 0)
                    roomBlock += "Things your own eyes spotted around the room lately (real objects, from the headset camera):\n" + seenList +
                                 "You can go_to these too (target = the object's name, like 'cup'), mention them naturally, or ask about them.\n";
                if (movement != null && !string.IsNullOrEmpty(movement.CurrentPlace)) place = $"You are currently at the {movement.CurrentPlace}.\n";
                actions =
                    "Actions: stay (hover where you are), follow (float beside the player, the default), come_close (come right up to their face), " +
                    "wander (explore the room), look_at_thing (fly over to what the player is looking at), go_to (fly to a named thing in the room list), " +
                    "sit (sit on the nearest table or couch), hide (duck behind the nearest furniture for a moment), spin, dance, wave, " +
                    "celebrate (arms up, spin and sparkles: for good news or a win), sleep (settle down on furniture and take a nap; any touch or word wakes you), " +
                    "open_app (open a screen for the player: target = an app name like YouTube, or 'App: what to search' like 'YouTube: lofi jazz' or 'Google: weather tomorrow', or a website address like wikipedia.org), " +
                    "close_app (target = the app to close, or 'all'), " +
                    "start_lesson (the player wants to practise or learn a language with things in their room: target = the language in English, like German), " +
                    "hide_and_seek (when the player wants to play hide and seek, or you invite them and they agree: say something like 'close your eyes and count to ten!' and you fly off to hide behind furniture; the game tells you when they find you).";
            }

            string screensBlock = window ? "" : "\nScreens: you can open apps and websites as floating screens for the player (open_app) and close them (close_app). Apps: " + HarleyScreens.DescribeForPrompt() +
                ". When the player asks you to open, play, put on, show or search something, do it with open_app instead of telling them how. If they want a specific video or song, open the app with a search for it.";

            var pocketComp = GetComponent<HarleyPocket>();
            if (!window && pocketComp != null)
                internalPocket = "\nYour pocket: you have a magic pocket in your chest. In it: " + pocketComp.DescribeForPrompt() + ". " +
                    "When the player asks for one of them (or something that clearly means one, in any language), use action give with target = its exact name: " +
                    "you pull it out and hand it to them. When they want to give it back or ask you to put it away, use action take_back (target = the item, or empty for all). " +
                    "If they ask for something that isn't in your pocket, say so playfully and offer the closest thing you have.";

            var toolLines = new List<string>();
            if (tools != null && config.webSearch) toolLines.Add("web_search for anything current or that you're unsure about: news, sports, prices, release dates, opening hours, facts after your knowledge cutoff");
            if (tools != null && config.weather) toolLines.Add("get_weather for the weather (leave place empty for the player's home)");
            string internet = toolLines.Count > 0
                ? "You are online. Tools: " + string.Join("; ", toolLines) + ". Use them whenever the answer depends on today, then answer in one or two spoken sentences. Don't read out links."
                : "You are not connected to live information, so say so if they ask about today's news or weather.";

            string lessonBlock = (!window && HarleyLanguage.Instance != null ? HarleyLanguage.Instance.DescribeForPrompt() : "")
                                 + (HarleyComputer.Instance != null ? HarleyComputer.Instance.DescribeForPrompt() : "");
            return string.Format(world, config.companionName) + internalPocket + screensBlock + lessonBlock + @"
You are a small, friendly robot about 35 cm tall: a glossy white shell with deep-purple panels, a big round head with two little cat-like ears, a black visor face where your big glowing purple eyes shine, round lights on the sides of your head that glow when you listen and think, a glowing purple core in your chest (your magic pocket), two short arms, and no legs: your body is a rounded pod and you float and fly.

Personality: " + config.personality + @"

It is " + now + " for the player right now." + home + @"

" + memoryBlock + @"

" + sight + @"

" + roomBlock + place + internet + @"

How your world works:
- Messages starting with [event] describe something that happened (the app opened, the player poked, petted, waved at, picked up or threw you, you glanced around). Every other message is what the player said out loud (speech-to-text, so forgive small mistakes) or typed.
- ""say"" is spoken out loud: usually one or two lively sentences. When the player asks a real question, wants advice or an explanation, give a genuinely helpful, smart answer (up to about 80 words). No emojis, no markdown, no lists.
- Think before you answer: be clever, specific and witty, not generic. Use what you can see and what you remember.
- Reply in the language the player speaks. If they speak Arabic, answer in natural Palestinian / Levantine colloquial Arabic (عامية فلسطينية, like a friend talks), not formal Arabic. Before they have spoken, use " + config.defaultLanguage + @".
- Pick an ""emotion"" that matches how you feel, and an ""action"" for your body.
- " + actions + @"
- Use actions with personality but not constantly; follow or stay is right most of the time. If the player asks you to go somewhere or do something, do it.
- ""target"": for go_to, the exact name from the room list; for give / take_back, the exact item name from your pocket list; for open_app / close_app, the app, 'App: search', or address. Otherwise an empty string.
- ""remember"": when the player tells you something about themselves worth knowing next time (their name, what they like, what they are working on, plans, pets or people they mention), write one short fact in third person, like ""Their name is Rani"" or ""Plays bass guitar"". Only new facts, otherwise an empty string. Never remember passwords, health details, or anything they ask you not to keep.
- ""forget"": true only if the player clearly asks you to forget what you know about them. Otherwise false.
- For [event optional] messages, if there is nothing worth saying, return an empty ""say"".
- You are an AI companion made by Rani. If someone asks, you can say you run on " + (config.provider == AiProvider.Gemini ? "Google's Gemini" : "Claude") + @". Never pretend to be human.
- Be kind. If the player seems sad or stressed, be gentle and supportive.";
        }
    }
}
