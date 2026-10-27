# Benchmix — what has actually been verified

*2026-09-16.* Evidence, not assurances. Every number here was measured on the target machine.

## The test suite

**319 tests, 0 failures, 0 skipped** (2026-10-12). `dotnet test` — about 90 seconds (the stem
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

## 2026-10-11 — dragging several sections at once

**In the test project.** `GroupMoveTests` (16), on a ramp source:
- two sections moved together at speed 0.93 to a point off a whole frame still meet at a seam (`==`
  on frames and source, `SeamAfter`);
- a split section moved as a group renders the same as the unsplit one moved the same way (every
  sample `==`);
- a group on two tracks at different tempos moves the same time on both (within a frame), and
  stays on its own tracks whatever track it is dragged over;
- a one-track group dropped on an empty track lands together, plays at the speed it had, and the
  empty track is put back as it was when only passed over;
- the group stops at 0:00 as a whole without being squeezed; dragged before a track's start it
  moves the start back, and dragged back again puts it back (`==`);
- any member snaps to another section's exact frame (a butt join), never to its own old edges,
  never to a bar, and not with Ctrl;
- the drop overwrites what is under it but never its own sections;
- a knob turned during the drag is kept;
- a group pushed against 0:00 is not snapped further back on one track only — run first without
  that guard, it failed (the two decks landed 0.03 s apart).

`SectionFuzzTests.NoSequenceOfGroupMovesLeavesATrackBroken` (4 seeds × 150 steps): groups of two
to four sections from anywhere, dragged anywhere; every track keeps its rules, and sections that
started on one track end on one track, the same frames apart. **240 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 140 to **158 checks**, through the lanes'
own handlers with a scripted pointer:

```
PASS  a plain press on one of several selected sections keeps them all selected while the button is down (2)
PASS  letting go without moving leaves just that one selected
PASS  dragging one of several selected sections moves them all by the same amount (+3.0000s and +3.0000s)
PASS  the seam between two sections moved together is still a seam, so it plays straight through
PASS  both stay selected after the drop, and the one grabbed is the one commands act on
PASS  one undo puts the whole group back
PASS  a group on one track follows the pointer onto another track, together (2 landed there)
PASS  the empty track took the group's tempo and pitch
PASS  a group on two tracks moves sideways only: each stays on its own track, both 2 s later
PASS  a straight pull on a group across two tracks keeps the whole group selected (2 of 2)
PASS  a drag on the edge of a selected section trims just that one and lets go of the rest (2 selected on the press, 1 once trimming)
PASS  Ctrl+I, Ctrl+D, Ctrl+X and Ctrl+Z each wait while a group is held, from the press on
PASS  double-clicking one of several selected sections plays and keeps them all selected (2)
PASS  double-clicking a selected section's edge plays and keeps the group selected (2)
PASS  double-clicking empty space plays from there and keeps what was selected (2)
PASS  two sections 12 px wide can be grabbed by the middle and dragged as a group
```

**The review** (one reader over the diff) found seven things, all fixed before anything was
committed: a straight pull on a group across two tracks counted as a click and threw the group
away; a double-click near a member's edge lost the group; Ctrl+X, Delete and Clear trim did not
wait while a section was pressed but not yet moved, so a cut section could come back under the
drag; the held-edits check proved only Ctrl+I (it now checks each command, pressed and dragging);
the snap guard at 0:00 had no test; the smaller edge handles also changed single short sections
(now only selected ones in a group); and the wait-while-busy rule blocked single moves during a
song split (now groups only). The three window fixes were run first against a build without them:
those three checks failed, then passed.

`scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way (gesturetest 158,
buttontest 36).

**Not verified:** the drag under a real hand with real mouse capture, a group dragged while the rows
are scrolled part-way, and how a group dropped across many sections sounds — what lands is checked,
not listened to.

## 2026-10-11 — tempo ramps, part 1: the sound engine

**A track without a ramp is unchanged.** Every test from before passed untouched. 30-second
renders of two real songs through the command-line renderer — untouched, LINK and phase vocoder —
were taken from the working tree just before the change and again after it: all three files hash
the same (`819464bf2adcdcdb`, `d3fea3134b55cec4`, `6086fee0a8249967`).

**In the test project.** Where each moment of the song should come out is worked out from the
integral written in the test, never asked of the tempo map.
- `TempoRampRenderTests` (27), on the real voice:
  - smooth swells on a 431 Hz carrier before, all through and after a ramp, up (0.92→1.10) and
    down (1.10→0.90), on the phase vocoder and on LINK, and from the song's own tempo (110 → 115 BPM
    on the vocoder), in blocks of 1000 and 4096: **all 90 swells land within 2 frames** of where the
    ramp puts them;
  - the stretcher is never restarted part-way through a ramp, at blocks of 333, 1000 and 4096;
  - on LINK a ramp renders the same sample for sample in blocks of 333 and 4096 (`==`), so preview
    and export agree; a seek into a ramp plays exactly what playing through it does (`==`); on the
    vocoder it lands in time;
  - two renders of one ramped track are identical; a split inside a ramp changes no sample on LINK
    (`==`) and does not restart the vocoder (largest difference from the unsplit render 0.00000);
  - a cut edge inside a ramp is declicked on its own frames, and the gap after it is silent;
  - a render runs to the end of a ramped track; any ramp sends the whole track through the
    stretcher, and removing it makes the track untouched again.
- `TempoMapTests` (9): with no ramp the map is exactly the multiply and divide it replaced (`==`,
  at ten speeds from 0.25 to 4 and 4,000 random frames out to ±10⁸); before the first ramp it is
  exactly the knob (`==`); the position never steps backwards; the inverse brackets the render
  loop's frame exactly at every frame near a ramp's edges and middle, and is within one frame of
  the integral there; the tempo never
  jumps at a ramp's edges; it goes evenly in BPM and holds after; a ramp before a track's start is
  measured from it; impossible ramps are refused by one builder and dropped by the other.
- `TempoRampEditingTests` (18): a ramp moves nothing before it and later sections by what the tempo
  says; moving a ramped track's start leaves every section and ramp where they were; a section
  dragged under a ramp lands on a whole frame where it was dropped, and its end snaps to the next
  section's start; a split under a ramp is a seam; an empty track takes a section's ramps with its
  tempo and gives them back; a paste onto an empty track and the tracks from *Split* take their
  song's ramps; a ramped project reopens rendering byte for byte the same; it is saved as version
  3; a version 2 file opens with no ramps; ramps that cannot be played are dropped on load; undo
  is deep for ramps; a group moved into a ramp keeps its seam.
- `JoinTests` run every join check on two ramped paths as well, plus a butt join inside a ramp on
  a frame that rounds awkwardly: still crossfaded.
- `SectionFuzzTests.NoSequenceOfEditsUnderTempoRampsLeavesATrackBroken` (4 seeds × 150 steps): every
  track keeps its rules, its ramps never overlap, and the engine plays exactly the tempo map the
  project describes.

**Broken on purpose.** With the read-ahead under a ramp taken out, 4 swell tests failed; with the
vocoder fed by an accumulated count instead of the closed form, 3 restart tests failed. Both
passed again once put back.

**The review** (one reader over the diff) found five things, all fixed before anything was
committed:
- **Snapping a group under a tempo ramp missed the line.** A group whose section under a ramp
  snapped to an edge on another track landed short of the guide (about 22 ms in the reviewer's
  case): the move was the gap added to the grabbed section's start, and under a ramp the sections
  move by the same frames, not the same time. It is now worked back from the exact frame the edge
  lands on. `AGroupUnderARampSnapsItsEndOntoAnotherTracksEdge` and
  `AGroupAcrossTracksUnderARampSnapsAndStillMovesTogether` (which also checks that both decks
  still move by the same time).
- **A paste before a ramped track's start reached the engine in three steps** — origin, sections,
  tempo map — and a block played between them would put the ramp seconds away and jump back. The
  three now go in one step (`TrackVoice.Place`); `AnEditBeforeARampedTracksStartReachesTheEngineInOneStep`
  counts the steps. The same gap existed between the origin and the sections before tempo ramps.
- **Two checks that the engine plays the project's tempo map compared it with itself.** Replaced by
  a check that every section starts in the engine on the frame the lane draws it at — the engine's
  map in frames against the editor's in seconds — in the fuzz test and in
  `ARampedTrackPlaysEachSectionWhereTheLaneDrawsIt`, which renders a section on a track that
  starts at 30 s under a ramp that begins before it, and reads where it starts and stops off the audio.
- **The test for a section's end snapping under a ramp would have passed without the fix**: at its
  zoom the snap reached far enough for the ramp-blind estimate too. Zoomed in, it no longer does.
- **No test rendered a ramp from the song's own tempo**, his own case (110 → 115 BPM from 1.0×),
  where nothing else forces the stretcher on. Added to the timing and the restart tests.

Each fix was then taken out again, one at a time, and its tests run: both group tests failed; the
placement test and all four fuzz seeds failed with the engine's ramps measured from 0:00 instead of
the track's start; both restart tests from the song's own tempo failed with a ramped track allowed
to skip the stretcher at 1.0×; the step count failed with the three separate writes; and the
end-snap test, once it drops short of the edge, failed with the ramp-blind estimate. (Dropped long,
it had passed either way: the overwrite trims the next section into a join.)

**In the published exe.** `--latencycheck` now also plays seven 431 Hz bursts untouched and through
the phase vocoder speeding up from 1.0 to 1.08 over 17.5 s. Each lands within 166 frames
(3.5 ms) of where the ramp puts it: 1, 6, −19, −121, −122, −166, −125. That spread is the
measuring, not the ramp: the check finds each burst by the first sample over a threshold, and the
vocoder smears an onset. Measured the same way at a constant speed, with no ramp at all, 1.04 gave
−68 to −158 frames and 1.08 gave −49 to −218; at 1.0, 0. The swells above are the precise measure.
`scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way (gesturetest 158,
buttontest 36). **304 tests, 0 failed, 0 skipped.**

**Not verified:** nobody has listened to a tempo ramp; the swells are a sine, not music.

## 2026-10-12 — tempo ramps, part 2: making and changing them

**In the test project.** `TempoRampEditingTests` grew by seven, on the ramp WAV:
- a ramp starts at exactly the tempo the track plays there (`==`), and a second one at exactly the
  tempo the first arrived at;
- a new ramp's ends land on whole frames, and the status says where it goes and how much later
  audio moves (1.36 s sooner for 10 s from 1.0× to 1.1× — checked against the arithmetic, not the map);
- a ramp cannot start inside another or run into the next; butting both neighbours is allowed; the
  free room between ramps is right;
- a typed tempo is read as typed; a word, a tempo the engine cannot play, and a track with no known
  tempo are refused, with messages;
- changing the end tempo moves later sections, and putting it back puts them back exactly (`==`);
  removing the ramp puts them back where they were before it (`==`);
- dragging a dot stops at the next ramp and at its own ramp's other end, lands on a whole frame, and
  dragging it back restores the ramp exactly;
- a start dot snaps to its own track's section edge, an end dot does not (it would not stay there),
  both snap to another deck's section, and Ctrl frees them.

`ProjectTests.SaveThenLoadRoundTripsEveryField` now carries two ramps. **311 tests, 0 failed,
0 skipped.**

**In the real window.** `--gesturetest` grew from 158 to **187 checks**, through the lane's own
handlers with a scripted pointer, modifiers and focus:

```
PASS  Ctrl+R is bound to ramp mode
PASS  Escape is not a bare KeyBinding, so it cannot fire inside a text box
PASS  RAMP MODE turns on through its own toggle
PASS  loop mode and ramp mode are never on together
PASS  a track whose tempo is unknown starts no ramp and says why
PASS  the first click puts a dot at 30.00s, filled in with the tempo it plays there (93.0 BPM)
PASS  Esc with PROJECT BPM focused is left to the box
PASS  Esc takes the first dot away again
PASS  the second click opens the tempo box beside the end point, filled in with 93.0
PASS  the box is never given the keyboard in a scripted run
PASS  the control map labels the box textbox:rampBpm
PASS  typing 97.5 and Enter makes the ramp 30.00s → 45.00s, ending at 97.5 BPM
PASS  the engine plays the tempo map the project describes
PASS  the lane is handed the ramp to draw
PASS  a section after the ramp now comes 0.67s sooner, as the faster tempo says
PASS  a section before the ramp did not move
PASS  it is drawn going up: first dot on the bottom edge, second on the top, joined by a line
PASS  Ctrl+R waits while a ramp's dot is held
PASS  dragging the end dot to 50 s moves only the end
PASS  clicking the end dot opens the box with 97.5
PASS  typing 99.0 and Enter changes only the end tempo
PASS  a tempo it cannot reach is refused and the box stays open
PASS  clicking elsewhere keeps what was typed, and that click starts nothing else
PASS  a new ramp cannot start inside another
PASS  in ramp mode a drag across a section moves no section
PASS  the right-click menu offers Remove tempo ramp over a ramp, and not elsewhere
PASS  Remove tempo ramp takes it off, and the section after it goes back where it was
PASS  each ramp edit was one undo step: back to 101.0, then 99.0, then 97.5, then ending at 45 s, then none
PASS  RAMP MODE turns off through its toggle and drops an unfinished ramp
```

The section after the ramp is checked against the integral written out in the check, within a
frame. `--buttontest` grew from 36 to **41**: a ramp on the topper to 100.0 BPM; the real *Match
BPM* button sets the tempo before it and keeps where it goes, and says so; the real *Reset*
removes it; one Ctrl+Z brings it back with the matched tempo; two more take back the match and the
ramp, one each. Both demo pictures now draw ramps: `--demosections` a ramp going down and one being
typed (dashed, following the typed tempo), `--demoediting` one going up and one coming back down,
with LOOP MODE still on. Looked at: the slowing ramp runs from the top edge to the bottom, the
speeding one from the bottom to the top.

`scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way (gesturetest 187,
buttontest 41).

**Not verified:** nobody has listened to a ramp; typing into the real box with real keyboard focus,
and the dots under a real hand, have not been tried — the checks drive the handlers with a scripted
pointer and focus; the window checks were not re-run against a deliberately broken build.

## 2026-10-12 — the wheel over the ruler, and a double-click of the wheel

**In the real window.** `--gesturetest` grew from 187 to **196 checks**, through the ruler's and
the tracks' own wheel handlers with a scripted pointer and modifiers, and the window's own press
handler for the wheel button:

```
PASS  a double-click of the scroll wheel stops, and the playhead and the view go back to 0:00
PASS  a notch of the wheel up over the ruler moves the playhead 0.50s later (engine at 10.500s)
PASS  two notches down move it back 1.00s (engine at 9.500s)
PASS  it stops at 0:00
PASS  while playing it carries on playing from the new spot
PASS  over the tracks the wheel still scrolls them up and down, and leaves the playhead alone
PASS  Ctrl+wheel over the ruler still zooms, and leaves the playhead alone
PASS  Shift+wheel over the ruler still scrolls sideways, and leaves the playhead alone
PASS  turned past the right edge of the view, the view follows the playhead
```

The engine itself is asked where the playhead is. **311 tests, 0 failed, 0 skipped**; `--playtest`
still lands a wheel-click back where play started. Run on a staged build, because his copy of
Benchmix was open.

**Not verified:** a real wheel, including a smooth-scrolling one that sends part-notches, and a
real double-click of the wheel, have not been tried by hand.

## 2026-10-12 — joining sections back into one

**In the test project.** `SectionJoinTests` (8), on a ramp source: splitting a section in three
and joining it back changes not one sample (`==` over 30 s); cutting a 4 s piece out and joining
around the gap renders exactly the song before it was cut (`==`), also under a tempo ramp; one
selected section finds the neighbour that lines up, the next one first; a loop's repeat, a section
of another song, and a section put into the gap each stop a join, with nothing changed; the outer
fades are kept. Broken on purpose — the line-up rule made to accept any later piece of the same
song — the repeat test failed. **319 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 196 to **202 checks**: Ctrl+J is bound; split a
song, move the middle piece to another track, select what is left and Ctrl+J — one section again,
the 4.0 s gap filled, the moved piece left where it was put; one Ctrl+Z takes it back; *Join
sections* in the lane menu joins the two selected; a repeat is refused with the reason. Run on a
staged build, because his copy of Benchmix was open.

**Not verified:** by hand.

## 2026-10-12 — typing a knob's exact value

**In the real window.** `--gesturetest` grew from 202 to **214 checks**, through the knobs' own press
handling and the box's own key handler:

```
PASS  double-clicking TEMPO's number opens a box over it, filled in with its value (0.00)
PASS  the box is never given the keyboard in a scripted run
PASS  typing -4.21 and Enter sets TEMPO to 4.21% slower, exactly, and shows it (-4.21%)
PASS  typing −2.88 sets PITCH to 2.88 semitones down (-2.88 st)
PASS  typing -6.5 sets LOW to -6.5 dB (-6.5)
PASS  typing KILL sets VOL all the way down (-∞ dB)
PASS  a value past the knob's range is refused, the box stays open and says the range ("TEMPO goes from -50.00% to +50.00%.")
PASS  Esc keeps the value as it was
PASS  clicking elsewhere keeps what was typed
PASS  a double-click on the knob itself still resets it
PASS  with LINK on, PITCH cannot be typed and the status says why
PASS  one Ctrl+Z takes back the reset, and the next the typed HIGH — each one undo step
```

The values are checked with `==` against the arithmetic written in the check. `--demoediting` now
shows the box open over a TEMPO number; looked at, it sits over the number. **319 tests, 0 failed,
0 skipped**; `scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way (gesturetest
214, buttontest 41).

**Not verified:** typing into the box with the real keyboard and real focus.

## 2026-10-12 — the review of everything after tempo ramps part 1

Two readers over `552a2f8..b73d9fc` (tempo ramps part 2, the wheel, joining, typing knob values),
about 536,000 tokens between them. They found **13 things, all real, all fixed**:
- a knob's box opened only to read it wrote the value back rounded to what it showed (after Match
  BPM, −8.8235% became −8.82%); the same for a ramp's tempo box, and a new ramp left as filled in
  ended 0.04 BPM off and held it — **the two that mattered**;
- a double-click's second press in ramp mode closed the tempo box the first had opened;
- Ctrl+L was not refused while a ramp's dot was held;
- a right-click inside the ramp's box finished the typing;
- *Remove tempo ramp* could act on a ramp from an earlier right-click, by an index that had moved;
- a ramp clicked right to left was snapped for the wrong ends;
- a twitch on the first click of a double-click on a knob's number turned the knob;
- a knob's box stayed open over rows that New, Open or undo had rebuilt;
- turned fast while playing, the wheel over the ruler lost notches;
- four checks could not fail against what they were named for: the three "never given the
  keyboard" checks (a queued focus had not run yet when they looked — they now let it run first),
  the one-undo-step check for typed knob values (every step was on a different knob), and the
  wheel double-click check (it skipped the window's own handler, where the click count comes in).

**In the real window**, `--gesturetest` grew from 214 to **221 checks**: a double-click's second
press leaves the tempo box open; a right-click inside it leaves the typing open; Ctrl+R and Ctrl+L
wait while a dot is held; a ramp's box opened only to look keeps 99.03 exactly where it shows 99.0;
a right-click while a dot is held offers no *Remove tempo ramp* left over; a knob's box opened only
to read it leaves 93/102 exactly; a value typed straight after turning the same knob is its own
undo step; an undo that rebuilds the rows closes a knob's box. **Broken on purpose** — the two
"only to look" fixes, the click count reaching the window's handler, and the knob's own undo step
taken out together — the checks for each failed (7 of 221 with what followed from them), then
passed again once put back.

Not checked by a test, and said so: the right-to-left snapping, the knob's 4 px slop (it needs a
real mouse), and the fast-wheel notches (the offline engine seeks at once). **319 tests, 0 failed,
0 skipped**; `scripts/verify-exe.ps1`: all eight runs passed and stayed out of the way.

## 2026-10-12 — marking a stretch to copy, cut, delete or repeat

**In the test project.** `MarkedStretchTests` (12), on a ramp source (value == position), renders
checked against renders with `==`:
- a stretch copied across a seam plays the very audio that was marked wherever it is pasted, the
  seam inside it untouched (no guard around it);
- cut and pasted straight back where it was, a stretch that starts inside one section, crosses a
  seam and ends inside another changes **not one sample** over 30 s at 100% speed, and none under a
  tempo ramp;
- at 92% speed it equals the same song split at the stretch's two ends, exactly. Not against the
  unsplit song: measured while writing it, **a plain split on a slowed track moves samples by up to
  6.8e-6 at 90% and 2.3e-6 at 92% (about −103 dB)**, starting just after the split — the stretcher
  is fed in different blocks. At 100% and at 110% the same splits change nothing. This predates
  marking: Ctrl+I does it too. The split is still a seam (no declick);
- a stretch with a gap in it pastes the gap too and clears what was under it;
- delete leaves silence in the stretch and moves nothing outside it; repeat lands on the very frame
  the stretch ends; a stretch ending in a gap repeats every stretch-length;
- a section's own fade is kept and the side a stretch cuts has none; a stretch wholly in a gap has
  nothing to copy, and deleting it changes nothing;
- a mark's end snaps to the exact frame of an edge on its own track (at 90% speed, where that edge
  is not on a whole frame of the timeline), and to the playhead when that is nearer.

**Broken on purpose** — the copied piece's start moved one frame later — 6 of the 12 failed, then
passed again once put back. **331 tests, 0 failed, 0 skipped.**

**In the real window.** `--gesturetest` grew from 221 to **244 checks**, through the MARK MODE toggle's
own automation peer, the lane's own press, move and release handlers, the window's key handler and
the commands the keys are bound to: Ctrl+M is bound; the mode is never on with loop or ramp mode; a
drag marks the stretch where the pointer went, moves no section and lets go of the selection; the
lane is handed the mark to draw; an end snaps to the exact frame of a section's edge, and Ctrl lets
go; dragging an end moves only that end; a click puts the playhead there and keeps the mark; the lane
menu names the marked stretch; Ctrl+C copies across a seam; a click on an empty track and Ctrl+V
lands it at that frame with the seam intact and moves the mark onto it; Ctrl+X takes it out and one
undo puts it back, mark and all; Shift+click marks from the playhead; Delete, Ctrl+X and Ctrl+D wait
while the mouse is held; Delete removes exactly the stretch; Ctrl+D repeats on the frame it ends and
the mark walks onto the repeat; a double-click plays; turning the mode off takes the mark away and
the menu goes back to sections. `--demoediting` draws a mark on the song from 0:29 to 0:33; looked at,
it is the lit band with a bar at each end and "4.00 s" at its top left. `scripts/verify-exe.ps1`:
all eight runs passed and stayed out of the way (gesturetest 244, buttontest 41).

**Not verified:** marking with a real mouse, and how the band reads on his screen.

## 2026-10-12 — four things from his testing

**Opening a project from Explorer.** `scripts/verify-exe.ps1` gained an `openproject` run: it writes
a project file of the two sample songs (in the oldest project format) and starts the exe with only
that file on the command line, as Explorer does on a double-click, under `--loadcheck`. The run fails
unless the project opened and every track in it loaded. Before the fix the file was loaded as a song
and failed.

**The icon.** Drawn at 16, 24, 32, 48, 64, 128 and 256 px and looked at, all sizes side by side; the
published exe carries it (read back out of the published exe as a 32 px icon and looked at).

**Following the sound output.** `OutputFollowingTests` (10), through fake outputs whose streams the
test pulls by hand, on a 431 Hz sine through an untouched track, checked sample for sample with `==`:
Windows moving the output mid-song continues on the new one from exactly the frame reached; a stream
let go reads silence and never pulls the mix again; a stream that dies is replaced by the output
Windows uses now; with no output left playback stops, says so once, and takes the next output that
appears; a notice about the output already in use changes nothing; disposing lets go of the stream
and the notifications; an output at 44.1 kHz gets one second of mix per second (within the
converter's look-ahead) and the sine to within 2e-3; the converter alone keeps time and pitch from
48 to 44.1, 44.1 to 48 and 48 to 16 kHz across blocks of 1 to 4096 frames. **Broken on purpose** —
the stale-stream guard removed, and the converter one frame late — 5 of the 10 failed. `--playtest`
still pulls the real device in realtime through the new code.

**The wheel over the waveforms.** `--gesturetest` grew from 244 to **250 checks**, through the tracks' own
wheel handler: a notch over a waveform moves the playhead and is used up, and back; under the last
track too; over the track controls it is passed on and the playhead stays; over the tracks'
scrollbar it is passed on; Ctrl+wheel and Shift+wheel over a waveform still zoom and scroll.

**341 tests, 0 failed, 0 skipped**; `scripts/verify-exe.ps1`: all nine runs passed and stayed out of the way (gesturetest 250, buttontest 41, openproject).

**Not verified:** switching with his real Bluetooth headphones; how the converted sound compares by
ear; the icon on his desktop (Windows may show the old one until its icon cache refreshes).

## 2026-10-12 — the review of MARK MODE and the four fixes

Three read-only reviewers (the stretch core; mark mode in the app; following the output and the other
fixes), each followed by a skeptic told to refute every finding: six agents, about 1.07 million
tokens. **11 findings, 9 confirmed, all fixed**; 2 refuted (a snapping test said to be unable to fail —
the change it would miss is not a defect; and a start with no output never picking one up — that was
so before and was not promised). The four that mattered: a mark edge a hair past a section's edge left
a gap on cut-and-paste-back and on Ctrl+D; a paste after the copied song left the project put down
silence; a moment with no output while Windows switched stopped the song for good; song files pasted
in mark mode left the mark on the old place. Smaller: the repeat message gave song seconds, the
no-output message promised what it could not keep, a lost mouse counted as a click, a mark-mode click
did not end a knob's undo step, and the copy check could not fail.

**One regression test each where a test can reach it**: `MarkedStretchTests` +4, `OutputFollowingTests`
+4 (and the fake output can now refuse to open), and in the window three new checks (a knob turn either
side of a mark-mode click is two undo steps; a lost mouse is not a click; a song file put on a track in
mark mode gets the mark) plus the copy check made to look at what Ctrl+C took. **Broken on purpose** —
the tidying, the paste guard, keeping a working stream and resuming switched off — 7 of the 30 tests
in those two files failed, then passed again once put back.

**349 tests, 0 failed, 0 skipped**; `scripts/verify-exe.ps1` on a staged build (his copy of Benchmix was open): all nine runs passed and stayed out of the way (gesturetest 253, buttontest 41, openproject). This is the new review point.

## 2026-10-18 — warp markers

**Not measured on the target machine.** This was built in a cloud container on Linux: the .NET 10 SDK
from Ubuntu's archive, and the stretcher package's own Linux native library, which loads there. The
WPF app compiles there (`-p:EnableWindowsTargeting=true`) but cannot run, so **none of the exe switches
has run** — `--gesturetest` (23 new checks, 22 of them only with a song that has a section over 30 s),
`--buttontest` (5 new), `--latencycheck` (a pitched track through two moved markers, expected positions
written out), `--demoediting --uiprobe` (markers in the shot). The counts in CLAUDE.md, 253 and 41, are
from before; the new totals are read off his next `verify-exe.ps1` run. Nobody has listened to it.

**429 tests: 422 passed, 0 failed, 7 skipped** on Linux — the skips need his songs (3) or
DirectML (4), so the "0 skipped" bar is for his machine. New: `WarpMapTests` 14, `WarpEditingTests`
39, `WarpRenderTests` 27. What they measured:

- **Placing a marker changes nothing.** 200 markers put down one at a time on random frames of an
  untouched track: the engine's map stays the constant one after every one, and the render is
  bit-identical. On a ramped track too, two sessions compared sample for sample.
- **A dragged beat lands on the frame it was dropped on**; the marker on its left and everything
  before it stay exactly where they were (`==`); the one on its right within 1e-9 s.
- **Rendered through the real voice**, swells on a 431 Hz carrier before, between and after the
  markers land 0–2 frames from where the warp written out in the test puts them (200 allowed), on
  the vocoder and on LINK, over a ramp too, at blocks of 1000 and 4096. No stretcher restart. LINK
  bit-exact at blocks of 333, 1000, 4096 and 8192, and a seek plays exactly what playing through does.
- **A marker mid-block**: a swell 30 ms after a marker lands 21 frames off at blocks of 777, 1000,
  2500 and 4096 alike; without ending the render run at the marker it was 16, 15, −27 and −63.
- **Broken on purpose**: each of the four fixes made while building it (an unmoved marker's frames,
  group and loop drags putting markers back, a ramp's tempo without the warp) reverted on its own
  made exactly its own test fail.

**The review** — run without his go-ahead, against his rule of 2026-10-12, and said so to him: four
read-only reviewers (engine and core; the app; the exe checks; the tests, the rules and the docs),
each followed by a skeptic told to refute every finding: eight agents, about 1.86 million tokens.
**15 findings, 12 confirmed, all fixed**; 3 refuted (VERIFICATION had no entry yet; a ramp-mode
message said to be wrong inside a warped stretch — the tempo it names is the track's tempo, as
defined; a whole-frame check said to be fragile — it is right for its input). The one that mattered:
**grabbing a marker with no room either side — both stretches at twice or half their tempo — threw on
every pointer move**, which in the app is an error box; reproduced in 60 of 60 setups. Then: a project
saved at one rate and opened at another could drop a warp at the limit in the engine while the lane
kept it (5 of 16 projects); markers went on empty tracks; removing a middle marker said later audio
moved; the live drag status lacked the %; `--latencycheck`'s second marker was unmoved, so it could
not tell the last marker's move carried on from no warp; four tests could not fail, or were missing
(dragging back off the song's own tempo, the exact answer for an unmoved stretch, a section's end
snapping through a warp, a ramp left at its box's tempo through the real box); and one paraphrase in
DECISIONS read as his words. **Broken on purpose**: with each fixed bug put back, 10 of the warp tests
failed, each the one written for it; the exact-answer test first could not fail over 480 s (the
hair is rare that far in), was narrowed to the first 10 s, and then failed without the fix.

## 2026-10-27 — solo, one track at a time; warp markers on his PC

The first run on his machine since warp markers. **439 tests: 439 passed, 0 failed, 0 skipped** —
the seven warp-era skips were his songs and DirectML, both here. (439 = the 429 above, six for solo,
and the four of `SustainedLoopTests`, still uncommitted, measurement only.)

`scripts/verify-exe.ps1` on a staged build (`-p:ArtifactsPath=%LOCALAPPDATA%\Benchmix.stage\`, so
neither his Desktop copy nor the build folder that double-clicking a project opens was touched): all
nine runs passed and stayed out of the way. **The warp exe checks ran for the first time and passed**:
`--gesturetest` **276 checks** (253 before warp markers), `--latencycheck` with a pitched track through
two moved markers, the markers in `--demoediting`'s shot. `--buttontest` **55 checks** (46 with warp
markers, 9 for solo). Nobody has listened to a warped track.

**Solo** (`SoloTests`, `--buttontest`): a click on S plays that track alone, measured — the render
equals, sample for sample, a session holding only that song (two ramps running opposite ways, so
either leaking in shows). A click on another S moves the solo; Ctrl+click adds and takes away; a click
on the only soloed track turns solo off; mute still wins. Through the real S buttons, reading the
engine's own mute, not the lit button: the same five, the status hint, and one Ctrl+Z taking back a
click that changed both tracks. **Broken on purpose**: with a plain click made additive again, the two
tests written for it failed and the other four passed.

## 2026-10-27 — a render holds what can be heard

**444 tests: 444 passed, 0 failed, 0 skipped** — the 439 above and the five of `RenderScopeTests`.

**The render's length** (`RenderScopeTests`): two ramps running opposite ways, 4 s and 10 s. With
the 10 s song muted, or with the 4 s song soloed, the file is 4.5 s (the tail is 0.5 s in the test)
and byte for byte the file a session holding only the 4 s song renders. Unmuting puts the 10 s song
back: 10.5 s, byte for byte the file of both. With every track muted, or with the soloed track muted
too, the audible length is 0 while the longest track is still 10 s. What a render leaves out is listed
with its reason, and an empty track that is muted is not listed. **Broken on purpose**: with the
render's end taken from the longest track again, the three tests that measure the file failed and
the two that read the lists passed.

`scripts/verify-exe.ps1` on a staged build: all nine runs passed and stayed out of the way.
`--buttontest` **61 checks** (55 before): through the view-model's render — the Render button's
work once its dialog has a name — to a file in the temp folder, read back with NAudio. With the
longer song muted (T.R.O.Y., 13,007,151 frames) the file is 13,017,887 frames: the shorter song's
12,921,887 and the 2 s tail; the same with the shorter song soloed; the status names the song left
out and why, each time; with both muted the button refuses and says why. `--gesturetest` still
276. Nobody has listened to a render.

## 2026-10-27 — snapping to the playhead

**452 tests: 452 passed, 0 failed, 0 skipped** — the 444 above and the eight of `PlayheadSnapTests`.

**Where things land** (`PlayheadSnapTests`, a 60 s ramp, the snap distance 0.357 s): a section
dropped 0.2 s from the playhead starts on its exact frame, and a second in, the render is the
untouched song's a second after the playhead; a section whose *end* is 0.2 s from it ends on its
frame; a group with one member's edge 0.2 s from it lands that edge on it, the spacing kept in
frames; a trimmed end lands on it, and a trimmed start with its audio left in place; a tempo
ramp's dot placed near it, and a ramp's end dragged to it. With the playhead exactly on a
section's edge the edge wins and the engine sees a butt join; 0.05 s from the playhead and 0.15 s
from an edge, the playhead wins. Ctrl frees it. **Broken on purpose**: with the playhead taken out
of the snap settings, 8 of the 10 playhead tests failed — the mark-end and warp-marker ones among
them, since they now go through the same settings; the two still passing were the tie, where an
edge wins anyway, and Ctrl.

`scripts/verify-exe.ps1` on a staged build: all nine runs passed and stayed out of the way.
`--gesturetest` **283 checks** (276 before): the playhead parked at 58 s, a section dropped 4 px
short of it lands on it, the guide line marks it and the status says it snapped to the playhead;
one whose end is dropped 4 px past it ends on it; with Ctrl held it stops 4 px short; and while
playing — the offline engine's transport, the playhead parked — it stops 4 px short too.
`--buttontest` still 61. Nobody has listened to the result.

## 2026-10-27 — scrolling the tracks, the wheel, and the scroll bars

**452 tests, unchanged**: nothing in Core or the engine changed. `dotnet test` ran on this tree all
the same (452 passed, 0 failed, 0 skipped).

`scripts/verify-exe.ps1` on a staged build: all nine runs passed and stayed out of the way.
`--gesturetest` **297 checks** (283 before). With eight empty tracks added for room (740 px to
scroll, undone after): a right-drag pulled up 80 px scrolls the tracks down 80 px and suppresses
the clip menu; PageDown scrolls a screen (628 px) and PageUp back; Ctrl+End to the bottom (740) and
Ctrl+Home to the top; PageDown with PROJECT BPM focused is left to the box; none of the four keys
is a KeyBinding. The plain wheel: a notch down over a waveform, routed through the lane up to the
scroller, scrolls the tracks 48 px, and a notch over the ruler scrolls them 48 px through the
ruler's own handler; over a waveform, over the strip and over the ruler a plain notch leaves the
playhead where it was. Alt+wheel over the ruler, over a waveform and under the last track moves the
playhead half a second a notch at 40 px/s, stops at 0:00, carries on playing from the new spot, and
the view follows it past the right edge. The wheel over a knob, clicked or not, turns it (1 → 1.02)
and goes no further. The scroll bars' new look is in the `--demosections` and `--demoediting`
shots, but nobody has looked at them, and nobody has used the pan or the wheel by hand.

A first cut made a knob take the wheel only once clicked. It passed the same checks (295, at 16:00)
and was taken out within the hour on his instruction.

**32-bit float first in the Render box** (71943fd, the same evening): one line in the dialog, no
test reaches it; `scripts/verify-exe.ps1` on the build from that commit, all nine runs passed
(297, 61) and stayed out of the way.

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
