using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// Empty tracks, dragging sections between tracks, snapping, and loop mode.
///
/// <para>
/// The source is a ramp, so a sample's value says where in the song it came from, and wherever it
/// matters the check is a render of the edited project against a render of the original — the
/// same audio at the same moment — rather than a number that could be right about the wrong thing.
/// </para>
/// </summary>
public class SectionEditingTests : IDisposable
{
    const int Rate = 48000;
    const int SongFrames = Rate * 60;
    private readonly string _dir;
    private readonly string _wav;
    private readonly ITestOutputHelper _out;

    public SectionEditingTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-editing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");

        var s = new float[SongFrames * 2];
        for (int f = 0; f < SongFrames; f++)
        {
            float v = f / (float)SongFrames;
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(ProjectSession Session, TrackModel Track)> OpenAsync()
    {
        var session = ProjectSession.CreateOffline(Rate);
        var track = await session.AddTrackAsync(_wav);
        return (session, track);
    }

    static float[] Render(ProjectSession session, int frames)
    {
        session.ApplyAll();
        var g = session.Graph;
        g.LimiterEnabled = false;
        g.Seek(0);
        g.SnapParameters();
        var buf = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            g.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    /// <summary>Samples that differ, ignoring a window around each cut edge where a declick belongs.</summary>
    static int Differing(float[] a, float[] b, params double[] edgesSeconds)
    {
        int guard = (int)(TrackVoice.DeclickSeconds * Rate) + 2;
        int n = 0;
        for (int f = 0; f < a.Length / 2; f++)
        {
            if (edgesSeconds.Any(e => Math.Abs(f - e * Rate) <= guard)) continue;
            if (a[f * 2] != b[f * 2] || a[f * 2 + 1] != b[f * 2 + 1]) n++;
        }
        return n;
    }

    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);

    // ================================================================ empty tracks

    [Fact]
    public async Task AnEmptyTrackHasNothingOnItAndIsNotReportedAsMissingAudio()
    {
        var (session, _) = await OpenAsync();
        using (session)
        {
            var empty = session.AddEmptyTrack();

            Assert.Empty(empty.Clips);
            Assert.Equal("Track 2", empty.Name);
            Assert.NotNull(session.VoiceFor(empty));
            Assert.True(session.HasAllAudio(empty),
                "an empty track reads from no file, so it must count as loaded — otherwise it shows as a missing song");
        }
    }

    [Fact]
    public async Task AnEmptyTrackReopensEmpty()
    {
        var (session, _) = await OpenAsync();
        using (session)
        {
            session.AddEmptyTrack();
            var path = Path.Combine(_dir, "empty.benchmix");
            ProjectStore.Save(session.Project, path);

            var reopened = ProjectStore.Load(path).Project.Tracks[1];
            Assert.Empty(reopened.Clips);
            Assert.True(string.IsNullOrEmpty(reopened.SourcePath));
            Assert.Equal("Track 2", reopened.Name);
        }
    }

    [Fact]
    public async Task ASectionPastedOntoAnEmptyTrackPlaysExactlyWhenAndAsItDid()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 10, session);
            TimelineEditor.Split(a, 20, session);
            var reference = Render(session, Rate * 25);

            // Cut the middle out of A and paste it at the same moment on a new, empty track.
            var middle = a.Clips[1];
            var clipboard = TimelineEditor.Copy(middle);
            TimelineEditor.Delete(a, middle);
            var b = session.AddEmptyTrack();
            var result = TimelineEditor.Paste(b, clipboard, 10, session, copiedFrom: a);
            Assert.True(result.Changed, result.Message);

            int differing = Differing(reference, Render(session, Rate * 25), 10, 20);
            _out.WriteLine($"{differing} samples differ away from the two cut edges");
            Assert.True(differing == 0,
                $"{differing} samples differ: the section moved to its own track is not playing the same "
              + "audio at the same moment it did on the original");
        }
    }

    [Fact]
    public async Task PastingOntoAnEmptyTrackTakesTheTempoAndPitchTheSectionWasPlayingAt()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            a.Speed = 0.92;
            a.Semitones = -2;
            a.Mode = StretchMode.Independent;
            a.Bpm = 101.5;

            var b = session.AddEmptyTrack();
            TimelineEditor.Paste(b, TimelineEditor.Copy(a.Clips[0]), 4, session, copiedFrom: a);

            Assert.Equal(0.92, b.Speed);
            Assert.Equal(-2, b.Semitones);
            Assert.Equal(101.5, b.Bpm);
            Assert.Equal(a.SourcePath, b.SourcePath);
            // The EQ is a mix decision for the deck, and a fresh deck starts flat.
            Assert.Equal(1.0, b.EqLow);
        }
    }

    [Fact]
    public async Task PastingOntoATrackWithASongKeepsThatTracksOwnTempo()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            var other = await session.AddTrackAsync(_wav);
            other.Speed = 1.1;
            a.Speed = 0.9;

            TimelineEditor.Paste(other, TimelineEditor.Copy(a.Clips[0]), 70, session, copiedFrom: a);
            Assert.Equal(1.1, other.Speed);
        }
    }

    // ================================================================ dragging between tracks

    [Fact]
    public async Task ASectionDraggedToAnotherTrackLandsThereAtTheSameMoment()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 10, session);
            TimelineEditor.Split(a, 20, session);
            var reference = Render(session, Rate * 25);

            var b = session.AddEmptyTrack();
            var move = new SectionMove(session, a, a.Clips[1]);
            move.DragTo(b, 10, SnapSettings.Off);
            var result = move.Commit();
            session.ApplyAll();

            Assert.Equal(2, a.Clips.Count);           // a hole where it was
            Assert.Single(b.Clips);
            Assert.Contains("took its tempo and pitch", result.Message);

            int differing = Differing(reference, Render(session, Rate * 25), 10, 20);
            Assert.True(differing == 0,
                $"{differing} samples differ: dragging a section to another track changed what plays when");
        }
    }

    [Fact]
    public async Task DraggingPastAnEmptyTrackPutsItBackAsItWas()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            a.Speed = 0.95;
            var empty = session.AddEmptyTrack();
            var third = await session.AddTrackAsync(_wav);

            var move = new SectionMove(session, a, a.Clips[0]);
            move.DragTo(empty, 0, SnapSettings.Off);
            Assert.Equal(0.95, empty.Speed);          // took the section's tempo while it was there
            move.DragTo(third, 0, SnapSettings.Off);
            move.Commit();

            Assert.Empty(empty.Clips);
            Assert.Equal(1.0, empty.Speed);
            Assert.True(string.IsNullOrEmpty(empty.SourcePath), "the empty track kept a song it no longer holds");
            Assert.Equal("Track 2", empty.Name);
        }
    }

    [Fact]
    public async Task ADroppedSectionOverwritesWhatIsUnderItSoNothingOverlaps()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 10, session);
            TimelineEditor.Split(a, 20, session);

            // Drag the first ten seconds to start at 15 s, over the second half of the middle.
            var move = new SectionMove(session, a, a.Clips[0]);
            move.DragTo(a, 15, SnapSettings.Off);
            var result = move.Commit();
            Assert.Contains("replaced", result.Message);

            var ordered = a.Clips.OrderBy(c => Start(c, session)).ToList();
            for (int i = 0; i + 1 < ordered.Count; i++)
                Assert.True(End(ordered[i], session) <= Start(ordered[i + 1], session),
                    $"section {i} runs to frame {End(ordered[i], session)} but the next starts at "
                  + $"{Start(ordered[i + 1], session)} — two sections sounding at once on one track");
        }
    }

    [Fact]
    public async Task ASectionCanBeDraggedEarlierThanWhereItsTrackBegan()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            a.TimelineOffsetSeconds = 30;             // dropped half a minute in
            TimelineEditor.Split(a, 40, session);

            var move = new SectionMove(session, a, a.Clips[0]);
            move.DragTo(a, 12, SnapSettings.Off);
            move.Commit();

            double landed = TimelineEditor.TimelineSecondsAt(a, a.Clips[0].TrackStartSeconds);
            Assert.True(Math.Abs(landed - 12) < 1.0 / Rate,
                $"dragged to 12.00 s but it stopped at {landed:0.000} s — the track's start acted as a wall");
            Assert.Equal(40.0, TimelineEditor.TimelineSecondsAt(a, a.Clips[1].TrackStartSeconds), 9);
        }
    }

    // ================================================================ snapping

    [Fact]
    public async Task ASectionDraggedCloseToAnotherSnapsOntoTheExactFrameItEnds()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            // An awkward end — a trimmed, moved section never lands on a round number.
            TimelineEditor.Split(a, 20, session);
            a.Clips[0].SourceEndSeconds = (8 * Rate + 0.37) / Rate;
            var neighbour = a.Clips[0];
            var mover = a.Clips[1];

            // 28 px/s: ten pixels is about a third of a second, and this is a fifth off.
            var snap = new SnapSettings(true, 28, 0);
            var move = new SectionMove(session, a, mover);
            move.DragTo(a, 8.2, snap);
            move.Commit();
            session.Apply(a);

            Assert.Equal("a section", move.SnappedTo);
            Assert.True(End(neighbour, session) == Start(mover, session),
                $"snapped to frame {Start(mover, session)} but the section before ends at {End(neighbour, session)} — "
              + "a near miss leaves a gap with a declick either side instead of a join");
            Assert.True(session.VoiceFor(a)!.Timeline.ButtJoinAfter(0),
                "the engine does not see the snapped sections as butted together, so the join is not crossfaded");
        }
    }

    [Fact]
    public async Task TheEndOfADraggedSectionSnapsToTheStartOfTheNextOne()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 10, session);
            TimelineEditor.Split(a, 30, session);
            TimelineEditor.Delete(a, a.Clips[1]);           // leaves [0,10) and [30,60)
            var first = a.Clips[0];
            var last = a.Clips[1];

            var move = new SectionMove(session, a, first);
            move.DragTo(a, 19.85, new SnapSettings(true, 28, 0));    // its end at 29.85 — near 30
            move.Commit();

            Assert.True(End(first, session) == Start(last, session),
                $"its end landed on frame {End(first, session)}, the next section starts on {Start(last, session)}");
        }
    }

    [Fact]
    public async Task HoldingCtrlDragsExactlyWhereThePointerGoes()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 20, session);
            var mover = a.Clips[1];

            var move = new SectionMove(session, a, mover);
            move.DragTo(a, 20.1234, new SnapSettings(false, 28, 120));
            move.Commit();

            Assert.Null(move.SnappedTo);
            Assert.Equal(session.SecondsToFrames(20.1234), Start(mover, session));
        }
    }

    [Fact]
    public async Task WithNoSectionNearbyADragStillSnapsToBarLines()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 20, session);
            var mover = a.Clips[1];

            // 120 BPM: a bar is 2 s. 36.7 s is far from any section edge, and nearest bar 36 s.
            var move = new SectionMove(session, a, mover);
            move.DragTo(a, 36.7, new SnapSettings(true, 28, 120));
            move.Commit();

            Assert.Equal("a bar", move.SnappedTo);
            Assert.Equal(36.0, TimelineEditor.TimelineSecondsAt(a, mover.TrackStartSeconds), 9);
        }
    }

    [Fact]
    public async Task ASectionSnapsToAnEdgeOnADifferentTrack()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            var b = await session.AddTrackAsync(_wav);
            b.Clips[0].SourceEndSeconds = 12.345;          // B's only section ends at 12.345 s

            TimelineEditor.Split(a, 30, session);
            var mover = a.Clips[1];
            var move = new SectionMove(session, a, mover);
            move.DragTo(a, 12.2, new SnapSettings(true, 28, 0));
            move.Commit();

            double landed = TimelineEditor.TimelineSecondsAt(a, mover.TrackStartSeconds);
            Assert.True(Math.Abs(landed - 12.345) <= 1.0 / Rate,
                $"landed at {landed:0.00000} s, not on the other track's edge at 12.34500 s");
        }
    }

    // ================================================================ trimming

    [Fact]
    public async Task TrimmingAnEndOutwardStopsExactlyAtTheNextSection()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(a, 10, session);
            TimelineEditor.Split(a, 20, session);
            var first = a.Clips[0];
            var third = a.Clips[2];
            TimelineEditor.Delete(a, a.Clips[1]);
            first.SourceEndSeconds = 6;                     // [0,6) then a gap then [20,60)

            var trim = new SectionTrim(session, a, first, start: false);
            trim.DragTo(25, SnapSettings.Off);              // pulled well past the next section

            Assert.True(End(first, session) == Start(third, session),
                $"trimmed to frame {End(first, session)}; the next section starts at {Start(third, session)} — "
              + "a trim must never run into the section beside it");
        }
    }

    [Fact]
    public async Task TrimmingNearAnotherEdgeSnapsToIt()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            var b = await session.AddTrackAsync(_wav);
            b.Clips[0].SourceEndSeconds = 7.777;

            var trim = new SectionTrim(session, a, a.Clips[0], start: false);
            trim.DragTo(7.7, new SnapSettings(true, 28, 0));

            double end = TimelineEditor.TimelineSecondsAt(a, TimelineEditor.SecondsOf(End(a.Clips[0], session), session));
            Assert.True(Math.Abs(end - 7.777) <= 1.0 / Rate, $"trimmed to {end:0.00000} s instead of snapping to 7.77700 s");
        }
    }

    // ================================================================ loop mode

    /// <summary>A track whose only section is 2 s of the ramp, 10 s to 12 s of the song, at 10 s.</summary>
    private async Task<(ProjectSession Session, TrackModel Track, ClipModel Section)> LoopableAsync()
    {
        var (session, a) = await OpenAsync();
        var c = a.Clips[0];
        c.SourceStartSeconds = 10;
        c.SourceEndSeconds = 12;
        c.TrackStartSeconds = 10;
        return (session, a, c);
    }

    [Fact]
    public async Task LoopModeRepeatsTheSectionInWholeRepeats()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            var loop = new LoopDrag(session, a, section, left: false);
            var result = loop.DragTo(12 + 2 * 3.3, wholeRepeats: true);     // 4.3 plays → 4
            Assert.True(result.Changed, result.Message);
            session.Apply(a);

            Assert.Equal(4, a.Clips.Count);
            var timeline = session.VoiceFor(a)!.Timeline;
            for (int i = 0; i + 1 < timeline.Count; i++)
            {
                Assert.Equal(timeline[0].SourceStartFrame, timeline[i + 1].SourceStartFrame);
                Assert.Equal(timeline[0].SourceEnd, timeline[i + 1].SourceEnd);
                Assert.True(timeline.ButtJoinAfter(i), $"repeat {i + 1} does not butt exactly against repeat {i}");
            }

            // Each repeat, clear of its crossfade, is exactly the first pass — the same part of the
            // song, not whatever followed it in the file.
            var buf = Render(session, Rate * 20);
            int len = 2 * Rate, first = 10 * Rate, fade = (int)(TrackVoice.DeclickSeconds * Rate) + 2;
            for (int k = 1; k < 4; k++)
                for (int f = fade; f < len - fade; f += 97)
                    Assert.True(buf[(first + k * len + f) * 2] == buf[(first + f) * 2],
                        $"repeat {k} at {f} frames in plays {buf[(first + k * len + f) * 2]:F5}, the first pass {buf[(first + f) * 2]:F5}");
        }
    }

    [Fact]
    public async Task HoldingCtrlInLoopModeEndsOnPartOfARepeat()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            var loop = new LoopDrag(session, a, section, left: false);
            loop.DragTo(12 + 2 * 3.3, wholeRepeats: false);

            Assert.Equal(3, loop.Repeats);
            Assert.True(loop.Partial);
            var tail = a.Clips.OrderBy(c => c.TrackStartSeconds).Last();
            // The part-repeat is the START of the section, 0.6 s of it, so the loop stays in phase.
            Assert.Equal(TimelineEditor.SourceStartFrameOf(section, session), TimelineEditor.SourceStartFrameOf(tail, session));
            Assert.Equal(session.SecondsToFrames(0.6), TimelineEditor.LengthFramesOf(tail, session));
            Assert.Equal(session.SecondsToFrames(18.6), End(tail, session));
        }
    }

    [Fact]
    public async Task PullingTheLoopBackDuringTheDragGivesBackWhatItRanOver()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            // Something else on the track, 6 s later.
            var other = TimelineEditor.Copy(section);
            other.SourceStartSeconds = 40;
            other.SourceEndSeconds = 43;
            other.TrackStartSeconds = 18;
            a.Clips.Add(other);

            var loop = new LoopDrag(session, a, section, left: false);
            loop.DragTo(24, wholeRepeats: true);                     // runs over it
            Assert.DoesNotContain(a.Clips, c => c.SourceStartSeconds == 40);
            loop.DragTo(16, wholeRepeats: true);                     // and back off it

            var back = a.Clips.SingleOrDefault(c => c.Id == other.Id);
            Assert.NotNull(back);
            Assert.Equal(40, back!.SourceStartSeconds);
            Assert.Equal(43, back.SourceEndSeconds);
            Assert.Equal(18, back.TrackStartSeconds);
        }
    }

    [Fact]
    public async Task LoopingLeftCutsThePartRepeatAtItsHeadSoItStaysInPhase()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            var loop = new LoopDrag(session, a, section, left: true);
            loop.DragTo(10 - 2 * 1.25, wholeRepeats: false);          // 1 whole repeat and a quarter
            session.Apply(a);

            Assert.Equal(1, loop.Repeats);
            Assert.True(loop.Partial);

            // In phase means: every frame before the section plays what the loop would play there,
            // which is the section's own audio one or more whole lengths later.
            var buf = Render(session, Rate * 13);
            int start = 10 * Rate, len = 2 * Rate, fade = (int)(TrackVoice.DeclickSeconds * Rate) + 2;
            for (int back = fade; back < len + len / 4 - fade; back += 211)
            {
                if (Math.Abs(back - len) <= fade) continue;          // the join between repeats
                int whole = (back + len - 1) / len;
                float expected = buf[(start + whole * len - back) * 2];
                float actual = buf[(start - back) * 2];
                Assert.True(actual == expected,
                    $"{back} frames before the section the loop plays {actual:F5}, but in phase it should play {expected:F5}");
            }
        }
    }

    [Fact]
    public async Task LoopingLeftPastWhereTheTrackBeganMovesNothingThatWasThere()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            // The whole track dropped at 30 s: the section is at 40 s on the timeline.
            a.TimelineOffsetSeconds = 30;
            var before = Render(session, Rate * 45);

            var loop = new LoopDrag(session, a, section, left: true);
            loop.DragTo(24.5, wholeRepeats: true);                    // well before the track began
            Assert.True(a.TimelineOffsetSeconds < 30, "the track's origin should have moved back");

            var after = Render(session, Rate * 45);
            // Everything from the section on is untouched, to the sample — bar its first 5 ms, which
            // now crossfade from the repeat before it instead of declicking out of a gap.
            int from = 40 * Rate + (int)(TrackVoice.DeclickSeconds * Rate) + 2;
            int differing = 0;
            for (int i = from * 2; i < before.Length; i++) if (before[i] != after[i]) differing++;
            Assert.True(differing == 0, $"{differing} samples moved after looping left past the track's start");
            Assert.Equal(40.0, TimelineEditor.TimelineSecondsAt(a, loop.Base.TrackStartSeconds), 9);
        }
    }

    [Fact]
    public async Task GrabbingTheEndOfAnExistingLoopShrinksTheWholeLoop()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            new LoopDrag(session, a, section, left: false).DragTo(20, wholeRepeats: true);   // 5 plays
            Assert.Equal(5, a.Clips.Count);

            var lastRepeat = a.Clips.OrderBy(c => c.TrackStartSeconds).Last();
            var shrink = new LoopDrag(session, a, lastRepeat, left: false);
            shrink.DragTo(14.1, wholeRepeats: true);

            Assert.Equal(2, a.Clips.Count);
            Assert.Equal(section.Id, shrink.Base.Id);
        }
    }

    [Fact]
    public async Task TheEdgeBetweenTwoRepeatsIsNotALoopHandle()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            new LoopDrag(session, a, section, left: false).DragTo(18, wholeRepeats: true);   // 4 plays
            var ordered = a.Clips.OrderBy(c => c.TrackStartSeconds).ToList();
            var run = LoopRun.Of(a, ordered[1], session);

            Assert.Equal(4, run.All.Count);
            Assert.False(run.IsOuterEdge(ordered[1], left: false));
            Assert.False(run.IsOuterEdge(ordered[1], left: true));
            Assert.True(run.IsOuterEdge(ordered[3], left: false));
            Assert.True(run.IsOuterEdge(ordered[0], left: true));
        }
    }

    [Fact]
    public async Task ALoopKeepsItsFadeOutAtItsNewEnd()
    {
        var (session, a, section) = await LoopableAsync();
        using (session)
        {
            section.FadeOutSeconds = 0.5;
            new LoopDrag(session, a, section, left: false).DragTo(16, wholeRepeats: true);
            var ordered = a.Clips.OrderBy(c => c.TrackStartSeconds).ToList();

            Assert.Equal(0, ordered[0].FadeOutSeconds);
            Assert.Equal(0.5, ordered[^1].FadeOutSeconds);
        }
    }

    // ================================================================ the track's origin

    [Fact]
    public async Task MovingATracksOriginBackMovesNoAudio()
    {
        var (session, a) = await OpenAsync();
        using (session)
        {
            a.TimelineOffsetSeconds = 12.3456789;         // an unrounded drop position
            TimelineEditor.Split(a, 20, session);
            TimelineEditor.Split(a, 31.25, session);
            var before = Render(session, Rate * 40);

            Assert.True(TimelineEditor.EnsureOriginAtOrBefore(a, 3.21, session));
            var after = Render(session, Rate * 40);

            int differing = 0;
            for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) differing++;
            Assert.True(differing == 0, $"moving a track's origin back changed {differing} samples");
            Assert.True(session.VoiceFor(a)!.Timeline.SeamAfter(0) && session.VoiceFor(a)!.Timeline.SeamAfter(1),
                "moving the origin broke a seam");
        }
    }

    // ================================================================ files

    [Fact]
    public async Task AFilePlacedOnAnEmptyTrackBecomesThatTracksSongAtThePlayhead()
    {
        var session = ProjectSession.CreateOffline(Rate);
        using (session)
        {
            var track = session.AddEmptyTrack();
            var result = await session.PlaceFileAsync(track, _wav, 3.5);
            Assert.True(result.Changed, result.Message);

            Assert.Equal(_wav, track.SourcePath);
            Assert.Equal("ramp", track.Name);
            Assert.True(session.HasAllAudio(track));

            var buf = Render(session, Rate * 5);
            Assert.True(Math.Abs(buf[(int)(3.4 * Rate) * 2]) < 1e-6f, "audio before the playhead");
            Assert.Equal(1000f / SongFrames, buf[((int)(3.5 * Rate) + 1000) * 2], 6);
        }
    }
}
