# Benchmix — what has actually been verified

*2026-09-16.* Evidence, not assurances. Every number here was measured on the target machine.

## The test suite

**190 tests, 0 failures, 0 skipped** (2026-09-24). `dotnet test` — about 90 seconds (the stem
tests dominate).

The ones that matter most:

| Test | What it pins down |
| --- | --- |
| `Isolator_AtUnity_IsBitExactPassthrough` | Every sample of 20,000 frames of noise through the EQ at unity comes out identical. Not "close" — `==`. |
| `UntouchedTrack_RendersBitIdenticalToItsSource` | The same, through the whole chain: clip, stretcher, EQ, gain, master bus. Zero differing samples — **with the limiter off**. |
| `TheLimiterDoesAlterALoudUntouchedTrack` | The honest counterpart: with the limiter on (the default) a track peaking at 0.99 *is* attenuated. Recorded so the guarantee above is not read as unconditional. |
| `Render_IsDeterministic` | Rendering the same project twice gives byte-identical files, dither included. |
| `ASavedProjectReopensSoundingTheSame` | Save, close, reopen, re-render → byte-identical audio. Nothing is lost in the project file. |
| `ChangingSpeed_LeavesPitchAlone` | 1.25× faster, measured pitch still 440 Hz. |
| `ChangingPitch_LeavesSpeedAlone` | +12 semitones, measured 880 Hz, length unchanged. |
| `TurningAKnobDuringPlayback_DoesNotClick` | Sweeping the low knob from unity to kill and back mid-playback: worst sample-to-sample jump 0.0366 against a clean-signal step of 0.0288. |
| `ACorruptProjectFallsBackToTheBackup` | A truncated project file loads the previous save and says so. |
| `UndoIsDeepSoLaterEditsDoNotLeakBackwards` | Undo restores a value mutated on the live object. |

## 2026-09-24 — editing: empty tracks, paste target, drag between tracks, loop mode, snapping

**Joins between sections, measured.** A 431 Hz sine at 0.5, looped at two points half a cycle
apart; the largest sample-to-sample step within 10 ms of the join, against the same render's
largest step anywhere else — the sine's own. 431 Hz and not 440: the stretcher's 7200-frame delay
is a whole number of cycles of 440 Hz, which hid a defect from the first version of these tests.
`JoinTests`:

| Path | Before | After | Elsewhere in the same render |
| --- | --- | --- | --- |
| untouched track | 0.85 | 0.028 | 0.028 |
| LINK on | 0.99, 65 frames after the join | 0.028 | 0.028 |
| LINK at +5% | 0.95 | 0.030 | 0.030 |
| LINK at 0.7× | a hard switch at ~1 join in 40 (0.40) | 0.020 | 0.020 |
| pitched +2 st | 0.76 (440 Hz); 0.10 with only the EQ prime wrong | 0.031 | 0.028 |
| pitched +0.5 st | 0.079 with only the EQ prime wrong | 0.039 | 0.028 |
| stretched to 0.93× | 0.14 (reviewer's measurement) | 0.039 | 0.028 |
| stretched to 1.25× | 0.10 with only the EQ prime wrong | 0.037 | 0.028 |

The phase-vocoder rows are not quite at the signal's own step — within 1.4× of it, against up to
5× before. That is small next to what it was, but it is not nothing, and nobody has listened.

Also pinned: after the 5 ms fade the incoming repeat is **bit-identical** to the same repeat played
on its own, on all four paths (the fade smooths the edge and moves nothing), and a join 30 frames
before a block boundary renders bit-identically to one mid-block (preview and export agree). A
fade on one side of a join no longer leaves the other side bare: 0.42–0.43 steps before, 0.029
after.

**Placement, to the frame.** `SectionEditingTests` (26) and `SectionRegressionTests` (12), each on a
ramp so a sample's value is its position, most by rendering the edited project against the
original:

- A section pasted or dragged onto an empty track at the same moment renders **identical** to how
  it played on its own track, everywhere but the 5 ms declicks at its two cut edges.
- Snapping onto a neighbour's end lands on **exactly** that frame, and the engine sees a butt join.
- A loop's repeats are each **bit-identical** to the first pass once past their crossfade; a
  part-repeat keeps the loop in phase on either side; pulling a loop back during the drag restores
  what it ran over.
- Moving a track's origin back changes **no samples** and breaks no seam.
- The four regressions: a split of a moved section is now a true seam (0 samples change), Ctrl+D
  on a moved section leaves no gap, an emptied track reopens empty, and paste lands at the playhead
  even before where the track's song began. Each failed against the code before the fix.

**Hundreds of edits in a row.** `SectionFuzzTests` runs four seeded sequences of 250 random moves
(across tracks, with and without snapping), trims, loops, pastes, splits, deletes and repeats on
four tracks at three tempos, and after every one checks that no track has overlapping sections,
nothing sits before its track's timebase, and the engine holds exactly the sections the model
says — nothing silently truncated.

**Two adversarial reviews**, told to assume the change was wrong and to reproduce anything they
reported. The first found seven defects, and fixing them turned up two more; the second, of just
those fixes, found four in them. All thirteen are fixed, and each fix was taken out again to
confirm its test then fails — see `DECISIONS.md`, 2026-09-24.

**In the published exe** (the single file, 78 MB): `--latencycheck` drift 0 frames between a
pitched and an untouched track; `--gesturetest` 57/57; `--loadcheck` pass; `--playtest 6` ratio
0.999 (silent); `--stemtest` pass on DirectML in 4.1 s; both `--uiprobe` demos drawn and looked at.

**In the real window.** `--gesturetest` grew from 27 to **57 checks**, 30 of them new, driving the
real lane handlers with a scripted pointer whose positions come from where the lanes actually are
in the laid-out window: Ctrl+T and Ctrl+L are bound; an empty track is added, selected and not
reported missing; paste lands on the selected track at the playhead; undo keeps the selection; a
right-click selects its lane; a section dragged down one track lands there at the dropped time; a
drop 6 px from an edge snaps onto it and shows the guide line; Ctrl frees it; loop mode builds four
exactly-butted repeats and one undo takes them back; a song file lands on an empty track where it
was aimed; the menu's *Move section to playhead* lands exactly. Pasting a file copied in Explorer
is **not** driven — setting that up would overwrite the clipboard of whoever is using the machine —
but the Windows clipboard counter it relies on is checked to resolve in the published exe.

`--demoediting --uiprobe` renders a moved, looped section with its loop handles at the loop's outer
ends, the snap guide line, the selected track's bar and an empty track's hint — looked at, and
correct.

## The stretch engine

[Spike 01](SPIKE-01-stretch.md), on this CPU:

- Six correctness cases pass — speed alone, pitch alone, both together, in both directions.
- **~150× realtime** on the quality preset. A 4-minute stereo render takes about 3.4 seconds.
- **16 stereo tracks at once with 4× headroom** at a 512-frame block.
- No clicks at 256, 512 or 1024-frame blocks; no zipper noise while sweeping tempo and pitch
  together mid-playback.

## Tempo and key, against real ground truth

[Spike 03](SPIKE-03-analysis.md), scored against 34 Serato-tagged tracks from Daniel's own library:

| | Result |
| --- | --- |
| Tempo exact (within 2%) | **32 / 34 — 94%** |
| Tempo half/double time | 2 / 34 |
| Tempo **wrong** | **0 / 34** |
| Key exact | 15 / 32 — 47% |
| Key within a harmonic neighbour | 19 / 32 — 59% |

The key figure is the ceiling across 30 combinations of five published profile sets and three
chroma front ends. It is why key values are labelled estimates in the UI.

A refuted idea is recorded there too: chroma cross-correlation for ranking semitone shifts scored
3.4–7.4% top-1 over 888 pairs against an 8.3% random baseline. Not implemented.

## On real music, end to end

`EndToEndTests`, run against the actual MP3s (skips itself if they are absent — they were not):

- Tempo and key read from tags in **under a second**, no decoding.
- Build a mashup: T.R.O.Y. at 102 BPM matched onto Still D.R.E. at 93, EQ split, offset four bars,
  rendered 30 seconds. The **rendered file analyses back at 90–96 BPM** — it really landed on the
  bed's tempo.
- Save → reopen in a fresh session → re-render → **byte-identical**.

Audible proof for a human is in `Music\Benchmix Demo\`:
`A-not-matched-102-vs-93.wav` (the two tracks fighting) and `B-matched-both-93.wav` (locked).

## The app itself

- **Starts, loads and draws.** Screenshot in [`screenshot.png`](screenshot.png): two real tracks,
  tempos and keys from their tags, waveforms, full channel strips.
- **Audio device pulls in realtime.** `--playtest 6` on the published exe:
  ```
  device='LG HDR WQHD (HD Audio Driver for Display Audio)' rate=48000 offline=False tracks=2
  wall=6.01s audio-advanced=6.01s ratio=1.001
  verdict=PASS - device is pulling in realtime
  ```
  Run at master zero, so proving the pipeline did not mean playing music at anyone.
- **Published build runs standalone.** 78 MB single self-contained file, clean exit, desktop
  shortcut at `OneDrive\Desktop\Benchmix.lnk`.
- **Zero build warnings** across the solution.

## The UI, driven for real

`scripts/ui-verify.ps1` clicks the actual buttons in the actual window through UI Automation and
reads the actual labels back. It runs minimised and never takes focus, so it is safe to run while
someone is using the machine. **10/10 checks pass:**

```
Loading
  PASS  both tracks loaded            PASS  bed tempo from tag
  PASS  topper tempo from tag         PASS  keys read from tags
Match BPM on the second track
  PASS  two Match BPM buttons         PASS  topper stretched to 93
  PASS  status explains it
Reset on the second track
  PASS  topper back to 102            PASS  status confirms reset
Match key
  PASS  same key recognised
```

The substantive one: before the click the second track reads `102.0 BPM`; after it reads
`93.0 BPM  (was 102.0)` and the status bar says *"They Reminisce Over You (T.R.O.Y.) (Clean):
-8.8% to hit 93.0 BPM."* That is the whole feature working through the real control.

## The adversarial review

A five-dimension review (DSP correctness, threading, edge cases, UI logic, and a pass dedicated to
breaking the four invariants above) raised 34 candidate defects. Each was handed to an independent
verifier told to assume the finding was wrong and to reproduce it against the real code. **31 were
confirmed by measurement, 2 rejected**, and all 31 are now fixed.

The one that mattered most: **a clip whose timeline offset was not a multiple of the audio block
size played the wrong part of its own song.** Measured — a clip at offset 1000 began 1000 source
frames late for 3,096 frames, then snapped backwards with a 0.175 sample step (an audible click),
and because preview and export use different block sizes they produced *different* wrong audio.
Every existing offset test missed it: the only one that set an offset used a block-aligned value
and asserted on RMS. It is now pinned by `ClipOffsetTests`, which uses a positional marker rather
than a constant tone — a constant tone cannot see the difference, which is why my first attempt at
the test passed against the bug.

Others confirmed and fixed included pitch not reaching the first block after the stretcher was
created (so two renders of one graph differed), a native stretcher freed under the render thread
when a track was removed during playback, seeks resetting every voice mid-block, mute cutting in a
single sample, renders reporting their peak *after* the limiter so an overdriven mix always looked
fine, and undo reverting an older action than the one just performed.

`RegressionTests` holds one test per fix, each named for the behaviour that was wrong.

## What has *not* been verified

Stated plainly so nobody assumes otherwise:

- **Nobody has listened to a loop join, or to a section moved between tracks.** The join numbers
  above are a sine, not music. A section dragged onto another track at the same moment gets a 5 ms
  declick at each end on both tracks, so the level dips there; that is read off the envelope code,
  not measured, and not heard.
- **Drag feel is untested.** The gestures are driven with a scripted pointer; how they feel under
  a real mouse, and whether 10 px is the right snap distance, needs a person.
- **Nobody has listened to live playback through the speakers.** The pipeline is proven to run at
  realtime and the rendered files are proven correct; the final "does sound come out" step needs a
  human to press play. Thirty seconds of Daniel's time.
- **Pointer gestures are exercised by `--gesturetest`** (57 checks, including the section commands
  and, since 2026-09-24, section drags, trims, snapping and loop mode through the lane handlers).
  Buttons are also driven and checked by `ui-verify.ps1`. Knob dragging and shift-drag loop
  selection are not driven by either. The feel of all of them is untested.
- **Long-session behaviour.** No soak test. Memory should be flat once clips are loaded, but that
  is reasoning, not measurement.
- **Stem separation quality** is proven only by a 39 dB control test, not by ear — see
  `DECISIONS.md`. (It is in the app; this line used to say it was not.)
