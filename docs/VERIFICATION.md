# Benchmix — what has actually been verified

*2026-09-16.* Evidence, not assurances. Every number here was measured on the target machine.

## The test suite

**72 tests, 0 failures, 0 skipped.** `dotnet test` — about 8 seconds.

The ones that matter most:

| Test | What it pins down |
| --- | --- |
| `Isolator_AtUnity_IsBitExactPassthrough` | Every sample of 20,000 frames of noise through the EQ at unity comes out identical. Not "close" — `==`. |
| `UntouchedTrack_RendersBitIdenticalToItsSource` | The same, through the whole chain: clip, stretcher, EQ, gain, master bus. Zero differing samples. |
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

## What has *not* been verified

Stated plainly so nobody assumes otherwise:

- **Nobody has listened to live playback through the speakers.** The pipeline is proven to run at
  realtime and the rendered files are proven correct; the final "does sound come out" step needs a
  human to press play. Thirty seconds of Daniel's time.
- **The UI has not been driven by hand.** Knob drags, clip dragging, trimming, loop setting and the
  render dialog are built and compile-verified, and their underlying logic is unit-tested, but the
  gestures themselves have not been exercised by a person.
- **Long-session behaviour.** No soak test. Memory should be flat once clips are loaded, but that
  is reasoning, not measurement.
- **Stem separation** is not in the app — see `DECISIONS.md`.
