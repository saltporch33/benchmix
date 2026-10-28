# Benchmix — design

*Written 2026-09-16, after the spikes in `SPIKE-01`…`SPIKE-03` settled the open questions.*

## The shape of the problem

Daniel makes mashups in Audacity. He wanted three things Audacity makes tedious — speed without
pitch, pitch without speed, and per-track EQ — with a DJ mixer's feel, but offline, ending in a
saved file rather than a performance.

Two properties fall out of "offline, ending in a file" that a live DJ tool would not care about:

1. **The render must be exactly what he heard.** A preview that approximates the render is a tool
   that lies to you at the last step.
2. **Nothing should change that he did not change.** A track passed through untouched should come
   out untouched — not "close enough".

Both are achievable here, and both shaped the architecture more than any feature did.

## The decision that shaped everything else

The usual editor design has two signal paths: a cheap stretcher for realtime preview and a good
one for the offline render. It exists because high-quality stretching was historically too
expensive to run live on many tracks.

[Spike 01](SPIKE-01-stretch.md) measured that assumption on this machine and it did not hold:
Signalsmith Stretch runs at **~150× realtime** on the quality preset, and sixteen stereo tracks
still leave **4× headroom** at a 512-frame block. Knobs can be swept mid-playback with no zipper
noise and no clicks.

So Benchmix has **one signal path**:

```
clip → stretch → 3-band EQ → clip envelope → gain/pan → sum → master → limiter
```

Live playback pulls blocks from that chain in realtime. The renderer pulls blocks from *the same
object* as fast as the CPU allows. There is no second implementation that could disagree.

**Tabs** (2026-10-27) are separate arrangements in one file: `BenchmixProject.Tabs`, each a
`TabModel` with its own tracks, project tempo and listening loop. `Project.Tracks`, `ProjectBpm`
and the loop forward to the open tab, so the editor, the snapping and the engine wiring read "the
project's tracks" as they always did and only ever see the open tab. Every tab's audio is decoded
when the project opens; only the open tab's tracks have voices, and `SwitchTabAsync` swaps them.
Undo is one `UndoStack` per tab, and a step restores only the tabs it touched (`TabMerge.Restore`):
the open tab whole, another tab only its name and which tracks it holds. A version 4 file opens as
one tab; every save writes version 5.

A render holds what can be heard, and no more. A muted track — by its own M, or because another
track is soloed — is silent in the file as it is in the speakers, and the file ends where the last
track that can be heard ends (`MixGraph.AudibleLengthFrames`), not where the longest track does
(2026-10-27, his request). The one place the two differ: with nothing audible the speakers play
silence, and the Render button refuses.

## Keeping untouched audio untouched

Three things could quietly alter audio that nobody asked to alter, and each is handled:

**The stretcher.** A track at speed 1.0 with no pitch shift bypasses it entirely
(`TrackVoice.IsUnprocessed`) — samples are copied, not analysed and resynthesised.

**The EQ.** Built as a subtractive complementary crossover rather than three parallel filters,
after Lipshitz & Vanderkooy (1983) — the same family Mixxx ships as "LV-Mix":

```
low  = LP1(x)                    4th-order Bessel at 246 Hz
mid  = LP2(D2(x)) - low          4th-order Bessel at 2.5 kHz, delay-aligned
high = D3(x)      - LP2(D2(x))
y    = (gLow-gMid)·LP1 + (gMid-gHigh)·LP2 + gHigh·D3(x)
```

At unity the first two terms cancel identically and `y = D3(x)`: the input, delayed. Three
parallel filters cannot do this — their different group delays make the bands add as complex
vectors near each crossover, leaving ±1–3 dB of ripple even with the knobs centred.

Bessel rather than Butterworth because the high band is formed by *subtraction*, which only
behaves like a real highpass when the lowpass has near-constant group delay. The pole set is
hardcoded but **verified at runtime** against its own polynomial, and the −3 dB normalisation and
the band delay alignment are computed numerically rather than taken from a table — a mistyped
constant throws instead of quietly detuning the crossover.

**The EQ's latency.** It delays by ~65 samples, so each track reads that far ahead and the render
stays sample-aligned with the source.

The result is asserted, not hoped for: `UntouchedTrack_RendersBitIdenticalToItsSource` compares
every sample of a rendered track against its source and requires zero differences.

The one deliberate exception is a **trimmed** clip edge, which gets a 5 ms declick — cutting into
a waveform lands on a non-zero sample, and that is a click. An untrimmed clip gets no ramp at all,
which is what preserves the guarantee.

## Joins between sections

A track holds sections, and where two meet there are three cases:

- **A seam** — a section split and neither half moved. The second continues the first in the same
  file, so the engine plays straight through: no fade, no re-prime, not one sample changed.
- **A butt join** — the next section starts on the very frame the last one ends but does not
  continue it: every repeat of a loop. Neither side is declicked (two ramps back to back are a
  hole). Instead, at the join the voice renders five milliseconds of what the outgoing section
  *would have played next* — its handle, read past its cut, through the real stretcher and EQ —
  and crossfades it, equal power, into the incoming section. The fade survives a render-block
  boundary, so preview and export agree. Measured: the largest step at a phase-mismatched loop join
  equals the signal's own largest step, on the untouched, linked and phase-vocoder paths alike.
- **A gap** — each side gets its 5 ms declick where it was cut.

**Every placement is in whole frames.** The seam test and the butt-join test both compare two
frame numbers for exact equality, and the engine rounds each position separately. Summing
positions in seconds and rounding the total can land a frame or two away — enough to turn a seam
into a gap with a declick either side. So `TimelineEditor` and the drag gestures work out every
position in frames, exactly as the engine will, and only then convert back to seconds.

**A track's origin moves.** `TimelineOffsetSeconds` is where a song was first dropped, and it is
bookkeeping rather than something anyone chose. When a section is pasted, dragged or looped to a
point before it, the origin moves back and every section on the track moves forward by the same
whole number of frames, so nothing is heard to move.

## Tempo ramps

*2026-10-11.* A track can change tempo over a span of the timeline: from whatever it plays where
the ramp starts, in a straight line, to the speed the ramp ends on, then holding there. The TEMPO
knob is the speed before the first ramp. Ramps sit on the timeline in seconds
(`TrackModel.Ramps`); sections stay where they always were, in the track's own frames.

So the one number that turned track frames into timeline frames — the speed — becomes a
**tempo map** (`TempoMap`): speed is piecewise linear in timeline time, so the distance travelled
through the track is piecewise quadratic, and both directions have a closed form. Everything that
multiplied or divided by the speed now asks the map instead: the engine's read position, a seek, a
section's length on the timeline, its declick ramps, the edge a section snaps to, moving the
origin. With no ramp, the map *is* the old multiply and divide, the same expressions in the same
order, so a track without ramps places and renders every sample exactly as before (30-second
renders of two real songs, untouched, through LINK and through the phase vocoder, hash the same
before and after).

Two rules keep a ramped track in time at every block size:

- **Read positions are computed, never accumulated.** The phase vocoder is fed the difference
  between where the map says the read should be at the end of this block and where it was, so
  rounding cannot build up. LINK (no pitch kept) reads each output frame at its closed-form
  position; its anti-alias kernel is chosen per fixed 1024-frame cell of the timeline, not per
  block, so a preview and an export pick the same kernels.
- **A ramped track never bypasses the stretcher**, even where it plays at 1.0. Dropping in and
  out of it would restart it, and its delay, partway through a song.

The map is immutable, and it is measured from the track's origin, so the origin, the sections and
the map reach the engine together: one snapshot, swapped by reference (`TrackVoice.Place`), read
once per block. A block never sees a moved origin with the old map — which, after a paste before a
ramped track's start, would play the wrong part of the song for a block and then jump back.

## Warp markers

*2026-10-18.* A warp marker pins one point of a track's audio — a beat — to the moment it should be
heard. Markers sit on the audio, in the track's own seconds (`TrackModel.Warps`), not on the
timeline as ramps do: each holds the point of the audio (`TrackSeconds`, on a whole frame) and how
far the track's tempo has to carry it before that point is heard (`TempoSeconds`). So TEMPO, Match
BPM and a ramp before a marker carry it along with its beat. A marker nobody has moved has the two
equal.

The tempo map gets a second stage. The first — the knob and the ramps — says how far the track's
*tempo* has carried it at a moment of the timeline; the position is then the **warp of the tempo's
position**: straight lines from marker to marker, so a marker's audio is reached exactly when the
tempo reaches its `TempoSeconds`; through the origin before the first marker, so the track is still
at 0 where it starts; slope 1 after the last, so the track plays at its tempo again and everything
after it moves along with it. Both directions stay closed form. The speed is the tempo's speed times
the warp's slope, so it **jumps at a marker**, by the ratio of the slopes either side, as in
Ableton; the position never jumps. Like a ramp, the warp lives in the read position; it adds no
stage to the signal path.

**With no marker moved, the warped map is the map before it — the very same object**
(`TempoMap.LenientWarped` hands back its input). Putting a marker down changes not one sample, and
an untouched track whose markers are all unmoved still bypasses the stretcher. In frames an unmoved
marker is its rounded frame in both coordinates (`ProjectSession.EngineTempoOf`): seconds times the
rate is not always a whole number in floating point, and a hair off would count as moved. A track
with any moved marker goes through the stretcher for its whole length, as a ramped one does.

A third rule joins the two above: **a render run ends where the read position reaches a marker**
(`TempoMap.NextJumpAfter`, looking ahead by the EQ's latency and, on the phase vocoder, the
stretcher's), so the vocoder is fed each side's speed exactly at every block size. Fed across a
marker in one go, it would smear the jump over the run, by an amount that depends on the block size.

When the origin moves back and every section moves forward by whole frames, the markers move
forward by the same frames (`WarpMarkers.ShiftWithOrigin`), and every beat is heard where it was. The
map is built leniently, as for ramps: a marker out of order, or one that would spread the audio
before it past half or twice its tempo (`TempoMap.LowestWarp`, `HighestWarp`), is left out, and if
what is left would ask the engine for a speed outside 0.25–4× the whole warp is dropped. It reaches
the engine in the same one snapshot as the origin and the sections (`TrackVoice.Place`).

## Tempo and key: measure, then decide

Daniel asked for detection of both. [Spike 03](SPIKE-03-analysis.md) calibrated against his own
library, where Serato had already tagged 34 files — a free ground-truth set of exactly the
material the tool will see.

- **Tempo** — spectral-flux onsets, autocorrelation reinforced by a comb over period multiples,
  a log-Gaussian prior at 120 BPM, parabolic refinement. **94% exact, 0% wrong.** Trusted.
- **Key** — chroma against key profiles. **47% exact** at best, across 30 combinations of five
  published profile sets and three chroma front ends. Not trusted; always labelled an estimate.

So the policy is **tags first, analysis only when a tag is missing**, and detected values carry
their provenance into the UI. A `?` next to a key means Benchmix guessed it.

One idea was tested and **refuted**: cross-correlating two tracks' chroma to rank candidate
semitone shifts. Measured over all 888 ordered pairs it scored 3.4–7.4% top-1 against an 8.3%
random baseline — at or below chance. It is not implemented, and `SPIKE-03` records why so it does
not get proposed again.

## Structure

| Project | Holds | Knows about |
| --- | --- | --- |
| `Benchmix.Engine` | clips, stretch, EQ, resampler, mixer, limiter, renderer, tempo/key detection | NAudio, Signalsmith. No UI types. |
| `Benchmix.Core` | project model, file format, undo, the model↔graph bridge | Engine |
| `Benchmix.App` | timeline, waveforms, knobs, transport | Core, Engine, WPF |

**The model is the truth.** Every UI change writes the `TrackModel`, then pushes that one track
into its `TrackVoice`. Nothing reads state back out of the audio engine, so a value lives in
exactly one place and saving cannot miss a change.

**Threading.** The track list is published to the render thread by reference swap, so structural
edits never lock it. Per-track parameters are plain aligned 64-bit stores, atomic on x64, so a
knob turn cannot tear. Gains ramp over ~10 ms rather than stepping.

**Undo is whole-project snapshot.** The model is a few kilobytes of numbers; cloning it per edit
costs nothing and removes every bug where an undo command forgets one field. Command-pattern undo
would be right for a large model and would only add ways to be wrong here.

## Choices made and why

| Choice | Over | Because |
| --- | --- | --- |
| Signalsmith Stretch | Rubber Band | MIT, and ships a prebuilt `win-x64` binary. Rubber Band is GPL and needs compiling — and this machine has no C++ toolchain. |
| | SoundTouch.Net | Lower quality on full mixes, and LGPL. |
| Clips fully in RAM | streaming from a disk cache | A mashup editor scrubs and loops constantly. 92 MB per 4-minute stereo track against 32 GB of RAM is a good trade for making every seek free. |
| Subtractive isolator EQ | shelf/peak EQ | Exact unity passthrough and a true kill. Shelving is offered as a second mode. |
| Tags before analysis | always analysing | 97% of his BPMs and 91% of his keys are already correct in the files, and reading them is instant. |
| Per-track stretch | per-clip stretch | Matches the mental model — a deck has one tempo fader. Revisit if a project ever needs two tempos on one track. |
| JSON project files | SQLite | Single writer, a few KB, diffable, no native dependency. |

## Known limits

- **Sections on one track never overlap.** A track is one read position, one stretcher and one EQ;
  two sections sounding at once is not representable in that. Two things at once is a second track.
  A dropped section or a loop overwrites what it lands on; a trim stops at the next section.
- **A group moved onto a track at another tempo keeps its joins**, so it plays at that tempo and
  spreads or tightens on the timeline, as one dragged section's length does.
- **No auto-scroll** while dragging a section, a group, a box or a track.
- **A join between two tracks is not seamless.** Drag a section onto another track without moving
  it in time, and each track still renders on its own: the end left behind and the start of the
  moved section each get the normal 5 ms declick, so the level dips at both ends of the moved
  section. That follows from the envelope code; it has not been measured on music or listened to.
  A seam, which is inaudible, only exists within one track.
- **A loop is ordinary sections.** Loop mode lays down the same repeats Ctrl+D makes; there is no
  "looped clip" object. Changing the original section afterwards does not change its repeats.
- **No per-section EQ, volume or speed.** Those belong to the track, so every section on it gets
  the same treatment. A section that needs its own EQ wants its own track.
- **No automation curves, except a track's tempo ramps and warp markers.** Every other knob is
  static per track; fades are per clip.
- **A tempo ramp is a straight line in BPM.** No curves, and the tempo holds after it until the
  next one.
- **A track with a tempo ramp or a moved warp marker is processed for its whole length**, so it is
  no longer bit-identical to its file even where it plays at 1.0.
- **The ruler's bar lines do not follow a tempo ramp or a warp**, and the BPM on the strip is the
  tempo before the first ramp; it ignores warp markers.
- **A tempo ramp is a span of the timeline**: moving a section under one changes the tempo it plays
  at, and adding, moving or retyping a ramp moves every later section on that track.
- **The speed jumps at a warp marker**, on purpose, and the audio between two markers is spread no
  further than half or twice its tempo.
- **A warp marker belongs to the track, not to a section**: a section moved past one is warped by
  where it now is, and moving or removing the last marker moves every later section on that track.
- **Dragging a ramp's dot while playing re-primes that track** on each move, as turning TEMPO does.
- **Key detection is 47%.** Documented, surfaced, and worked around rather than hidden.
- **No effects** — no reverb, delay, compression or plugins, by design.
- **Memory grows with track count.** Eight 4-minute tracks is roughly 740 MB.
