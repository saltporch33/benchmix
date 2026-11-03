# Benchmix

A purpose-built DAW for creating simple music mixes and mashups and mixing down to mp3 or wav. Standard functions like BPM and Key stretching (including bpm warping). Standard three-band EQ. Ramp mode for programmed temp changes. And here's where one would normally say "...and so much more!" However, that would defeat the point of this app. I vibe coded this to use in lieu of Ableton or Audacity, because I only need a handful of tools for my purposes and don't need the clutter and bloat. There certainly _are_ more features and functions, but it's a curated set! __



---

## Features and Functions

- **Speed without pitch.** Stretch a track's tempo while maintaining key.
- **Pitch without speed.** Stretch a track's pitch while maintaining bpm.
- **Or pure sampling** (think: slowing a record down)
- **Three-band EQ per track**, isolator style. Kill the bass on one track so the other's low end carries.
- **Tempo and key detection** that reads a loaded file's tags first and detects tempo/key in their absence.
- **Sync** similar to a sync button on dj software
- **Tempo ramps.** Change a track's tempo gradually over a stretch of time
- **Warp markers.** allows you to fine tune songs with slight tempo drift (think: music performed by human beings vs digitally produced music). 
- **Tabs.** Several arrangements in one file, like sheets in an excel workbook. handy for quick decluttering.
- **Render** to 32-bit float WAV (the default), 24/16-bit WAV, or a 320 kbps MP3, with a safety limiter.
  Muted tracks are left out, a solo leaves out every other track, and it ends where the last track
  audible (i.e. not muted) ends.
- **Autosave, and versions history.** Revert back to a previous, timestamped state. Also a message bar in bottom left of app's window which indicates what significant action was last taken (super handy to know what you'll be undoing if you hit ctrl z).

## Out of scope

I don't need an effects rack, no complex automation curves. All i need is tempo ramps and warp markers), no MIDI,
no audio recording, no plugins. Nothin' nerdy. Purists need not apply. 

## Ok, the rest of the readme is unedited AI-generated, so I apologize for the sloppy register. But it should contain sufficient instructions for using the app. 
---


### Controls

| | |
| --- | --- |
| <kbd>Space</kbd> | play / pause (not while typing a name or a tempo) |
| double-click a lane, the ruler, or the empty room under the tracks | play from there; if already playing, jump there and keep playing |
| click the scroll wheel | pause exactly where the playhead is, anywhere in the window — press <kbd>Space</kbd> to play on from there. (The ■ button stops and goes back to 0:00.) |
| double-click the scroll wheel | stop, and go back to where you started playing — press <kbd>Space</kbd> to hear the same bit again |
| turn the scroll wheel anywhere over the tracks | scroll the tracks up and down when they don't all fit; over a knob it turns that knob instead |
| <kbd>Alt</kbd>+scroll, over the ruler or a waveform | move the playhead back and forth — up is later, down is earlier; the view follows it. While playing, it plays on from the new spot |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> | save now, keeping a version / save as (edits are autosaved anyway once the project has a file) |
| <kbd>Ctrl</kbd>+<kbd>E</kbd> | render |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | undo / redo |
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>−</kbd> / <kbd>0</kbd> — top row or number pad | zoom in / out / fit |
| <kbd>Ctrl</kbd>+scroll | zoom about the pointer |
| <kbd>Shift</kbd>+scroll | scroll sideways |
| hold the right mouse button and drag — on the ruler, the lanes or the track controls | pan the view sideways, and up and down through the tracks |
| <kbd>PageUp</kbd> / <kbd>PageDown</kbd>, <kbd>Ctrl</kbd>+<kbd>Home</kbd> / <kbd>Ctrl</kbd>+<kbd>End</kbd> | scroll the tracks a screen at a time, to the top, to the bottom (not while typing in a box) |
| click the ruler | move the playhead |
| <kbd>Shift</kbd>+drag the ruler, or <kbd>Shift</kbd>+click twice | set a listening loop |
| <kbd>Ctrl</kbd>+<kbd>T</kbd> | add an empty track |
| right-click a track's controls → *Duplicate track* | a copy right below it: the same sections, knobs, tempo ramps and warp markers |
| right-click a track's controls → *Move to tab* ▸ | send it to another tab; <kbd>Ctrl</kbd>+<kbd>Z</kbd> here brings it back |
| right-click a track's controls → *Find missing file…* | point at a song that moved or was renamed; every section cut from it follows, on every tab. Only there while a song is missing |
| click a tab / double-click it / right-click it | open it / rename it / rename, move or remove it. <kbd>+</kbd> at the end of the row adds a tab |
| <kbd>Ctrl</kbd>+<kbd>Tab</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> | the next tab / the one before |
| <kbd>Ctrl</kbd>+<kbd>I</kbd> | split the selected track at the playhead |
| <kbd>Ctrl</kbd>+<kbd>J</kbd>, or right-click → *Join sections* | join pieces of a song back into one: the selected ones, or the one you clicked and the piece next to it. Any gap between them — where you took a piece out — is the song again |
| right-click a section → *Crossfade from the section before* ▸ | how long the fade is where that section starts on the very frame the one before ends: 5 ms unless you pick longer, up to 200 ms. The lane draws it as a narrow X just after the join |
| <kbd>Ctrl</kbd>+<kbd>X</kbd> / <kbd>C</kbd> | cut / copy the selected section — or, in mark mode, the marked stretch |
| <kbd>Ctrl</kbd>+<kbd>V</kbd> | paste onto the **selected track** at the **playhead** — a section, or song files copied in Explorer |
| <kbd>Ctrl</kbd>+<kbd>D</kbd> | repeat the selected section — or, in mark mode, the marked stretch — straight after itself |
| <kbd>Ctrl</kbd>+<kbd>L</kbd> | loop mode on / off |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> | ramp mode on / off |
| <kbd>Ctrl</kbd>+<kbd>M</kbd> | mark mode on / off |
| <kbd>Ctrl</kbd>+<kbd>W</kbd> | warp mode on / off |
| in mark mode: drag across a track | mark that stretch of it; drag either end of the mark to move it |
| in mark mode: click / <kbd>Shift</kbd>+click a track | put the playhead there / mark from the playhead to there |
| in ramp mode: click a track, then click it again further along | start and end a tempo ramp, then type the tempo to reach (<kbd>Enter</kbd> keeps it, <kbd>Esc</kbd> cancels) |
| in ramp mode: drag a ramp's dot / click its end dot | move that end / change the tempo it reaches |
| right-click on a tempo ramp → *Remove tempo ramp* | take it away; <kbd>Ctrl</kbd>+<kbd>Z</kbd> puts it back |
| in warp mode: click a track | put a warp marker on the beat there; nothing moves |
| in warp mode: drag a warp marker, or press on a track and drag | move that beat to where you let go; the markers either side stay put, and everything after the last marker moves with it |
| right-click on a warp marker → *Remove warp marker* | take it away; <kbd>Ctrl</kbd>+<kbd>Z</kbd> puts it back |
| drag a section | move it; drag it up or down to put it on another track. Snaps to other sections' edges (never to bars); <kbd>Ctrl</kbd> frees it |
| drag one of several selected sections | move them all together. All on one track: sideways, or onto another track. On several tracks: sideways only. They keep their exact spacing, snap as one to other sections' edges, and one <kbd>Ctrl</kbd>+<kbd>Z</kbd> puts them all back |
| drag a section's end | trim it (snaps to other edges; stops at the next section). In loop mode: repeat it instead |
| <kbd>Alt</kbd>+click a lane | move the playhead without moving anything |
| <kbd>Ctrl</kbd>+click a section | add it to the selection, or take it out again |
| <kbd>Shift</kbd>+click a section | select every section between the section you clicked before and this one |
| drag across empty space (a gap, an empty track, or under the tracks) | draw a box: every section it touches is selected, as on the Windows desktop. <kbd>Ctrl</kbd> or <kbd>Shift</kbd> held as you start adds to what is selected |
| <kbd>Delete</kbd> | delete the selected sections — or, in mark mode, what is in the marked stretch — leaving the gaps (not while typing in a text box) |
| drag a track's name (or its ≡) up or down | move the track; a blue line shows where it will land |
| double-click a track's name, or right-click its strip → *Rename track* | rename it: type, then <kbd>Enter</kbd> keeps it (so does clicking anywhere else), <kbd>Esc</kbd> keeps the old name |

A saved project opens from Explorer with a double-click, once Windows has been told to open
`.benchmix` files with Benchmix. Playback follows Windows' sound output: unplug the headphones
mid-song and it carries on through the speakers from the same moment, and back again when they
reconnect.

The selected track has a blue bar down its left edge. Clicking anywhere on a track — its lane, its
name, a knob — selects it, and that is where <kbd>Ctrl</kbd>+<kbd>V</kbd> pastes.
<kbd>Ctrl</kbd>+click other tracks' strips to select several, or <kbd>Shift</kbd>+click for a run
of them: *Match key* on one then shifts the others to its key, and a right-click on *Match BPM*
matches them to their average tempo. A press on a selected track's button or knob keeps the
selection; a click on a strip's background or in a lane goes back to one track.

**Several sections at once.** Click one section, then <kbd>Ctrl</kbd>+click others to add them, or
<kbd>Shift</kbd>+click to take every section between the two — on every track from the one to the
other, but only sections wholly inside that stretch of time. Or drag a box across empty space: it
takes every section it **touches**, even partly, on every track it covers — but only what is on
screen, as it does not scroll the view. <kbd>Delete</kbd> removes the whole
selection in one undo step and leaves the gaps. Dragging any one of them moves them all; a click on
one without dragging keeps just that one. <kbd>Ctrl</kbd>+<kbd>C</kbd>, <kbd>Ctrl</kbd>+<kbd>X</kbd> and
<kbd>Ctrl</kbd>+<kbd>D</kbd> act on all of them: a paste puts each section down where it fell, the same
distance apart to the frame, leaving what lies under the gaps between them; rows are kept across
tracks, with empty tracks added for any that would fall off the bottom; a repeat puts the group
straight after itself. To copy or cut part of a track, or a whole stretch with its gaps, mark it
(below). Every button, knob and slider explains itself in a tooltip.

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

**Warp markers.** To pull a song that drifts back into time with another:

1. Get the two as close as they will go first: press *Match BPM*, or type TEMPO's exact value.
2. Press *WARP MODE* (or <kbd>Ctrl</kbd>+<kbd>W</kbd>).
3. Click the drifting song's track on a beat that is still in time with the other song. An
   aqua-mint line with a small triangle at its top appears there: a warp marker. Nothing moves.
4. Further along, find a beat that has drifted. Zoom in (<kbd>Ctrl</kbd>+scroll) until you can see
   the beats of both songs. <kbd>Alt</kbd>+click the other song's beat to put the playhead on it.
   Then press on the drifting song's beat and drag it onto the playhead: a marker goes down where
   you press, and the beat snaps onto the playhead when it gets close. Let go.
5. Further along again, wherever it drifts, do step 4 again.
6. To take a marker away, right-click it → *Remove warp marker*. <kbd>Ctrl</kbd>+<kbd>Z</kbd>
   takes back the last click, drag or removal.

Between two markers the audio is sped up or slowed down evenly so that both land where they are;
the top of the lane says by how much where there is room, "+0.8%" faster or "−0.8%" slower. Nothing
before the marker on the left moves. After the last marker the song plays at its own tempo again,
so everything after it moves along with it. A stretch can go at most twice as fast or half as fast;
the drag stops there and the status bar says so. A dragged beat also snaps to the edges of sections
and to warp markers on other tracks, and to tempo ramps' dots; hold <kbd>Ctrl</kbd> to place it
freely. The markers belong to the track, not to a section: a section moved past one is warped by
where it now is, so warp after arranging. They go with the song onto an empty track it is pasted or
dragged onto, and onto the tracks *Split into vocals + instrumental* makes. *Match BPM* keeps them
on their beats; *Reset* removes them. While warp mode is on, clicks place and move markers instead
of selecting or moving sections, and a double-click plays only on the ruler or under the tracks;
turn it off to edit sections again.

**Mark mode.** To copy, cut, delete or repeat part of a track without splitting it first:

1. Press *MARK MODE* (or <kbd>Ctrl</kbd>+<kbd>M</kbd>).
2. Drag across a track from where the part starts to where it ends. It lights up, with its length
   at its top left. The ends snap to the edges of sections on any track and to the playhead; hold
   <kbd>Ctrl</kbd> to place them exactly. Drag either end of the mark to move it. Or click where it
   should start and <kbd>Shift</kbd>+click where it should end.
3. <kbd>Ctrl</kbd>+<kbd>C</kbd> copies it, <kbd>Ctrl</kbd>+<kbd>X</kbd> cuts it, <kbd>Delete</kbd>
   removes it and <kbd>Ctrl</kbd>+<kbd>D</kbd> repeats it straight after itself. Cut and Delete
   leave a gap, as always.
4. To paste: click a track where it should go — in mark mode a click puts the playhead there — and
   press <kbd>Ctrl</kbd>+<kbd>V</kbd>. The mark moves onto what was pasted.

A mark can start or end anywhere: in the middle of a section, across several, or in a gap. What is
copied is the whole stretch, gaps included, and pasting it covers the whole stretch it lands on —
a gap in it clears what was under it. While mark mode is on, a drag never moves a section, and a
double-click still plays. The mark is one stretch on one track; it goes when mark mode is turned
off, and an undo leaves it where it is. Mark mode, loop mode, ramp mode and warp mode are never two
on together.

**Empty tracks.** *+ Empty track* makes a track with nothing on it. Paste or drag a section onto it
and it takes on the tempo and pitch that section was playing at, so it sounds and lands exactly as
it did — but with its own EQ and volume, which is usually why you moved it. Drop a song file on an
empty track's lane (or copy one in Explorer and press <kbd>Ctrl</kbd>+<kbd>V</kbd>) and the track
becomes that song.

Knobs: drag up and down, <kbd>Shift</kbd> for fine, double-click the knob to reset, wheel to nudge.
**Double-click the number under a knob to type the exact value**, then <kbd>Enter</kbd> (<kbd>Esc</kbd>
keeps it as it was): TEMPO as a percentage to 0.01 (−4.21 is 4.21% slower), PITCH in semitones to
0.01 (−2.88), LOW, MID, HIGH and VOL in dB to 0.1 (−6.5, or KILL). A value outside the knob's range is
refused and the status bar gives the range. Each typed value is one <kbd>Ctrl</kbd>+<kbd>Z</kbd>.

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

A track with a **tempo ramp**, or a **warp marker** that has been moved, goes through the stretcher
for its whole length, so it is not bit-identical to its file anywhere — even where it plays at its
own tempo. A warp marker put down and never moved changes nothing. With *LINK* on, a moved marker
changes the pitch along with the speed, as turning TEMPO does: a stretch played 1% faster is about
17 cents higher.

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
| `Benchmix.Tests` | 429 tests, engine-level and model-level, plus end-to-end on real files. |

The signal chain per track is `clip → stretch → EQ → clip envelope → gain/pan → mix`, and **live preview and the
final render are the same chain** — the renderer just pulls blocks as fast as the CPU allows
instead of in realtime. There is no second code path that could disagree with what you heard.

Time-stretching is [Signalsmith Stretch](https://github.com/Signalsmith-Audio/signalsmith-stretch)
(MIT) via `SignalsmithStretch-CS`. Audio I/O and decoding is NAudio (MIT). Everything else —
EQ, resampler, tempo and key detection, waveform rendering — is written here.

Design notes and the measurements behind the choices are in [`docs/`](docs/).
