# Spike 01 — is the time-stretch engine good enough, and fast enough to run live?

Run 2026-09-16 on the target machine (Core Ultra 7 258V, 32 GB). Code in
`spikes/01-stretch/`. This spike decided the whole architecture, so the numbers are kept.

## Engine chosen

**`SignalsmithStretch-CS` 1.1.2** — a C# wrapper over
[Signalsmith Stretch](https://github.com/Signalsmith-Audio/signalsmith-stretch).

| Property | Value | Why it mattered |
| --- | --- | --- |
| License | **MIT** (both wrapper and core) | No GPL entanglement, unlike Rubber Band. |
| Native binary | ships `runtimes/win-x64/native/SignalsmithStretch.dll` prebuilt | **Decisive** — this machine has no C++ compiler, so anything needing a build was out. |
| TFM | `net8.0` (works on net10.0) + `net462` | Matches the Sports DJ stack. |
| Single-file publish | RID-scoped `runtimes/` layout, so `dotnet publish -r win-x64` carries it | Keeps the "one .exe" promise. |

API surface that matters: `PresetDefault` / `PresetCheaper`, `Process(in, inLen, out, outLen)`
(the in/out length ratio *is* the time-stretch), `SetTransposeSemitones(semis, tonalityLimit)`,
`SetFormantSemitones` / `SetFormantFactor` (formant preservation — keeps pitched-up vocals from
sounding like chipmunks), `InputLatency()` / `OutputLatency()` for sample-accurate alignment,
`Seek` / `Reset` for scrubbing, and `splitComputation` to spread CPU across calls.

## Results

**Correctness** — 6/6 pass. Speed changes leave pitch alone; pitch changes leave length alone;
both together behave. Measured by Goertzel on the output.

| Test | Length | Pitch | CPU |
| --- | --- | --- | --- |
| 1.5× slower, pitch untouched | 3.00 s (want 3.00) ✓ | 440.0 Hz (want 440.0) ✓ | 161× realtime |
| 0.8× faster, pitch untouched | 1.60 s ✓ | 440.0 Hz ✓ | 158× |
| same speed, +12 semitones | 2.00 s ✓ | 880.5 Hz (want 880.0) ✓ | 158× |
| same speed, −5 semitones | 2.00 s ✓ | 330.5 Hz (want 329.6) ✓ | 155× |
| 1.25× slower **and** +3 semitones | 2.50 s ✓ | 525.0 Hz (want 523.3) ✓ | 128× |
| 1.5× slower, `PresetCheaper` | 3.00 s ✓ | 440.0 Hz ✓ | 273× |

**Streaming** — feeding it `outBlock / ratio` input samples per `outBlock` of output produces a
continuous signal at 1024, 512 and 256-sample blocks. Worst sample-to-sample jump 0.0296–0.0326
against a clean-sine step of 0.0288 — i.e. no click, at the noise floor of the measurement.

**Live knobs** — sweeping tempo 1.0→1.25 *and* pitch 0→+4 semitones continuously during
playback: worst jump 0.0366, RMS flat (0.348 → 0.347 → 0.342). **No zipper noise.** Parameters
can be changed every block without a crossfade or a rebuild.

**Load** — one instance per channel, 512-frame blocks, `splitComputation: true`:

| Stereo tracks | CPU per block | Budget | Headroom |
| --- | --- | --- | --- |
| 2 | 311 µs | 10,667 µs | 34× |
| 4 | 747 µs | 10,667 µs | 14× |
| 8 | 1,334 µs | 10,667 µs | 8× |
| 16 | 2,651 µs | 10,667 µs | 4× |

A 4-minute stereo render at high quality ≈ **3.4 s**.

## What this changes

The plan assumed the usual editor compromise: a cheap stretcher for live preview, a good one
for the final render, and the two never quite matching. **That compromise is unnecessary here.**
There is enough headroom to run `PresetDefault` — the quality preset — for 16 live tracks.

So Benchmix uses **one signal path for both preview and render**. What you hear while dragging a
knob is sample-for-sample what lands in the exported file; the only difference is that the render
runs the same graph as fast as the CPU allows instead of in realtime. That removes a whole class
of "it sounded right in the editor" bugs, and it is the right shape for meticulous offline work.

`PresetCheaper` is kept in reserve for scrubbing and for a low-power fallback, not as the
default preview path.
