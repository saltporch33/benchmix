# Benchmix — decisions log

Every entry records who decided, when, and (for the user's decisions) his own words.
Nothing is attributed to Daniel here unless it is quoted verbatim with a date.

## 2026-09-16 — what the tool is

Daniel, opening the project:

> "I've used audacity before to create mashups of songs. It's free and very capable, but I
> only need a few small features: speed adjustment that doesn't change pitch; pitch
> adjustment that doesn't change speed, and honestly I think that's it. Oh, and if we could
> also have eq adjustment knobs on each of the tracks as well. Basically just like a dj
> mixer, except that I can do this thru audacity meticulously, and save the file at the
> end...instead of having to mix it live."

So: an **offline** multitrack mashup editor with a DJ-mixer feel that renders a finished
audio file. Not a live DJ app.

## 2026-09-16 — scope answers (his words, in reply to direct questions)

| Question | His answer |
| --- | --- |
| Replace Audacity, or sit beside it? | *"i mean, i need the multi track timline for sure. But I was thinking that you'd build this by basicaly just taking audacity and removing all other extraneous features? Wouyldnt that be called a wrapper? Basically just audacity with a simplified GUI that helpsm me focus just on this functionality."* |
| Beats and keys | Chose **"Detect both, suggest the match"** — read each song's BPM and key, propose the stretch % and semitone shift, apply in one click. |
| Live preview | Chose **"Yes, live"** — turn a knob, hear it immediately. |
| Stem separation | *"But not Asa a replacement for the eq knobs. I want the option to instead just adjust highs mids and lows on the complete song file"* → stems are an **option**; the 3-band EQ always works on the whole song. |
| Delivery | Chose **"One big build, tell me when it's done."** |
| Install shape | Chose **"One .exe, keep it clean"** — self-contained like Sports DJ, no runtime to maintain, AI parts run in-process on the Arc GPU. |
| Name | Chose **Benchmix**. |

## 2026-09-16 — the "Audacity wrapper" question, answered

He asked whether this could be built by stripping Audacity down. It cannot, and the reasons
are worth keeping so the question does not come back:

- Audacity has no seam between its GUI and its engine. There is no embeddable core library —
  the UI *is* the application. "Removing features" means forking ~600k lines of C++/wxWidgets
  and doing surgery.
- Building that fork needs MSVC + CMake + vcpkg. This machine has no C++ toolchain and no
  Visual Studio, and Audacity is not even installed.
- Audacity **is GPLv2+**, so a derived tool inherits GPL. That is fine personally, but it
  forecloses ever shipping this any other way.
- The part worth keeping — its tempo/pitch engine — is SoundTouch and SBSMS, libraries that
  can be called directly without the fork.
- Audacity's scripting pipe (`mod-script-pipe`) *can* drive it from outside, which is the
  only thing that would honestly be called a "wrapper" — but it drives destructive,
  apply-and-wait effects. It cannot give live knobs, which he asked for.

Decision: purpose-built app. Recorded so this is not relitigated.

## Hardware this must run on (measured 2026-09-16)

Intel Core Ultra 7 258V (8c/8t, Lunar Lake) · Intel Arc 140V iGPU + NPU · 32 GB RAM ·
568 GB free on C: · Windows 11 26200. **No NVIDIA GPU**, so no CUDA — AI inference goes
through DirectML/OpenVINO on the Arc, or CPU.

Toolchain present: .NET SDK 10.0.401, Node 24.15.0, Python 3.13 (Store build), Git 2.54.
**Absent: any C++ compiler, Visual Studio, VS Code, ffmpeg, Audacity, rubberband/soundstretch
binaries, any VST.** This is a hard constraint on library choice: anything native must arrive
as a prebuilt binary (NuGet), because nothing here can compile C++.

## 2026-09-16 — decisions I took while building (he was AFK for most of it)

Recorded so they are visible and reversible, not buried in code.

| Decision | Why |
| --- | --- |
| **Signalsmith Stretch** for time/pitch | MIT, and it ships a prebuilt `win-x64` binary. Rubber Band is the quality leader but is GPL and source-only, and this machine has no C++ compiler. Measured at ~150x realtime with 4x headroom on 16 tracks. |
| **One signal path for preview and render** | The measured headroom removed the usual reason to have two. What he hears is what he gets. |
| **Subtractive (isolator) EQ as the default** | Reconstructs the input exactly at unity, so an untouched track is untouched. Shelving EQ kept as a second mode. |
| **Tags before analysis** | 97% of his BPMs and 91% of his keys were already correct in the files. Reading them is instant; analysing is not. |
| **Key values always labelled as estimates** | Mine is 47% accurate. Presenting that as fact would be a lie the tool tells hourly. |
| **Chroma cross-correlation NOT implemented** | The research round recommended it; measuring it over 888 pairs put it at or below random chance. |
| **Clips held fully in RAM** | ~92 MB per 4-minute stereo track against 32 GB. Makes every seek, loop and scrub free. |
| **Per-track stretch, not per-clip** | Matches the mental model of a deck with one tempo fader. Still true now that a track holds several sections: tempo, pitch and EQ belong to the track, and a section pasted from another song takes them on. |
| **5 ms declick on trimmed edges only** | Cutting into a waveform lands on a non-zero sample, which clicks. Untrimmed clips get no ramp, which is what preserves bit-exact passthrough. |
| **Snap clips to bars by default**, Ctrl to override | Mashups are assembled on bar lines. |
| **Self-contained single-file exe** (68.5 MB) | He asked for "one .exe, keep it clean". Self-contained means it keeps working even if the .NET SDK is ever removed. |
| **Silent play-test rather than a loopback recording** | Proving the audio pipeline runs should not mean blasting music at someone who is working. Master at zero, measure the frame-pull rate. |

## 2026-09-16 — stem separation: status

He asked for stems as an **option alongside** the EQ knobs, not as a replacement:
*"But not Asa a replacement for the eq knobs. I want the option to instead just adjust highs mids
and lows on the complete song file"*, and chose the one-exe install, which rules out a bundled
Python.

Verified feasible ([SPIKE-02](SPIKE-02-directml.md)): `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4
initialises on the Arc 140V, so in-process GPU inference works with no CUDA.

**Not yet in the app.** The remaining work is the model itself: MDX-Net `.onnx` weights are
mirrored across community repos rather than one authoritative host, and the STFT pre/post
processing has to match the specific model exactly. That is being proven in a standalone spike
before anything touches the app, because a half-working separator is worse than none.

## 2026-09-16 — offered, not done

- **Writing analysis results back into file tags** (`TBPM` / `TKEY`), so Serato, djay and anything
  else would see Benchmix's numbers. This modifies his music files, so it waits for an explicit
  yes. He was told it is a flag away.


## 2026-09-16 — stem separation: shipped

Built and verified. MDX-Net (`UVR-MDX-NET-Voc_FT`) through ONNX Runtime DirectML, running
in-process on the Arc 140V. Right-click a clip → *Split into vocals + instrumental*.

- The 64 MB model downloads on first use, with its **SHA-256 pinned**. MDX-Net weights are
  mirrored across community repos rather than one authoritative host, so a successful download is
  not the same as the right file; a mismatch is discarded, never used.
- Stems are written out as WAVs and added as ordinary tracks, inheriting the original's tempo,
  key, placement, trim and speed. The original is **muted, not deleted**, so one click undoes it.
- Evidence: a **39 dB gap** between the vocals stem of a vocal track and that of an instrumental;
  stems reconstruct the mix; two independently-trained models sum back to −22.6 dB. About 20
  seconds per song.
- The published single-file exe really carries the native libraries — proven by `--stemtest`, not
  assumed from the file size.

**License caveat, unresolved:** UVR's README grants MIT-with-attribution for its models, but there
is no LICENSE file attached to the weights and the distribution repo has none at all. The training
data is undisclosed. Fine for personal use; worth a decision before Benchmix is ever shared.

**Known limits:** the vocal stem is band-limited to 17.6 kHz (inherent to the model — the derived
instrumental keeps all the HF), and separation holds the whole song in memory.

## 2026-09-16 — the adversarial review

31 confirmed defects, all fixed. Detail in `VERIFICATION.md`. Two things worth carrying forward:

1. **A constant test signal cannot detect a positional bug.** My first attempt at the clip-offset
   regression test used a steady tone and passed against the very bug it was written for — the
   clip was playing the wrong *part* of the song, and every part sounded identical. Positional
   markers (a ramp, or a single loud sample) are what catch alignment errors.
2. **"Bit-identical passthrough" was overstated.** It holds for the signal path with the limiter
   off. The limiter is on by default and does attenuate a loud untouched track. Both statements
   now have a test, and the README says so.
