# Harley — AI Companion for Meta Quest

Harley is a small white-and-purple AI creature for Meta Quest 3 / 3S. It talks with you (Claude),
sees what you see, looks things up online (news, weather, scores), remembers you between sessions,
and reacts when you poke it, pet it, grab it or throw it.

There are two versions in this repo, sharing the same brain, body, voice and sounds:

| | **Window app** (recommended) | **Mixed reality** |
|---|---|---|
| How it opens | A normal app window, next to YouTube, the browser, Spotify… | Full mixed-reality app |
| What it sees | **Everything you see in the headset**: other apps, web pages, videos, and the room | The real room only (passthrough camera) |
| Where it lives | Inside its own window | Flies around your room, sits on your couch |
| Touch | Click to poke, drag to grab, flick to throw | Real hands: poke, pet, pinch, throw |

## What Harley can do

- **Talk** — press **Talk** (window) or look at it / press **A** (MR). Or **Type** in the window app.
- **See** — every message carries one picture of what you see, so you can ask "what's this video about?"
  or "read me this page".
- **Look things up** — web search for news, sports, prices and anything recent; live weather and a 3-day forecast.
- **Remember you** — short facts (your name, what you like) saved on the headset only. Say "forget everything about me" to wipe.
- **Feel** — 8 emotions, blinking, eyes that follow you, a halo that spins while it thinks, a babble voice, naps when ignored.

## What's inside

```
Assets/Harley/
  Scripts/
    CompanionBrain.cs        the mind: hears → sees → (looks things up) → asks Claude → speaks, emotes, moves
    ClaudeClient.cs          Claude Messages API: images, structured replies, tool loop, web search
    CompanionTools.cs        web_search + get_weather (Open-Meteo, free)
    MiniJson.cs              small JSON reader/writer
    CompanionSight.cs        base for Harley's eyes
      ScreenEyes.cs          window app: sees your whole view (Android screen capture)
      PassthroughEyes.cs     MR: sees through the passthrough camera
    CompanionMotion.cs       base for Harley's body
      PanelMotion.cs         window app: floats in its window, grab & throw inside it
      CompanionMovement.cs   MR: follow, wander, go to furniture, sit, hide, thrown off your walls
    CompanionInput.cs        base for touching Harley
      PanelInput.cs          window app: pointer click / drag / flick
      CompanionInteraction.cs MR: real hands and controllers
    PanelControls.cs         window app buttons: Talk, Type, Screen
    CompanionRoom.cs         MR: furniture from the room scan (MRUK)
    CompanionMemory.cs       facts about you, kept between sessions
    CompanionVoice.cs        Voice SDK auto-connect, subtitles, babble fallback
    CompanionSounds.cs       synthesized babble voice + reaction sounds
    CompanionVisual.cs       bob, blink, emotions, mouth, halo, pop-in, sparkles
    CompanionConfig.cs       settings: API key, model, city, personality, memory, sound
  Plugins/Android/           HarleyCapture*.java — screen capture service for the window app
  Editor/
    CompanionBuilder.cs      menus: Harley > Create Window App Scene / Create Mixed Reality Harley
    HarleyAndroidManifest.cs adds screen-capture entries and the window size to window-app builds
preview/harley-preview.html  web preview of the character
```

## Window app setup

1. **Project** — Unity Hub → New project → Unity 6 → **Universal 3D**. Switch the platform to **Android**.
   Keep XR off for Android (*Project Settings > XR Plug-in Management*), so Quest opens Harley as a window.
2. Copy `Assets/Harley` into the project. If asked, import **TMP Essentials**.
3. Menu **Harley > Create Window App Scene**, then save the scene and add it to the build.
4. Menu **Harley > Select Config**: paste your Claude API key (console.anthropic.com), and set
   **Home City** (e.g. `Münster, Hessen`) and **Country Code** (`DE`) for weather and local news.
   The config asset is git-ignored on purpose.
5. *Player Settings > Android*: Internet Access = **Require**, Scripting Backend **IL2CPP**, Target Architecture **ARM64**.
6. **Build And Run** with the headset connected (developer mode on).
7. On the headset Harley opens as a window. Quest asks to allow screen capture: tap **Start**.
   Open YouTube or the browser next to it and ask Harley about what you're watching.

### Voice for the window app (optional, free)

Without voice you can **Type**, and Harley answers with subtitles and its babble voice.
To talk out loud, add Meta's Voice SDK to `Packages/manifest.json`:

```json
"scopedRegistries": [
  { "name": "Meta XR", "url": "https://npm.developer.oculus.com", "scopes": ["com.meta.xr.sdk"] }
],
"dependencies": {
  "com.meta.xr.sdk.voice": "85.0.1"
}
```

Then *Meta > Voice SDK > Get Started* (paste a free Wit.ai app token),
*Assets > Create > Voice SDK > Add App Dictation Experience to Scene* and
*Assets > Create > Voice SDK > TTS > Add Default TTS Setup*. Harley connects to both by itself when it starts.

## Mixed reality setup

1. **Project** — Unity 6 (6000.0.38+), Android, URP, with **Meta XR All-in-One SDK** + **Meta MR Utility Kit**.
   Run *Meta > Tools > Project Setup Tool*.
2. **Building blocks**: Camera Rig, Passthrough, Hand Tracking, **Passthrough Camera Access**, **MR Utility Kit**,
   **Effect Mesh** (colliders on), and for voice **Dictation** + **Speak Text**.
3. In **OVR Manager** check *Enable Passthrough Camera Access*, and under *Permission Requests On Startup*
   tick **Passthrough Camera Access** and **Scene**. (Quest 3 / 3S, Horizon OS v74+.)
4. Copy `Assets/Harley`, then **Harley > Create Mixed Reality Harley** and **Harley > Select Config**.
5. Delete `Assets/Harley/Editor/WindowAppBuild.txt` if it exists (it adds the screen-capture entries).

## Privacy and limits

- **Screen sharing** sends one picture of your view to Claude only when Harley thinks: when you talk to it,
  type, or when it glances on its own (*Proactive Comments*, every 1–2 minutes; turn it off in the config).
  A notification shows while sharing is on; tap **Stop screen** to end it.
- Meta allows the screen capture API for casting, streaming and screen sharing. That's fine for your own
  sideloaded app; a Meta Store release may not pass review with it.
- Protected videos (e.g. Netflix) show up black in the capture.
- The API key lives inside the app. Fine for your own headset; for a public release, put a small server in between.
- Arabic subtitles need an RTL plugin for TextMeshPro (e.g. RTLTMPro). The babble voice works in any language.
- Cost: each message sends one picture (768–1024 px), web searches cost extra per search (see Anthropic pricing).

## Testing in the Editor

On `CompanionBrain`, type in **Test Message** and press **Enter** in Play mode (or use **Type** in the window app).
Put any screenshot in **Editor Test Image** on the eyes component to test vision.
Memory lives in *Harley > Open Memory Folder* (`harley_memory.json`).
