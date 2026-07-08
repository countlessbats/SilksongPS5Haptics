# Silksong PS5 Haptics (PC)

**Play Hollow Knight: Silksong's real PS5 haptic feedback on PC with a DualSense controller — wired or wirelessly via DSX.**

Silksong's PC build secretly ships the *entire* PS5 haptic dataset: 314 authored
haptic waveforms (footsteps, nail hits, bench rests, bells, bosses...) inside
`vibrationstatic_assets_vibrationdataps5.bundle` — 7.4 MB of feel that the
desktop build never plays. The game's PC code path only evaluates two
dual-motor rumble curves. This mod plays the real waveforms.

## How it works

```
Silksong (BepInEx plugin)                    HapticsBridge (tray app)
┌──────────────────────────────┐            ┌──────────────────────────────┐
│ VibrationManager.GetMixer ── │  48 kHz    │ TCP :48111 ──> WASAPI shared │
│ hook: PS5-clip emissions ──► │  stereo    │ mapped to actuator channels  │
│ mixed on a 10 ms thread      │  float PCM │ (3/4 of the DualSense audio  │
│ everything else = rumble     │ ─────────► │ endpoint)                    │
└──────────────────────────────┘            └──────────────────────────────┘
```

- The **plugin** hooks the game's `VibrationManager`. Every vibration event in
  Silksong carries up to four representations (canned, Switch HD, gamepad
  curves, PS5 waveform); when a PS5 waveform exists, the plugin mixes it
  (respecting strength, looping, speed, pause, and the in-game vibration
  settings) and streams it to the bridge. Events without PS5 data — or any
  time the bridge is missing — fall back to normal rumble automatically.
- The **bridge** is a small system-tray app that plays the stream into the
  DualSense's audio endpoint, where channels 3/4 drive the left/right voice
  coil actuators. On first run it extracts the haptic clips **from your own
  game files** (no game assets are distributed with this mod).
- The chime you feel on startup is the bridge confirming the actuator path.

## Requirements

- Hollow Knight: Silksong (Steam, Windows)
- A DualSense (PS5) controller
- For **wireless** haptics: [DSX](https://store.steampowered.com/app/1812620/DSX/)
  v3.2 beta or later with the DSX+ DLC, "BT Audio/Haptics" enabled
  (Haptics/Rumble page). For **wired**, just plug in USB — no DSX needed.
- BepInEx 5 (the installer sets this up if missing)

## Install

1. Download the latest release zip and extract it anywhere.
2. Run `Install.bat`. It finds your Steam install automatically or asks for
   the folder (paste it in any format — quotes or not, it copes).
3. Launch the game. First run extracts the haptic data (~10 s, tray dot turns
   blue), then you'll feel a shimmer chime in the controller.

Tray dot: 🟢 game connected · 🟠 waiting for game · 🔵 extracting · ⚫ no
DualSense audio device (enable DSX BT Audio/Haptics or plug in USB).

Manual install: copy `files/BepInEx` from the zip over your game folder
(requires BepInEx already installed).

## Config

`BepInEx/config/com.will.silksong.ps5haptics.cfg`:

| Setting | Default | Meaning |
|---|---|---|
| `MasterGain` | 1.0 | Haptic intensity multiplier |
| `KeepRumble` | false | Also fire normal rumble alongside haptics |
| `AutoStartBridge` | true | Launch the tray bridge with the game |
| `BridgePort` | 48111 | Localhost TCP port |

Bridge flags: `--device <substring>` (default `DualSense`), `--map 12|34|auto`,
`--buffer-ms <n>` (default 60, lower = less latency), `--latency-ms <n>`
(default 100), `--event-sync` (low-latency WASAPI; only helps on solid wired
endpoints), `--no-keepalive` (disable the inaudible pilot tone that keeps
Bluetooth links awake), `--no-chime`, `--list`, `--extract-only <dir>`.

The in-game vibration options (Off / Reduced / On) are respected.

## Troubleshooting

- **No chime, gray tray dot** — Windows can't see the DualSense as an audio
  device. Wireless: enable DSX's BT Audio/Haptics (needs DSX+). Wired: replug
  USB. `HapticsBridge.exe --list` shows what it can see.
- **Haptics feel doubled/smeared** — disable DSX's own Audio-To-Haptics for
  this game; the mod feeds the real waveforms already.
- **Haptics work at first, then go dead (esp. Bluetooth)** — some BT audio
  stacks idle the link on sustained digital silence and never resume. Since
  0.3.3 the bridge streams an imperceptible keepalive tone to prevent this and
  watches the device render clock, reopening the session automatically if the
  endpoint stops consuming audio (`render clock frozen` in the log). If you
  still see repeated `Reopening audio session` lines, the endpoint itself is
  unstable — re-pair the controller or use USB.
- **Feels laggy** — start the bridge with `--buffer-ms 30` (and, on wired,
  optionally `--latency-ms 40`).
- **Diagnosis** — `BepInEx/LogOutput.log` (game side; look for
  `Connected to HapticsBridge` and `Haptic play:` lines) and
  `HapticsBridge.log` next to the bridge exe.

## Building from source

```
dotnet build src/Bridge/HapticsBridge.csproj -c Release
dotnet build src/Plugin/SilksongPS5Haptics.csproj -c Release -p:GameDir="<your Silksong folder>"
```

The plugin references `Assembly-CSharp.dll` and Unity modules from your local
game install (not distributable); `GameDir` defaults to the standard Steam path.

## Legal

This mod contains no Team Cherry assets. The haptic clips are extracted
locally, at runtime, from the game files you already own. MIT licensed;
Hollow Knight: Silksong is © Team Cherry; DualSense is a trademark of Sony
Interactive Entertainment.

## Credits

- Team Cherry — for authoring (and shipping!) the haptic data
- [BepInEx](https://github.com/BepInEx/BepInEx), [HarmonyX](https://github.com/BepInEx/HarmonyX),
  [NAudio](https://github.com/naudio/NAudio), [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET),
  [Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp), [NVorbis](https://github.com/NVorbis/NVorbis)
- [DSX](https://store.steampowered.com/app/1812620/DSX/) — wireless DualSense haptics on Windows
