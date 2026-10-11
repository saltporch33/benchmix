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
- **Splitting a section and moving neither half changes not one sample.** That is the *seam rule*
  (`VoiceClip.JoinsSeamlesslyTo`) and it is load-bearing — it is why hand-built loops do not tick.
  It rests on an **exact** equality between one section's `SourceEnd` and the next one's
  `SourceStart`. Do not "improve" it with a tolerance: that would start suppressing declicks
  between sections the user meant to be separate.
- **Neither side of a butt join gets a declick.** Two abutting 5 ms ramps do not sum to unity —
  they punch a hole at every loop repeat. The join is covered by a crossfade from the outgoing
  section's handle, rendered through the real stretcher and EQ at the join (`CaptureHandle`), on
  every path. Before 2026-09-24 it was not covered on any path and every loop repeat clicked
  (`JoinTests`). Do not "simplify" it back to reading the handle raw from the file: that is what
  landed the old fade 65 frames late.
- **Positions are worked out in whole frames, then turned into seconds.** `TimelineEditor` and
  the drag gestures never add seconds together to place a section: the engine rounds each position
  separately, and a sum of seconds can land a frame off — which silently turns a seam or a butt join
  into a gap with two declicks. Keep new placement code in frames.
- **A track's origin (`TimelineOffsetSeconds`) moves.** Paste, drag or loop before it and it moves
  back, with every section moving forward by the same whole frames. It is not a wall; do not clamp
  to it.
- **Build output goes to `%LOCALAPPDATA%\Benchmix.build\`** on purpose, to keep bin/obj out of
  OneDrive *and* because the native `SignalsmithStretch.dll` fails to load from a deep path
  (MAX_PATH). Do not "fix" it.
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
| `--latencycheck` | a pitched track lands with an untouched one (no file needed) |
| `--gesturetest <song>` | pan, double-click-to-play, every section command, empty tracks, paste target, drag between tracks, snapping, loop mode, Ctrl/Shift+click selection, the Delete key, tooltips — 80 checks |
| `--buttontest <bed> <topper>` | Match BPM, Reset and Match key through the real buttons, Split into vocals + instrumental through the real right-click menu item, then undo, Reset-as-one-step and New, from inside (an outside UI Automation client made Windows activate the hidden window) — 31 checks |
| `--demosections --uiprobe <png> <song>` | the lane really draws sections and selection |
| `--demoediting --uiprobe <png> <song>` | the lane draws a moved, looped section, loop handles, the snap guide and an empty track |
| `--loadcheck <songs>` | tracks are actually ready to split into stems |
| `--stemtest <song>` | ONNX/DirectML works in the packaged exe |
| `--playtest 6 <songs>` | WASAPI is pulling in realtime (silent) |

These **exit 1 on failure** and also log a `verdict=` line to
`%LOCALAPPDATA%\Benchmix\benchmix.log`. Close any running Benchmix first — it locks the exe.

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
- Where the invariant is exactness, assert `==`, not a tolerance.
- One regression test per fixed defect, named for the behaviour that was wrong, with an assertion
  message stating the musical consequence.

## Decided — do not quietly reopen

`docs/DECISIONS.md` has the full list and the reasoning. The ones most likely to be reopened:

- **Not an Audacity fork or wrapper.** Investigated and rejected with five reasons.
- **Offline editor, not a live DJ app and not a DAW.** No effects, plugins, automation or MIDI.
- **Stems are an option beside the EQ, never a replacement** — *"But not Asa a replacement for the
  eq knobs."*
- **One signal path** for preview and render. A second implementation is not permitted.
- **An untouched track is bit-identical — with the limiter OFF.** The limiter is on by default and
  does attenuate a loud track. Overstating this is a logged mistake with its own test.
- **Every editing shortcut is Ctrl-modified**, because a bare key bound on the Window also fires
  from inside the PROJECT BPM TextBox. The one bare key that acts, Delete (2026-10-03), is
  deliberately **not** a KeyBinding: `OnWindowPreviewKeyDown` runs it only when the keyboard focus
  is not in a text box, and `--gesturetest` fails if a Delete KeyBinding ever appears.
- **Nothing touches his own music files without an explicit yes.** Tag write-back is offered, not
  done.

Nothing is attributed to Daniel unless quoted verbatim with a date. Keep that rule.

## Unproven — say so rather than implying otherwise

- **Nobody has ever listened to Benchmix's output.** Every claim in this repo is a measurement.
  Measurements can be right about the wrong thing. Do not say something "sounds" any way.
- Joins between sections are measured on a sine (`JoinTests`), not on music and not by ear.
- A section dragged onto another track at the same moment dips in level at both ends (each track
  declicks its own cut). That is read off the envelope code, not measured.
- Stem separation quality is proven only by a 39 dB control test, not by ear.

## Working with Daniel

- Lead with the answer; he skims. No internal vocabulary.
- **When a turn ends blocked on him, call `PushNotification` and say plainly that work has
  stopped.** A question buried in a long report reads as "still working". He has asked for this
  three times.
- Prefer deciding on a stated default over stopping to ask.
- A status question wants a status answer, not an audit.

*Revised 2026-10-03.*
