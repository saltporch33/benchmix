# Benchmix — what has actually been verified

*2026-09-16.* Evidence, not assurances. Every number here was measured on the target machine.

## The test suite

**220 tests, 0 failures, 0 skipped** (2026-10-11). `dotnet test` — about 90 seconds (the stem
tests dominate).

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

## 2026-09-24 — editing: empty tracks, paste target, drag between tracks, loop mode, snapping

**Joins between sections, measured.** A 431 Hz sine at 0.5, looped at two points half a cycle
apart; the largest sample-to-sample step within 10 ms of the join, against the same render's
largest step anywhere else — the sine's own. 431 Hz and not 440: the stretcher's 7200-frame delay
is a whole number of cycles of 440 Hz, which hid a defect from the first version of these tests.
`JoinTests`:

| Path | Before | After | Elsewhere in the same render |
| --- | --- | --- | --- |
| untouched track | 0.85 | 0.028 | 0.028 |
| LINK on | 0.99, 65 frames after the join | 0.028 | 0.028 |
| LINK at +5% | 0.95 | 0.030 | 0.030 |
| LINK at 0.7× | a hard switch at ~1 join in 40 (0.40) | 0.020 | 0.020 |
| pitched +2 st | 0.76 (440 Hz); 0.10 with only the EQ prime wrong | 0.031 | 0.028 |
| pitched +0.5 st | 0.079 with only the EQ prime wrong | 0.039 | 0.028 |
| stretched to 0.93× | 0.14 (reviewer's measurement) | 0.039 | 0.028 |
| stretched to 1.25× | 0.10 with only the EQ prime wrong | 0.037 | 0.028 |

The phase-vocoder rows are not quite at the signal's own step — within 1.4× of it, against up to
5× before. That is small next to what it was, but it is not nothing, and nobody has listened.

Also pinned: after the 5 ms fade the incoming repeat is **bit-identical** to the same repeat played
on its own, on all four paths (the fade smooths the edge and moves nothing), and a join 30 frames
before a block boundary renders bit-identically to one mid-block (preview and export agree). A
fade on one side of a join no longer leaves the other side bare: 0.42–0.43 steps before, 0.029
after.

**Placement, to the frame.** `SectionEditingTests` (26) and `SectionRegressionTests` (12), each on a
ramp so a sample's value is its position, most by rendering the edited project against the
original:

- A section pasted or dragged onto an empty track at the same moment renders **identical** to how
  it played on its own track, everywhere but the 5 ms declicks at its two cut edges.
- Snapping onto a neighbour's end lands on **exactly** that frame, and the engine sees a butt join.
- A loop's repeats are each **bit-identical** to the first pass once past their crossfade; a
  part-repeat keeps the loop in phase on either side; pulling a loop back during the drag restores
  what it ran over.
- Moving a track's origin back changes **no samples** and breaks no seam.
- The four regressions: a split of a moved section is now a true seam (0 samples change), Ctrl+D
  on a moved section leaves no gap, an emptied track reopens empty, and paste lands at the playhead
  even before where the track's song began. Each failed against the code before the fix.

**Hundreds of edits in a row.** `SectionFuzzTests` runs four seeded sequences of 250 random moves
(across tracks, with and without snapping), trims, loops, pastes, splits, deletes and repeats on
four tracks at three tempos, and after every one checks that no track has overlapping sections,
nothing sits before its track's timebase, and the engine holds exactly the sections the model
says — nothing silently truncated.

**Two adversarial reviews**, told to assume the change was wrong and to reproduce anything they
reported. The first found seven defects, and fixing them turned up two more; the second, of just
those fixes, found four in them. All thirteen are fixed, and each fix was taken out again to
confirm its test then fails — see `DECISIONS.md`, 2026-09-24.

**In the published exe** (the single file, 78 MB): `--latencycheck` drift 0 frames between a
pitched and an untouched track; `--gesturetest` 57/57; `--loadcheck` pass; `--playtest 6` ratio
0.999 (silent); `--stemtest` pass on DirectML in 4.1 s; both `--uiprobe` demos drawn and looked at.

**In the real window.** `--gesturetest` grew from 27 to **57 checks**, 30 of them new, driving the
real lane handlers with a scripted pointer whose positions come from where the lanes actually are
in the laid-out window: Ctrl+T and Ctrl+L are bound; an empty track is added, selected and not
reported missing; paste lands on the selected track at the playhead; undo keeps the selection; a
right-click selects its lane; a section dragged down one track lands there at the dropped time; a
drop 6 px from an edge snaps onto it and shows the guide line; Ctrl frees it; loop mode builds four
exactly-butted repeats and one undo takes them back; a song file lands on an empty track where it
was aimed; the menu's *Move section to playhead* lands exactly. Pasting a file copied in Explorer
is **not** driven — setting that up would overwrite the clipboard of whoever is using the machine —
but the Windows clipboard counter it relies on is checked to resolve in the published exe.

`--demoediting --uiprobe` renders a moved, looped section with its loop handles at the loop's outer
ends, the snap guide line, the selected track's bar and an empty track's hint — looked at, and
correct.

## 2026-10-03 — several sections at once, the Delete key, tooltips

**In the test project.** `SectionSelectionTests` (5): a Shift+click range takes every section
between its two ends whichever end was clicked first and never the one after; across tracks it
takes the tracks between but only sections wholly inside the time, so a whole song on the track in
between is **not** swept up by two short pieces (the assertion message says what Delete would then
have removed); a range on one track leaves the others alone; deleting three selected sections on
two tracks removes exactly those and moves no sample of the section kept (`==` on its start and
source offset); deleting nothing changes nothing.

**In the real window.** `--gesturetest` grew from 58 to **80 checks**, the 22 new ones through the
same lane handlers a mouse reaches, with a scripted pointer, scripted modifier keys and a scripted
focused element (a scripted run never holds the real keyboard): a plain click selects one; Ctrl+click
adds a second, which becomes the one commands act on, and on a selected one takes it out again;
Shift+click selects every section between and makes the one clicked last the target; the right-click
menu's target follows the section right-clicked, not the first selected; Delete while a section is
held under the mouse is refused; Delete through the real key handler with a `TextBox` as the focused
element is left to the box, with a `ContextMenu` is left to the menu, and with the window focused
removes all three in one go while the status bar counts them; one undo brings all three back; Delete
with nothing selected deletes nothing even with the playhead on a section; no Delete `KeyBinding`
exists; and every `ButtonBase`, `Knob` and `Slider` in the visual tree carries a tooltip (53 checked
with two tracks loaded — the check names any that does not). The existing "with Ctrl held it goes
exactly where it was put" check still passes, which is what proves a Ctrl-drag is still a free drag
and not a toggle.

**The adversarial review** (five lenses over the diff, 36 findings, the 14 non-trivial ones each
handed to a skeptic told to refute it; all 14 stood, collapsing to six distinct defects) found, before
anything was deployed: Delete went dead after typing in PROJECT BPM, because a lane click moves no
keyboard focus; the right-click menu's fades, trims and *Move section to playhead* acted on the first
selected section rather than the one right-clicked; Delete mid-drag left the drag holding a deleted
section, whose drop then cleared audio under its ghost; Shift+click swept up the invisible zero-length
sections of a track whose file is missing; after a Shift+click commands acted on the section clicked
first while the README said last; and Delete fired under an open menu. All six are fixed above, each
with a check or test that fails without the fix (`SectionSelectionTests` is now 6).

**In the published exe** (`scripts/verify-exe.ps1`, after the review fixes; Daniel had closed his
copy, so this is the build his shortcut opens):

```
latencycheck      PASS   gesturetest  PASS (80 checks)   buttontest  PASS (12 checks)
loadcheck         PASS   playtest     PASS (silent)      stemtest    PASS
uiprobe-sections  PASS   uiprobe-editing PASS
VERIFY: every run passed and stayed out of the way
```

**Not verified:** how Ctrl+click and Shift+click feel under a real mouse (the scripted pointer
presses and releases at the same point; a human's hand moves a pixel or two, and the 4 px move
threshold is what keeps that a click), and the focus move itself — click PROJECT BPM, type, click a
section, press Delete: the section should go and the typed tempo should stick. That is a by-hand
check, because a scripted run is not allowed to take the keyboard. The guard logic behind it is
driven with a real `KeyEventArgs` and a stand-in focused element.

## 2026-10-03 — "Split into vocals + instrumental" did nothing

Daniel, 2026-10-03, verbatim: *"the split vocals instrumental feature does nothing."* He was right,
and it had done nothing since the day it shipped (87f3676). The right-click menu item's command was
bound by looking up the window (`RelativeSource AncestorType=Window`). A right-click menu opens in a
popup of its own, outside the window, so the lookup found nothing, the command was empty, and WPF
draws an item with no command as enabled. A click went nowhere and nothing said so. Every other
item on that menu calls a `Click` handler, which is why they all worked.

Nothing caught it because nothing went through the menu: `--stemtest` and the stem unit tests call
the separator directly and passed throughout. The engine was fine — stems written from T.R.O.Y. on
2026-10-03 measure vocals −19.0 dB against an instrumental at −14.1 dB.

**Proved before the fix, in a published exe** (staged build with the new check and the old wiring):
`--buttontest` clicked the real menu item through its automation peer and `FAIL - clicking it
starts separating`; the status bar still showed the previous Match key message.

**The fix** is a `Click` handler like its neighbours (`OnSplitStems`). Two checks guard it:

- `MenuWiringTests.NoRightClickMenuItemReachesTheWindowThroughABinding` reads every XAML file and
  fails if anything inside a `ContextMenu` binds through `AncestorType`, `FindAncestor` or
  `ElementName`. Run against the old line it fails, naming `MainWindow.xaml:359`.
- `--buttontest` now ends by pressing *Split into vocals + instrumental* on the topper through the
  real menu item: **22 checks**, up from 12. Published exe, 2026-10-03:

```
PASS  clicking it starts separating (status: 'Separating They Reminisce Over You (T.R.O.Y.) (Clean)...')
PASS  it finishes with a vocals track and an instrumental track (4 tracks; status: 'Split ... in 24.2s on DirectML. ...')
PASS  the original is muted, not removed
PASS  the vocal stem carries the voice: -6.4 dB against the original (a song with no voice measured -44)
PASS  the instrumental is not silent: -1.4 dB against the original
PASS  '... — vocals' lands exactly on the original's sections
PASS  '... — instrumental' lands exactly on the original's sections
PASS  one Ctrl+Z undoes the whole split (2 tracks, original muted=False)
```

The −6.4 dB is the same figure as the 2026-09-16 control test's real-vocal result (−6.5 dB), now
reached through the menu. `scripts/verify-exe.ps1` gained `-Only <name>` to run one switch, still
watched from outside; the full set passed afterwards (all eight runs, gesture test 80 checks).

**Not verified:** the first-run download dialog (the model is already on this machine, and a
scripted run shows no dialogs), and the menu opening under a real right-click — the check invokes
the item without opening the popup, because an open popup would appear on his screen.

## 2026-10-03 — undo, New and Clear trim, found while rewriting the guide

Checking the click-by-click guide against the code found six more defects; the reasons are in
`DECISIONS.md`. Each check below was run first against a published build with the old code (staged
under `%LOCALAPPDATA%\Benchmix.stage\`) and failed, then against the fix and passed.

| Defect | Check | Against the old code |
| --- | --- | --- |
| Undo rewound the playhead to 0:00 | `--buttontest`: split at 37.5 s, Ctrl+Z, read the **engine's** position | `the engine is at 0.000s`. A first version read the view-model's position, which nothing updates in a scripted run, and passed against the bug; it now reads the engine. |
| A refused command used up a press of Ctrl+Z | `UndoSkipsAStepThatChangedNothing`; `--buttontest` split, refused split, one Ctrl+Z | speed left at 0.5; `2 sections, want 1` |
| Reset took one Ctrl+Z per knob | `--buttontest`: LOW 0.5, VOL 0.5, TEMPO 1.1, the real Reset button, one Ctrl+Z | `LOW 1, VOL 0.5, TEMPO 1` |
| Undo after saving left no `*` | `--buttontest`: save to a temp folder, Ctrl+Z, read the title | `title 'Benchmix — undo-check'` |
| New left a deleted track selected, and a `*` | `--buttontest`: New, then add a song | selected still T.R.O.Y.; `Untitled *`; the added song not selected |
| Clear trim slid the audio and overlapped the next section | `ClearTrimUncoversTheSongInPlaceAndStopsAtTheSectionsEitherSide` — a ramp, rendered against the untouched song | `spans frames 624000–3504000; its neighbours end at 480000 and start at 960000` |

With the fixes: **199 tests, 0 failed, 0 skipped**; `scripts/verify-exe.ps1` all eight runs pass,
`--buttontest` **31 checks**, `--gesturetest` 80. One existing test, `TheStackIsBounded`, pushed 20
identical states and counted 5 undos; identical steps are now skipped by design, so it makes a real
change before each push.

## 2026-10-11 — bar snapping off

**In the test project.** The bar test `WithNoSectionNearbyADragStillSnapsToBarLines` is replaced
by `WithNoSectionNearbyADragLandsExactlyWhereItWasDropped`: PROJECT BPM 120 (a bar line every 2 s),
snapping on, a section dropped at 36.7 s, far from any edge. It must land on exactly
`SecondsToFrames(36.7)` (`==`, in frames) and report no snap. The edge-snapping tests are unchanged
and still pass; `SnapSettings` no longer carries a tempo, so nothing else can reintroduce bar
snapping without changing its signature. The fuzz test still draws the random number that used to
pick its bar tempo, so every recorded seed replays the same sequence. **199 tests, 0 failed,
0 skipped.**

**In the real window.** `--gesturetest` grew from 80 to **81 checks**. The new one sets PROJECT BPM
to 120 and drags a section with no modifier, through the lane handlers, from 48.10 s to 49.47 s —
1.47 s from the nearest section edge, 0.53 s from the bar line at 50 s that the old snap would have
pulled it to. Published exe, 2026-10-11:

```
PASS  a plain drag far from any section lands exactly where it was dropped, not on a bar line (at 49.47000s, expected 49.47000)
gesturetest       PASS  exit 0   outside: focus taken: no  on screen: no  taskbar button: no  activatable: no
```

Run with `verify-exe.ps1 -Only gesturetest`: the change touches no audio, timing or native code,
so the other seven runs were not repeated. Before this change no check dragged without Ctrl away
from a section edge, so nothing in the window proved where a plain drag lands.

**Not verified:** the new check was not run against a build with the old code; that it would have
failed there (landing at 50.00 s) is read off the removed branch, not measured.

## 2026-10-11 — a click of the scroll wheel stops

**In the test project.** `TransportTests` (4), on the offline engine pulled block by block the way a
device pulls it: stop goes back to where play started (30.000 s), not to 0:00 or to where it got to;
**Space after a pause is where the wheel-click goes back to** (31.000 s); a second Play while playing
does not move the return point; the return point read before a re-seek and put back afterwards (what
undo does) still holds. All `==` on frames. Run against a build with the new line in `Play()` taken
out, `SpaceAfterAPauseIsWhereTheWheelClickGoesBackTo` fails and the other three pass — they pin
behaviour the engine already had. **203 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 81 to **86 checks**. The wheel-click is raised on
a real lane, so it travels the route a real press does, window first:

```
PASS  a click of the scroll wheel stops and goes back to where play started (engine at 30.000s, started at 30.000s)
PASS  the wheel-click is not passed on to whatever is under the pointer
PASS  after Space pauses and plays on, the wheel-click goes back to where Space started it (engine at 31.000s, expected 31.000s)
PASS  a left press is not a stop
PASS  a wheel-click in the middle of a drag stops playback and the drag carries on (landed 2.000s along, expected 2.000)
```

**On the real sound device.** `--playtest` (silent, master at zero) now also starts and wheel-click
stops eight times, at different moments of the device's cycle, and requires each to land on the very
frame play started from: **8 of 8, `off=0`**. That proves the return lands exactly with a live audio
thread. It does not prove the race fix in `Stop()`: rendering a block takes a small fraction of each
device period, so a Stop without the wait would usually land exactly too.

**In the published exe** (`scripts/verify-exe.ps1`, all eight runs):

```
latencycheck PASS   gesturetest PASS (86 checks)   buttontest PASS (31 checks)   loadcheck PASS
playtest PASS (realtime; 8 of 8 wheel-clicks exact)   stemtest PASS   uiprobe-sections PASS   uiprobe-editing PASS
VERIFY: every run passed and stayed out of the way
```

**Not verified:** a real wheel press. The checks raise the event; a mouse whose driver maps the wheel
button to something else (some send it as a keystroke) would never reach the window.

## 2026-10-11 — double-click to play from there

No unit test: this is wiring in the window, which the test project cannot see. **203 tests,
0 failed, 0 skipped**, unchanged.

**In the real window.** `--gesturetest` grew from 86 to **95 checks**. The ruler's press, move and
release were split out of their event handlers so the test can make a double-click (WPF lets nobody
set `ClickCount`); the three old "double-click" checks had called `PlayFrom` directly and so proved
nothing about the ruler. Every check below goes through the real handler with a scripted pointer
and click count, and asks the engine where it is. Published exe, 2026-10-11:

```
PASS  double-click on the ruler seeks (engine at 18.500s)
PASS  double-click on the ruler starts the transport
PASS  double-click on the ruler while playing jumps there and keeps playing (engine at 25.000s)
PASS  Shift+double-click on the ruler still sets a listening loop and does not play (20.00s to 22.00s)
PASS  a double-click on a section plays from where it was double-clicked (engine at 2.250s)
PASS  the second press of a double-click drags nothing, even if the hand moves before letting go
PASS  a double-click while playing jumps there and keeps playing (engine at 3.100s)
PASS  a double-click on a section's edge plays and trims nothing
PASS  a Shift+double-click on a lane is two Shift+clicks: it does not play
PASS  a double-click on empty lane space plays from there (engine at 272.206s)
PASS  a double-click under the last track plays from there (engine at 251.706s)
PASS  a plain click under the last track lets go of the selected sections
```

`scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way. The editing probe's
picture was looked at after the track list was wrapped in a grid to make room under it: rows, strips
and lanes unchanged, the room under the last track empty.

**Not verified:** Windows' own double-click timing and distance under a real hand. The checks hand
the handler a click count of 2; whether two real clicks are close enough together to be one is
Windows' decision (its double-click setting).

## 2026-10-11 — renaming a track, and Space as a bare key

**In the test project.** `TrackOrderAndNameTests` (6): a blank name (empty, spaces, tabs and line
breaks, null) is refused and the old name kept; spaces around a name are trimmed, a pasted line break
becomes a space, a 200-character name is cut at 80; renaming to the same name is not a change; a
rename of a track no longer in the project writes nothing; a renamed track keeps its name through
save and load, and undo gives the old one back; an empty track he named "Drums" keeps that name when
a song is put on it. **209 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 95 to **115 checks**, through the window's own
handlers with a scripted focused element — the box is never actually given the keyboard:

```
PASS  a single click on a track's name does not open it for typing
PASS  double-clicking a track's name opens it for typing
PASS  the box starts with the current name ('Still D.R.E. (Instrumental)')
PASS  the box is never given the keyboard in a scripted run
PASS  Enter keeps the new name, on screen and in the project ('Still D.R.E. (Instrumental) (renamed)')
PASS  one undo puts the old name back ('Still D.R.E. (Instrumental)')
PASS  Esc leaves the name as it was
PASS  a blank name is refused and the old one kept ("A track needs a name — kept "Still D.R.E. (Instrumental)".")
PASS  a press inside the name box does not end the renaming
PASS  the box's own right-click menu taking the keyboard does not end the renaming
PASS  switching to another program does not end the renaming
PASS  clicking elsewhere keeps what was typed ('Clicked away')
PASS  a name still being typed when an undo rebuilds the tracks is dropped, not written in afterwards
PASS  the name says how to rename it on hover
PASS  the control map labels the name box textbox:rename and PROJECT BPM textbox:projectBpm
PASS  Space is not a bare KeyBinding, so it cannot fire inside a text box
PASS  Space on the timeline plays, and again pauses
PASS  Space with PROJECT BPM focused is left to the box
PASS  Space while typing a track's name is left to the box, so a name can have spaces in it
PASS  Space with a track's button focused plays, and is used up before it can press that button again
```

`--buttontest` grew from 31 to **36 checks**: the strip's right-click menu offers *Rename track*; a
right-click on a strip selects that track (the bed is selected first, so renaming the wrong track
cannot pass); the real menu item, pressed through its automation peer, opens that track's name
filled in with the current name; typing and Enter renames it on screen and in the project; one
Ctrl+Z puts the old name back.

The menu check failed the first time it ran: a right-click menu's items only inherit their track
once the menu has opened, and the check presses the item without opening it (an open popup would
appear on his screen). The lane menu's *Split into vocals + instrumental* had been passing the same
way only because it falls back to the selected track. *Rename track* now does the same; in real use
the right-press that opens the menu has just selected that track.

The full set then failed once, 13 gesture checks at once, on a build that had just passed: the
gestures read the real keyboard's modifiers in a scripted run (see DECISIONS.md). Fixed; all eight
runs then passed and stayed out of the way: gesturetest 115, buttontest 36.

**Not verified:** typing into the box with a real keyboard; the keyboard really moving into the box
when it opens (never done in a scripted run, which must not take the keyboard), and in particular
after choosing *Rename track* from a menu that has just closed; and Space after really clicking a
button. By hand: double-click a name, type a name with spaces, press Enter; right-click a strip →
Rename track, type, click a lane.

## 2026-10-11 — dragging a track up or down

**In the test project** (`TrackOrderAndNameTests`, 5 more): a moved track is in its new place when
the project is saved and reopened; one undo puts the order back; moving a track onto its own place,
or the top one further up, is not a change, and past the bottom lands at the bottom; a Shift+click
range from A to C takes B's section with the tracks in order A, B, C, and **not** once B has been
moved to the top; and **moving a track is not heard** — three tracks (the ramp, a 431 Hz tone and
noise) render the same, sample for sample (`==`), straight after the move, and within 2.4e-7 of the
original after a reload rebuilds the voices in the new order. **214 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 115 to **125 checks**, through the name row's own
handlers with a scripted pointer. Published exe, 2026-10-11:

```
PASS  a small pull on a track's name is a click, not a move
PASS  a line shows where the track will land while it is dragged, and goes away after
PASS  dragging a track's name down past the next one moves it down (now place 2)
PASS  the project's order moves with the rows, so saving, undo and Shift+click see the same order
PASS  the moved track keeps its voice: the sound engine is not touched
PASS  the status bar says where it went ("Moved Still D.R.E. (Instrumental) down, to place 2 of 3.")
PASS  one undo puts the order back
PASS  dragging a track's name up past the one above moves it up (now place 1)
PASS  a track cannot be moved while a section is held under the mouse ("Let go of the section first.")
PASS  the grip explains itself on hover
```

`scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way. The editing probe's
picture was looked at: the ≡ grip sits left of each track's colour, the rest of the strip unchanged.

**Not verified:** the drag under a real hand with real mouse capture (a scripted run takes no
capture), and the line's position when the rows are scrolled part-way.

## 2026-10-11 — the selection box

**In the test project** (`SectionSelectionTests`, 6 more, on the four-section ramp tracks): a box
over 15–25 s takes the two sections it half covers; a box over two of three tracks takes nothing
from the third; a section whose file is missing is never taken; a box ending exactly where a section
starts does not take it, and a box of no width takes the section under it; dragged either way the
box takes the same sections; and they come topmost track first, then by time, so the first is the
top-left one. **220 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 125 to **140 checks**, through the lanes' and the
empty room's own press, move and release handlers with a scripted pointer. What the box should take
is worked out in the check from the sections and rows on screen, not from the code that selects.
The song is cut into 3 s pieces with one taken out, so there is a gap to start from, and a copy of
the piece before the gap is pasted onto the row below, so the box must reach two rows. Published
exe, 2026-10-11:

```
PASS  a box started under the last track selects what it reaches on the rows above (got 1, expected 1)
PASS  a click on empty lane space without dragging still lets go of the selection
PASS  dragging across empty lane space draws a box where the pointer went (at 180, 76, 120 x 152 px)
PASS  the selection follows the box while it is dragged (0 while small, 2 when larger)
PASS  every section the box touches is selected, on every row it covers, even ones only partly inside (got 2, expected 2)
PASS  the box disappears when the button is let go
PASS  Delete is refused while a box is being dragged, even with sections in it selected
PASS  after a box, the earliest section on the topmost row is the one commands act on, and the status bar counts them
PASS  Ctrl held as the box starts adds to the selection (3 selected)
PASS  a box over empty space selects nothing, and lets go of what was selected
PASS  a box from under the tracks is let go when the button is (its own press, move and release handlers ran)
PASS  a Control+click on empty lane space keeps the sections already selected (3 of 3)
PASS  a Shift+click on empty lane space keeps the sections already selected (3 of 3)
PASS  a Control+click under the tracks keeps the sections already selected (3 of 3)
PASS  a right-press while a box is held keeps what the box selected (2 of 2)
```

**The review** (one reader over the diff, as planned for this feature) found three things, all fixed
the same day: a right-press while the left button held a box — the start of a pan, say — replaced
the box's selection with whatever was under the pointer; the checks for a box under the tracks
called the box code by name, so the room's own press, move and release handlers were never run (they
are now raised as routed events on it); and nothing checked that a Ctrl- or Shift-click on empty space
still keeps the selection. The right-press check was run first against a build without the fix and
failed (1 of 2 kept).

The double-click checks under the tracks now release each press, as WPF does, because a press there
arms a box.

**Not verified:** the box under a real hand with real mouse capture (a scripted run takes none), a
box dragged while the rows are scrolled part-way, and the box's look — it is drawn as a translucent
blue rectangle, but no probe picture shows one mid-drag.

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
- **Published build runs standalone.** 78 MB single self-contained file, clean exit, desktop
  shortcut at `OneDrive\Desktop\Benchmix.lnk`.
- **Zero build warnings** across the solution.

## The UI, driven for real

`scripts/ui-verify.ps1` presses the actual buttons in the actual window and reads the actual
labels back. On 2026-09-16 it did so from outside, through UI Automation, with the window
minimised; since 2026-09-24 the same presses happen inside the app (`--buttontest`) — see "Staying
out of the way" below for why. **10/10 checks passed** on 2026-09-16:

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

The 2026-09-24 in-app version makes the same checks, plus the button counts: **12/12 pass** in the
published exe. Since 2026-10-03 it also splits the topper through the real right-click menu item —
**22 checks** (see above).

## Staying out of the way

*2026-09-24.* Daniel uses this machine while work happens here, and the verification runs were
interrupting him — his words: *"I keep getting interrupted by you opening and closing the app,
playing songs, etc."* Two things were wrong. The runs were launched minimised, which still puts a
button on the taskbar for each one. And every run that loaded a song ended by closing an unsaved
project, which popped *"This project has unsaved changes. Discard them?"* on his screen until a
two-second watchdog killed the process — the log's "shutdown did not take, exiting hard".

Now any verification switch, or `--autoclose`, makes a **scripted run** (`App.IsScriptedRun`): the
window is placed beyond every monitor, has no taskbar button, carries `WS_EX_NOACTIVATE` so Windows
never activates it, shows no dialog of any kind, and — except `--playtest` — uses the offline
engine, so the audio device is never opened. A mistyped switch is refused before any window
exists rather than becoming an ordinary on-screen session.

It is measured twice, independently. Inside, `EndVerificationRun` asks Windows (not the WPF
properties it set) whether the window is on any monitor, has a taskbar button or can be activated,
and whether it was ever activated, and fails the run on any of them. Outside,
`scripts/Intrusion.cs` starts each run without the "working in background" pointer and samples it
on its own thread about every 10 ms: whether the foreground window belongs to it, and whether any
of its visible windows is on a monitor, would have a taskbar button, or could be activated. The
watcher was first pointed at Daniel's own open Benchmix, a normal window, to prove it can say yes:
on screen, taskbar button, activatable — all YES.

`scripts/verify-exe.ps1`, published exe, all eight runs:

```
latencycheck      PASS  focus taken: no  on screen: no  taskbar button: no  activatable: no
gesturetest       PASS  (58 checks)                               — same four: no
buttontest        PASS  (12 checks)                               — same four: no
loadcheck         PASS                                            — same four: no
playtest          PASS  (ratio 1.000, the one run with a device)  — same four: no
stemtest          PASS                                            — same four: no
uiprobe-sections  PASS                                            — same four: no
uiprobe-editing   PASS                                            — same four: no
VERIFY: every run passed and stayed out of the way
```

**What the watch caught.** The first run of the old `ui-verify.ps1` under this regime — still
driving the buttons from outside with UI Automation — passed every functional check and failed on
both watches: *took focus = YES*. The app's own log put the activation at 14.06 s after launch, the
exact moment the script woke from its 14-second wait and made its first UI Automation call; the
window then held the keyboard for about 30 seconds until the run closed itself. Anything typed in
that time went to the hidden window. `WS_EX_NOACTIVATE` did not stop it. So the button presses
moved inside the app (`--buttontest`), through each button's own automation peer, and that run
takes no focus.

Two adversarial review rounds (52 and 49 agents, none of them allowed to launch the app) went over
this before it was run; between them they found the activation route through Windows' own
choice of the next window, the dialogs, the switch-parsing hole, a `--uiprobe` path that would
have deleted a song given in the wrong argument position, and checks that could not fail. All
are fixed.

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

- **Nobody has listened to a loop join, or to a section moved between tracks.** The join numbers
  above are a sine, not music. A section dragged onto another track at the same moment gets a 5 ms
  declick at each end on both tracks, so the level dips there; that is read off the envelope code,
  not measured, and not heard.
- **Drag feel is untested.** The gestures are driven with a scripted pointer; how they feel under
  a real mouse, and whether 10 px is the right snap distance, needs a person.
- **Nobody has listened to live playback through the speakers.** The pipeline is proven to run at
  realtime and the rendered files are proven correct; the final "does sound come out" step needs a
  human to press play. Thirty seconds of Daniel's time.
- **Pointer gestures are exercised by `--gesturetest`** (57 checks, including the section commands
  and, since 2026-09-24, section drags, trims, snapping and loop mode through the lane handlers).
  Buttons are also driven and checked by `ui-verify.ps1`. Knob dragging and shift-drag loop
  selection are not driven by either. The feel of all of them is untested.
- **Long-session behaviour.** No soak test. Memory should be flat once clips are loaded, but that
  is reasoning, not measurement.
- **Stem separation quality** is proven only by a 39 dB control test, not by ear — see
  `DECISIONS.md`. (It is in the app; this line used to say it was not.)
