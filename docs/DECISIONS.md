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
| **Delete removes every selected section as one undo step**, leaving the gaps. Cut, copy, repeat and the drags still act on one section — the one clicked last. *(2026-10-11: dragging any selected section now moves them all — see that entry. Cut, copy and repeat still act on the one clicked last. 2026-10-12: to cut or copy several sections and the gaps between them, mark a stretch — see that entry.)* | Multi-section cut, copy and drag are a larger feature he did not ask for. Keeping "the one clicked last" as the target leaves every existing command meaning what it did. |
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
*Superseded 2026-10-28: a click now pauses exactly where the playhead is, and the jump back to where play started is the double-click. See "2026-10-28 — the wheel click pauses in place" below.*

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

## 2026-10-11 — renaming a track, and Space as a bare key

Daniel, 2026-10-11, verbatim: *"3. rename tracks"*.

| Decision | Why |
| --- | --- |
| **Double-click a track's name, or right-click its strip → *Rename track*.** The name becomes a box with the old name selected. **Enter**, or a click anywhere else, keeps what was typed; **Esc** keeps the old name. One undo step, and only when the name really changed. | Double-clicking a name to edit it is what Explorer does. The menu is there for anyone who does not think to double-click. |
| **A blank name is refused** and the old one kept; spaces around a name are trimmed, line breaks become spaces, and a name stops at 80 characters. | A blank track is a lane nobody can tell apart in a status message, and a saved blank name is replaced by the file's name on reopening anyway. |
| **The box edits a draft, never the name itself.** | Any edit elsewhere refreshes the row from the project, which would wipe a box bound to the name mid-word. |
| **Switching to another program, or the box's own right-click menu, does not end the renaming.** Only the keyboard moving somewhere else in this window, or a press outside the box, keeps the name. | Alt+Tab away and back must not commit half a name; nor must right-click → Paste inside the box. |
| **A name still being typed when an undo rebuilds the tracks is dropped**, not written in afterwards. | It would be written into a track that is no longer the one on screen. |
| **A name he types that looks like "Track 3" is still treated as a placeholder**: the first song put on that empty track renames it after the song. Any other name he types is kept. | That is the existing rule for empty tracks (2026-09-24); telling his "Track 3" from Benchmix's would need a new field in the file for a case that hardly arises. |
| **Space is no longer a KeyBinding.** Like Delete it is handled in code, and only when the keyboard is not in a text box or a menu, so a track's name can have spaces in it. The window now sees Space before a focused button does: after clicking a button, Space plays or pauses rather than pressing that button again. | A bare KeyBinding on the window fires from inside a text box — the rule CLAUDE.md records for Ctrl shortcuts. That a focused button used to take Space is read off WPF's key routing, not measured. |
| **A held Delete or Space inside a text box now repeats as typing does.** | The repeat used to be swallowed before the focus was checked, so holding Delete in PROJECT BPM removed one character. |

Found on the way: in a scripted run the gestures read the **real** keyboard's Ctrl, Shift and Alt
whenever a check had not scripted them. Run while Daniel was typing, a Ctrl he happened to be
holding turned snapping off, made double-clicks into two Ctrl+clicks and left Space unhandled —
13 gesture checks failed at once, on a build that had just passed. A scripted run now never reads
the keyboard; the same build then passed all 115.

## 2026-10-11 — dragging a track up or down

Daniel, 2026-10-11, verbatim: *"4. click and drag tracks to reorder them up and down"*.

| Decision | Why |
| --- | --- |
| **Drag a track by its name row — the ≡ grip, its colour or its name — up or down.** Past 4 px it is a move; a blue line shows where it will land; the move happens once, when it is let go. One undo step. | The knobs and buttons in the strip already use a press and a drag for themselves, so the name row is the one free handle. The rows stay put until the drop, because moving a row while it holds the mouse would lose the mouse. |
| **The project's order and the order on screen move together** (`ProjectSession.MoveTrack` and the rows' `Move`). | Shift+click ranges, saving and undo all read the project's order; if only the rows moved, a range would sweep up the wrong tracks and the next undo would put them back. |
| **The engine is not touched.** Each track's voice is found by its id, so the mix plays on exactly as it was. | Moving a row is not a change to the sound. After the next undo or reopen the voices are rebuilt in the new order and summed in that order, which in floating point can change the last bit of a sample and nothing more — measured as within 2.4e-7 on three tracks. |
| **Refused while a section is held under the mouse**, or while something is loading. | The section's drag is part-way through putting it somewhere. |
| **No auto-scroll**: the line only reaches the rows on screen. | As for every drag today. |
| **Match key still matches the first other track in the list**, which is now whichever track is at the top. Its tooltip says so. | The behaviour is unchanged; what "first" means is now his to choose. |

## 2026-10-11 — the selection box

Daniel, 2026-10-11, verbatim: *"2. Left click + dragging the mouse should create a classic windows
square lasso thingy (like onwindows desktop that allows you to drag a box to select multiple desktop
icons) that allows me to select clips"*.

| Decision | Why |
| --- | --- |
| **The box starts on empty space** — a gap between sections, an empty track, before or after a song, or the room under the last track. | A press on a section already means "drag it". On the desktop, too, the box starts where there is no icon. |
| **It selects every section it touches, even partly**, on every track it covers, live as it grows. | His reference: the Windows desktop selects every icon the box touches. Shift+click keeps its own rule — only what is wholly inside — because two short pieces must not sweep up a whole song on the track between them; the box is drawn, so what it touches is in plain view. |
| **Ctrl or Shift held as it starts adds to the selection**; otherwise it replaces it. | As on the desktop. A Ctrl- or Shift-click on empty space already left the selection alone. |
| **Afterwards the earliest section on the topmost track is the one commands act on**, and where the next Shift+click measures from. | Cut, Copy and Ctrl+D act on one section; the top-left one is the one the eye starts from. |
| **A click on empty space still lets go of the selection — now when the button is let go, not when it is pressed.** | The press might be the start of a box. The click ends in the same state as before. |
| **The box only covers what is on screen**; it does not scroll the view. | Nothing in Benchmix auto-scrolls yet; selecting rows nobody can see would make Delete act on them unseen. |
| **Delete is refused while the box is being dragged, and a right-press then changes no selection.** | What it would delete is still changing; and a right-press with the box still held — to pan further, say — used to swap the box's sections for the one under the pointer. |

## 2026-10-11 — dragging several sections at once

Daniel, 2026-10-11, verbatim: *"1. While having multiple clips selected, I should be able to click
any one of them and then drag the group around. If all are just one a single track, then i should be
able to drag left, right, or onto different tracks. If multiple clips span different tracks, then
they should just be able to be dragged left or right."*

| Decision | Why |
| --- | --- |
| **A press on one of several selected sections keeps them all; let go without moving and it is just that one.** A press on an unselected section still selects only it, at once. | Explorer's rule, and the only way the same press can start either a group drag or a click. |
| **The rule is fixed when the drag starts:** all on one track, the group follows the pointer sideways and onto another track, together; spread over several, it goes sideways only, and a pull straight up or down does not start it. | His words. A vertical twitch on a group that cannot change track would otherwise start a drag that moves nothing and swallows the release that should have been a click. |
| **On each track every section of the group moves by one whole number of the track's frames.** Across tracks, each turns the same move in time into its own frames. | Seams and butt joins between them stay exactly that — the seam rule — and two sections lined up across decks stay lined up. |
| **A group moved onto a track at another tempo keeps its spacing in the song's own frames**, so it plays at that track's tempo and spreads or tightens on the timeline, as one dragged section's length does. | Keeping the timeline spacing instead would break the seams between them. |
| **The group stops at 0:00 as a whole**, never squeezed; dragged before a track's start, the start moves back, as for one section. | Squeezing would change the spacing he lined up. |
| **It snaps as one**: any edge of any section in it, to the edges of other sections; never to its own sections' old places; Ctrl lets go. | The same rule as one section. Its own old edges would make it jump back onto its ghost. |
| **The drop overwrites what is under it, never its own sections. One undo step.** | As for one section. |
| **Dragged onto an empty track, the track takes the group's tempo and pitch**; passed over, it is put back as it was. | As for one section. |
| **A drag on a section's edge trims that one** and lets go of the rest; a click there, without dragging, is a click on that one. On one of several selected sections the edges are at most a quarter of the section wide, so a short one can still be grabbed by its middle. A section on its own keeps its full 7 px handles. | Trimming a group was not asked for. At 7 px a side, a selected section under 14 px wide — 0.5 s at the default zoom, a short loop repeat — was all edge and could never be dragged with its group. Single sections keep their handles so trimming and LOOP MODE work as before. |
| **A group spread over several tracks pulled straight up or down does not move, and that is not a click either**: the group stays selected, and the status bar says it moves sideways only. | Otherwise the release counted as a click and threw away the group he had picked. |
| **Ctrl held as a group is dragged frees it from snapping**; Ctrl+click without moving still adds or takes one away. | Ctrl means "place it freely" everywhere. |
| **While a section, a box or a track's name is held under the mouse — from the press, not only once it moves — Ctrl+I, Ctrl+V, Ctrl+D, Ctrl+X, Ctrl+Z, Ctrl+Y, Delete, Clear trim and removing a track wait** ("Let go of the section first."). A group drag waits while a file is loading or a song is being split. | A group drag puts the picture back before every pointer move, so an edit made meanwhile would be silently undone by the next move; an undo would rebuild every row under a press that is about to become a drag. |
| **A double-click on one of several selected sections — its edges included — plays and keeps them all selected**; so does a double-click on empty space. | The first click's release had collapsed the group (or let go of the selection); the second press puts that back. Double-clicking to listen should not throw away a selection. |

## 2026-10-11 — tempo ramps, part 1: the sound engine

Daniel, 2026-10-11, verbatim: *"7. This one may be more difficult: I want to be able to have a
track's tempo change over a span of time. THis would allow me to, for example, mix two songs that
are 110 vs 120bpm. I'd slowly increase the first song to 115bpm, then mix into the next song which I
have start at 115, and then slowly increase that song up to its true bp[m of 120."*

And, asked how he wants to make one, verbatim: *"I don't want any project bpm I want to keep full
independence. Keep the bpm info exactly as it is. [...] As for how to change bpm, I'd like to just
click within the track, first click puts a dot indicating start of ramp, second click puts the end
point, and a yellow line connects them indicating up or down. Setting bpm should have my type it
in. Whatever the current bpm of the clip is will be filled in automatically at the start point of
the ramp. Then I'll type in the bpm for the end point. If it goes down, then first point goes on top
line of track and second point on bottom with yellow line connecting and vice versa"*.

He asked for it to be built and approved one piece at a time; this is the engine and the project
file. Clicking, typing and drawing ramps is part 2, below.

| Decision | Why |
| --- | --- |
| **"No automation curves" is reopened, by his request, for tempo only.** A track's tempo can change in straight lines between points; nothing else moves on its own. | His item 7. Nothing else was asked for, and this is still not a DAW. |
| **A ramp belongs to one track and sits on the timeline, not on the song.** It runs from one moment to another; a section moved under it plays at the tempo there. | He clicks *within the track* at a point in the mix. Each track keeps its own tempo, as he asked ("full independence"); nothing uses PROJECT BPM. |
| **The tempo goes in a straight line in BPM over the span, then holds** until the next ramp. | His example: "slowly increase" to 115 and stay there to mix into the next song. BPM is the song's tempo times the speed, so straight in speed is straight in BPM. |
| **The tempo at a ramp's start is never stored**: it is whatever the track plays there. Only the end tempo is. | His words: the current BPM "will be filled in automatically at the start point". Storing it would let the tempo jump where a ramp begins. |
| **The TEMPO knob, and the BPM on the strip, are the tempo before the first ramp.** The strip text is unchanged. | "Keep the bpm info exactly as it is." What the strip shows under a ramp is part 2's to settle with him. |
| **A track with any ramp goes through the stretcher (or LINK's resampler) for its whole length**, even where it plays at 1.0. | Switching between the untouched copy and the stretcher partway through would restart the stretcher, and its delay, mid-song. Consequence: such a track is no longer bit-identical to its file anywhere, ramp or not. A track without ramps is exactly as before. |
| **Positions under a ramp are worked out in closed form**, never by adding up speeds block by block. | A running sum drifts with the block size, so preview and export would place the audio differently. |
| **Ramps never overlap, are at least 0.1 s long, on whole frames, after 0:00, at 0.25×–4×.** Loading a project drops anything else. | A hand-edited or damaged file must neither crash the app nor play a tempo change nobody set up. |
| **Project files are now version 3.** Every save writes 3, so an older Benchmix refuses a file saved from now on rather than opening it. | An older build would open a ramped project, drop its ramps without a word and save over them. |
| **Ramps travel with the tempo**: an empty track that takes a dragged section's tempo and pitch takes its ramps too (and gives them back when only passed over); a paste onto an empty track takes them; the vocal and instrumental tracks from *Split* take their song's. | They are part of how that song plays. A stem without its song's ramp would drift out of time with it. |
| **Moving a track's origin leaves its ramps where they are** on the timeline. | Nothing he placed should move. |

## 2026-10-12 — tempo ramps, part 2: making and changing them

His description, quoted in full under part 1, is followed as written: a first click puts a dot at
the tempo the track is playing there, a second puts the end, he types the tempo to reach, and a
yellow line joins them from the top edge to the bottom when it goes down and the other way when it
goes up. Everything else here is a default he can change.

| Decision | Why |
| --- | --- |
| **RAMP MODE** is a toggle beside LOOP MODE, **Ctrl+R**. Off by default, not saved. Turning one of the two on turns the other off. | Both change what a press in a lane does; with both on, one of them would be dead. Ctrl+R was free, and every editing shortcut is Ctrl-modified. |
| **In ramp mode a press in a lane acts on ramps only**: sections are not selected or moved, and there is no selection box. Alt+click still moves the playhead; the ruler and the room under the tracks keep their double-click to play. Ramps are drawn whatever the mode. | Nothing competes with the dots. |
| **The dot is placed on the press**, at the tempo in effect there, shown on its label. | His words: the current BPM "will be filled in automatically at the start point". |
| **A second click left of the first swaps them**; a click on another track starts the ramp there instead; a first click inside an existing ramp is refused; a second click past the next ramp stops at its start. Less than 0.1 s, or 4 px, between the clicks: "click further along". | Defaults. Two tempo changes at once on one track are not representable. |
| **The typed tempo must be a number between a quarter and four times the song's own tempo** — what the engine can play. Enter keeps it; Esc drops the new ramp (or, when retyping, keeps the old tempo). Clicking anywhere else keeps it if it is valid and drops the ramp if not; a click on a lane that does this does nothing else. Leaving the app is not clicking elsewhere. | As the rename box behaves. A click that was only meant to finish typing must not also start a new ramp. |
| **The box sits in the lane beside the end dot**, not in a popup window, and a scripted run never gives it the keyboard. | A popup is a window of its own, which a verification run would put on his screen. |
| **Escape is a third bare key**, alongside Delete and Space: handled in the window, only while a ramp is being placed or retyped, and not from another text box. | Escape is not a KeyBinding, for the same reason as the other two: a bare KeyBinding would fire from inside a text box. |
| **Drawing:** yellow, 2 px; down from the top edge to the bottom when slowing, up from the bottom when speeding up, level across the middle within 0.05 BPM; the tempo at each dot labelled. While its tempo is typed the line is dashed and follows what is typed. | His description; the level line and labels are defaults. |
| **Changing a ramp:** drag either dot sideways; click the end dot to type a new tempo; a click on the start dot says why its tempo cannot be typed. Right-click on a ramp, in any mode: *Remove tempo ramp*. | He described making a ramp, not changing one. The start's tempo is never stored (part 1), so it cannot be typed. |
| **Dot snapping:** within 10 px to section edges on other tracks and to ramp ends on any track; the start dot also to its own track's section edges. Ctrl frees it. Never to bars. | A section after a ramp's start moves as soon as the ramp changes the tempo, so an end snapped to one on its own track would not stay on it. |
| **One undo step** for each new ramp, each dot drag, each retyped tempo and each removal. **Reset** removes the track's ramps in its own one step. **Match BPM** sets TEMPO — the tempo before the first ramp — and keeps the ramps, and the status bar says where they still go. | As for every other edit. |
| **While a dot is held** — from the press — Ctrl+R, Ctrl+L and every other edit wait, as for a held section. | The same rule, for the same reason. |
| **The BPM on the strip is unchanged**: the tempo before the first ramp. | "Keep the bpm info exactly as it is." |

## 2026-10-12 — a dropped section overwrites what it lands on: his choice

Trying out the 2026-10-11 features, Daniel, verbatim: *"i actually LOVE the existing functionality when I DO overlap a clip onto another, the clip i’m actively dragging overrides the portion of the clip i dragged it over onto, as proven when I then move the clips apart again and see that the underlying clip is now trimmed. So please, DONT REMOVE THAT…. i was thinking it was an unintentional function you created, so if you ever flag it as a bug, do not “fix” it or recommend fixing it."*

| Decision | Why |
| --- | --- |
| **A section or group dropped onto another section trims away the part it covered, permanently.** Dragging it off again later leaves the covered part trimmed; Ctrl+Z is the way back. | His words. It was built on purpose (2026-09-24: "the drop overwrites what is under it", because sections on one track never overlap), and he has now confirmed it is what he wants. It is not to be reported as a defect or changed. |

## 2026-10-12 — the wheel over the ruler moves the playhead; a double-click of it goes to 0:00

Daniel, 2026-10-12, verbatim: *"scroll wheel scrubs (if scrubs means just seeking back and forth on
the rule, i.e. fastforward or rewind"*. Asked where, he chose the offered option "Wheel over the
ruler" (offered as: over the ruler, wheel up moves the playhead forward, down moves it back, a small
step on screen per notch; over the tracks the wheel still scrolls them up and down). These are the
option's words, not his. Then, verbatim: *"double click mouse wheel brings playhead back to true
start point of project, also focuses screen back to true start point (0:00)"*.

| Decision | Why |
| --- | --- |
| **Over the ruler, a plain turn of the wheel moves the playhead**: up is later, down is earlier. Over the tracks the wheel still scrolls them up and down; Ctrl+wheel still zooms and Shift+wheel still scrolls sideways, over the ruler too. | His choice. Nothing he already uses changes. |
| **A notch is 20 pixels on screen**, so it is a fine step zoomed in and a big one zoomed out (half a second at the default zoom). | A fixed step in seconds is too coarse zoomed in and too slow zoomed out. |
| **While playing, it plays on from the new spot**; stopped, it just moves the playhead. It stops at 0:00 and at the end of the mix. | As a click on the ruler while playing does. |
| **The view follows the playhead** when the wheel takes it off either edge. | Otherwise turning the wheel loses the playhead off screen. |
| **A double-click of the wheel stops, and puts the playhead and the view at 0:00.** A single click still stops and goes back to where play last started. | His words. Its first click is a single click, so it has already stopped. |

## 2026-10-12 — joining sections back into one

Daniel, 2026-10-12, verbatim: *"need to be able to join two clips, especially after making a split.
sometimes i split to extract just a part of a song, and move it to a diff track, but then I just
want the original song to go back to normal. Alternatively, perhaps we could improve the actual
split functionality so I can instead just visually demarcate a start and end point within a track
and copy, or cut, and then paste that chunk elsewhere?"* Asked, he chose the offered option "Join
now, range later" — the option's words, not his: join first, marking a start and end to copy or cut
as its own feature afterwards.

| Decision | Why |
| --- | --- |
| **Ctrl+J, or right-click → *Join sections*.** Several selected on one track: those are joined. One selected: it and the section straight after it — or, failing that, straight before it — if that one lines up. | The pieces he wants back together are the ones either side of the piece he took out. Ctrl+J was free; every editing shortcut is Ctrl-modified. |
| **Pieces "line up" when the later one sits exactly where the song carries on from the earlier one**: the same file, and as far apart on the track as in the song — exact, in frames, as the seam rule is. | That is what "the original song" means for pieces of it. A loop's repeat jumps back in the song, and a piece that has been moved sits somewhere else; neither lines up. |
| **Joining fills any gap between them with the song again**, and pieces that were only split play exactly as before (measured: not one sample changes). The piece that was taken out stays wherever it was put. | "I just want the original song to go back to normal." |
| **Refused, and the status bar says why**, when they come from different songs, do not line up, or something else has been put in the gap between them. | Joining over something else would delete it without a word. |
| **The outer fades are kept**; one Ctrl+Z takes the join back. | As for every other edit. |

## 2026-10-12 — typing a knob's exact value

Daniel, 2026-10-12, verbatim: *"Any knobs that have numbered indicators showing the +/- adjustment....
give mne the ability to manually type in those numbers. will allow me to really fine tune things. Use
whatever level of granularity you recommend."*

| Decision | Why |
| --- | --- |
| **Double-click the number under a knob** to type it; a double-click on the dial still resets it. | The number is what he reads; a reset he already knows stays where it is. |
| **Granularity:** TEMPO as a percentage to 0.01% (about 0.01 BPM); PITCH in semitones to 0.01 (one cent); LOW, MID, HIGH and VOL in dB to 0.1, or KILL. | His "whatever level of granularity you recommend". A hundredth of a percent of tempo, a cent of pitch and a tenth of a dB are each finer than anyone hears; finer would only be noise in the numbers. |
| **TEMPO and PITCH now show two decimals** under the knob. | So what he typed is what he reads back. |
| **Out of range is refused, and the status bar gives the range**; a word is refused with an example. A unit typed after the number, and a Unicode minus, are fine. | A silent clamp would set something he did not type. |
| **Enter keeps it, Esc keeps the old value, clicking elsewhere keeps what was typed** if the knob can take it; leaving the app does not. Each typed value is one undo step. | As the name and ramp boxes behave. |
| **With LINK on, PITCH cannot be typed**, and the status bar says why. | Under LINK the pitch follows TEMPO. |
| **One box for every knob, in the window itself**, not a popup; a scripted run never gives it the keyboard. | A popup is a window of its own, which a verification run would put on his screen. |

## 2026-10-12 — from the review of everything after tempo ramps part 1

He asked for a review only when he says so (2026-10-12: *"cue me when it's time to begin it and ask
my approval"*), and said *"review"*. Decisions taken in fixing what it found:

| Decision | Why |
| --- | --- |
| **A box opened only to look changes nothing.** A knob's box, or a ramp's tempo box, left as it was filled in, keeps the exact value behind the rounded number it shows — and makes no undo step. A new ramp whose box is left as filled in ends at exactly the tempo it starts at. | Otherwise a look at TEMPO after Match BPM wrote back −8.82% for −8.8235% (about 7 ms of drift in three minutes), and an untouched ramp ended 0.04 BPM off and held it for the rest of the song. |
| **In ramp mode the second press of a double-click does nothing.** | It used to close the tempo box the first press had just opened, placing the ramp at its pre-filled tempo. |
| **A ramp clicked right to left is snapped for the ends it becomes**: the later click as the start, the first as the end. | An end must not snap to its own track's sections, which move once the ramp changes the tempo. |
| **Ctrl+L, like Ctrl+R, waits while a ramp's dot is held.** A right-click inside the ramp's box is the box's own. A right-click offers *Remove tempo ramp* only for the ramp under it now. | As already decided for held sections; the old index could name another ramp. |
| **A press on a knob's number turns it only after 4 px of movement.** | It is usually the first click of a double-click to type; a twitch then changed the value the box opened with. |
| **A knob's box closes when the rows are rebuilt** (New, Open, undo). | It was left floating over a knob that no longer existed. |
| **The wheel over the ruler counts from where the playhead is going**, not where the last screen refresh saw it. | While playing, a seek can still be waiting for the audio thread; turned fast, notches were lost. |

## 2026-10-12 — marking a stretch to copy, cut, delete or repeat

Daniel, 2026-10-12, verbatim (quoted in full under *joining sections* above): *"perhaps we could
improve the actual split functionality so I can instead just visually demarcate a start and end
point within a track and copy, or cut, and then paste that chunk elsewhere?"* He chose join first and
this afterwards, then said *"go for it, do the stretch thing"*. How it works was not discussed with
him; everything below is a default he can change.

| Decision | Why |
| --- | --- |
| **MARK MODE**, a toggle beside LOOP MODE and RAMP MODE, **Ctrl+M**. Off by default, not saved. Turning any of the three on turns the other two off. | A drag on a section already means "move it" and a drag on empty space draws the selection box, so marking needs a mode, as ramps did. Ctrl+M was free; every editing shortcut is Ctrl-modified. |
| **In mark mode a drag across a lane marks that stretch of that track**; a drag that starts on an end of the mark moves that end. Sections are never moved or selected, and there is no selection box. | His words: "visually demarcate a start and end point within a track". One track, as he said. |
| **A click puts the playhead there** (and selects the track); **Shift+click marks from the playhead to there**; a double-click plays, as everywhere. The mark stays when you click. | The playhead is where Ctrl+V pastes, so a click is how you say where it should go. Shift+click reaches past the edge of the screen, which a drag cannot (nothing auto-scrolls). A double-click to listen must not lose the mark. |
| **The ends snap**, within 10 px, to section edges on any track and to the playhead; **Ctrl frees them**. An edge on the same track is taken to its exact frame. Never to bars. | As every other drag. The exact frame means a stretch marked from one section's edge cuts exactly there and leaves no sliver. |
| **While there is a mark, Ctrl+C, Ctrl+X, Delete and Ctrl+D act on it**, from the keys and from the lane menu, whose items then say "marked stretch". With no mark they act on sections as before. Making a mark lets go of the selected sections. | The mark is the newest, most visible thing; two different things for one key to act on would be a trap. |
| **What is copied is the whole stretch, gaps included**, and a paste covers the whole stretch it lands on: a gap in it clears what was under it. | "What you marked is what you get." The positions inside it stay as he lined them up. |
| **A section cut by an end of the mark loses its fade on that side**; a section whose own end is inside keeps its fade. | As a split or an overwrite already does: a fade belongs to the outer edges. |
| **The cut ends are cut exactly**: cut a stretch and paste it straight back where it was and nothing changes — at 100% speed not one sample; on a stretched track, exactly the song split at the stretch's two ends. | The seam rule. Measured, `MarkedStretchTests`. |
| **Cut and Delete leave the gap**; the mark stays on it. **Ctrl+D puts a copy straight after the stretch** — on the very frame it ends, so the join is crossfaded like every repeat — **and the mark moves onto the copy**, so pressing again adds another. | Nothing ripples. Ctrl+D on a section works the same way (the repeat becomes the selection). |
| **After a paste in mark mode, the mark moves onto what landed**; outside mark mode what landed is selected — every section of it, so it drags as one. | It shows exactly what was put down, and Ctrl+X takes it straight back out. |
| **The mark is not part of the project**: not saved, not undone. It goes when mark mode is turned off; an undo leaves it where it is. It is kept in the track's own frames, so it stays on the same audio when TEMPO turns, and when something pasted before the track's start moves the track's origin. | It is a pointer at audio, like a selection. |
| **One undo step** for each cut, delete, paste and repeat. While the mouse is held down on a lane, Delete, Ctrl+X, Ctrl+D and the modes wait. | As for every other edit. |

## 2026-10-12 — four things from his testing

He tried every feature of the day and wrote back (2026-10-12). He chose the order,
verbatim: *"all 4 in that order"*.

**Opening a project from Explorer.** His words: *"opening a project by double clicking the .benchmix
file in explorer opens the app but gives error msg that it could not load the project."*

| Decision | Why |
| --- | --- |
| **A `.benchmix` on the command line opens as the project**, through the Open button's own code; songs passed with it are added to it. Only the first project counts. | Windows passes the double-clicked file to the exe, and it was being loaded as a song. One window holds one project. |
| **Benchmix does not register itself as the program for `.benchmix` files.** | He has already told Windows to open them with Benchmix. Writing to the registry changes his system for him. |

**An icon.** His words: *"please give benchmix shortcut a simple graphical icon so i know what i'm
clicking in windows."*

| Decision | Why |
| --- | --- |
| **A blue tile in the app's accent colour with a waveform on it**, white then ramp-yellow, drawn by `scripts/make-icon.py` at every size from 16 to 256 px and embedded in the exe. | His desktop shortcut already takes its icon from the exe, so nothing on his desktop needs changing. A waveform says "audio"; two colours say "two songs". |

**Following the sound output.** His words: *"when i start the app using one audio output (bluetooth
headphones) and then disconnect my headphones, i need to restart the app in order for the sound to
come out of the new output"*, and, asked, *"please do it."*

| Decision | Why |
| --- | --- |
| **Playback follows Windows' default output** (the multimedia one): when Windows changes it, or the stream dies with its device, the stream moves to whatever Windows uses now and carries on from the same frame, playing or not. | What he described. Windows has already decided where sound should go; Benchmix agreeing with it is the least surprising. |
| **With no output left, playback stops and the status bar says so; the next output to appear is taken without a restart.** Play also looks again. | Before, a lost device needed a restart. |
| **The mix keeps the rate it started at; an output at another rate gets a windowed-sinc converter on the way out**, live only. | Every position in a project is counted in the mix's frames, so the mix cannot change rate mid-session. Renders never pass through it, so preview and render are still one signal path. |
| **The status bar says where sound went**: `Sound now plays through Speakers (Realtek(R) Audio).` | So a switch he did not notice is not a mystery. |

**The wheel moves the playhead from anywhere.** His words: *"can we make it so i dont have to mouse
over the ruler? I'd like to be able to do it from anywhere my mouse is."* Offered two ways, he chose,
verbatim, *"this is preferable"* for: *"A plain wheel over the waveforms moves the playhead. Scrolling
up and down through tracks moves to the track controls on the left and the scrollbar."* (the
option's words, not his).

| Decision | Why |
| --- | --- |
| **Over the waveforms, the gaps and the room under the last track, a plain turn moves the playhead**, exactly as over the ruler. Ctrl+wheel and Shift+wheel there still zoom and scroll sideways. | His choice. |
| **Over the track controls on the left and the tracks' scrollbar, the wheel scrolls the tracks up and down, or turns the knob under it**, as before. | The other half of the option he chose. With a few tracks they all fit, so there was nothing to scroll — which is why he saw the wheel do nothing over the tracks. |

## 2026-10-12 — from the review of MARK MODE and the four fixes

He said, verbatim, *"Please review, yes."* Three reviewers and three skeptics went over
`6f2ca04..669aa4f..6bbcf5c`. Decisions taken in fixing what held up:

| Decision | Why |
| --- | --- |
| **A mark's end within 20 ms of a section's edge on its own track is moved onto that edge**, when it is let go and again by copy, cut, delete and repeat themselves. | An end that close cuts a sliver off, which a cut throws away but a copy left out; pasting back, or a repeat, then left a 10 ms gap with a declick either side. |
| **A paste of something copied from a song no longer in the project is refused** ("Copy it again"). | Its length was measured when it was copied. It cleared the span and put down a section with no audio: silence now, the song again after a reload. |
| **A moment with no output named leaves a working stream alone; an output back within 5 s of losing the last one carries on playing.** Pause or Stop in the meantime are kept. | While Windows changes outputs it can name none for a moment, or refuse to open the one it names. The song must not stay stopped for that — but sound starting by itself minutes later would startle. |
| **The message when playback stops for want of an output says it may be busy, and to press Play.** | The output can be connected but held by another program; then no notice comes when it is free. |
| **Song files pasted or dropped in MARK MODE get the mark**, as a pasted section does. | Left on the old place, Delete and Ctrl+X acted there instead of on the file just put down. |
| **In MARK MODE a press that loses the mouse is not a click**, and a click or a mark drag ends a knob's undo step. | As everywhere else on the lane. |

## 2026-10-18 — warp markers

Daniel, 2026-10-18, verbatim: *"i believe ableton has a functionality called "warping" that helps to
fine tune and perfect bpm matching, especially between electronically programmed songs vs organic
songs (which somewhat drift in exact bpm). I think you basically match the bpm as closely as possible
for starters, then you flag the beats that start to drift apart, and then you click warp to match
them up."* and *"you can see where I'm struggling to keep the beat synced up at around the 6 minute
mark. I'd like a functionality to fix that, and I'm thinking warping would be what i need?"* Told
that it would widen the one exception to "no automation curves", which until then was tempo ramps,
and asked "Do you want me to build warp markers?" (the question's words, not his), he answered,
verbatim, *"yes please."* How it works beyond his description was not discussed with him;
everything else below is a default he can change.

| Decision | Why |
| --- | --- |
| **"No automation curves" is widened, by his request, for a second kind of tempo change: warp markers.** Nothing else moves on its own. | His request and his yes, above. This is still not a DAW. |
| **A warp marker pins a point of a track's audio — a beat — to the moment it is heard. Between two markers the audio is spread evenly. Nothing before the marker on the left moves. After the last marker the track plays at its tempo again, so everything after it moves along with it.** | His words (2026-10-18): *"match the bpm as closely as possible for starters, then you flag the beats that start to drift apart, and then you click warp to match them up"*. Ableton's markers work this way. Carrying the corrected tempo on past the last marker instead would move the rest of the song by an amount that grows with its length. |
| **A marker sits on the audio, in the track's own timebase — not on the timeline as a ramp does.** TEMPO, Match BPM and ramps carry it along with its beat. | A marker is on a beat. Pinned to the timeline it would stay put while its beat moved away. |
| **A marker belongs to the track, not to a section** (unlike Ableton). A section moved past a marker is warped by where it now is. Warp after arranging. | Sections are counted in the track's frames, one to one with the song. Warping a section's own audio would change every section's length in frames, and with it every seam and butt join. Ramps already work this way. |
| **The speed jumps at a marker**, by the ratio of the spreads either side; the position never jumps. The vocoder is fed each side of a marker separately. | As in Ableton: it is what makes the beat land exactly where it was dropped. Fed across a marker in one go, the vocoder would smear the jump over a block, by an amount that depends on the block size. |
| **The audio between two markers is never spread past half or twice its tempo**, nor is the audio from the track's start to the first; a drag stops there and says so. | Warping is for a beat that drifted, not for changing a song's tempo. |
| **A marker nobody has moved changes nothing, to the sample.** A track whose markers are all unmoved plays through the very same tempo map, so an untouched one stays bit-identical. A track with any moved marker goes through the stretcher for its whole length, as a ramped one does. | Putting a marker down is flagging a beat; it must not change the sound. |
| **WARP MODE**, a toggle beside LOOP MODE, RAMP MODE and MARK MODE, **Ctrl+W**. Off by default, not saved. Turning any of the four on turns the other three off. | A press on a section already means "move it", so warping needs a mode, as ramps did. Ctrl+W was free, and every editing shortcut is Ctrl-modified. |
| **RAMP MODE's "turns the other off" and MARK MODE's "turns the other two off" (both 2026-10-12, above) now read "the other three"**: LOOP, RAMP, MARK and WARP MODE are never two on together. | All four change what a press in a lane does; with two on, one of them would be dead. |
| **In warp mode a press on a track puts a marker there, and dragging on moves that beat; a press on a marker drags it.** Let go without moving and the marker just stays. Sections are never selected or moved, and there is no selection box. Alt+click moves the playhead; the ruler and the room under the tracks keep their double-click to play. | His words: "flag the beats that start to drift apart, and then you click warp to match them up" — flag, then drag. |
| **A warp marker cannot go on a track with nothing on it** (review, 2026-10-18). | A marker pins a beat of the song. On an empty track there is none, and the next song put there would be warped by a marker nobody set on it — or, pasted, silently lose it. |
| **One undo step per press**, placing and moving together; a press on a marker that leaves it where it was leaves nothing for Ctrl+Z to take back. Each removal is one step. While a marker is held, Ctrl+W and every other edit wait. Losing the mouse mid-press (Alt+Tab) finishes the press as if it were let go. | As for every other edit. |
| **A dragged marker snaps**, within 10 px, to section edges and warp markers on other tracks, to tempo-ramp ends on any track and to the playhead. **Ctrl frees it.** Never to bars, and never to its own track's sections or markers. | Its own track's sections move as soon as the warp changes, so a marker snapped to one would not stay on it. The playhead lets him park on the other song's beat and drop onto it. |
| **Right-click on a marker, in any mode: *Remove warp marker*.** The markers either side stay; the audio between them is even again. | As for ramps. |
| **Drawn whatever the mode**: a line in the warp colour with a handle at its top, and each stretch whose audio is spread labelled with how much, "+0.8%" faster or "−0.8%" slower. | Defaults. The figure says how hard a stretch is being pushed. |
| **Markers travel with the tempo, as ramps do**: an empty track that takes a dragged section's tempo takes its markers, and with them the origin they are counted from; a paste onto an empty track takes them; the vocal and instrumental tracks from *Split* take their song's. **Reset removes them** in the same one step as the ramps; **Match BPM keeps them**, on their beats. | They are part of how that song plays. A stem without its song's warp would drift away from it. |
| **When a track's origin moves back, its markers move with its sections**, by the same whole frames, and their beats stay where they were heard. | Unlike ramps, they are on the audio. |
| **A tempo ramp starts from the track's tempo, not its warp.** A ramp's start tempo, the BPM on its dots and the box's pre-filled tempo ignore the markers. | A ramp is a change of tempo; the warp is a correction on top of it. A new ramp left as filled in still changes nothing. |
| **Project files are now version 4.** Every save writes 4, so an older Benchmix refuses a file saved from now on rather than opening it. | An older build would open a warped project, drop its markers without a word and save over them. |
| **On LINK a moved marker changes the pitch with the speed** — a 1% change is about 17 cents. Not changed. | LINK is speed and pitch together, as on a record. The default mode keeps the pitch. |
| **Not changed: the BPM on the strip and the ruler's bar lines ignore warp markers**, as they ignore ramps. How far each stretch is spread is on its label instead. | The strip shows the tempo TEMPO sets; the warp is a correction on top of it. |


## 2026-10-27 — solo, one track at a time

Daniel, 2026-10-27, verbatim: *"shouldn’t the “s” button for “solo”
mean that only one track can be selected thusly at one time? THat’s the functionality i’d like… but i
do know in other daw’s, there is the ability to do it like you have it here in this app…. if there’s a
specific reason for it, let me know, otherwise let’s make it a tgoggle so whichever track i click it
on, that’s the track it will play solo"*

| Decision | Why |
| --- | --- |
| **A click on S solos that track alone**: every other solo goes off. A click on the only soloed track turns solo off; a click on one of several soloed tracks leaves just that one. | His words: "whichever track i click it on, that's the track it will play solo". |
| **Ctrl+click on S adds that track to the ones already soloed, or takes it away**, and leaves the others as they are. | The reason other programs solo several at once: to hear two tracks together — a vocal against its bed — with the rest silent. Ableton does this by default: a click is exclusive, Ctrl+click adds. Ctrl already means "add to" when selecting sections. |
| **One undo step per click**, however many tracks it changes. | As for every edit. |
| **Mute still wins over solo**; soloing a muted track now says so in the status bar. | The rule is unchanged; the silence was unexplained. |
| **A project saved with several tracks soloed opens with them all soloed.** | Nothing in the project file changed. |


## 2026-10-27 — a render holds what can be heard

Daniel, 2026-10-27, verbatim: *"mixdown should only do non-muted or
selected tracks only"*. Which tracks "selected" means was not discussed with him; soloed is the
reading taken, and it is a default he can change.

| Decision | Why |
| --- | --- |
| **A render leaves out every muted track and, while anything is soloed, every track that is not soloed** — exactly what the speakers leave out — **and the file ends where the last track that can be heard ends.** Before, a muted track was already silent in the file, but the file ran on in silence to the end of the longest track, muted or not. | His request. A mixdown is what he heard. |
| **"Selected tracks" is read as the soloed tracks**: solo the tracks you want, then render. | Solo is the one way Benchmix has of picking tracks to hear, and it is already what decides what the speakers play, so a render decided by the same thing cannot differ from the preview. The highlighted track strip is the paste target, not a choice of what to hear, and a selection of sections is not a choice of tracks. |
| **With nothing audible, the Render button refuses** and says that every track with sound on it is muted or silenced by a solo. | A file of nothing but two seconds of silence helps nobody, and the reason is on screen. |
| **The status bar names what was left out, and why**: "Left out: Topper (muted)", or "(not soloed)". | A file shorter than the project should say what it dropped, or a lit M that was forgotten looks like a bug in the render. |
| **Not changed: the file still starts at 0:00**, even when the first audible section is later, and still ends with the 2 s tail, so fades finish. | The first second of the file is still the first second of the mix, so a render lines up with the project. |
| **The Render box offers 32-bit float first**, with 24-bit and 16-bit after it (same evening). | His words, after being told 24-bit was the sensible choice: *"keep all options but make 32 default."* |


## 2026-10-27 — snapping to the playhead

Daniel, 2026-10-27, verbatim: *"should be able to snap to play
marker"*. How it should behave beyond that was not discussed with him; everything below is a
default he can change.

| Decision | Why |
| --- | --- |
| **A section being dragged, a group being dragged, an end being trimmed and a tempo ramp's dot all snap to the playhead**, within the same 10 px as to a section edge, by whichever end of the section is nearer. Mark ends and warp markers already did. | His request. Parking the playhead on a beat — a click on the ruler, or Alt+click in a lane — and dropping onto it is how he aims. |
| **A section edge wins a tie with the playhead.** | On the dragged section's own track an edge is taken to its exact frame, which is what makes a butt join. The playhead is only ever a position, and a tie that went to it could turn a butt join into a near miss with a declick either side. |
| **While playing, the playhead is not a target** — for any drag, mark ends and warp markers included. (Those two used to snap to it whatever the transport was doing.) | It is moving: a section dragged while a loop plays would jump as the playhead swept past it. |
| **Ctrl frees a drag from the playhead too**, as from edges. Never to bars. | One rule for snapping. |
| **The playhead travels with the snap settings** (`SnapSettings.PlayheadSeconds`), decided once per pointer move by the window, instead of being a separate argument. | One source. Mark ends and warp markers took it as a separate argument, and the section drags would have been a third way of passing it. |
| **The status bar says "on the playhead"** while dragging and "snapped to the playhead" on the drop, and the guide line is drawn on it. | As for an edge. |


## 2026-10-27 — scrolling the tracks up and down, and the scroll bars

Daniel, 2026-10-27, verbatim: *"think of possible way to more easily scroll vertically…. currently
need to do via scroll bar, or by mouse wheel while overing over the tracks area…. problem is, then
doing that, if im also hovering over a knob, it adjusts that nob up and down…. possibly ctrl end
jumps to bottom? ctrl home to top? page up page down to scroll vertically? Although i do really
prefer a mouse method."*, then *"vertical scrolling is biggest pain at the moment"*. On the scroll
bar: *"more contrast, especially the window scroll bar….currently white vs black and i never know
which to click and drag"*.

| Decision | Why |
| --- | --- |
| **The knob under the pointer still takes the wheel**, clicked or not, exactly as before. | His words, after a first cut that made a knob take the wheel only once clicked: *"retain functionality of hovering over knobs and wheeling to make adjustments there."* The complaint about knobs catching the wheel is answered by the row below instead: the wheel scrolls the tracks over the waveforms, where there is no knob. |
| **A right-button drag pans the tracks up and down as well as sideways.** The cursor shows four arrows when there is room to scroll up and down, two when there is not. | His preference for a mouse method. The right-drag already panned sideways, and grabbing the picture is the same gesture in both directions. |
| **PageUp and PageDown scroll a screen of tracks; Ctrl+Home and Ctrl+End go to the top and the bottom.** Not from a text box; bare Home stays the focused knob's "back to normal". | His suggestion. Handled with the other bare keys, never as KeyBindings (DECISIONS 2026-10-03). |
| **The scroll bars are restyled**: a dark track, a clearly lighter thumb that brightens under the pointer and turns blue while dragged, 14 px wide, arrows kept. | The thumb is the part to drag, and now looks like it. The arrows stay because the one under the lanes moves a second a click (the manual says so). |
| **The plain wheel scrolls the tracks wherever it is over the track area — the waveforms, the room under the tracks, the ruler, the scrollbar and the track controls away from a knob — and Alt+wheel moves the playhead.** This supersedes 2026-10-12, when a plain turn over the ruler and the waveforms moved the playhead. | His words, 2026-10-27: *"for vertical scroll, just make the mouse wheel do that. If i want to usew mouse wheel to scrub, i can hold alt. that work?"* and then *"vertical scroll when mouse is in the main area. hold alt, and it scrubs."* Alt already means the playhead: Alt+click puts it somewhere. Ctrl+wheel still zooms and Shift+wheel still scrolls sideways. |


## 2026-10-27 — duplicate a track

Daniel, 2026-10-27, verbatim: *"duplicate a track with the existing
clips on it"*. Nothing beyond that was discussed; the rest is defaults he can change.

| Decision | Why |
| --- | --- |
| **Right-click a track's strip → *Duplicate track*: a copy right below it**, with the same sections at the same frames, the same knobs, LINK, M and S, the same tempo ramps, warp markers and origin, under its own name — "Bed copy", then "Bed copy 2" — and the next colour. Selected, and playing at once. | His request. Right below is where he will look for it, and the next colour keeps the two apart on screen. |
| **The copy's sections are copies**, with ids of their own, as are its ramps and markers. | Editing one side must never touch the other, and a saved project must not hold one section twice. |
| **A soloed track's copy is soloed too**, and the status bar says both play. | The copy plays as the original does; two soloed tracks is what Ctrl+click on S makes anyway. |
| **One undo step.** | As for every edit. |
| **No shortcut key**, only the menu item. | Nobody asked for one, and the strip menu is where *Rename track* lives. |


## 2026-10-27 — tabs: separate arrangements in one file

Daniel, 2026-10-27, verbatim, pasted in chat: *"possible to create multiple tabs? use case: I'm
chopping up a long sound bite into smaller bites and the number of tracks is getting very long.
Moving them to different tabs that can each contain any number of tracks would be dope, like an
excel workbook."* Asked whether every tab should still play and render together, he chose *"No,
separate"*. On undo: *"only issue here is that i dont want to hit ctrl z and have something undone
on an unseen tab, know what I mean?"* Then *"Yes, build tabs"*. Everything else below is a default
he can change.

| Decision | Why |
| --- | --- |
| **A tab is an arrangement of its own**: its tracks, its PROJECT BPM and its listening loop, in one `.benchmix` file. The MASTER slider, the limiter and the EQ mode are shared. | "No, separate" — like sheets in a workbook. The master and the limiter are the room's, not the song's. |
| **Only the open tab plays and renders.** Switching tabs stops playback; each tab remembers where its playhead was (not saved). | His choice. Two tabs sounding at once would be one mix again. |
| **Each tab keeps its own undo history.** Ctrl+Z undoes the last thing done on the tab that is open, and says "Nothing to undo on Bites" when there is nothing there. | His objection, verbatim above. |
| **A track sent to another tab is undone from the tab it was sent from**: it comes back here and leaves there, and whatever was done on the other tab since stays. Renaming a tab, moving it along the row and removing it are undone from the tab he was looking at when he did it — a removed tab from the tab he landed on — and a tab brought back by Ctrl+Z is opened. | The row of names is in sight from every tab, so nothing about it is "unseen". A step that touched another tab puts back only what it moved there (`TabMerge.Restore`). |
| **Adding a tab is not an undo step.** *Remove tab* on its menu takes it away with its tracks, and that is undone from the tab landed on. The only tab stays. | Undoing "add tab" after tracks were moved onto it would take them with it. |
| **The tab row sits under the transport bar.** A click opens a tab, a double-click renames it, right-click offers Rename, Move left, Move right and Remove; **+** adds one, named "Tab 2", "Tab 3", …; Ctrl+Tab and Ctrl+Shift+Tab step along the row. **Move to tab ▸** on a track's strip menu sends it to the end of another tab. | Where a workbook puts them. The strip menu is where a track's own commands live. Ctrl+Tab is the browser's. |
| **Copy and paste work across tabs.** The clipboard is the window's: a section or a marked stretch copied on one tab pastes on another, at that tab's playhead. | Chop on one tab, place on another: his use case. |
| **Every tab's audio is decoded when the project opens**; only the open tab's tracks have voices in the engine. | A switch must not stall, and a tab that is not open must not sound. |
| **Project files are version 5.** Every save writes 5, so an older Benchmix refuses the file. A version 4 file opens as one tab, "Mix", with its tracks, tempo and loop. | An older build would open one tab and save over the rest. |
| **Not changed:** Match key compares with the topmost track of the open tab; New clears every tab; Open replaces them all; a render is the open tab. | Tabs are inside the project, not beside it. |

## 2026-10-28 — the crossfade X

Daniel, 2026-10-27, verbatim: *"the crossfade thingy when im splicing a clip
into another one….on other daw's it looked like a very narrow tall X….basically i think it just did
a small fraction of a second volume decrease/fade out and fade into the next clip (very very small,
fraction of second)"*. On 2026-10-28 he picked it as the next item, without saying whether he hears
a click at a splice or only misses the picture — so this does both: the X is drawn, and the length
can be chosen. Every butt join had already been crossfaded over 5 ms since 2026-09-24. Everything
below is a default he can change.

| Decision | Why |
| --- | --- |
| **The X is drawn at every join the engine crossfades** — a section starting on the very frame the one before ends, not continuing it, with no fade of its own on either side — and nowhere else. It covers the fade's real stretch, from the join forward, the full height of the lane, and never less than 6 px. | The 5 ms standard is a fraction of a pixel at any zoom: drawn to scale it would vanish, and he asked for "a very narrow tall X". At a seam the X would say audio is being faded where not one sample changes; at a gap, where each side is declicked on its own, it would lie the same way. |
| **The length is picked from the lane's right-click menu, *Crossfade from the section before*: 5 ms (the standard), 10, 20, 50, 100 or 200 ms.** The one in force is ticked. It lives on the section whose start it is, is undone with Ctrl+Z, and is saved. | "A small fraction of a second": the menu covers that and no more. A menu, as *Fade in* and *Fade out* are, rather than dragging the X: there is nothing to grab at 5 ms. It belongs to the incoming section because the fade is applied over that section's first frames. |
| **The fade stays where it was: from the join forward, into the incoming section, equal power.** A longer length renders a longer handle of the outgoing section past its cut and fades it out under more of the incoming one. | The join does not move, so nothing after it is a sample late (`CrossfadeTests`, as `JoinTests` prove at 5 ms). Other DAWs draw the X astride the splice; here the engine fades only after it, and the X is drawn where the fade is. |
| **Never longer than the incoming section.** A fade longer than its section ends with the section. | The next join would otherwise cut it dead mid-fade — the click a crossfade exists to remove. Measured: 200 ms into a 1000-frame section follows the 1000-frame law. |
| **Repeats carry the length; a split's second half, and any new start a cut makes, go back to the standard.** Ctrl+D and loop mode copy it to every repeat. | A loop should splice alike at every repeat. A split's second half starts at a seam, and the start a cut makes is a new splice: the length was picked for the old one. The same as a fade-in, which a cut always clears. |
| **With a fade of its own on either side there is no crossfade, and no X.** | Unchanged since 2026-09-24: the fade is the ramp there, and a crossfade under it would double up. |
| **Project files are version 6.** A version 5 file opens with every splice at the standard. | An older build would open a version 6 file with the lengths gone and save over them. The rule since version 3. |
| **Not changed: the law.** Equal power at every length. For a loop of one steady note the 2026-10-21 measurement stands: a longer crossfade makes the bump at each repeat longer, not smaller (same height, 6–14× longer; `SustainedLoopTests`, kept uncommitted). This is for splicing one passage into another, which is what he asked for. | He has not answered the held-note question; the join law stays until he does. |

## 2026-10-28 — the status bar is quiet about the transport

Daniel, 2026-10-28, verbatim: *"no need for status message on
something simple as changing play position. Instead, statyus messages should be for major events,
and also is especially helpful for reference when I'm about to hit ctrl z to undo, so I know what
i'm about to take back."* Then: *"Regardless, remove these minor notifications. dont need a reminder
that i hit play or pause."*

| Decision | Why |
| --- | --- |
| **Double-click to play, the wheel click, the wheel double-click, Stop and a click in MARK MODE say nothing.** "Playing from …", "Stopped, back at …", "Stopped, back at 0:00." and "Playhead at … on …" are gone. "Nothing to play yet." stays, as does every message about the listening loop, the sound output, rendering and edits. | His words. The status line is what he reads before Ctrl+Z; a note that he pressed play pushes the edit he wants to see off it. |
| **Not changed yet: what Ctrl+Z will take back is not shown.** Offered to end every edit's message with it ("… Ctrl+Z: fade in"); waiting on his word. His further idea — an action log to unwind to a chosen state — is his "future idea, not right now (too complex)". | One feature per approval. |

## 2026-10-28 — the number pad zooms too

Daniel, 2026-10-28, verbatim: *"ctrl 0 on top row numbers works, but didnt work
on keybad on right side of keyboard for some reason (made sure num lock was toggled correctly)"*.

| Decision | Why |
| --- | --- |
| **Ctrl with the number pad's +, − and 0 zoom in, out and fit**, exactly as Ctrl with the top row's =, − and 0 do. Three more KeyBindings, all Ctrl-modified. | His comment. The pad's keys are different keys to Windows (`Add`, `Subtract`, `NumPad0`), never bound before. Ctrl keeps them out of any box being typed in, as the rule since 2026-10-03 requires. |

## 2026-10-28 — the wheel click pauses in place

Daniel, 2026-10-28, verbatim: *"adjust one click stop functionality. the
playhead should stop where the playhead currently is at the time of my clicking mouse wheel; it
should not go to the location of the cursor. This will allow me to listen to a song and hit stop at
the precise moment i want it to as i listen, so i can identify crucial parts of the song. make
sense?"* (It never went to the cursor; it went back to where play started.) Proposed to him: a
single click pauses in place, a double-click goes back to where play started. His answer: *"Yes to
the wheel-click proposal: a single click stops in place, a double-click goes back to where play
started."*

| Decision | Why |
| --- | --- |
| **A click of the scroll wheel pauses exactly where the playhead is**, anywhere in the window, mid-drag included. Nothing moves; Space plays on from there. A click while nothing plays does nothing. | His words. It is the engine's Pause, which takes hold at the next block boundary — within a tenth of a second on his device — and moves the playhead nowhere. |
| **A double-click stops and goes back to where play last started.** | What the single click did from 2026-10-11; he still wants the jump back, as the second gesture. Its first click has already paused, which moves nothing, so the second lands on the very frame play started from. |
| **The double-click no longer goes to 0:00.** The ■ button does; the view no longer scrolls back either. | Two gestures for 0:00 were one too many once the double-click was needed for the return. |
| **The old return point rules stand**: Space after a pause counts as a start, an undo keeps the point, Stop waits for the block in flight. | Unchanged in the engine (`TransportTests`). |

## 2026-10-28 — a project follows its songs

Daniel, 2026-10-28, verbatim, in chat with a screenshot of eight lanes reading `Missing:` after he
had moved a project and its mp3s to another folder: *"got this error, logically, because i moved
the mp3's and project file into a diff folder. Any way we can make these follow each other? like,
it looks for the source files in current folder project .benchmix file is located? Buit also
brings up diff issue... if I ever rename the source sound files, would i still get this message
even if we resolve the folder location following issue?"* Proposed three parts; his answer: *"yes
please do it. i'm not worried about fixing it right this minute though"*.

| Decision | Why |
| --- | --- |
| **A song that is not where the project says is looked for when the project opens: next to the project file, then anywhere under the project's folder — by name first, then by fingerprint.** What is found is used at once and written back by the next save (the project opens with `*` in the title). A song that is still where the project says is never touched, whatever sits next to the project. | His first question. Moving a project with its songs, or a whole folder, then just works. Name first because a same-named file beside the project is almost always the song; a song that exists is left alone because a different file of the same name next to the project must not replace it. |
| **Every song gets a fingerprint — its size and a hash of its first 256 KB — when it is added and at every save** (`BenchmixProject.SourceFingerprints`, by path). A renamed song under the project's folder is recognised by it. | His second question. The size is compared first, so a folder of a thousand songs costs a few reads, and the hash is of the file, not the audio: nothing is decoded. A re-encoded copy is a different file and is not recognised, which is right. |
| **The file's version stays 6.** | The fingerprints are a lookup table: an older Benchmix ignores it and loses nothing on save (the next save here makes it again). Refusing the file for that would be the rule's letter against its purpose (versions 3–6 each guarded real data). |
| **The strip menu offers *Find missing file…* only on a track that reads from a song that is not there.** A file box opens on the project's folder; the file picked replaces the missing one for every track and section that read from it, on every tab, and is loaded at once. One undo step. | His "anywhere else" case — a song moved to a folder the search does not see, or renamed with no fingerprint yet. Every section follows because a song pasted across tracks is still one song. The item hides when nothing is missing rather than saying so. |
| **The search stops at five thousand files and leaves the project's own Backups folder out.** | A project saved at the root of a drive must not walk the drive; the backups are projects, not songs. |
| **A scripted run never opens the file box**; the probe drives `RelinkAsync` and reads the item's visibility. | The standing rule: no dialog of any kind in a verification run. |

## 2026-10-29 — select several tracks; Match BPM to their average; Match key between them

Daniel, 2026-10-28, verbatim: *"need to clarify which song
matches to which. Also need a way to remove bpm matching and switch which song matches to which.
Also, add: ctrl/shift click to select multiple tracks. Then, right click on Match BPM for
alternative option that matches all selected songs to the average bpm between them all."* And on
Match key's "compares with the top track": *"i dont think this works. also, cant see how logically
this would work with more than 2 tracks in a project. again, must be able to select multiple
tracks"*. Picked on 2026-10-29 as the first round: *"Round 1: multi-select"*.

| Decision | Why |
| --- | --- |
| **Ctrl+click on a track's strip flips it in or out of the selection; Shift+click takes every track from the anchor to it; a plain click selects that track alone.** The selected tracks share the highlight the one selected track had; the one clicked last is still where Ctrl+V pastes and what the lane menu acts on. | The three gestures sections already have (2026-10-03), on the strips, so there is nothing new to learn. |
| **A press on one of a track's buttons or knobs keeps a selection the track is part of**, making that track the one commands act on; a press on the strip's background, or in a lane, selects one track alone. | Match key and Match BPM's right-click act on the selection, and the press that reaches them goes through the strip first: if it collapsed the selection there, they could never see more than one track. Ctrl and Shift are read on the background only, so Ctrl on the S button still means "solo alongside". |
| **Match BPM's right-click offers one item: match the selected tracks — every track, when fewer than two are selected — to the average of their tempos, and make that PROJECT BPM.** The item says what it will do, with the average in it, or is greyed out and says why not. One Ctrl+Z puts every tempo and PROJECT BPM back. | His request, word for word. PROJECT BPM follows because it is the tempo every later Match BPM goes to: left where it was, the next track matched would land away from the rest. |
| **The average is of the tracks' own tempos, each read in the octave of the track commands act on first** (half/double-time folded, as Match BPM does: `Analyzer.AverageBpm`). | A 172 BPM reading beside two at 85 is a double-time reading of 86, not a third tempo; and an average of the tracks' *current* speeds would change with every click. |
| **Match key with several tracks selected: the track whose button is pressed stays as it is, and the other selected tracks are shifted to sit with its key**, one undo step; a track without a key is left alone and named. With one track selected it is as before: that track is shifted to sit with the topmost other track. | The button pressed is the natural way to say which key the others should follow, and it works for any number of tracks; the old rule — the topmost other track — is what he could not see working past two. |
| **Nothing else acts on the selection yet.** Mute, solo, ✕, Reset, Move to tab and the knobs act on their own track. | One round, as he asked; the rest can follow once the selection has been used by hand. |
| **The selection is not saved and is not an undo step**, as a section selection is not; an undo keeps it, as it keeps a section selection. | A selection is where you are looking, not what the project is. |

## 2026-10-29 — copy, cut, paste and repeat several sections at once

Daniel, 2026-10-27, verbatim: *"MUST be able to select multiple clips to
copy/paste them"*. Picked on 2026-10-29 as the second round, the defaults left to me: *"Start Round
2: copy and paste several selected sections at once. Use your defaults for pasting across tracks
and for gaps."*

| Decision | Why |
| --- | --- |
| **With two or more sections selected, Ctrl+C, Ctrl+X and Ctrl+D act on all of them**, and the lane menu says so (*Copy 3 sections*). With one selected, or none, they act on one section as before; while a stretch is marked, on the mark, as before. | His request; and Delete and a drag already act on the selection, so the keys now all agree. |
| **A group is exactly those sections, not the stretch they span: a paste puts each one down where it falls and leaves what lies under the gaps between them alone.** A marked stretch still clears its whole span, gaps included. | That is what dropping the same group does, and what "copy these clips" means in every DAW; MARK MODE is there for "this whole span". |
| **Each section keeps its place to the frame.** The group starts where its earliest section starts; that section's row measures from its exact frame, every other row from the same moment through its own map (`SectionGroup`, `SectionSelection.Copy`). | A loop built with Ctrl+D is a row of butt joins; copied as a group and summed in seconds, one could land a frame off and become a gap with two declicks. The same rule as every other placement here. |
| **Across tracks the rows keep their order: the top row lands on the selected track, each further row on the track that many below. A row that would fall off the bottom gets an empty track added for it, in the same undo step**, and the status says so. | Pasting a group copied off three tracks onto the last track has to put the lower rows somewhere; adding empty tracks is what he would do next by hand, and one Ctrl+Z takes the lot back. Refusing was the alternative, and a dead end. |
| **An empty track a row lands on takes the tempo and pitch of the track that row came off**, as it does for one section. | The sections then play as they did. |
| **Ctrl+D with several selected repeats the group straight after itself, on the same tracks: the copies start on the very frame the group's last section ends** (in that row; the other rows at the same moment, never before their own last section), and the copies become the selection, so holding the keys walks the group along. | A group that is a loop goes on being one, with no gap at the repeat; the walk is what Ctrl+D does for one section. |
| **Ctrl+X with several selected copies them as they lie and removes them, each gap left as it is.** | Cut never ripples, one section or ten. |
| **What was pasted or repeated is selected afterwards, the first of it the one commands act on; in MARK MODE a group paste sets no mark.** | So a pasted group can be dragged as one at once. A mark is one track's span; a group may be several tracks'. |
| **A track multi-selection (Round 1) and a section multi-selection are separate things**, and mute, solo and the knobs still act on one track. | One round at a time. |

## 2026-10-29 — four small things: Save as…, the Ctrl+Z hint, the loop by clicks, Mute section

Daniel, 2026-10-29, after two rounds in a day: *"please go ahead and work through everything as far
as you can go, but stop working once you've hit 60% of my fable token cap"*. These four are his
comments of 2026-10-28, each answered then with a question or an offer he never needed
to answer; the defaults are mine.

| Decision | Why |
| --- | --- |
| **A Save as… button in the toolbar, next to Save.** Ctrl+Shift+S still works. | His comment on Part 1 step 9: *"need option to 'save as' as well, so i can make alternate copies"*. It existed as a shortcut only, and nothing on screen said so. |
| **The status bar's right-hand end names what Ctrl+Z would take back** (*Ctrl+Z: paste 3 sections*), kept current after every edit, undo, redo, tab switch, New and Open (`MainViewModel.UpdateTitle`). The messages themselves are unchanged. | His words: *"especially helpful for reference when I'm about to hit ctrl z to undo, so I know what i'm about to take back"*. A hint that stays beats a message the next one replaces. |
| **The listening loop by clicks: a Shift+click on the ruler sets where it will start (a dashed line, and the status says so); the next Shift+click sets its end and the band lights.** Shift+drag still paints the whole loop at once, and with a start waiting a drag paints from it, so the end can be nudged before letting go. Clear loop forgets a waiting start. **A Shift+click no longer clears the loop.** | His words: *"click to set start and end points of loop…. click and hold allows setting and micro adjusting, and sets it at release of click. next click does same thing, then section highlights to indicate looping."* On the ruler only: on a lane a click selects, Alt+click moves the playhead, Ctrl and Shift+click select sections. Clearing by Shift+click was the one thing he could do by accident. |
| **Mute section on the lane menu silences the selected sections where they are** — drawn faded with a dotted outline, a gap to the engine (`ProjectSession.BuildTimeline` leaves them out), declicked either side as any gap is — **until unmuted the same way**: the item reads *Unmute section* when every target is muted, *Mute 3 sections* with several selected. One undo step; the selection stays. | His words: *"retain looped sections on timeline but gray them out to temporarily deactivate them"*. Leaving the section out of the voice's timeline is exactly a gap, so nothing new is rendered and the join rules stay what they were. |
| **Project files are version 7.** A file with a muted section opened by an older Benchmix would play it; the older one refuses the file instead, as for tabs (5) and crossfade lengths (6). | The rule since 2026-10-27: a file that would sound different in an older release is a new version. |
| **No X at a join with a muted section, and no crossfade there.** | To the engine it is a gap. |

## 2026-10-29 — Sync: a track's beats onto another's

Daniel, 2026-10-28, on Match BPM: *"after bpm mmatching, also an automated
snap button that realligns to match peaks like kicks and snares? Like how dj software does it.
typically called "sync" i think. They not only match bpm's but they also move songs on the
timeline to match them up."* Built in the longer round of 2026-10-29, under his 60% line.

| Decision | Why |
| --- | --- |
| **A Sync button on the strip, next to Match key.** On its own, the pressed track slides to the top track (for the top track, the one below it); with several tracks selected, the pressed track stays and the other selected tracks slide to it — Match key's rule exactly. | One rule for the two "between tracks" buttons, learned once. |
| **The beats are read near the playhead** — 12 s of each track's own audio around it, in the section under it or the nearest one — never over the whole song (`BeatSync.BeatNear`). | A song's tempo drifts; a grid fitted to three minutes lands a beat where the average says, not where the drums are at the moment he is listening. The playhead is where he is listening. |
| **The move is the short way round, never more than half a beat**, and forward by a whole beat instead when the track has no room before 0:00. | The two grids repeat every beat; more than half a beat is the long way to the same place, and would pull the track out of the picture he lined up by ear. |
| **A track moves by its origin, in whole frames** (`TimelineEditor.SlideTrack`): its sections and warp markers go with it, its tempo ramps are carried by the same amount, and nothing changes its place on the track. | Moving the sections instead would re-trim at any overlap and, past a warp marker, re-warp them. Whole frames, then seconds, as every position. |
| **Refused: unmatched tempos, tempo ramps, nothing to read.** Tempos further apart than 0.5% get *Match BPM first*; a track with tempo ramps is refused (the slide would change what tempo its sections play at); a stretch the comb finds no beat in gets *move the playhead to where its drums play*. | Lining up two grids that drift apart again in ten seconds is worse than saying so. |
| **Within 2 ms is in time**: nothing moves and nothing goes on the undo stack. | A frame or two is below anything a second press could improve on, and would make Ctrl+Z take back nothing. |
| **The beat is found by a comb over the onset envelope at the track's own tempo** (`BeatPhase`: the tempo detector's spectral flux at a 512-sample window and a 32-sample hop, the peak refined between hops), with the flux's lag behind a hit added back as a constant measured on clicks (0.955 windows). | The tempo is known already — the tag or the detector — so only the phase is unknown, and a comb at a known period is the one-parameter version of the tempo detector that already works. The lag is the same for both tracks and cancels in the difference. |
| **Beats, not downbeats.** Sync lands a beat on a beat; which beat is bar 1 is his to hear. | Downbeat detection is a different and less reliable problem; DJ software's sync does the same unless a beatgrid's downbeat was set by hand. |

## 2026-10-29 — MP3 render

Daniel, on *Render to WAV*: *"I want the ability to mixdown and save as
a wav (can we offer mp3 as well?) … In fact, please also add mp3 mixdown option to desktop
version."* Built in the second part of the longer round of 2026-10-29, under his new 75% line.

| Decision | Why |
| --- | --- |
| **The Render box's fourth type is MP3 at 320 kbps**, after the three WAVs; 32-bit float stays first. | His default of 2026-10-27 stands. 320 kbps is the most an MP3 holds and what a DJ library is made of; a choice of bitrates is a question nobody asked. |
| **Windows' own encoder, through Media Foundation** (`MediaFoundationEncoder`, in the NAudio package the engine already uses) — no LAME, no new native library. | Media Foundation is the decoder every song comes in through, so the single-file exe loads nothing new (a native DLL from a deep path is the MAX_PATH failure); its MP3 encoder has been in every Windows since 8, Home included. The earlier answer on the row — "needs a free encoder library bundled in" — was wrong. |
| **The encoder is fed the very blocks the WAV loop pulls** (`Renderer.GraphSource`), as 16-bit PCM with the WAV's own dither (`Dither16`, one function for both). | One signal path, still: the MP3 adds the encoder and nothing else. 16-bit is the input every MP3 encoder takes, and its quantisation noise sits 40 dB under the encoder's own. |
| **A mix at a rate MP3 cannot hold — anything but 32, 44.1 or 48 kHz — is converted on the way in**, by the `DeviceRateConverter` the live output uses: to 44.1 kHz for a multiple of it, 48 kHz otherwise (`Renderer.Mp3RateFor`). The status says so. | The mix runs at the output's rate for the whole session, and a 96 kHz output is real. Refusing would leave the type dead on such a machine; the converter is already proven on the live path. |
| **The file is written as `<name>.tmp.mp3` and moved into place when whole.** | As the WAV's `.tmp`, but the MP3 writer picks its container from the extension. |
| **No tags.** The MP3 carries no title, tempo or key. | The WAV carries none either; writing tags is its own request (row 52), with its own questions. |
| **The MP3 decodes about 33 ms late against the WAV, and about 50 ms longer. Not corrected.** | The codec's own delay — encoder and decoder together — and padding to whole 1152-sample frames, in every MP3 that carries no gapless information; Windows' encoder writes none. Trimming the mix to pre-empt it would move the start of every render by a number that belongs to the player, which may or may not take it out itself. Measured, see VERIFICATION. |
