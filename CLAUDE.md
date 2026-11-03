# Benchmix — read this first

An offline mashup editor for Daniel Mackey. `README.md` says what it is, `docs/DESIGN.md` is the
architecture, `docs/DECISIONS.md` what was decided and why, `docs/VERIFICATION.md` the evidence.

This file only covers what a confident first read gets **wrong**. It earns its place by preventing
mistakes, not by describing the project.

## Things that look like bugs and are not

- **Cut and delete leave a hole. They never ripple.** Audacity closes the gap; this deliberately
  does not. Two songs are aligned by ear, and sliding one along pulls it out of time with every
  other deck — silently, usually unnoticed until thirty seconds later in the mix.
- **Sections on one track never overlap**, and paste overwrites what is under it. A track is one
  read position, one stretcher and one EQ; two sections sounding at once is not representable.
  Two things at once is a second track.
- **Dropping a section or a group onto another section trims away what it covered, for good** —
  drag it off again later and the covered part stays gone (Ctrl+Z is the way back). Daniel wants
  exactly this. Verbatim, 2026-10-12: *"i actually LOVE the existing functionality when I DO overlap a clip onto another, the clip i’m actively dragging overrides the portion of the clip i dragged it over onto, as proven when I then move the clips apart again and see that the underlying clip is now trimmed. So please, DONT REMOVE THAT…. i was thinking it was an unintentional function you created, so if you ever flag it as a bug, do not “fix” it or recommend fixing it."* Never flag it as a bug, and never recommend changing it.
- **Splitting a section and moving neither half changes not one sample.** That is the *seam rule*
  (`VoiceClip.JoinsSeamlesslyTo`) and it is load-bearing — it is why hand-built loops do not tick.
  It rests on an **exact** equality between one section's `SourceEnd` and the next one's
  `SourceStart`. Do not "improve" it with a tolerance: that would start suppressing declicks
  between sections the user meant to be separate. ("Not one sample" is exact at 100% speed. Through
  the stretcher a split can move samples by up to 7e-6, −103 dB — measured 2026-10-12 at 90–92% —
  because the stretcher is then fed in different blocks; the split is still a seam, with no declick.)
- **Neither side of a butt join gets a declick.** Two abutting 5 ms ramps do not sum to unity —
  they punch a hole at every loop repeat. The join is covered by a crossfade from the outgoing
  section's handle, rendered through the real stretcher and EQ at the join (`CaptureHandle`), on
  every path. Before 2026-09-24 it was not covered on any path and every loop repeat clicked
  (`JoinTests`). Do not "simplify" it back to reading the handle raw from the file: that is what
  landed the old fade 65 frames late.
- **The crossfade X sits after the join, not astride it** (2026-10-28, his *"very narrow tall X"*).
  The engine fades from the join forward, over the incoming section's first frames; the X covers
  exactly that stretch, never less than 6 px (5 ms is a fraction of a pixel). A section's
  `CrossfadeSeconds` of 0 means the standard 5 ms, not none: a butt join is always crossfaded. The
  length is picked from the lane menu (5–200 ms), lives on the incoming section, is copied to
  repeats and cleared by a split or a cut, like a fade-in. Do not "centre" the X on the join, and do
  not offer a longer crossfade as the fix for a steady-note loop: measured 2026-10-21, a longer fade
  makes that bump longer, not smaller.
- **Positions are worked out in whole frames, then turned into seconds.** `TimelineEditor` and
  the drag gestures never add seconds together to place a section: the engine rounds each position
  separately, and a sum of seconds can land a frame off — which silently turns a seam or a butt join
  into a gap with two declicks. Keep new placement code in frames.
- **While a stretch is marked, Ctrl+C, Ctrl+X, Delete and Ctrl+D act on it, not on the selected
  sections** (MARK MODE, 2026-10-12), and the lane menu's items say so. **A pasted stretch covers the
  whole of itself, gaps included**: a gap in it clears what was under it. Both on purpose
  (`docs/DECISIONS.md`). The mark is kept in the track's own frames and is not part of the project.
  **A pasted group of selected sections does the opposite** (2026-10-29): each section clears only
  its own span, and what lies under the gaps between them stays. Do not make the two agree.
- **The mix runs at the rate of the output Benchmix started on, for the whole session.** Every
  position is counted in its frames. When Windows moves playback to an output at another rate,
  `DeviceRateConverter` converts on the way out, live only (2026-10-12) — a render is the mix as
  it is. Do not "fix" it by rebuilding the graph at the new rate.
- **A render ends where the last track you can hear ends**, not where the longest track does, and
  leaves out muted tracks and, while anything is soloed, the rest (`MixGraph.AudibleLengthFrames`;
  his request, 2026-10-27). A file shorter than the project is not a truncated render: look for a
  lit M or S. The status bar names what was left out, and with nothing audible the button refuses.
- **An MP3 render decodes about 33 ms later than the WAV of the same mix, and about 50 ms longer**
  (measured 2026-10-29: 1564 frames late and 2352 frames longer at 48 kHz). That is the codec's own
  delay, encoder and decoder together, plus padding to whole MP3 frames; every MP3 that carries no
  gapless information has it, and Windows' encoder writes none. Do not "fix" it by trimming the mix
  or the file: the number belongs to the player, which may or may not take it out.
- **The project file changes without a Save, two seconds after an edit** (autosave, 2026-10-29;
  his words: *"auto-save + version history"*). Only a project that has a
  file; an untitled one is not autosaved and still asks *Discard them?* at close. The title's `*`
  shows for those two seconds and goes; the status bar keeps the edit's message. A dated version
  (`Backups\` beside the project) is kept at every Save and every ten minutes of edits, forty kept;
  *Versions…* restores one after keeping the present as a version, one Ctrl+Z back. Scripted runs
  have autosave off (`MainViewModel.AutosaveEnabled` follows the interactive flag), so the probes
  save when they mean to; `--buttontest` turns it on for its own check. Do not "fix" the star's
  flicker, and do not make an autosave keep a version per edit.
- **Nothing snaps to the playhead while playing** (`MainViewModel.SnapFor` passes no playhead
  then): it is moving, and a section being dragged would jump as it swept past. Stopped, every
  drag snaps to it (his request, 2026-10-27), and a section edge wins a tie, so a butt join is
  never lost to it. Do not "fix" either.
- **The plain wheel scrolls the tracks wherever it is, and Alt+wheel moves the playhead**
  (2026-10-27, his words: *"for vertical scroll, just make the mouse wheel do that. If i want to
  usew mouse wheel to scrub, i can hold alt."*). Until then a plain turn over the ruler or the
  waveforms moved the playhead (2026-10-12); do not put that back. **The knob under the pointer
  still takes the wheel, clicked or not** — his words the same day: *"retain functionality of
  hovering over knobs and wheeling to make adjustments there"* (a first cut that needed a click
  was reverted within the hour). A right-drag pans up and down as well as sideways.
- **Only the open tab plays and renders, and Ctrl+Z works per tab** (2026-10-27, his choices:
  *"No, separate"*; *"i dont want to hit ctrl z and have something undone on an unseen tab"*).
  `Project.Tracks`, `ProjectBpm` and the loop forward to the open tab (`BenchmixProject.CurrentTab`);
  the other tabs' tracks have no voice (`ProjectSession.SwitchTabAsync` swaps them). An undo step
  puts back only the tabs it touched (`UndoStack`, `TabMerge.Restore`): the open tab whole, another
  tab only its name and its tracks' comings and goings. Adding a tab is not an undo step, on
  purpose. Do not "fix" any of this into one history or one mix.
- **A click of the scroll wheel pauses exactly where the playhead is; a double-click goes back to
  where play started; ■ goes to 0:00** (2026-10-28, his words: *"the playhead should stop where
  the playhead currently is at the time of my clicking mouse wheel"*). From 2026-10-11 to
  2026-10-28 the single click was the jump back and the double-click went to 0:00. Do not put the
  jump back on the single click; `--playtest` measures both gestures on the real device.
- **Play, pause, stop, the wheel click and a playhead move write nothing to the status bar**
  (2026-10-28, his words: *"remove these minor notifications. dont need a
  reminder that i hit play or pause."*). It is for edits, saves, renders, errors and the sound
  output — what he reads before Ctrl+Z. Do not add a "Playing from" or "Stopped" message back.
  The bar's right-hand end is the Ctrl+Z hint (2026-10-29), kept current from `UpdateTitle`; it is
  not a message.
- **A Shift+click on the ruler sets one end of the listening loop; the next sets the other**
  (2026-10-29, his *"click to set start and end points of loop"*). Until then a Shift+click without
  a drag cleared the loop; Clear loop does that now, and forgets a waiting start. Do not put the
  clear back on the click.
- **A muted section is left out of the voice's timeline** (`ProjectSession.BuildTimeline`), so the
  engine sees a gap and no X is drawn at its joins. That is the design (2026-10-29), not a lost
  section: the model still has it, faded on the lane, and project files are version 7 for it.
- **A project follows its songs** (2026-10-28, his request after a move left eight lanes reading
  `Missing:`). At open, a song that is not where the project says is looked for next to the
  project file, then under its folder — by name first, then by fingerprint (`SourceRelinker`,
  `BenchmixProject.SourceFingerprints`: size + a hash of the first 256 KB, taken when a song is
  added and at every save). What is found is used and written back by the next save; a song that
  still exists where the project says is never swapped for a same-named file. *Find missing file…*
  on the strip menu shows only while something is missing. **The file's version stayed 6 on
  purpose**: the fingerprints are a lookup table an older Benchmix can ignore. Do not bump it for
  that, and do not make Save rewrite a path that still exists.
- **The order on screen is the project's order.** Moving a track moves both (`ProjectSession.MoveTrack`
  and the rows' `Move`) and leaves the engine alone; Shift+click ranges index by it. Moving only the
  rows would make a range sweep up the wrong tracks.
- **A press on a track's button or knob keeps a multi-selection of tracks; a press on the strip's
  background, or in a lane, collapses it to that track** (2026-10-29). Match key and Match BPM's
  right-click read the selection, and the press reaches them through the strip's handler first
  (`SelectTrackByPress`): collapse it there and they could never see more than one track. Ctrl
  and Shift are read on the background only, so Ctrl on the S button still means "solo alongside".
  Do not "fix" the strip to select one track on every press.
- **A press on one of several selected sections keeps them all until release.** It collapses to
  that one only when let go without moving (`_pendingCollapse`), and the second press of a
  double-click puts the group back. Do not "fix" it to select on press: that is what breaks a group drag.
- **A group drag puts the whole picture back before every pointer move** (`GroupMove`, as `LoopDrag`),
  so edits are refused while anything is held under the mouse (`PointerHeld`), from the press on.
- **A track's origin (`TimelineOffsetSeconds`) moves.** Paste, drag or loop before it and it moves
  back, with every section moving forward by the same whole frames. It is not a wall; do not clamp
  to it.
- **Sync moves a track by its origin, in whole frames, never by its sections**
  (`TimelineEditor.SlideTrack`, 2026-10-29): sections and warp markers go with it, tempo ramps are
  carried by the same amount, and nothing changes its place on the track. It reads 12 s around the
  playhead (`BeatSync.BeatNear`), never the whole song; the move is the short way round, never more
  than half a beat, forward by a beat instead at 0:00; within 2 ms nothing moves and no undo step is
  pushed. Tempos more than 0.5% apart, tempo ramps and a stretch with no beat are refused, not
  lined up for a moment. Do not "improve" it to fit a grid to the whole song or to move the
  sections.
- **A tempo ramp sits on the timeline, not on the audio** (`TrackModel.Ramps`, timeline seconds).
  Sections stay in track frames; the track's `TempoMap` turns one into the other. Moving a section
  under a ramp changes the tempo it plays at. A ramp stores only its end speed: the start is
  whatever is in effect there, so the tempo cannot jump. Never multiply or divide by `Speed` to
  place anything on a track that may have ramps or warp markers — ask `TimelineEditor` or the map.
- **A warp marker sits on the audio, not on the timeline** (`TrackModel.Warps`, the track's own
  seconds) — the opposite of a tempo ramp. TEMPO, Match BPM and ramps carry it along with its beat;
  when the origin moves back it moves with the sections (`WarpMarkers.ShiftWithOrigin`), and
  `GroupMove`/`LoopDrag` put it back with the origin. Never store a marker's timeline position. It
  belongs to the track, not to a section: a section dragged past one is warped by where it now is.
- **After the last warp marker the track plays at its tempo again**, so everything after it moves
  with it. Between two markers the audio is spread evenly, never past half or twice its tempo.
- **The speed jumps at a warp marker, on purpose** (as in Ableton): that is what lands the beat
  exactly where it was dropped. Do not "smooth" it into a ramp. The voice ends a render run where
  the read position reaches a marker (`TempoMap.NextJumpAfter`), so the vocoder is fed each side's
  speed exactly.
- **A marker nobody has moved changes nothing — the tempo map is the very same object**
  (`TempoMap.LenientWarped` returns its input when every pin has Audio == Tempo), so an untouched
  track with unmoved markers stays bit-identical. That is why an unmoved marker's frames are its
  rounded frame in both coordinates (`EngineTempoOf`) and why `TempoMap.TempoOf` returns the audio
  position itself in a stretch nothing moved: seconds × rate is a hair off a whole number, and a
  hair counts as moved. No tolerance, as for the seam rule.
- **A tempo ramp ignores the warp.** Its start tempo, its dots' BPM and the box's pre-filled tempo
  use the tempo alone (`TimelineEditor.TempoSpeedAt`, `TempoRamps.BpmAt`), never `SpeedAt`, which
  includes the warp.
- **A box opened only to look changes nothing.** A knob's box or a ramp's tempo box shows the value
  rounded (−8.82%, 99.0 BPM); left as it was filled in and closed, the exact value behind it stays
  — no rounding written back, no undo step. Do not "simplify" that to committing what the box shows:
  after Match BPM it drifted the deck about 7 ms in three minutes (review, 2026-10-12).
- **A track with any tempo ramp or moved warp marker goes through the stretcher for its whole
  length**, even where it plays at 1.0, so it is not bit-identical anywhere. That is deliberate:
  dropping in and out of the stretcher would restart it, and its delay, mid-song.
- **Build output goes to `%LOCALAPPDATA%\Benchmix.build\`** on purpose, to keep bin/obj out of
  OneDrive *and* because the native `SignalsmithStretch.dll` fails to load from a deep path
  (MAX_PATH). Do not "fix" it.
- **A Release build has no real folder paths in it** (`PathMap` in `Directory.Build.props`,
  2026-11-02): in what the compiler writes into the assemblies, the build folder reads `/_build/`
  and the source folder `/_/`. Every release exe up to v2026.10.29.7 carried this machine's user
  folder in its PDB paths, and release exes go public. Debug builds keep the real paths, so the
  debugger still finds the source, and the tests are left out: `MenuWiringTests` finds the app's
  folder through `[CallerFilePath]`. Do not drop the mapping.
- **`--playtest` runs at master zero** and `--uiprobe` uses `RenderTargetBitmap` rather than a
  screen grab. Daniel uses this machine while work happens here. Nothing may steal focus, make
  noise, or photograph his desktop.
- **Every scripted run is invisible, on purpose** (`App.IsScriptedRun`: any verification switch or
  `--autoclose`). The window is placed beyond every monitor, has no taskbar button, carries
  `WS_EX_NOACTIVATE` so Windows can never activate it, shows no dialog of any kind, and — except
  `--playtest` — uses the offline engine, so the speakers are never opened. That is not a bug in
  the probes. Minimising was not enough: it left a taskbar button, and closing the dirty project
  popped "Discard them?" on his screen until a watchdog killed it. Never launch the exe with
  `-WindowStyle Minimized`. `scripts/verify-exe.ps1` and `scripts/ui-verify.ps1` watch each run
  from outside (`scripts/Intrusion.cs`) and fail it if it ever got in the way.

## Two words that mean two different things

- A **loop** is either hand-built (Ctrl+D repeating a section, or LOOP MODE dragging a section's
  end) or the transport loop region (shift-drag the ruler, listening only). Loop mode and Ctrl+D
  make the same thing: ordinary sections. The transport loop shares no code with either. "My loop
  does X" is ambiguous — ask.
- A **repeat is not a seam.** The seam rule needs the source to continue; a repeat jumps the source
  backwards. Every seam test is about splitting and *not* moving. They do not cover loops.
- A **ramp** in a test is a source signal whose value is its own position. A **tempo ramp** is a
  change of tempo over time. The tempo-ramp tests use both.
- A **stretch** (a marked stretch) is a span of one track marked in MARK MODE. The **stretcher** is
  the time-stretch engine (Signalsmith). `MarkedStretchTests` is about the first.
- **Warp** is warp markers. The **stretcher** still does the work under them; a warped track goes
  through it for its whole length, as a ramped one does. A **warp marker** is neither MARK MODE's
  **mark** nor the house style's **marker sample**.

## How to prove a change

No CI. Every gate is a human typing a command.

```bash
dotnet test        # bar: 0 failed AND 0 skipped
```

Anything touching native interop, timing or the stretcher must **also** be proved in the published
single-file exe — that is where a new P/Invoke fails to resolve and nowhere else:

```bash
dotnet publish src/Benchmix.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Then run all of the switches below, invisibly, with the outside watch, in one go:

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify-exe.ps1
```

They run against `%LOCALAPPDATA%\Benchmix.build\publish\Benchmix.App\release_win-x64\Benchmix.exe`:

| Switch | Proves |
| --- | --- |
| `--latencycheck` | a pitched track lands with an untouched one, and so do a track through a tempo ramp and a track through moved warp markers (no file needed) |
| `--gesturetest <song>` | pan, double-click-to-play on the ruler, lanes and the room under the tracks, every section command, empty tracks, paste target, drag between tracks, snapping, loop mode, Ctrl/Shift+click selection, the Delete key, tooltips, no bar snapping, the scroll-wheel click and double-click, Alt+wheel over the ruler and over the waveforms, the plain wheel scrolling the tracks (over a waveform, the ruler and the strip; a knob under the pointer still turns), renaming a track, Space, dragging a track up or down, the selection box, dragging a group, tempo ramps (RAMP MODE, placing, typing, moving, retyping, removing, undo), joining sections, typing a knob's value, marking a stretch (MARK MODE, marking, snapping, moving an end, click, Shift+click, copy, paste, cut, Delete, repeat, undo), warp markers (WARP MODE, placing, dragging, snapping, the spread limit, removing, undo), snapping to the playhead (a section's start and its end, the guide line, Ctrl, not while playing), scrolling the tracks (a right-drag up and down, PageUp/PageDown, Ctrl+Home/Ctrl+End, PageDown left to a text box), tabs (the + button, the chips, Move to tab, undo per tab, rename, remove and bring back, Ctrl+Tab, the row showing its tab from the start), the crossfade X (the standard X at every repeat join and none on a first section, the lane menu's lengths through the real item, the tick, the engine carrying it, undo, a length kept where nothing butts), the zoom keys on the top row and the number pad, the wheel click pausing in place and the double-click going back to where play started — 333 checks (2026-10-28) |
| `--buttontest <bed> <topper>` | Match BPM, Reset and Match key through the real buttons, Split into vocals + instrumental through the real right-click menu item, Rename track through the real strip menu item, Match BPM and Reset on a tempo-ramped track, then undo, Match BPM and Reset on a warped track, then undo, the S buttons (solo alone, Ctrl+click to add, undo), a render with the longer song muted and with the shorter one soloed, read back from the file, and the Render button refusing with nothing audible, Duplicate track through the real strip menu item (the copy below, its sections, knobs, selection and voice, undo), Reset-as-one-step and New, from inside (an outside UI Automation client made Windows activate the hidden window), then a project moved to a temp folder with its songs beside it — one found by name, one renamed and found by its fingerprint, one nowhere and still Missing, the status saying so, *Find missing file…* offered only on the missing track and driven through the view-model (the real item opens a file box), Save keeping the found places, Sync through the real button (its button fitting the strip, refusing before Match BPM, the topper slid under half a beat to whole frames with the bed still, a second press finding it in time, undo of the slide and of the match), an MP3 of the two songs through the view-model's render (written and named in the status, decoded back at the mix's rate, the WAV's length within 0.1 s, 320 kbps by its size, the Render box's fourth type with 32-bit float still first), autosave and versions through the view-model (a file in a temp folder; an edit in the file on its own two seconds later with the title's star gone and the status still the edit's; no version per autosave; the Save's version restored, the present kept as a version, one Ctrl+Z back; the Versions… button in the toolbar, asking for a Save first and refusing in a scripted run), writing tempo and key into a song file on an MP3 the run rendered itself (the item offered for it and not for the bed, nothing written without the yes, the values in the file read back by the app's own reader, the music the same bytes, the ? gone, the file added again with its tempo from the tags) — 127 checks (2026-10-29) |
| `--demosections --uiprobe <png> <song>` | the lane really draws sections and selection, and a tempo ramp placed and one being typed |
| `--demoediting --uiprobe <png> <song>` | the lane draws a moved, looped section, loop handles, the snap guide, an empty track, tempo ramps up and down, the box for typing a knob's value, a marked stretch, warp markers, and the crossfade X at a loop's joins |
| `--loadcheck <songs>` | tracks are actually ready to split into stems; given a `.benchmix` instead, that it opens from the command line (what a double-click in Explorer does) — `verify-exe.ps1` writes one |
| `--stemtest <song>` | ONNX/DirectML works in the packaged exe |
| `--playtest 6 <songs>` | WASAPI is pulling in realtime (silent), a scroll-wheel click pauses where the playhead is, and a double-click lands back on the frame play started from |

Before any switch runs, `verify-exe.ps1` unpacks the exe (`scripts/BundleScan.cs`) and fails it if
this machine's user folder is anywhere inside: the `user-folder` line.

These **exit 1 on failure** and also log a `verdict=` line to
`%LOCALAPPDATA%\Benchmix\benchmix.log`. Close any running Benchmix first — it locks the exe. (That
log, seen from here, is the Claude app's private copy and holds only scripted runs; his own runs
are in the real one, which this sandbox cannot read — see "His copy" below.)

### Test house style — copy it or the test will lie

- **Never a constant tone for anything positional.** The first regression test for the clip-offset
  bug used one and passed against the very bug it was written for. Use a ramp (value == position)
  or a single marker sample.
- **Never 440 Hz near the phase vocoder.** Its delay is 7200 frames — exactly 66 cycles of 440 Hz —
  so audio misplaced by that much comes back in phase and passes. It hid a real defect in the first
  join tests. `JoinTests` use 431 Hz.
- **Measure a processed track against an unprocessed one**, never a number in isolation. The
  150 ms stretcher delay survived for months because every test checked *what note* came out and
  none checked *when*.
- **Prove a feature through the control he clicks**, not the method behind it. *Split into vocals
  + instrumental* did nothing from the day it shipped until 2026-10-03, while every stem test
  passed — they all called the separator directly. Its right-click menu item was bound through
  `RelativeSource AncestorType=Window`, which a ContextMenu cannot see (it lives in its own popup),
  so the command was empty and the item drew as enabled. Menu items use `Click` handlers;
  `MenuWiringTests` fails on the binding.
- **Check a tempo ramp's positions against an integral written out in the test, and a warp's
  against the warp written out in the test** (`WarpMapTests.Spread`), never against `TempoMap`
  itself — a test that asks the map where audio should land agrees with the map's bugs.
- Where the invariant is exactness, assert `==`, not a tolerance.
- One regression test per fixed defect, named for the behaviour that was wrong, with an assertion
  message stating the musical consequence.

## Decided — do not quietly reopen

`docs/DECISIONS.md` has the full list and the reasoning. The ones most likely to be reopened:

- **Not an Audacity fork or wrapper.** Investigated and rejected with five reasons.
- **Offline editor, not a live DJ app and not a DAW.** No effects, plugins, automation or MIDI.
  The exceptions are a track's tempo ramps (2026-10-11, his request: straight lines in tempo) and
  its warp markers (2026-10-18, his request: *"yes please."*), nothing else automated.
- **Stems are an option beside the EQ, never a replacement** — *"But not Asa a replacement for the
  eq knobs."*
- **One signal path** for preview and render. A second implementation is not permitted.
- **An untouched track is bit-identical — with the limiter OFF.** The limiter is on by default and
  does attenuate a loud track. Overstating this is a logged mistake with its own test.
- **Every editing shortcut is Ctrl-modified**, because a bare key bound on the Window also fires
  from inside a TextBox (PROJECT BPM, a track's name, a ramp's tempo). The three bare keys that act,
  Delete (2026-10-03), Space (moved out of the KeyBindings 2026-10-11, so a name can have spaces)
  and Escape (2026-10-12, only to cancel a tempo ramp being placed or retyped), are deliberately
  **not** KeyBindings: `OnWindowPreviewKeyDown` runs them only when the keyboard focus is not in
  another text box or a menu, and `--gesturetest` fails if any of them ever becomes a KeyBinding.
- **Nothing touches his own music files without an explicit yes.** Tag write-back (2026-10-29,
  *Write tempo and key into the song file…* on the strip menu) is offered per file and done only on
  the OK in its box: it fills only what the file lacks, works on a copy checked byte for byte
  (`TagWriter`), and a scripted run can never give the yes — the probe passes `confirmed: true` on an
  MP3 it rendered itself.

Nothing is attributed to Daniel unless quoted verbatim with a date. Keep that rule.

## Unproven — say so rather than implying otherwise

- **Nobody has ever listened to Benchmix's output.** Every claim in this repo is a measurement.
  Measurements can be right about the wrong thing. Do not say something "sounds" any way.
- Joins between sections are measured on a sine (`JoinTests`), not on music and not by ear.
- A section dragged onto another track at the same moment dips in level at both ends (each track
  declicks its own cut). That is read off the envelope code, not measured.
- Stem separation quality is proven only by a 39 dB control test, not by ear.
- **Warp markers have never been heard.** Since 2026-10-27 `dotnet test` (0 skipped) and every
  probe have passed on his machine, on a staged build; before that, only on Linux. The jump in speed
  at a marker is a measurement.
- **Tabs have never been used by hand** (2026-10-27): every claim about them is `TabsTests` and
  `--gesturetest`. Project files have been version 5 since; v2026.10.27.7 is the first release that
  opens one, and an older one refuses it.
- **No chosen crossfade length has been heard** (2026-10-28): the law and the lengths are
  `CrossfadeTests`, on a ramp and on a sine; the X and the menu are `--gesturetest`. Project files
  have been version 6 since; v2026.10.28 is the first release that opens one.
- **Sync has never been heard** (2026-10-29): the detector is `BeatSyncTests` on click trains, the
  move is the same clicks through a session, and `--buttontest` presses the real button on his two
  songs — where the beats are read by Sync's own detector, so that run proves the button and the
  bookkeeping, not that the beats sound together. The flux lag is measured on clicks (0.955
  windows); on a dense mix it is read off the window shape as up to about 4 ms shorter, so two
  tracks of different character can land that far apart. The confidence floor (0.1) is a guess.
- **No rendered MP3 has been heard** (2026-10-29): `Mp3RenderTests` decodes one back and measures
  it against the WAV of the same render, on a 431 Hz tone (−49 dB residual, the delay above);
  `--buttontest` renders his two songs to MP3 and measures length, rate and bitrate. Nobody has
  played one, and the Render box's MP3 type has not been clicked by hand.
- **Autosave and versions have never been used by hand** (2026-10-29): the store is `VersionsTests`,
  the timer and the restore are `--buttontest` through the view-model. The Versions… window has
  never been opened — a scripted run refuses it — so its look is unseen.
- **No tag has been written into one of his files** (2026-10-29): `TagWritingTests` and
  `--buttontest` write into MP3s they rendered themselves; on his Serato-tagged songs the item does
  not even show, the tags being there. Whether Serato, djay or Explorer read a TBPM or TKEY Benchmix
  wrote is untested.
- **A project following its songs was proven on copies in `%TEMP%`** (`RelinkTests`, and the
  `--buttontest` moved-project check), never on the project he actually moved: he said he was not
  in a hurry to open it, and nothing touches his files without his yes.
- **His copy of Benchmix is one shortcut on his OneDrive Desktop, named for the release**
  (`Benchmix-v2026.10.27.2.lnk`), pointing at that release's exe in the project's `releases\`
  folder (gitignored), where every older release stays. After each verified release, without
  asking: copy the very exe verify-exe passed to `releases\Benchmix-vYYYY.MM.DD.exe`, and replace
  the Desktop shortcut with one named for it — *"Just keep one shortcut to benchmix on my desktop.
  have it point to the latest release, and have it named as usual with release number etc. old
  releases should just stay in a project folder once you make a new release."* (2026-10-27). Never
  delete an older exe. **Double-clicking a `.benchmix` has never followed the release** (found
  2026-10-29): this sandbox runs inside the Claude app's MSIX package, so every registry write made
  from here lands in the package's private hive (`…\Packages\Claude_pzs8sxrjxfjjc\SystemAppData\
  Helium\`), and Explorer never saw one of them. It kept launching whatever his real registry
  names — on the evidence, the 12 Oct build under `…\LocalCache\Local\Benchmix.build\publish\`,
  schema 3, which refuses today's project files: that is the bug he reported on 2026-10-28
  (*"opens the app but doesnt load the project"*). Do not write `HKCU\Software\Classes\…` from
  here again, and do not trust a read of it from here either: the view is merged with the private
  hive. Instead, at each release also copy the exe to `releases\Benchmix-latest.exe` (a fixed name,
  overwritten each time); the association was his to set once, *Open with → Always* on that file —
  done, his words 2026-10-29: *"i already did the open with always thingy."* (his word for it: a read
  of the association from here would see the private hive, so it is not checked from here). A
  double-click now runs whatever `Benchmix-latest.exe` holds, which every release overwrites. Keep
  publishing to the normal `%LOCALAPPDATA%\Benchmix.build\publish\…` path: `verify-exe.ps1` runs
  that exe and the release copies are taken from it. The same virtualisation
  hides his runs: the `benchmix.log` seen
  from here holds only the scripted runs, and what his copy logs — the real
  `%LOCALAPPDATA%\Benchmix\benchmix.log` — cannot be read from here (an outside process is refused
  by the sandbox). Read the shortcut's target and its version (`0.1.0+<commit>`) before saying what
  his copy has.

## Working with Daniel

- Lead with the answer; he skims. No internal vocabulary.
- **When a turn ends blocked on him, call `PushNotification` and say plainly that work has
  stopped.** A question buried in a long report reads as "still working". He has asked for this
  three times.
- Prefer deciding on a stated default over stopping to ask.
- A status question wants a status answer, not an audit.

*Revised 2026-11-02.*
