# Harley — AI Companion for Quest (Mixed Reality)

Harley is a small white-and-purple AI creature that lives in your real room on Meta Quest 3 / 3S.
It floats around you, sees what you see, knows where your furniture is, talks with you (Claude),
remembers you between sessions, and reacts when you poke it, pet it, wave at it, pick it up or throw it.

## What Harley can do

- **Talk** — look at Harley for a second (or press **A**) and speak. It answers out loud with
  Meta Voice SDK, or in a cute babble voice with subtitles when no TTS is set up.
- **See** — every message carries a small frame from the passthrough camera, so it can comment on what you're looking at.
- **Know your room** — reads the room scan (MRUK): "Harley, go sit on the couch" works.
- **Remember you** — keeps short facts about you between sessions (your name, what you like), saved on the headset only.
  Say "forget everything about me" to wipe it.
- **Move** — follows you lazily, wanders, sits on tables, hides behind you, flies to things.
- **Be touched** — poke, pet (stroke the top of its head), wave, **pinch to pick it up**, let go to place it,
  or **throw it** and watch it bounce off your walls.
- **Feel** — 8 emotions, blinking, eyes that follow you, spinning halo while it thinks, sleeps when ignored.

## What's inside

```
Assets/Harley/
  Scripts/
    CompanionBrain.cs        the mind: hears → sees → asks Claude → speaks, emotes, moves
    ClaudeClient.cs          Claude Messages API (image + structured JSON reply)
    CompanionEyes.cs         grabs a frame from the Quest passthrough camera
    CompanionRoom.cs         room objects from the MRUK room scan (couch, table, window...)
    CompanionMemory.cs       facts about you, kept between sessions (JSON on the headset)
    CompanionMovement.cs     follow / wander / go to / sit / hide / held / thrown
    CompanionVisual.cs       bob, blink, emotions, mouth, halo, pop-in, sparkles, tumble
    CompanionVoice.cs        Voice SDK auto-connect, subtitles, babble fallback
    CompanionSounds.cs       synthesized babble voice + reaction sounds (no audio files)
    CompanionInteraction.cs  poke, pet, wave, pinch/grip to grab, throw, look-to-talk
    CompanionConfig.cs       settings asset (API key, model, personality, memory, sound)
    CompanionTypes.cs        emotions, actions, reply parsing
  Editor/
    CompanionBuilder.cs      menu: Harley > Create Harley In Scene
preview/
  harley-preview.html        web preview of the character
```

## Setup

1. **Project** — Unity 6 (6000.0.38+), Android, URP. Easiest: Meta's *Mixed Reality* template, or a URP project
   with **Meta XR All-in-One SDK** + **Meta MR Utility Kit (MRUK)**. Run *Meta > Tools > Project Setup Tool* and fix everything.
2. **Building blocks** (*Meta > Tools > Building Blocks*): Camera Rig, Passthrough, Hand Tracking,
   **Passthrough Camera Access**, **MR Utility Kit** and **Effect Mesh** with colliders
   (so Harley sees, knows your furniture and avoids walls).
3. **Permissions** — select the Camera Rig. In **OVR Manager** check that *Enable Passthrough Camera Access* is on, and under
   *Permission Requests On Startup* tick **Passthrough Camera Access** and **Scene**.
   (Camera access needs Quest 3 / 3S with Horizon OS v74+, and doesn't run in the XR Simulator.)
4. Copy `Assets/Harley` into your project. If asked, import **TMP Essentials**.
5. Menu **Harley > Create Harley In Scene**. It builds Harley and its materials, and connects whatever it finds.
6. Menu **Harley > Select Config (API key)** → paste your Claude API key (console.anthropic.com).
   The config asset is git-ignored on purpose.
7. *Player Settings > Android*: Internet Access = **Require**. Build & Run.

### Voice (optional, free)

Install the **Meta Voice SDK** package, then add the **Dictation** and **Speak Text** building blocks
(they ask for a free Wit.ai app token). Or by hand: *Assets > Create > Voice SDK > Add App Dictation Experience to Scene*
and *Assets > Create > Voice SDK > TTS > Add Default TTS Setup*.

Harley finds both when you press Play and connects them itself (no event wiring). Without them,
it talks in its babble voice with subtitles. Prefer manual wiring? The UnityEvents on **CompanionVoice** still work.

## Testing in the Editor

On `CompanionBrain`, type in **Test Message** and press **Enter** in Play mode (or right-click the component →
*Send Test Message*). Put any room photo in `CompanionEyes > Editor Test Image` to test vision.
Memory lives in *Harley > Open Memory Folder* (`harley_memory.json`).

## Notes

- Model: `claude-haiku-5-5` by default (fastest). Use `claude-sonnet-5-5` in the config for smarter replies.
- The API key lives inside the app. Fine for your own headset; for a public release, put a small server in between.
- Arabic subtitles need an RTL plugin for TextMeshPro (e.g. RTLTMPro). The babble voice works in any language.
- Cost: each spoken message sends one 512 px image. Turn off *Proactive Comments* to save.
