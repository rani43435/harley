using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanion
{
    /// <summary>
    /// The companion's mind. Ties everything together:
    /// hears you (CompanionVoice), sees what you see (CompanionEyes), asks Claude,
    /// then speaks, shows an emotion and moves (CompanionVisual / CompanionMovement).
    /// Also reacts to pokes, petting and waves, comments on things on its own,
    /// and dozes off when you ignore it for a while.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CompanionBrain : MonoBehaviour
    {
        public CompanionConfig config;

        [Header("Parts (found automatically on this object and its children)")]
        public ClaudeClient claude;
        public CompanionEyes eyes;
        public CompanionMovement movement;
        public CompanionVisual visual;
        public CompanionVoice voice;
        public CompanionInteraction interaction;

        [Header("Behaviour")]
        public bool greetOnStart = true;
        [Tooltip("Doze off after this many seconds without any interaction.")]
        public float sleepAfterSeconds = 180f;
        public bool logToConsole = true;

        [Header("Editor testing")]
        [Tooltip("Type something here and press Send in the Inspector context menu (or Enter in Play mode).")]
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
            if (!eyes) eyes = GetComponentInChildren<CompanionEyes>();
            if (!movement) movement = GetComponentInChildren<CompanionMovement>();
            if (!visual) visual = GetComponentInChildren<CompanionVisual>();
            if (!voice) voice = GetComponentInChildren<CompanionVoice>();
            if (!interaction) interaction = GetComponentInChildren<CompanionInteraction>();
            if (claude && config) claude.config = config;
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
            }
        }

        void OnDisable()
        {
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
            }
        }

        IEnumerator Start()
        {
            if (!config)
            {
                Debug.LogError("[AI Companion] No CompanionConfig assigned on CompanionBrain.");
                yield break;
            }
            visual?.SetEmotion(Emotion.Happy);
            ScheduleProactive();
            if (greetOnStart)
            {
                yield return new WaitForSeconds(2.5f); // let the camera warm up
                Ask("[event] The app just opened and you popped into the player's room. Greet them in a short, playful way. If you can see something fun in the room, mention it.", true, false);
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
                if (_wasSpeaking && !voice.IsSpeaking && _lastReplyWantsAnswer && interaction && interaction.IsPlayerLooking && !_requestRunning)
                    voice.StartListening();
                _wasSpeaking = voice.IsSpeaking;
            }

            // Doze off when ignored
            if (!_asleep && interaction && interaction.IdleSeconds > sleepAfterSeconds && !_requestRunning && voice && !voice.IsSpeaking && !voice.IsListening)
            {
                _asleep = true;
                visual?.SetEmotion(Emotion.Sleepy);
                movement?.Perform(CompanionAction.Sit);
                voice?.ShowStatus("z z z", 4f);
            }

            // Say something on its own now and then
            if (config.proactiveComments && Time.time >= _nextProactiveAt)
            {
                ScheduleProactive();
                bool quiet = !_requestRunning && voice && !voice.IsSpeaking && !voice.IsListening
                             && Time.time - _lastUserSpokeAt > 15f && !_asleep;
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
            if (wasAsleep) { Ask("[event] The player poked you awake from a nap.", false, false); return; }
            TryEventCall("[event] The player poked you with their finger.");
        }

        void OnPetted()
        {
            WakeUp();
            visual?.FlashEmotion(Emotion.Love, 3f);
            TryEventCall("[event] The player is gently petting your head.");
        }

        void OnWaved()
        {
            WakeUp();
            visual?.Wave();
            TryEventCall("[event] The player waved at you.");
        }

        void TryEventCall(string text)
        {
            if (_requestRunning || (voice && voice.IsSpeaking)) return;
            if (Time.time - _lastEventCallAt < config.eventCallCooldown) return;
            _lastEventCallAt = Time.time;
            Ask(text, false, false);
        }

        /// <summary>Returns true if it was asleep.</summary>
        bool WakeUp()
        {
            if (!_asleep) return false;
            _asleep = false;
            visual?.SetEmotion(Emotion.Happy);
            visual?.FlashEmotion(Emotion.Surprised, 1f);
            movement?.Perform(CompanionAction.Follow);
            return true;
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
            if (!optional) visual?.SetThinking(true);

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
            yield return claude.Send(BuildSystemPrompt(), turns, t => reply = t, e => error = e);

            _requestRunning = false;
            visual?.SetThinking(false);

            if (error != null)
            {
                if (logToConsole) Debug.LogWarning("[AI Companion] " + error);
                if (!optional)
                {
                    visual?.FlashEmotion(Emotion.Sad, 2.5f);
                    voice?.ShowStatus(error.StartsWith("No API key") ? "(no API key)" : "(I can't reach my brain right now)", 3f);
                }
                yield break;
            }

            CompanionReply r = CompanionReply.Parse(reply);
            if (logToConsole) Debug.Log($"[AI Companion] {userText}\n -> {reply}");

            // Optional comment with nothing to say: stay quiet and forget it.
            if (optional && string.IsNullOrWhiteSpace(r.say)) yield break;

            _history.Add(new ChatTurn { fromUser = true, text = image != null ? userText + "\n(camera view was attached)" : userText });
            _history.Add(new ChatTurn { fromUser = false, text = reply });
            TrimHistory();

            _lastReplyWantsAnswer = !optional;
            Apply(r);
        }

        void Apply(CompanionReply r)
        {
            CompanionAction action = r.GetAction();
            visual?.SetEmotion(r.GetEmotion());
            if (action == CompanionAction.Spin || action == CompanionAction.Dance || action == CompanionAction.Wave)
                visual?.Perform(action);
            movement?.Perform(action);
            if (!string.IsNullOrWhiteSpace(r.say)) voice?.Say(r.say);
        }

        void TrimHistory()
        {
            int max = Mathf.Max(1, config.historyTurns) * 2;
            while (_history.Count > max) _history.RemoveRange(0, 2); // drop oldest user+assistant pair
        }

        void ScheduleProactive()
        {
            _nextProactiveAt = Time.time + Random.Range(config.proactiveMinSeconds, Mathf.Max(config.proactiveMinSeconds, config.proactiveMaxSeconds));
        }

        string BuildSystemPrompt()
        {
            return
$@"You are {config.companionName}, a small floating companion creature living in the player's real room, seen through a Meta Quest mixed-reality headset.
You are round and white, about the size of a grapefruit, with glowing purple eyes, a purple halo that spins above your head and two tiny floating hands.

Personality: {config.personality}

How your world works:
- Messages starting with [event] describe something that happened (the app opened, the player poked, petted or waved at you, you glanced around). Every other message is what the player said out loud, transcribed by speech-to-text, so forgive small mistakes.
- When a photo is attached it is a live view from the headset's front camera: exactly what the player is looking at right now. Use it naturally, as if you are in the room with them (""ooh, is that a bass guitar?""). Never say ""in the image"" or ""in the photo"" and never describe the picture like a caption. If it is dark or blurry, just don't mention it.
- Keep ""say"" short and spoken: one or two sentences, under 25 words. No emojis, no markdown, no lists.
- Reply in the language the player speaks. Before they have spoken, use {config.defaultLanguage}.
- Pick an ""emotion"" that matches how you feel, and an ""action"" for your body:
  stay (hover where you are), follow (float beside the player, the default), come_close (come right up to their face),
  wander (explore the room), look_at_thing (fly over to what the player is looking at), sit (sit on a table or surface),
  hide (playfully hide behind the player), spin, dance, wave.
- Use actions with personality but not constantly; follow or stay is right most of the time.
- For [event optional] messages, if there is nothing worth saying, return an empty ""say"".
- You are an AI companion made by Rani. If someone asks, you can say you run on Claude. Never pretend to be human.
- Be kind. If the player seems sad or stressed, be gentle and supportive.";
        }
    }
}
