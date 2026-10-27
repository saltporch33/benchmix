# Benchmix on iOS — the plan

*Written 2026-10-27, from an audit of the code as it stands (six readers over the engine, the
editing core, the window, persistence, the render path and the tests) and two independently drafted
plans, one ordered by risk and one by what Daniel can hold soonest. This document is the merge.
Nothing below is built. Every phase ends at a gate Daniel runs himself.*

## What the audit found

The port is smaller than the line count suggests, and the hard parts are not where a first read puts
them.

- **The engine is already portable except for three bindings.** `MixGraph`, `TrackVoice`,
  `ClipTimeline`, `TempoMap`, the resampler, `DeviceRateConverter`, the EQ and the analysis are plain
  managed code. Exactly three things tie `Benchmix.Engine` to Windows: the WASAPI output
  (`WasapiDevices`, and a dead `PlaybackEngine.ListDevices`), `MediaFoundationReader` inside
  `AudioClip.Load`, and one call `AppendExecutionProvider_DML(0)` in `MdxSeparator`. A seam for the
  output already exists (`IOutputDevices`, faked in `OutputFollowingTests`), and `PlaybackEngine.OpenOffline`
  runs the whole graph with no device. Tags are read by a hand-written ID3v2 reader, portable as is.
- **The editing core has no UI types in it.** Every gesture class in `Benchmix.Core` takes seconds, a
  `TrackModel` and a `SnapSettings` and returns an edit. About 80% of what a touch editor needs is
  already there. What is not: the glue in `MainViewModel.cs` (selection by id, the in-app clipboard,
  the mark and ramp state machines, drag begin/move/end, undo policy) and the hit-testing arithmetic
  in `MainWindow.xaml.cs`, of whose 5,135 lines only about 1,620 are live UI; the rest is the
  `--gesturetest` and `--buttontest` harness.
- **The stretcher is the one native dependency, and its source is open.** `SignalsmithStretch-CS`
  1.1.2 is a 26-function C shim over the header-only Signalsmith library, MIT, bound with
  `LibraryImport(DllName="SignalsmithStretch")`. It ships Windows, Linux and macOS binaries, no iOS.
  iOS requires native code statically linked (`DllImport("__Internal")`), so the shim is compiled on
  the Mac side and the four-file C# wrapper is vendored with a `#if IOS` name.
- **A project file ignores its own sample rate.** The engine runs at the rate of the output present
  when it opened; the file's `SampleRate` is read only to snap tempo-ramp ends. A 48 kHz project
  opened on a 44.1 kHz device re-decodes at 44.1 kHz and every section lands on a different frame
  grid, which can turn an exact butt join into a one-frame gap with two declicks. The converter that
  fixes this (`DeviceRateConverter`) already exists and is tested for 48↔44.1; it only needs to be
  used for the whole engine instead of just a device change.
- **Paths are absolute, and stems live outside the project.** `SourcePath` is written verbatim;
  nothing resolves relative to the project folder. Stems from *Split into vocals + instrumental* go
  to `%LOCALAPPDATA%\Benchmix\stems` and are referenced absolutely, so a synced project would open
  on the phone with every stem track *Missing*.
- **Save is atomic with twenty dated backups, and there is no autosave.** Every UI save writes a
  dated copy to `<project dir>\Backups\` (20 kept). Nothing lists or restores them; they are only the
  fallback when the main file is unreadable. Dirty is a bool; the close prompt is *Discard them?*
  with no Save. Nothing notices when the file on disk changed after it was loaded.
- **MP3 on the desktop is nearly free.** `MediaFoundationEncoder` ships in `NAudio.Wasapi` 3.1.0,
  already referenced. Apple ships no MP3 encoder; the phone gets WAV and M4A natively.
- **21 of 25 test files would run on Linux or macOS** once Engine and Core target plain `net10.0`
  and a stretcher native exists for the platform (the package already ships linux-x64). Four need
  Daniel's MP3s, the ONNX model or DirectML. No test opens a real audio device.
- **Memory.** A song is held decoded as float32: about 92 MB per four minutes of stereo at 48 kHz
  (arithmetic from the code, not measured). The desktop warns at 2 GB; a phone will not allow that.
- **Nobody has ever listened to the output.** The phone will be the first place anyone does.

## Decisions this plan rests on

Each is Daniel's to confirm. The recommendation is what the plan assumes.

| Decision | Recommendation | Why |
| --- | --- | --- |
| How iOS builds are made | GitHub Actions macOS runner building, signing and uploading to TestFlight, recorded in `DECISIONS.md` as the repo's first CI. A Mac of his own (a used Apple-silicon mini, or the 2016 MacBook on OpenCore Legacy Patcher) as an accelerator, not a requirement. | The cloud agent cannot touch his Mac. With CI the agent pushes, the runner builds, Daniel taps Update in TestFlight: 20–40 minutes a round. A local Mac makes a device build a two-minute loop, which matters most in the UI phases. |
| UI stack | Plain .NET for iOS (UIKit bindings) with the timeline as one custom view drawn with CoreGraphics. Not .NET MAUI, not Swift. | The app is one custom-drawn surface plus a few knobs, menus and sheets; MAUI's layout layer buys nothing and its gesture layer hides the recogniser ordering that the desktop's press-then-decide rules need (a press on one of several selected sections keeps them all until release; a toggle resolves only on release without movement). CoreGraphics keeps the statically linked native surface to the stretcher alone. SkiaSharp is the fallback if drawing proves slow. |
| Sync folder | OneDrive | His music and the repo already live there; the OneDrive iOS app exposes the folder through the Files app exactly as iCloud Drive does. |
| Sample rate | A project runs at the rate written in its file on every device; new projects are 48000 everywhere; the output converts on the way out. | The only way both devices place every section on the same frame grid. Reverses the deliberate match-the-device-rate choice of 2026-10-12, so `--playtest` must still pass. |
| Where the songs live | A project is a folder: `<Name>\<Name>.benchmix` beside `Audio\` (collected originals and stems) and `Backups\`. Save As into a folder that does not already hold the audio asks once whether to copy the songs beside the project; a menu item does it any time. | The phone can only reach files inside the folder it was handed. Copying his files is reading them, which the rule allows; the prompt's wording is his. |
| Autosave | Writes the real project file (debounced), so the other device sees edits without Ctrl+S, with changed-on-disk detection so it never overwrites another device's save. Explicit Save still makes a dated version. | A side file would mean the phone sees stale projects until he remembers to save, which is the failure sync exists to remove. |
| Two devices edited the same project | Copy, never merge. The device that saves second is told, keeps its edits as a dated copy, and he chooses. | A merge of two timelines is not definable while honouring never-ripple and overlap-trims-for-good. A copy loses nothing. |
| Phone export format | WAV and M4A/AAC on the phone; MP3 on the desktop. MP3 on the phone only if the software he plays out from refuses M4A. | Apple ships no MP3 encoder; bundling LAME is LGPL and a second native library on the pipeline that is already the riskiest part. |
| "Same bytes on both devices" | Exact only for WAV-sourced sections at 100% speed with the limiter off. Through the stretcher, a ramp, or an MP3 source: a measured number written into `VERIFICATION.md`, not a promise. | Windows and Apple decode MP3 with different decoders, and the stretcher's arithmetic on arm64 is not guaranteed identical to x64. |
| Songs per project on the phone | From a measurement on his actual iPhone in Phase 4. Placeholder: warn above five four-minute songs. | iOS kills apps that use too much memory; the figure is per device. |
| Stems on the phone | No. Split on the PC; the stem WAVs sync in the project's `Audio\stems` folder. | Hold in his list; half a gigabyte of buffers, no measured time on any iPhone, and the model's licence is unresolved. |

## What Daniel does before any phone work

1. Enrol in the Apple Developer Program ($99/year). Approval takes days; start it now.
2. Decide the build route above. If the 2016 MacBook is to be tried, check the model and RAM first.
3. Pick the sync folder (OneDrive recommended) and sign into its app on the phone.
4. Before Phase 1 starts, render two or three real projects to 24-bit WAV and keep the files; every
   desktop gate compares against them.
5. Report the `sampleRate` value in his existing `.benchmix` files (one command, supplied).
6. Say what he plays out from (Serato? the phone's Music app?). It settles the MP3 question.
7. Say which iPhone model he has.

## The phases

Sizes are relative: S, M, L, XL. Phases 1–3 are desktop only and change nothing he sees unless
stated; they are proven with the three Windows commands from `CLAUDE.md` (`dotnet test`, publish,
`scripts/verify-exe.ps1`), referred to below as *the Windows gates*. Phases 3 and 4 are independent
and can overlap once the Apple enrolment is through.

### Phase 1 — The engine goes portable (desktop) — L

**Goal.** `Benchmix.Engine` and `Benchmix.Core` compile for plain `net10.0`, with the three Windows
bindings behind seams in a Windows-only assembly, the stretcher wrapper vendored so iOS can link it
statically, and the tests split so the engine tests run in the cloud Linux session. Nothing Daniel
sees changes.

**Scope.**
- Output contract owned by the engine: `IOutputStream { Start(ISampleSource); Stop(); Stopped }` and
  `ISampleSource { SampleRate; Channels; Read(Span<float>) }`; `IOutputDevices.Open` returns
  `IOutputStream` instead of NAudio's `IWavePlayer`; `TransportSource`, `OwnedFeed` and
  `DeviceRateConverter` implement `ISampleSource`. The `_feedGate` handshake and the "Changed is raised
  on the creating context" rule stay exactly as they are. `ListDevices` (no callers) goes.
- Decoder contract: `IAudioDecoder { IDecodedAudio Open(path) }` and
  `IDecodedAudio { SampleRate; Channels; DurationSeconds?; Read(Span<float>) }`; `AudioClip.Load` keeps
  its placeholder probe, stereo folding, resampling and accumulation; a `WavDecoder` stays in the
  engine (tests use WAVs), `MediaFoundationDecoder` moves out. `Analyzer.Analyze` decodes through the
  same seam.
- Stems accelerator: `MdxSeparator` takes an `IInferenceAccelerator { Name; Configure(SessionOptions) }`
  instead of `bool useGpu`; the DirectML implementation, with its existing CPU fallback, moves out.
- New project `src/Benchmix.Engine.Windows` (`net10.0-windows10.0.19041.0`) holding `WasapiDevices`,
  `MediaFoundationDecoder`, the DirectML accelerator and the `NAudio.Wasapi` and
  `Microsoft.ML.OnnxRuntime.DirectML` references. `Benchmix.Engine` and `Benchmix.Core` retarget to
  `net10.0` with `NAudio.Core` only. `ProjectSession` gains a `Create(IOutputDevices, IAudioDecoder, …)`
  the App calls with the Windows implementations.
- The four `SignalsmithStretch-CS` C# files (MIT) vendored into `src/Benchmix.Engine/Stretch/`,
  `DllName = "SignalsmithStretch"` on Windows and `"__Internal"` under `#if IOS`; the win-x64 DLL kept
  as a content item so the single-file publish still carries it. The `long seed` width and the Span
  `OutputSeek` quirk fixed while there.
- Tests split: `tests/Benchmix.Tests` (`net10.0`, the 21 synthetic-audio files plus `MenuWiringTests`)
  and `tests/Benchmix.Tests.Windows` (`EndToEndTests`, `StemTests`, `StemSplitTests`). The cloud run
  uses the package's prebuilt linux-x64 native; if it will not load, the 13 stretcher-dependent files
  are filtered by trait and the run is reported as arithmetic-only, never as the full bar.
- `DECISIONS.md`: the split, the vendored binding, the Linux run as a gate the agent runs before
  every hand-off (the Windows gates stay authoritative), and an update to the 2026-09-16 "nothing
  here can compile C++" entry: still true of his PC; the iOS static library is compiled on the Mac
  side.

**Out of scope.** The sample-rate rule, paths, autosave, MP3, any iOS code, any change to editing.

**Gate.** The Windows gates pass, including `--latencycheck` and `--stemtest` (they prove the moved
natives still load inside the single packaged exe). A render of a kept project has the same
`Get-FileHash` as its pre-phase reference. In the cloud, the portable test project passes on Linux
with 0 skipped.

**Daniel.** The reference renders beforehand; one `dir` under `~/.nuget/packages` to confirm which
NAudio package holds `MediaFoundationReader`; run the gates.

**Risks.** Native probing in the single-file publish after the split (`verify-exe.ps1` is the proof;
no shortcut). Two ONNX Runtime versions (DirectML 1.24.4 now, base 1.31.x on iOS later) share one
managed assembly only if aligned; this phase may need to move the Windows build up and re-run
`--stemtest`.

### Phase 2 — One project file for both devices (desktop) — M

**Goal.** A project runs at the rate written in its file on every machine; its file carries paths
relative to its own folder so it opens from a OneDrive folder on either device; stems live in the
project; and a headless "render this project to WAV" command exists so Phase 4's comparison needs
no UI.

**Scope.**
- `PlaybackEngine.Open(IOutputDevices, int? mixRate)` builds the graph at the file's rate and lets the
  existing `Attach` insert `DeviceRateConverter` whenever the output differs. `MainViewModel` opens
  the file before the engine; a rate change while running closes and reopens the session the way
  New does. New projects are 48000.
- Schema 4: on save, a clone's `SourcePath`s under the project folder are written as forward-slash
  relative paths (paths outside stay absolute); the live model keeps absolute paths so the `_clips`
  cache and the seam comparisons are untouched. Loading resolves against the file's folder; v3 files
  with absolute paths load unchanged.
- `ProjectCollector.CollectBeside(project, path)`: copies every distinct source and stem into
  `Audio\` (de-duplicating by length and hash on a name collision), repoints the model, re-keys the
  session, saves. Offered once on Save As into a folder without the audio; always on the File menu.
- `StemDirectory` becomes `<projectDir>\Audio\stems` once the project has a path.
- `tools/Benchmix.Cli` gains a Core reference and two verbs: `render <project> <out.wav> [--f32]
  [--no-limiter]` and `compare <a.wav> <b.wav>` (prints identical, or the largest difference).
- Tests: a 44100 project opened on a 48000 engine renders byte-equal to a 44100 session; v3 loads
  unchanged; v4 resolves against the folder, not the working directory; save, move folder, load
  still finds the audio; collecting leaves the originals byte-identical.
- `DECISIONS.md`: the rate rule, schema 4 and the folder layout, what does not sync (undo, the mark,
  selection, playhead, zoom), copy-never-merge.

**Gate.** The Windows gates pass (`--playtest` matters: the converter is now always live). Open a
project, Save As into a new folder in OneDrive, say yes to copying the songs, close Benchmix, move
or rename the folder, double-click the project file: every track loads, nothing says Missing. With
the PC's output set to 44.1 kHz, open a 48000 project: the status line says 48000, playback is not
sped up, and its render hash equals the Phase 1 reference.

**Daniel.** The `sampleRate` values; the sync folder; the two decisions (file's rate, ask-once
collect); the 44.1 kHz step.

**Risks.** Everything that cached `Session.Engine` when the session is rebuilt (about eight reads of
`SampleRate` in `MainWindow.xaml.cs`, the meter timer). Path rewriting must be on a clone; on the live
model it would make one file look like two and silently break seams and joins.

### Phase 3 — Autosave, versions, MP3, and no silent overwrites (desktop) — M

**Goal.** Daniel's two desktop requests land, and the PC is safe against a second device editing the
same file before the phone can write one.

Daniel, 2026-10-27, verbatim: *"If feasible and not costly, I'd also like an auto-save + version
history functionality on the desktop app."* and *"please also add mp3 mixdown option to desktop
version."*

**Scope.**
- Autosave: a `DispatcherTimer` debounce, about 5 s after the last change and at most once a
  minute, never while anything is held under the mouse or a ramp or rename box is open; writes the
  main file atomically through a new `ProjectStore.Autosave` that does not consume the 20 dated
  slots (its own rolling slot in `Backups\`).
- Versions: *File → Versions…* listing `Backups\` newest first, with *Open a copy* and *Restore*
  (restore first writes a dated copy of what it replaces). `WriteDatedBackup` gets millisecond
  timestamps so two saves in a second do not collide. `Click` handlers; `MenuWiringTests` covers it.
- Changed-on-disk: `ProjectStore.StampOf` / `ChangedSince` (last write time, length, hash of the bytes
  written) recorded on open, save and autosave; before every write re-check; an explicit Save over a
  changed file offers *Keep mine / Load theirs / Save mine as a copy*; autosave never overwrites and
  shows a banner instead. No `FileSystemWatcher` and no dialog that could fire during a scripted run.
- MP3: `RenderFormat.Mp3` and a `Bitrate` on `RenderOptions` (320 default, 192 offered); `Renderer`
  renders the dithered 16-bit temp WAV as today, then `MediaFoundationApi.Startup()` once and
  `MediaFoundationEncoder.EncodeToMp3`, with a second progress phase. A `--rendertest` switch, or an
  extension of `verify-exe.ps1`, proves it in the published exe. A Cancel button beside the render
  progress bar, the token passed through (the engine side already handles it).

**Gate.** The Windows gates pass, including the MP3 check. Edit a project, wait ten seconds, kill
Benchmix from Task Manager, reopen: the edit is there. Restore a version from *File → Versions*; the
project is back to that state and the newer one is in the list. With a project open, overwrite its
file from another copy: a banner appears instead of a silent save. Export as MP3 and play it in the
software he plays out from.

**Daniel.** Confirm autosave writes the real file and copy-never-merge; run the gate.

**Risks.** Autosave into OneDrive uploads every few seconds and can briefly lock the file; Save's
`File.Replace` needs the same retry `ReadWithRetry` has. `MediaFoundationEncoder` details (bitrate
parameter, 16-bit input) are from memory; confirm against the SDK before writing the test.

### Phase 4 — Hear it on the phone (first iOS build, listening only) — L

**Goal.** A TestFlight build that opens a project from the Files app, shows it, plays it and checks
itself on a real iPhone. It proves the four unknowns below the UI: the stretcher statically linked and
numerically matching Windows, decoding, realtime output that follows headphone and Bluetooth changes,
and identical bytes from one project file. The first time anyone hears Benchmix's output.

**Scope.**
- Build route per the decision: a GitHub Actions macOS workflow (restore, build Release, sign with an
  App Store Connect API key, upload to TestFlight) or a documented local recipe; either way the
  CMake job that builds the Signalsmith shim for `iphoneos-arm64` and `iphonesimulator-arm64` as an
  xcframework under `native/ios`, referenced with `<NativeReference>`.
- `src/Benchmix.iOS` (plain .NET for iOS, UIKit): `AvAudioSessionDevices : IOutputDevices` (route
  id and name plus `AVAudioSession.sampleRate`; `Changed` from route-change and media-services-reset
  notifications on the main queue), an `AVAudioSourceNode` stream whose render block fills
  interleaved float32 from `ISampleSource.Read` with no allocation per block, an AVFoundation
  `IAudioDecoder`. The engine opens at the file's rate with `preferredSampleRate` set to it.
- Open a project: `UIDocumentPickerViewController` on a `.benchmix` (UTType in `Info.plist`),
  security-scoped bookmark kept across launches, siblings not yet downloaded triggered and shown as
  downloading, never as Missing; a recent-projects list.
- A read-only timeline: track rows with name and colour, waveform columns from `PeakData`
  (`WaveformColumns.Compute` moved into Core so both apps draw the same columns; a coarser 8192-frame
  level added for low zoom), a ruler, a playhead. Play/pause/stop, drag on the ruler to scrub, pinch
  to zoom, two-finger pan, master gain, mute/solo (transient, never written), a brief pop-up when the
  route changes. Background audio enabled so the screen can lock.
- Self-check behind a launch argument: `--latencycheck` ported in substance (a pitched track against
  an untouched one; a ramped track against the written-out integral), verdict written to a file in
  the app container and shown on screen.
- Cross-device comparison, numbers into `VERIFICATION.md`: (i) a WAV-sourced fixture at 100% speed,
  limiter off: every sample equals the PC's `render` verb output; (ii) a fixture at 92%, +3 semitones,
  with a ramp: the measured peak difference; (iii) one of his MP3-sourced projects: the measured
  difference, with the decoder named as the cause.
- Resident memory logged after opening a project; a provisional ceiling and a plain refusal message
  above it rather than being killed.
- The listening checklist, written into `VERIFICATION.md` with space for his answers verbatim, dated.

**Gate.** From TestFlight: open the Phase 2 project from OneDrive in the Files app; the same tracks
and waveforms as on the PC. Play with headphones: it plays and the playhead keeps time. Pull the
headphones out mid-song: it carries on through the speaker from the same moment. Scrub, pinch, mute.
The self-check says PASS. The WAV fixture's render, brought back to the PC, has the same hash as the
PC's; the stretched fixture's measured difference is written down. The listening checklist is done
with headphones and the speaker and answered in writing.

**Daniel.** Developer enrolment approved; TestFlight installed; the build secrets added if Actions;
the fixture projects in the sync folder; the iPhone model; the listening.

**Risks.** The first static native library through a pipeline nobody here can run locally: expect
several rounds before the stretcher links. The render block runs on a realtime thread; a per-block
allocation sounds like crackle and is easy to blame on the engine. Bookmarks expire after a
reinstall; the start screen must recover with a one-tap re-pick.

### Phase 5 — Arrange on the phone, part 1 — XL

**Goal.** The smallest useful editor: start a project on the phone from two songs, line them up,
mix it down through Files, and open the same project on the PC with the same arrangement and sound.

**Scope.**
- Core groundwork, desktop behaviour unchanged and proven by `--gesturetest` and `--buttontest`:
  `HitMetrics` (edge grab, ramp grab, drag-start slop, vertical slop, snap radius) with Desktop
  (7/7/4/8/10 px) and Touch (22/22/8/44/22 pt) presets, `SnapSettings` taking its radius from it;
  `LaneHitTest.ClipAt` / `KindAt` returning a `TimelineHit`; `Viewport` arithmetic; `KnobSpec` (unit,
  min/max/default per knob, `EditText` / `ParseTyped` / `Describe` moved verbatim from `Knob.cs`, the
  formatters and clamp from `TrackViewModel`); a `Hints` provider so shared messages stop saying
  Ctrl+Z and right-click.
- `EditingSession` in `src/Benchmix.Core/Editing`, slice one: selection by id (`SelectClip`,
  `MakePrimary`, the pending-collapse rule), drag glue (`BeginMove` / `DragMoveTo` / `BeginTrim` /
  `DragTrimTo` / `EndDrag`, `PointerHeld`), `SplitAtPlayhead`, `DeleteSelection`, undo policy. xunit
  tests for each, which today exist only as checks inside the exe.
- Phone: New project (pick the sync folder once, then a name; the folder is created there). Add
  songs from the document picker, copied into `Audio\` (the sandbox makes collecting the only path),
  decoded, analysed (tags first, then tempo detection; key shown as an estimate); add an empty track.
- Phone edits on `EditingSession`: tap = select; drag = move with snapping (a *Snap* toolbar toggle
  replaces Ctrl); a 44 pt vertical pull hops to another track; a drag within 22 pt of an edge trims;
  long-press on a section = menu (Split at playhead, Delete, Trim start/end to playhead); a drop onto
  another section trims what it covers, for good, as on the desktop.
- Phone knobs from `KnobSpec`: TEMPO, PITCH, LINK, VOL per track (vertical drag with the 200 pt sweep,
  drag sideways for fine, double-tap = default, tap the number = numeric sheet with nothing rounded
  back when left as shown); M/S; master; Match BPM. Undo/redo buttons. Brief pop-up status.
- Autosave on the phone (same debounce, never while a finger is held), writing the main file plus the
  rolling slot; an explicit Save button too.
- Mixdown to 24-bit WAV through the shared `Renderer` into Documents, progress and Cancel, then the
  export picker so he saves it through Files.
- A scripted-touch seam from day one (the phone's `_scriptedWindowPoint`), so a self-test can drive
  these gestures later without an external driver.

**Out of scope.** Cut/copy/paste, repeat, join, multi-select, loop mode, mark mode, EQ, colour,
reorder, listening loop, versions (part 2). Group move (Daniel: out of the MVP). Stems, ramps, Match
key, Reset, pan, meters (Hold). MP3 on the phone. Landscape.

**Gate.** On the phone: new project in the OneDrive folder, two songs from Files, Match BPM on the
second, split the first where the drop lands, drag that section to line up with the second (it
snaps), trim an end, set the two volumes, play. Mix down to WAV through Files and play the file. On
the PC: open the same project from OneDrive: same arrangement, tempo and volumes, plays the same;
edit on the PC, save, reopen on the phone: the edit is there. The `compare` verb against the phone's
mixdown of a WAV-sourced project prints identical. The Windows gates pass after the Core extraction,
with `--gesturetest` still at 253 checks and `--buttontest` at 41.

**Daniel.** Two songs as files the phone can reach (Apple Music tracks will not work); the round trip
both ways; say whether drag = move, edge = trim, long-press = menu and the Snap toggle feel right
one-handed before part 2 builds on them; the Windows gates.

**Risks.** The extraction is the riskiest desktop change in the plan: the only coverage of that glue
is the exe harness, so it lands in chunks each ending with `verify-exe.ps1`, at most four rounds.
Recogniser ordering on touch: if tap, long-press and pan are ordered wrongly, selections collapse on
the first touch; the lane recogniser is prototyped before anything is built on it.

### Phase 6 — Arrange on the phone, part 2 — L

**Goal.** Every remaining Foundational, Priority and add-with-modification feature on the phone, all
round-tripping to the PC.

**Scope.**
- `EditingSession` slice two: the in-app clipboard (`Piece`, `copiedFrom`), copy/cut/paste,
  duplicate, join, toggle and box selection, the mark state machine and its operations, the Delete
  rule while a mark exists. `MainViewModel` adopts it; the exe harness stays the authority.
- Phone: cut/copy/paste from the long-press menu (paste at the playhead on the selected track; a
  pasted stretch covers the whole of itself, gaps included); Repeat and Join as menu items.
- Multi-select: long-press then tap toggles membership, with a selection-mode indicator; a drag on an
  empty lane draws a box; dragging a member moves only that section (group move is out).
- LOOP MODE as a toolbar toggle: the outer edges become 44 pt handles; a free drag that snaps to whole
  repeats unless Snap is off.
- MARK MODE as a toolbar toggle: drag across a lane marks a stretch, ends as 44 pt handles, tap puts
  the playhead; menu items renamed exactly as `PrepareLaneMenu` does so Copy, Cut, Delete and Repeat
  act on the stretch.
- LOW/MID/HIGH knobs (0 shows KILL); a colour palette writing `ColorHex`; reorder by long-press on a
  grip and drag; rename; a listening loop by long-press on the ruler with two handles and a Clear.
- Versions on the phone over `ProjectStore.ListBackups` / `RestoreBackup` (added to Core, used by
  both apps); dated backups on every explicit Save.
- M4A/AAC 256 kbps export through `AVAssetWriter` beside WAV.

**Gate.** On the phone, with a Phase 5 project: copy a section and paste it on another track;
long-press then tap to select three and delete them; Repeat a section eight times and listen at each
join for a tick (answer written down); loop mode: drag an end and watch it grow in whole repeats; mark
a stretch across a gap, paste it elsewhere, and the gap clears what was under it; kill the lows on one
track; set a listening loop; change a colour; drag a track to the top; restore an earlier version;
export M4A through Files. Open the same project on the PC: every change is there. The Windows gates
pass after slice two.

**Daniel.** The checklist and the loop listening item; whether long-press-then-tap and the Snap toggle
feel right before the help screen is written around them; the Windows gates.

**Risks.** Mark semantics are easy to get subtly different on a second UI; the shared `EditingSession`
and its tests are the defence. Box selection fights one-finger pan; selection mode must own the lane
while it is on.

### Phase 7 — Landscape, help, and proving it on the device — M

**Goal.** Landscape as an option, the help screen that replaces tooltips, the phone's self-test
family, and the memory and heat questions answered on the real device.

Daniel, 2026-10-27, verbatim: *"I would still want landscape mode as an option."*

**Scope.**
- Landscape: the timeline widens, the strip collapses to a column, knobs move into a per-track sheet;
  portrait stays the default; a global orientation setting that respects the phone's rotation lock.
- A help screen listing every gesture and mode, worded from the same `Hints` as the pop-ups.
- Self-tests behind launch arguments with a verdict file: the `--playtest` equivalent (output pulls at
  0.95–1.05× wall time at master zero; stop lands on the exact frame play started from), the gesture
  test through the scripted-touch seam against fixture projects, both orientations.
- Memory: a four-song project on his phone, measured; on-demand decode only if the measurement says
  it is needed. A 20-minute playback soak and a long render with temperature and dropouts recorded.
- The listening checklist and the comparison tiers repeated on this build, numbers updated.

**Gate.** Rotate the phone: the project stays where it was and every gesture still works sideways.
The help screen lists every gesture he uses. A four-song project opens without the app being killed.
Twenty minutes of playback without a dropout; the phone not uncomfortably warm. The self-tests say
PASS on the device in both orientations.

**After this.** Hold items by request, each a small slice on Core code that already exists: tempo
ramps (the RAMP MODE mapping is already specified), key detection and Match key, Reset, pan, meters.
On-phone stems only after one measurement run on his device, and only if the measured time is
acceptable.

## Rejected

- **Touch UI first, to get something on the phone quickly.** Every unknown that could sink the port is
  below the UI. Phase 4 is the compromise: a listening-only build is both the risk retirement and
  something he can hold.
- **.NET MAUI.** See the decisions table.
- **Avalonia or Uno for one UI on both platforms.** The desktop UI is not being rewritten; its
  253-check gesture harness and 41-check button harness are WPF-bound and are the proof that the
  extraction changed nothing.
- **A Swift rewrite.** Out by constraint; it would also mean a second signal path or shipping the .NET
  engine as a native library.
- **Keeping the stretcher NuGet package and redirecting it on iOS with `SetDllImportResolver`.** The
  name is a const in the package, the iOS runtime needs `__Internal`, and whether a resolver works under
  it is unverified. Four MIT files vendored is smaller than finding out.
- **Each device at its own output rate, with a tolerance on the seam rule.** Forbidden by `CLAUDE.md`
  and wrong: a tolerance would suppress declicks between sections the user meant to be separate.
- **Merging two devices' edits.** Not definable while honouring never-ripple and overlap-trims-for-good.
- **Autosave reusing the dated-backup slots.** An autosave every few seconds would evict every
  human-made version within a minute.
- **MP3 on the phone via LAME in the first release.** LGPL, and a second native library on the riskiest
  part of the pipeline, for a format M4A replaces in almost every player.
- **Stems on the phone in the MVP.** Hold, unmeasured, licence unresolved. Synced stem WAVs give the
  same result.
- **Mac Catalyst as a test bed.** Not the phone; no ONNX native for it; a third platform to keep green.
- **Simulator-only proof of the stretcher and output.** It proves macOS arithmetic and the simulator's
  audio, not the phone's.

## Unproven, by design

- No on-device number exists for anything: memory, the stretcher's arm64 difference, heat. Phase 4
  and Phase 7 produce them.
- The touch mapping is a proposal. Daniel's hands decide it at the Phase 5 and 6 gates.
- A listening test can only say audible or not. It must never be written up as proof of correctness,
  and "sounds fine" must never override a measured difference.
