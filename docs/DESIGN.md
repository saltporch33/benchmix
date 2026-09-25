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
- **A join between two tracks is not seamless.** Drag a section onto another track without moving
  it in time, and each track still renders on its own: the end left behind and the start of the
  moved section each get the normal 5 ms declick, so the level dips at both ends of the moved
  section. That follows from the envelope code; it has not been measured on music or listened to.
  A seam, which is inaudible, only exists within one track.
- **A loop is ordinary sections.** Loop mode lays down the same repeats Ctrl+D makes; there is no
  "looped clip" object. Changing the original section afterwards does not change its repeats.
- **No per-section EQ, volume or speed.** Those belong to the track, so every section on it gets
  the same treatment. A section that needs its own EQ wants its own track.
- **No automation curves.** Knobs are static per track; fades are per clip.
- **Key detection is 47%.** Documented, surfaced, and worked around rather than hidden.
- **No effects** — no reverb, delay, compression or plugins, by design.
- **Memory grows with track count.** Eight 4-minute tracks is roughly 740 MB.
