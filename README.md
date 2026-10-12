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
- **Tempo ramps.** Change a track's tempo gradually over a stretch of time — speed one song from
  110 to 115 BPM while it mixes into the next.
- **Live preview at full quality.** What you hear while turning a knob is what lands in the file.
- **Render** to 16/24-bit or 32-bit float WAV, with a safety limiter.

## What it deliberately does not do

No effects rack, no automation curves (the one exception: tempo ramps), no MIDI, no recording, no
plugins. It is not a DAW. Every
control on screen is one you asked for.

---

## Getting started

1. **Add songs** — drag them onto the window, or use *+ Add tracks*. Drop position decides where
   on the timeline the clip lands.
2. **Set the project tempo** — it seeds from the first track you add. Everything matches to this.
3. **Hit *Match BPM*** on the other tracks. The status bar tells you exactly what it did.
4. **Line them up** — drag a section sideways, or up and down onto another track. It snaps to the
   edges of other sections when it gets close, and otherwise lands exactly where you let go; hold
   <kbd>Ctrl</kbd> to stop it snapping at all.
5. **Carve out space** — pull the topper's LOW knob to nothing so the bed keeps the bass.
6. **Listen** — <kbd>Space</kbd>. Shift-drag on the ruler to loop a section while you tweak.
7. **Render to file.**

### Controls

| | |
| --- | --- |
| <kbd>Space</kbd> | play / pause (not while typing a name or a tempo) |
| double-click a lane, the ruler, or the empty room under the tracks | play from there; if already playing, jump there and keep playing |
| click the scroll wheel | stop, and go back to where you started playing — press <kbd>Space</kbd> to hear the same bit again. (The ■ button goes back to 0:00.) |
| double-click the scroll wheel | stop, and go back to the very start: the playhead and the view both to 0:00 |
| turn the scroll wheel over the ruler | move the playhead back and forth — up is later, down is earlier; the view follows it. While playing, it plays on from the new spot |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> | save / save as |
| <kbd>Ctrl</kbd>+<kbd>E</kbd> | render |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | undo / redo |
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>−</kbd> / <kbd>0</kbd> | zoom in / out / fit |
| <kbd>Ctrl</kbd>+scroll | zoom about the pointer |
| <kbd>Shift</kbd>+scroll | scroll sideways |
| click the ruler | move the playhead |
| <kbd>Shift</kbd>+drag the ruler | set a listening loop |
| <kbd>Ctrl</kbd>+<kbd>T</kbd> | add an empty track |
| <kbd>Ctrl</kbd>+<kbd>I</kbd> | split the selected track at the playhead |
| <kbd>Ctrl</kbd>+<kbd>J</kbd>, or right-click → *Join sections* | join pieces of a song back into one: the selected ones, or the one you clicked and the piece next to it. Any gap between them — where you took a piece out — is the song again |
| <kbd>Ctrl</kbd>+<kbd>X</kbd> / <kbd>C</kbd> | cut / copy the selected section |
| <kbd>Ctrl</kbd>+<kbd>V</kbd> | paste onto the **selected track** at the **playhead** — a section, or song files copied in Explorer |
| <kbd>Ctrl</kbd>+<kbd>D</kbd> | repeat the selected section straight after itself |
| <kbd>Ctrl</kbd>+<kbd>L</kbd> | loop mode on / off |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> | ramp mode on / off |
| in ramp mode: click a track, then click it again further along | start and end a tempo ramp, then type the tempo to reach (<kbd>Enter</kbd> keeps it, <kbd>Esc</kbd> cancels) |
| in ramp mode: drag a ramp's dot / click its end dot | move that end / change the tempo it reaches |
| right-click on a tempo ramp → *Remove tempo ramp* | take it away; <kbd>Ctrl</kbd>+<kbd>Z</kbd> puts it back |
| drag a section | move it; drag it up or down to put it on another track. Snaps to other sections' edges (never to bars); <kbd>Ctrl</kbd> frees it |
| drag one of several selected sections | move them all together. All on one track: sideways, or onto another track. On several tracks: sideways only. They keep their exact spacing, snap as one to other sections' edges, and one <kbd>Ctrl</kbd>+<kbd>Z</kbd> puts them all back |
| drag a section's end | trim it (snaps to other edges; stops at the next section). In loop mode: repeat it instead |
| <kbd>Alt</kbd>+click a lane | move the playhead without moving anything |
| <kbd>Ctrl</kbd>+click a section | add it to the selection, or take it out again |
| <kbd>Shift</kbd>+click a section | select every section between the section you clicked before and this one |
| drag across empty space (a gap, an empty track, or under the tracks) | draw a box: every section it touches is selected, as on the Windows desktop. <kbd>Ctrl</kbd> or <kbd>Shift</kbd> held as you start adds to what is selected |
| <kbd>Delete</kbd> | delete the selected sections, leaving the gaps (not while typing in a text box) |
| drag a track's name (or its ≡) up or down | move the track; a blue line shows where it will land |
| double-click a track's name, or right-click its strip → *Rename track* | rename it: type, then <kbd>Enter</kbd> keeps it (so does clicking anywhere else), <kbd>Esc</kbd> keeps the old name |

The selected track has a blue bar down its left edge. Clicking anywhere on a track — its lane, its
name, a knob — selects it, and that is where <kbd>Ctrl</kbd>+<kbd>V</kbd> pastes.

**Several sections at once.** Click one section, then <kbd>Ctrl</kbd>+click others to add them, or
<kbd>Shift</kbd>+click to take every section between the two — on every track from the one to the
other, but only sections wholly inside that stretch of time. Or drag a box across empty space: it
takes every section it **touches**, even partly, on every track it covers — but only what is on
screen, as it does not scroll the view. <kbd>Delete</kbd> removes the whole
selection in one undo step and leaves the gaps. Dragging any one of them moves them all; a click on
one without dragging keeps just that one. Cut, copy and repeat act on one section: the one you
clicked last. Every button, knob and slider explains itself in a tooltip.

**Loop mode.** Click a section, press *LOOP MODE* (or <kbd>Ctrl</kbd>+<kbd>L</kbd>), then drag
either of its ends: instead of trimming, the section repeats to fill the space, in whole repeats.
Hold <kbd>Ctrl</kbd> to end on part of a repeat. The repeats are ordinary sections — the same thing
<kbd>Ctrl</kbd>+<kbd>D</kbd> makes — and a run of them is treated as one loop, so grabbing the end of
it again lengthens or shortens the whole thing. It overwrites whatever it runs over on that track, as
a paste does, and one <kbd>Ctrl</kbd>+<kbd>Z</kbd> takes the whole drag back. This is not the
Shift-drag listening loop on the ruler, which only changes what plays.

**Tempo ramps.** To change a track's tempo gradually:

1. Press *RAMP MODE* (or <kbd>Ctrl</kbd>+<kbd>R</kbd>).
2. Click the track where the tempo should start changing. A yellow dot appears, labelled with the
   tempo the track is playing there.
3. Click the same track where the change should be complete. A box opens beside that point.
4. Type the tempo to reach and press <kbd>Enter</kbd>.

A yellow line joins the two dots: from the top edge down to the bottom if the tempo slows, from the
bottom up to the top if it speeds up. After the ramp the track stays at the new tempo. Later audio
on that track moves to keep time with it. To change a ramp, drag either dot sideways, or click the
end dot to type a new tempo. Holding <kbd>Ctrl</kbd> while you click or drag stops the dot snapping
to nearby edges. Each track's ramps are its own; *TEMPO* is the tempo before the first ramp,
*Reset* removes the ramps, and each change is one <kbd>Ctrl</kbd>+<kbd>Z</kbd>. While ramp mode is
on, clicks place dots instead of selecting or moving sections; turn it off to edit sections again.

**Empty tracks.** *+ Empty track* makes a track with nothing on it. Paste or drag a section onto it
and it takes on the tempo and pitch that section was playing at, so it sounds and lands exactly as
it did — but with its own EQ and volume, which is usually why you moved it. Drop a song file on an
empty track's lane (or copy one in Explorer and press <kbd>Ctrl</kbd>+<kbd>V</kbd>) and the track
becomes that song.

Knobs: drag up and down, <kbd>Shift</kbd> for fine, double-click to reset, wheel to nudge.

---

## Two things worth knowing

**An untouched track is untouched — by the signal path.** If a track's tempo, pitch and EQ are all
at their defaults, its audio reaches the master bus bit for bit: the stretcher is bypassed entirely
and the EQ reconstructs its input exactly. Both are tested, sample by sample.

The one thing that *will* change it is the **master limiter**, which is on by default at −0.3 dBFS
and exists to stop a two-song mix clipping. A single loud track passed through on its own therefore
comes out slightly attenuated. That is the limiter doing its job, not the editor altering your
audio, and it is a per-project setting — but it is worth knowing before you use Benchmix as a
pass-through converter.

A track with a **tempo ramp** goes through the stretcher for its whole length, so it is not
bit-identical to its file anywhere — even where it plays at its own tempo.

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

To check the published exe after a change — every verification switch, one after another:

```
powershell -ExecutionPolicy Bypass -File scripts/verify-exe.ps1
```

To check the real UI still works — it presses the actual buttons and reads the actual labels:

```
powershell -ExecutionPolicy Bypass -File scripts/ui-verify.ps1
```

Both are safe to run while someone is using the machine, and both prove it: each run is watched
from outside and fails if it ever took focus, appeared on a monitor, had a taskbar button, or could
have been activated.

The app also has several switches used for verification (the authoritative list is the switch
block in `src/Benchmix.App/App.xaml.cs`): `--uiprobe <png>` renders the live window to a file,
`--playtest <seconds>` checks the audio device is pulling in realtime (silently), and
`--autoclose <seconds>` closes it again. Passing file paths preloads them as tracks.

**Any of these switches, `--autoclose` included, makes it a scripted run**, which cannot be seen
or heard: the window opens beyond the edge of every monitor, has no taskbar button, can never be
activated, shows no dialogs (not even "discard unsaved changes?"), and — except for `--playtest` —
opens no audio device. They are for checks, not for a normal session. A switch with a missing or
unreadable value, or one it does not know, is refused: the app logs why and exits before opening
anything. Do not launch them with `-WindowStyle Minimized`: a minimised window has a taskbar button.

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
| `Benchmix.Tests` | 190 tests, engine-level and model-level, plus end-to-end on real files. |

The signal chain per track is `clip → stretch → EQ → clip envelope → gain/pan → mix`, and **live preview and the
final render are the same chain** — the renderer just pulls blocks as fast as the CPU allows
instead of in realtime. There is no second code path that could disagree with what you heard.

Time-stretching is [Signalsmith Stretch](https://github.com/Signalsmith-Audio/signalsmith-stretch)
(MIT) via `SignalsmithStretch-CS`. Audio I/O and decoding is NAudio (MIT). Everything else —
EQ, resampler, tempo and key detection, waveform rendering — is written here.

Design notes and the measurements behind the choices are in [`docs/`](docs/).
