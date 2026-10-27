# Nova — AI Companion for Quest (Mixed Reality)

A small white-and-purple AI creature that lives in your real room on Meta Quest 3 / 3S.
It floats around you, sees what you see (passthrough camera), talks with you (Claude),
reacts when you poke, pet or wave at it, sits on your table, and comments on things on its own.

## What's inside

```
Assets/AICompanion/
  Scripts/
    CompanionBrain.cs        the mind: hears → sees → asks Claude → speaks, emotes, moves
    ClaudeClient.cs          Claude Messages API (image + structured JSON reply)
    CompanionEyes.cs         grabs a frame from the Quest passthrough camera
    CompanionMovement.cs     follow / wander / sit on table / hide / come close / look at thing
    CompanionVisual.cs       bob, blink, eyes follow you, emotions, mouth, halo, wave/spin/dance
    CompanionVoice.cs        speech in/out bridge (Meta Voice SDK) + subtitle bubble
    CompanionInteraction.cs  poke, pet, wave, look-to-talk, A button to talk
    CompanionConfig.cs       settings asset (API key, model, personality)
    CompanionTypes.cs        emotions, actions, reply parsing
  Editor/
    CompanionBuilder.cs      menu: AI Companion > Create Companion In Scene
```

## Setup (about 20 minutes)

1. **Project** — Unity 6 (6000.0.38+), Android, URP. Easiest start: Meta's *Mixed Reality* template,
   or a URP project with **Meta XR All-in-One SDK** + **Meta MR Utility Kit (MRUK) v81+**.
   Run *Meta > Tools > Project Setup Tool* and fix everything.
2. **Building blocks** (*Meta > Tools > Building Blocks*): Camera Rig, Passthrough, Hand Tracking,
   and Room Mesh / Effect Mesh **with colliders** (so Nova avoids walls and finds tables).
3. **Camera access** — select the Camera Rig, in OVR Manager enable *Passthrough Camera Access*,
   then add a **PassthroughCameraAccess** component to any GameObject.
   (Quest 3 / 3S, Horizon OS v74+. Doesn't work in the XR Simulator.)
4. Copy `Assets/AICompanion` into your project. If asked, import **TMP Essentials**.
5. Menu **AI Companion > Create Companion In Scene**. It builds Nova, the materials and the config,
   and connects the camera if it finds it.
6. Menu **AI Companion > Select Config (API key)** → paste your Claude API key
   (console.anthropic.com). The config asset is git-ignored on purpose.
7. *Player Settings > Android*: Internet Access = **Require**. Build & Run on the headset.

### Voice (Meta Voice SDK, free)

Add the **Dictation** and **Text to Speech** building blocks (needs a free Wit.ai app token), then on the
`AI Companion` object, in **CompanionVoice**:

| CompanionVoice event | → wire to |
|---|---|
| On Start Listening | `AppDictationExperience.Activate()` |
| On Stop Listening | `AppDictationExperience.Deactivate()` |
| On Speak (string) | `TTSSpeaker.Speak(string)` (dynamic) |

And the other way:

| Voice SDK event | → `CompanionVoice` method |
|---|---|
| Dictation · On Full Transcription | `ReceiveTranscript` |
| Dictation · On Partial Transcription | `ReceivePartialTranscript` (optional) |
| TTSSpeaker · Playback Start / Complete | `OnTtsStarted` / `OnTtsFinished` (optional) |

Drag the TTSSpeaker's AudioSource into **Tts Audio** so the mouth follows the voice.
Without voice wired, Nova still works with the subtitle bubble.

## How you interact

- **Look at Nova for ~1 second** (or press **A**) → it listens. Talk.
- **Poke** it with your finger, **pet** the top of its head, **wave** at it.
- Ignore it for 3 minutes and it falls asleep; poke to wake it.
- Every 1–2 minutes it glances at what you're looking at and may comment.

## Testing in the Editor

On `CompanionBrain`, type in **Test Message** and press **Enter** in Play mode (or right-click the
component → *Send Test Message*). Put any room photo in `CompanionEyes > Editor Test Image` to test vision.

## Notes

- Model: `claude-haiku-5-5` by default (fastest). Switch to `claude-sonnet-5-5` in the config for smarter replies.
- The API key lives inside the app. Fine for your own headset; for a public release, put a small
  server in between so the key isn't in the APK.
- Arabic text in the bubble needs an RTL plugin for TextMeshPro (e.g. RTLTMPro); English works out of the box.
- Cost: each message sends one 512 px image (~a few hundred tokens). Turn off *Proactive Comments* to save.
