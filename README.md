# Benchmix

An offline mashup editor. A DJ mixer's controls, but you work at your own pace and save a file at
the end instead of performing it live.

Built for one job: take two or more songs, get them to the same tempo and key without either one
sounding mangled, carve space for each with a three-band EQ, and render the result.

---

## What it does

- **Speed without pitch.** Stretch a track's tempo and its pitch stays put.
- **Pitch without speed.** Shift a track in semitones and its length stays put.
- **Or both linked**, which is pure resampling — like slowing a record down. Nothing is invented,
  so nothing can smear. Often the better choice under a few percent.
- **Three-band EQ per track**, isolator style: a knob at the bottom is a true kill, not a −23 dB
  approximation. Kill the bass on one track so the other's low end carries.
- **Tempo and key detection** that reads the file's own tags first and only analyses when it has
  to, so a tagged library loads instantly.
- **Match BPM** stretches a track onto the project tempo in one click, folding half-time and
  double-time readings first.
- **Live preview at full quality.** What you hear while turning a knob is what lands in the file.
- **Render** to 16/24-bit or 32-bit float WAV, with a safety limiter.

## What it deliberately does not do

No effects rack, no automation curves, no MIDI, no recording, no plugins. It is not a DAW. Every
control on screen is one you asked for.

---

## Getting started

1. **Add songs** — drag them onto the window, or use *+ Add tracks*. Drop position decides where
   on the timeline the clip lands.
2. **Set the project tempo** — it seeds from the first track you add. Everything matches to this.
3. **Hit *Match BPM*** on the other tracks. The status bar tells you exactly what it did.
4. **Line them up** — drag a clip sideways. It snaps to bars; hold <kbd>Ctrl</kbd> for free
   movement.
5. **Carve out space** — pull the topper's LOW knob to nothing so the bed keeps the bass.
6. **Listen** — <kbd>Space</kbd>. Shift-drag on the ruler to loop a section while you tweak.
7. **Render to file.**

### Controls

| | |
| --- | --- |
| <kbd>Space</kbd> | play / pause |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> | save / save as |
| <kbd>Ctrl</kbd>+<kbd>E</kbd> | render |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | undo / redo |
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>−</kbd> / <kbd>0</kbd> | zoom in / out / fit |
| <kbd>Ctrl</kbd>+scroll | zoom about the pointer |
| <kbd>Shift</kbd>+scroll | scroll sideways |
| click the ruler | move the playhead |
| <kbd>Shift</kbd>+drag the ruler | set a loop |
| drag a clip | move it (snaps to bars; <kbd>Ctrl</kbd> to free it) |
| <kbd>Alt</kbd>+click a lane | move the playhead without moving the clip |

Knobs: drag up and down, <kbd>Shift</kbd> for fine, double-click to reset, wheel to nudge.

---

## Two things worth knowing

**An untouched track is untouched.** If a track's tempo, pitch and EQ are all at their defaults,
its audio is copied to the render bit for bit — the stretcher is bypassed entirely and the EQ
reconstructs its input exactly. This is tested, not hoped for. It means running a song through
Benchmix and changing nothing gives you the song back.

**Key detection is a guess, and says so.** Values read from a file's tags are trusted. Values
Benchmix worked out itself are marked with a `?`. Tempo detection measured 94% exact with nothing
outright wrong; key detection measured 47%. Trust your ears over the key number.

---

## Building it

Needs the .NET 10 SDK. Nothing else — no C++ toolchain, no ffmpeg, no plugins.

```
dotnet build
dotnet test
dotnet run --project src/Benchmix.App
```

Build output goes to `%LOCALAPPDATA%\Benchmix.build\`, deliberately outside OneDrive.

To check the real UI still works after a change — it clicks the actual buttons through UI
Automation, runs minimised and never steals focus:

```
powershell -ExecutionPolicy Bypass -File scripts/ui-verify.ps1
```

The app also has three switches used for verification: `--uiprobe <png>` renders the live window
to a file, `--playtest <seconds>` checks the audio device is pulling in realtime (silently), and
`--autoclose <seconds>` closes it again. Passing file paths preloads them as tracks.

To publish a standalone exe:

```
dotnet publish src/Benchmix.App -c Release -r win-x64 --self-contained true \n  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

### The command line

`tools/Benchmix.Cli` drives the same engine without the UI:

```
benchmix analyze <file>...                       tempo and key, saying tag or estimate
benchmix mashup <bed> <topper> <out.wav> [opts]  tempo-match and render
```

---

## How it is put together

| Project | Role |
| --- | --- |
| `Benchmix.Engine` | audio and analysis. No UI types. Clips, stretch, EQ, mixer, render, tempo/key detection. |
| `Benchmix.Core` | the project model, its file format, undo, and the bridge that keeps the live graph matching the model. |
| `Benchmix.App` | WPF shell — timeline, waveforms, knobs, transport. |
| `Benchmix.Tests` | 72 tests, engine-level and model-level, plus end-to-end on real files. |

The signal chain per track is `clip → stretch → EQ → clip envelope → gain/pan → mix`, and **live preview and the
final render are the same chain** — the renderer just pulls blocks as fast as the CPU allows
instead of in realtime. There is no second code path that could disagree with what you heard.

Time-stretching is [Signalsmith Stretch](https://github.com/Signalsmith-Audio/signalsmith-stretch)
(MIT) via `SignalsmithStretch-CS`. Audio I/O and decoding is NAudio (MIT). Everything else —
EQ, resampler, tempo and key detection, waveform rendering — is written here.

Design notes and the measurements behind the choices are in [`docs/`](docs/).
