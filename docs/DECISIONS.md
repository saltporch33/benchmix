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
