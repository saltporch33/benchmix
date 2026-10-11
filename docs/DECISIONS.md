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
| **Snap clips to bars by default**, Ctrl to override | Mashups are assembled on bar lines. *(Superseded 2026-10-11: nothing snaps to bars any more — see that entry.)* |
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

## 2026-09-24 — empty tracks, paste target, dragging between tracks, loop mode, snapping

Daniel, 2026-09-24, verbatim:

> "1. allow me to add empty tracks.
> 2. pasting audio should land on the selected track and at the current playhead
> 3. allow selected audio to be dragged to another track
> 4. select a piece of audio, toggle a "loop" mode, then dragging the left and right terminus' of
> the audio automatically duplicates the audio to allow for looping
> 5. snapping audio clips to each other when dragged close (hold ctrl for manual granular control)"

He was away and asked for no questions, so everything below was decided on a stated default and
is recorded here to be reversible.

| Decision | Why |
| --- | --- |
| **An empty track takes on the tempo and pitch** a section was playing at, when the first section is pasted or dragged onto it. Not its EQ, volume or name. | The section must land and sound exactly as it did. A fresh deck starts flat because a separate EQ is usually why a section gets its own track. The name stays "Track N": two tracks both called after the song would make every status message ambiguous. |
| Dragged *past* an empty track and on to another, the empty track is put back exactly as it was. | Passing over a track must not change it. |
| **A song file put on an empty track** (dropped on its lane, or copied in Explorer and pasted) makes the track that song, named after it, with its own tempo and key. | That is the same as adding the song as a track, but in the lane you chose. On a track that already has a song, a pasted file is a section from another song and takes that track's tempo, pitch and EQ, like any cross-song paste. |
| **Paste is always the selected track at the playhead.** It already was in code; what made it land elsewhere was that the selected track was invisible, a press on a knob or button did not select its track, a right-click did not select anything (so the menu's *Paste* went to whatever was selected before), and undo reset the selection to track 1. All four are fixed; the selected track has a blue bar. | Those were the ways "the selected track" could be something other than the one he was looking at. |
| **Ctrl+V pastes files copied in Explorer** if they were copied more recently than anything copied in Benchmix, judged by Windows' own clipboard counter. | "Pasting audio" read literally. Most recent copy wins, wherever it was made. The clipboard is only ever read. |
| **A track's origin is not a wall.** Pasting, dragging or looping before where a track's song was first dropped moves the origin back and every section forward by the same whole number of frames. | Exact to the frame at the track's original tempo; within one frame (about 20 µs) at any other, because an origin cannot sit on a whole output frame and a whole track frame at once. Joins are untouched either way. |
| **A dropped section overwrites** what is under it, like paste. **A trim stops at the next section.** | Moving or pasting puts something somewhere new; trimming adjusts one section and must never change another. Both are one undo step. |
| **Snapping priority:** another section's edge, on any track, within 10 px; otherwise bar lines, as before; Ctrl turns both off. *(2026-10-11: the bar lines are gone; section edges and Ctrl are unchanged.)* Either end of the dragged section can be the one that snaps. Trims snap to section edges only, as trims never snapped to bars. | Bars were already the default ("Snap clips to bars by default, Ctrl to override", 2026-09-16). Section edges take priority because when one is that close, it is what is being aimed at. Across tracks this lines a vocal up against a beat on another deck. |
| **Loop mode builds ordinary sections** — the same repeats Ctrl+D makes — not a new "looped clip" object. | It reuses the hand-built loop path the engine already plays, needs no file-format change, and every repeat can still be edited on its own. |
| In loop mode the edge moves in **whole repeats**; **Ctrl** gives part of a repeat. A part-repeat on the right is the section's head, on the left its tail, so the loop stays in phase. | Whole repeats is the loop equivalent of snapping; Ctrl is "manual granular control" everywhere else. |
| **A run of repeats is one loop.** Grabbing the outer end of an existing loop lengthens or shortens all of it. The edge *between* two repeats is not a loop handle — pressing there moves that repeat. | Otherwise grabbing the end of a four-repeat loop and dragging back would start a new loop of the last repeat and never shrink the old one; and grabbing an inner edge would collapse every repeat beyond it the moment the drag began. |
| Loop repeats overwrite what they run over, but only when the drag ends there: pulling the edge back during the same drag gives it back. | Same rule as Ctrl+D, without punishing a drag that overshoots. |
| Labelled **LOOP MODE**, with a tooltip saying it is not the Shift-drag listening loop. | Daniel called it "loop" mode. CLAUDE.md already warns that "loop" means two things; this is the hand-built kind. |
| Every drag (move, trim, loop) is now **one undo step**. | Clip drags were not undoable at all before — the drag code never recorded an undo point. |

## 2026-09-24 — defects found while building it

All fixed, each with a regression test named for what was wrong.

1. **Every loop repeat clicked, on every playback path.** A butt join — two sections back to back
   that do not continue each other, which is every repeat of every hand-built loop — was meant to be
   crossfaded against the outgoing section's continuation. Measured on a 440 Hz sine looped at two
   points half a cycle apart, the largest sample-to-sample jump at the join against a natural 0.03:

   | Path | Before | After |
   | --- | --- | --- |
   | untouched track | 0.85 — no crossfade at all; the condition excluded the default mode even when the stretcher is bypassed | 0.028 (steady 0.028) |
   | LINK on | 0.99, 65 frames *after* the join — the fade was read from the raw file, ignoring the EQ's delay, so it added a second discontinuity | 0.028 (0.028) |
   | LINK at +5% | 0.95 | 0.030 (0.030) |
   | pitched (phase vocoder) | 0.76 — no crossfade; "the stretcher's own overlap-add" did not cover it | 0.031 (0.028) |

   The fix renders five milliseconds of the outgoing section's continuation through the real
   stretcher and EQ at the join, before re-priming for the incoming one, and crossfades the two —
   carrying the fade across render blocks so preview and export agree. `JoinTests`. (Measured
   first at 440 Hz; the "After" column is the final 431 Hz run — see item 6 for why that matters.)
2. **Splitting a section that had been moved or trimmed could leave a one-frame gap** instead of a
   seam, so the split point got two declicks — an audible split. Positions were summed in seconds
   and then rounded; the engine rounds each part. All placement is now whole frames.
3. **Ctrl+D on a moved section could start the repeat a frame or two late**, the same way — a hole
   at every repeat.
4. **A track with every section deleted came back holding the whole song** when the project was
   reopened: the version 1 migration treated any track with no sections as a version 1 track.
5. **Pasting before where a track's song began landed at the track's start**, not at the playhead.

Then an independent adversarial review of the change, told to reproduce anything it reported,
found seven more; two further were found while fixing those. All fixed, all with tests that fail
when the fix is taken out:

6. **On a pitched or stretched track, the first 65 frames after every jump were the wrong audio**
   — older than today. The EQ, which sits after the stretcher, was primed with the raw file from
   where the read position stood, 150 ms ahead of what the stretcher emits next. At a loop join
   that was a step of 0.08–0.15 against 0.03; it also hit the first moment after any seek,
   including the very start of a render. The first join tests missed it because they used 440 Hz,
   and the stretcher's 7200-frame delay is exactly 66 cycles of 440 Hz. The prime is now rendered
   through the real chain (`TrackVoice.Reposition`); the untouched path reads exactly the samples
   it always did. `JoinTests` now use 431 Hz and eight paths.
7. **Snapping a section's end near 0:00 pushed the whole track later.** The section would have had
   to start before 0:00; the origin could not move back that far, and every section moved instead.
   Such a snap is now not offered, and moving a track's origin never goes past 0:00.
8. **Grabbing an existing loop again dropped its fade** — the repeat holding the loop's outer fade
   is rebuilt by the drag, and the copies took the section's own fade, which inside a loop is zero.
9. **Removing a track, or pressing undo, while a song file was loading onto it** left a voice in
   the mix with no track on screen: audio in every render that could not be muted.
10. **At some tempos a join got no crossfade** — about one join in forty at 0.7×, where
    `ceil(T / speed)` and the render loop's `floor` disagree by a frame in floating point.
11. **Losing the mouse mid-drag** (Alt+Tab, a dialog) skipped the drop, which is where overlap is
    resolved, so a dragged section could be left sounding on top of another.
12. **Pasting several files where one failed** left the ones that worked undrawn and unsaved.
13. **A fade on one side of a join left the other side with no declick** — it started or stopped
    dead on the sample it was cut at, a step of 0.42–0.43 on the test signal.
14. **Trimming the end of a section shorter than 50 ms** could push it into the section after it.

A second review, of just those fixes, found four problems in them. Fixed, each with a test that
fails when the fix is taken out:

15. **The same trim problem at a section's start** — the mirror case was missed, and the start could
    be pushed into the section before it or before the start of its file. A trim now never makes a
    section shorter than it already is if that is under 50 ms, and never forces it to change.
16. **Seeking primed the phase vocoder on the calling thread** — the UI thread, on every pixel of a
    ruler scrub: about 1.75 ms per pitched track per move, and a way to be inside the native
    stretcher while the audio thread was too. A seek now only records where to go; the audio
    thread does the work at the start of its next block.
17. **An edit landing on the very block that starts a join** dropped the voice's binding, and that
    join got neither the crossfade nor a declick (0.97). The binding now carries across any edit
    that did not change the section being played — which also stops every edit made during
    playback re-priming it — and when the outgoing section *did* change, its state is rebuilt so the
    join is still crossfaded (0.045 against 0.028 on the phase vocoder; exact elsewhere).
18. **A song file too short to place still took over the empty track** it was aimed at.

One thing the review found is older and not part of this change, so it is recorded rather than
fixed: on the phase-vocoder path the output differs slightly with the size of the blocks it is
pulled in, even with no joins, so live preview and the rendered file are not bit-identical there.
It is flagged as its own task.

## 2026-09-24 — verification runs must not interrupt him

Daniel, 2026-09-24, verbatim: *"is there a way that you can work on this without interrupting
whatever else i'm doing on my computer? I keep getting interrupted by you opening and closing the
app, playing songs, etc."* — and, when that was first read as "stop": *"no no, i dont want you to
stop. I was just asking if it was possible"*.

| Decision | Why |
| --- | --- |
| Every scripted run (any verification switch, or `--autoclose`) is invisible: off every monitor, no taskbar button, never activatable, no dialogs, no audio device except `--playtest`. | Minimising left a taskbar button per run, and closing a run's unsaved project put a dialog on his screen. The runs still need a real, laid-out window — the probes photograph it and the gesture test aims at its lanes — so it is placed where nobody is looking rather than not created. |
| The button checks moved inside the app (`--buttontest`); `ui-verify.ps1` no longer drives the window from outside with UI Automation. | Measured: the outside UI Automation client made Windows activate the hidden window at its first call, despite `WS_EX_NOACTIVATE`, taking his keyboard for about 30 seconds. The buttons' own automation peers, called from inside, exercise the same controls and activate nothing. |
| Both the app and the scripts check that each run stayed out of the way, and a run that did not fails. | "It should be invisible" is an assumption; the watch is what caught the activation above. |
| A mistyped switch is refused before any window exists. | Otherwise it quietly became an ordinary session: on screen, taking focus, on the real audio device. |

## 2026-10-03 — several sections at once, the Delete key, tooltips

Daniel, 2026-10-03, verbatim:

> "please add:
> * tooltips w/short explanations over buttons
> * ctrl+click to select multiple sections of audio
> * shift+click to select a larger range of audio sections
> * pressing delete key while one or more sections is selected deletes them"

| Decision | Why |
| --- | --- |
| **Shift+click selects every section wholly inside the time between the section clicked before (the anchor) and this one, on every track from the one to the other.** Wholly inside, not merely overlapping. The anchor stays put, so a further Shift+click measures from the same place; the section just clicked becomes the one commands act on. | Two short pieces on one track must not sweep up the whole song on the track between them. "Wholly inside" is exactly what the eye sees between the two clicks. Either click order. |
| **Ctrl+click toggles on release, not on press**, and only if the pointer did not move. | Ctrl held through a drag already means "place it freely" (2026-09-24), and that drag has to start from the same press. |
| **Delete is handled in code with a focus check, not as a KeyBinding.** It acts only when the keyboard focus is not in a text box, and only when a section is selected — no fall-back to the section under the playhead. | The 2026-09-16 rule that every editing key is Ctrl-modified exists because a bare KeyBinding fires inside the PROJECT BPM box. Delete is the key he asked for, so the rule is kept by other means: the box keeps its Delete, the timeline gets it everywhere else. No fall-back, because a stray Delete with nothing selected must remove nothing. The menu's *Delete section* keeps its fall-back. |
| **Delete removes every selected section as one undo step**, leaving the gaps. Cut, copy, repeat and the drags still act on one section — the one clicked last. | Multi-section cut, copy and drag are a larger feature he did not ask for. Keeping "the one clicked last" as the target leaves every existing command meaning what it did. |
| A right-click on one of several selected sections keeps the selection and makes that one the target. | So the menu's *Delete section* removes them all, while its *Cut* and *Copy* act on the one under the pointer. |
| Loop mode's handles follow the first selected section on a track. | Loop mode is a one-section gesture; a plain click selects one. |
| Every button, knob and slider has a tooltip, and `--gesturetest` fails if one is missing. | Asked for; the check is what keeps it true as buttons are added. |
| **A real press on a lane, the ruler or a track's strip takes the keyboard away from a text box** (`Keyboard.Focus(this)`), never in a scripted run. | Otherwise, after typing a tempo, Delete kept going to the PROJECT BPM box: a click on a lane moves no focus in WPF, so the key never reached the timeline until a button or knob happened to be clicked. The same press now commits the typed tempo, which its binding only does on losing focus. Scripted runs are excluded because asking Windows for the keyboard would activate the hidden window — the one thing they must never do. |
| **Delete is refused while a section is held under the mouse**, armed or dragging, and Cut likewise during a drag. | Deleting the held section left the drag holding one no longer on the track; the drop then cleared whatever was under its ghost, or put the deleted section back on another track. |
| **Delete under an open menu is left to the menu**, and a held key counts once. | A right-click opens the menu and gives it the keyboard; Delete then removed the section under the menu while the menu's own *Delete section* fell back to the playhead. |
| **A section whose file is missing is never in a Shift+click range.** | It has no length, is not drawn and cannot be clicked, so Delete would have erased the missing song's arrangement for good once the file was back. |
| **The right-click menu acts on the section right-clicked**, not the first selected one on the track. | With several selected, *Fade*, *Trim* and *Move section to playhead* read the first selected section and changed the wrong one. |

An adversarial review of the change (five lenses, every finding handed to a skeptic to refute) found the five defects in the last five rows, all fixed the same day, each with a `--gesturetest` check or a unit test that fails without the fix. It also found that commands acted on the section clicked *first* after a Shift+click while the README promised the one clicked *last*; the anchor and the target are now two different things, as the first row says.

## 2026-10-03 — defects found while rewriting the user guide

Daniel, 2026-10-03, verbatim: *"the split vocals instrumental feature does nothing."* and *"I don't
need music lessons on how to make songs mix nicely. I need the How To section to give click here
click there instructions."* Writing click-by-click steps meant checking what every click really
does, and that turned up defects the guide would otherwise have had to warn about mid-recipe.

| Decision | Why |
| --- | --- |
| **Split into vocals + instrumental is a `Click` handler**, like every other item on the lane menu. | Its command was bound through `RelativeSource AncestorType=Window`, which a ContextMenu cannot see. The item drew enabled and did nothing, from the day it shipped. |
| **Undo and redo leave the playhead and the listening loop where they are.** They change the edit, not where you are listening. PROJECT BPM, MASTER and LINK still come back with the snapshot, as before. | Reloading the snapshot rewound to 0:00, so the split or paste after any Ctrl+Z landed at the start of the song; and a loop changed since was put back in the engine while the band on screen showed the new one. |
| **An undo step that changed nothing is skipped.** | A snapshot is taken before a command knows whether it will change anything, so a refused command left an identical step behind and the next Ctrl+Z seemed to do nothing. |
| **Match BPM, Match key and Reset are one undo step each.** | Each knob snapshots itself before moving, so Reset left six steps and one Ctrl+Z restored only the last knob; undoing Match key left LINK off. |
| **An undo or redo marks the project changed.** | Undo after saving left no `*`, so closing discarded the undone state without asking. Conservative: an undo back to exactly the saved state also shows `*`. |
| **New clears the selection and does not mark the project changed.** | The selected track survived New as a track no longer on screen: Ctrl+V and Ctrl+I reported success on it while nothing appeared, and songs added next were never selected. Setting PROJECT BPM back to 0 after clearing the flag left a `*` on a blank project. |
| **Clear trim uncovers the song in place**, as far as dragging each end outward could: to the start and end of the file, stopping at the sections either side. | It reset the source range in place, sliding the audio to start the song at the section's left edge and running it over every later section — two sections at once on one track, which is not representable. |
| **Not changed:** splitting a track that holds a section pasted from another song. The stem tracks remap every section to this song's stems, so that section plays the wrong audio. Documented in the guide as a known problem. | The right behaviour is a choice (drop the section from the stems, keep it unseparated, or separate its song too), not a fix. |

Each fix has a check that fails without it: `MenuWiringTests`, `UndoSkipsAStepThatChangedNothing`,
`ClearTrimUncoversTheSongInPlaceAndStopsAtTheSectionsEitherSide`, and the undo/Reset/New checks at
the end of `--buttontest`, each run against a staged build with the old code first.

## 2026-10-11 — bar snapping off

Daniel asked for seven features on 2026-10-11, and when asked how a tempo ramp should be set up
he added, verbatim: *"Secondly I don't want bars, and come to think of it, please disable bar
snapping (but retain the snapping of clips next to each other to avoid overlap and gaps. Retain
ctrl+drag for me to override that though)."* He also asked that the work be done one feature at a
time, verbatim: *"I'd like to approve you to move forward on one feature at a time, so I can
monitor and manage token usage"*. This is the first one; the others are recorded as they land.

| Decision | Why |
| --- | --- |
| **Nothing snaps to bar lines.** A dragged section snaps to another section's edge (either end, any track, within 10 px) and otherwise lands exactly where it is let go. Ctrl still turns snapping off. Trims, loop repeats and "Move section to playhead" were never bar-snapped and are unchanged. | His words. Edge snapping is what keeps sections from leaving gaps or overlapping, which he asked to keep. |
| **The ruler still draws bar lines** from PROJECT BPM, as a guide only. | He asked for bar *snapping* to go. Whether the lines should go too is a question for him, not a default to take. |
| **PROJECT BPM is unchanged.** It still seeds from the first track, sets Match BPM's target and spaces the ruler's bar lines; it no longer affects where anything lands. | The same answer: *"I don't want any project bpm I want to keep full independence. Keep the bpm info exactly as it is."* — read as "do not tie the new features to it, and leave what exists alone". |

## 2026-10-11 — a click of the scroll wheel stops

Daniel, 2026-10-11, verbatim: *"5. clicking the mouse 3 button (clicking the scroll wheel) stops
play"*. Asked what it should do, he chose the offered option "Stop, jump back", which was described
to him as: "Stops, and the playhead jumps back to where you started playing (for example, where you
double-clicked). Press Space to hear the same bit again." (The option's words, not his.)

| Decision | Why |
| --- | --- |
| **A wheel-click anywhere in the window stops and goes back to where play last started.** "Started" is where play was last started from — a double-click, a click on the ruler, or Space after a pause — or was moved to while playing. The ■ button still goes back to 0:00. | His choice. Space after a pause counts as a start because that is where he would want to hear from again; until this change only a seek set the return point, so a wheel-click after Space resumed went back to the seek before the pause. |
| **It works in the middle of a drag**: playback stops and the section being dragged carries on to its drop. | The click is caught by the window before anything under the pointer, and used up there, so nothing else acts on it. |
| **An undo while playing keeps the return point.** | Undo re-seeks to stay where it is, which would otherwise have made the undo moment the place to go back to. |
| **Stop waits for the sound card's block in flight before seeking back.** | Seeking at once let a block already being rendered finish afterwards and move the playhead on by its own length, so the jump back would have landed about 10 ms late. Not measurable offline; the real-device check below landed exactly, 8 of 8. |

## 2026-10-11 — double-click to play from there

Daniel, 2026-10-11, verbatim: *"6. double clicking anywhere starts play from that location of the
playhead"*. Read as "the playhead goes to where I double-click, and plays" — what a double-click on
the ruler already did.

| Decision | Why |
| --- | --- |
| **A double-click on a lane — on a section or on empty space — on the ruler, or in the empty room under the last track plays from the point double-clicked.** Already playing, it jumps there and keeps playing. | "Anywhere" on the timeline. The ruler already worked this way; the lanes did nothing on a double-click. |
| **Not on a track's strip.** A double-click on a knob still resets it; the name will be where renaming starts (feature 3 of the seven). | Those already mean something, and the strip has no point in time to play from. |
| **The second press of a double-click does nothing else**: it starts no drag and no trim, so a hand that moves before letting go moves nothing. | Otherwise double-clicking to listen would nudge the section or trim its edge. |
| **Shift or Ctrl held makes it two ordinary clicks**, which do not play. | As on the ruler, where Shift+double-click has always set a listening loop; and a Ctrl+double-click toggles a section twice, leaving it as it was. |
| **The empty room under the last track now takes clicks.** A double-click there plays, Alt+click moves the playhead, a plain click lets go of the selected sections (as a click on empty lane space does). | It used to ignore every click, so "anywhere" stopped at the last track. |
| **For now the first click of a double-click still does what a click does**: on a section it selects just that section, in a gap it clears the selection. | Keeping a group selected through a double-click belongs with dragging a group, which is still to come. |
