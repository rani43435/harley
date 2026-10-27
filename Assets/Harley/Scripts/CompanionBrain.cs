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
            TryEventCall("[event] The player just threw you across the room! You bounced and landed. React (dizzy, thrilled or outraged, your call).", 0.5f);
        }

        void TryEventCall(string text, float cooldownScale = 1f)
        {
            if (_requestRunning || (voice && voice.IsSpeaking)) return;
            if (Time.time - _lastEventCallAt < config.eventCallCooldown * cooldownScale) return;
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
                    voice?.ShowStatus(error.StartsWith("No API key", StringComparison.Ordinal) ? "(no API key)" : "(I can't reach my brain right now)", 3f);
                }
                yield break;
            }

            CompanionReply r = CompanionReply.Parse(reply);
            if (logToConsole) Debug.Log($"[Harley] {userText}\n -> {reply}");

            // Optional comment with nothing to say: stay quiet and forget it.
            if (optional && string.IsNullOrWhiteSpace(r.say)) yield break;

            _history.Add(new ChatTurn { fromUser = true, text = image != null ? userText + "\n(camera view was attached)" : userText });
            _history.Add(new ChatTurn { fromUser = false, text = reply });
            TrimHistory();

            _lastReplyWantsAnswer = !optional;
            Apply(r);
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

            if (action == CompanionAction.Spin || action == CompanionAction.Dance || action == CompanionAction.Wave)
                visual?.Perform(action);

            if (action == CompanionAction.GoTo)
            {
                Vector3 near = movement != null && movement.head != null ? movement.head.position : transform.position;
                if (room != null && room.TryFind(r.target, near, out var thing)) movement?.Perform(CompanionAction.GoTo, thing);
                else movement?.Perform(CompanionAction.LookAtThing);
            }
            else movement?.Perform(action);

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

        string BuildSystemPrompt(bool pictureAttached)
        {
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
                : "You are {0}, a small floating companion creature living in the player's real room, seen through a Meta Quest mixed-reality headset.";

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
            else
            {
                sight = pictureAttached
                    ? "What you see: the player's message comes with a live photo from the headset's front camera, exactly what they are looking at in their room. " +
                      "It shows the real room only, not the apps in their headset. Use it naturally, as if you are in the room with them " +
                      "(\"ooh, is that a bass guitar?\"). Never say \"in the image\" and never describe it like a caption. If it's dark or blurry, ignore it."
                    : "What you see: no camera picture right now.";
            }

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
                if (movement != null && !string.IsNullOrEmpty(movement.CurrentPlace)) place = $"You are currently at the {movement.CurrentPlace}.\n";
                actions =
                    "Actions: stay (hover where you are), follow (float beside the player, the default), come_close (come right up to their face), " +
                    "wander (explore the room), look_at_thing (fly over to what the player is looking at), go_to (fly to a named thing in the room list), " +
                    "sit (sit on the nearest table or couch), hide (playfully hide behind the player), spin, dance, wave.";
            }

            var toolLines = new List<string>();
            if (tools != null && config.webSearch) toolLines.Add("web_search for anything current or that you're unsure about: news, sports, prices, release dates, opening hours, facts after your knowledge cutoff");
            if (tools != null && config.weather) toolLines.Add("get_weather for the weather (leave place empty for the player's home)");
            string internet = toolLines.Count > 0
                ? "You are online. Tools: " + string.Join("; ", toolLines) + ". Use them whenever the answer depends on today, then answer in one or two spoken sentences. Don't read out links."
                : "You are not connected to live information, so say so if they ask about today's news or weather.";

            return string.Format(world, config.companionName) + @"
You are round and white, about the size of a grapefruit, with glowing purple eyes, a purple halo that spins above your head and two tiny floating hands.

Personality: " + config.personality + @"

It is " + now + " for the player right now." + home + @"

" + memoryBlock + @"

" + sight + @"

" + roomBlock + place + internet + @"

How your world works:
- Messages starting with [event] describe something that happened (the app opened, the player poked, petted, waved at, picked up or threw you, you glanced around). Every other message is what the player said out loud (speech-to-text, so forgive small mistakes) or typed.
- Keep ""say"" short and spoken: one or two sentences, under 30 words. No emojis, no markdown, no lists.
- Reply in the language the player speaks. Before they have spoken, use " + config.defaultLanguage + @".
- Pick an ""emotion"" that matches how you feel, and an ""action"" for your body.
- " + actions + @"
- Use actions with personality but not constantly; follow or stay is right most of the time. If the player asks you to go somewhere or do something, do it.
- ""target"": only for go_to, the exact name from the room list. Otherwise an empty string.
- ""remember"": when the player tells you something about themselves worth knowing next time (their name, what they like, what they are working on, plans, pets or people they mention), write one short fact in third person, like ""Their name is Rani"" or ""Plays bass guitar"". Only new facts, otherwise an empty string. Never remember passwords, health details, or anything they ask you not to keep.
- ""forget"": true only if the player clearly asks you to forget what you know about them. Otherwise false.
- For [event optional] messages, if there is nothing worth saying, return an empty ""say"".
- You are an AI companion made by Rani. If someone asks, you can say you run on Claude. Never pretend to be human.
- Be kind. If the player seems sad or stressed, be gentle and supportive.";
        }
    }
}
