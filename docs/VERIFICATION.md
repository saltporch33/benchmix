# Benchmix — what has actually been verified

*2026-09-16.* Evidence, not assurances. Every number here was measured on the target machine.

## The test suite

**91 tests, 0 failures, 0 skipped.** `dotnet test` — about 8 seconds.

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
- **Published build runs standalone.** 68.5 MB single self-contained file, clean exit, desktop
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

- **Nobody has listened to live playback through the speakers.** The pipeline is proven to run at
  realtime and the rendered files are proven correct; the final "does sound come out" step needs a
  human to press play. Thirty seconds of Daniel's time.
- **Pointer gestures have not been exercised.** Buttons are now driven and checked by
  `ui-verify.ps1`, but knob dragging, clip dragging, edge-trimming and shift-drag loop selection
  are mouse gestures UI Automation cannot invoke. Their maths is unit-tested; the feel is not.
- **Long-session behaviour.** No soak test. Memory should be flat once clips are loaded, but that
  is reasoning, not measurement.
- **Stem separation** is not in the app — see `DECISIONS.md`.
