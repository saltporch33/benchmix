using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// One test per section-editing defect, each named for the behaviour that was wrong.
///
/// <para>
/// Three of these are the same mistake in different commands: placing a section by adding
/// <b>seconds</b> together. The engine places sections in whole frames, rounding each number on
/// its own, so a sum of seconds and a sum of rounded frames can disagree by a frame or two. The
/// seam rule and the butt-join rule both need an exact match, so one frame is the difference
/// between an inaudible join and a hole. It only shows once a section has been moved or trimmed
/// by an amount that is not a whole number of frames — which is every drag — so a fresh song
/// split at a round number of seconds, which is what the older tests used, never sees it.
/// </para>
/// </summary>
public class SectionRegressionTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public SectionRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-sections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");

        // Sixty seconds of a ramp: a sample's value says where in the file it came from.
        int frames = Rate * 60;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = f / (float)frames;
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

    /// <summary>A position a fraction of a frame past a whole one, the way a drag leaves it.</summary>
    static double At(long frames, double fraction) => (frames + fraction) / Rate;

    static float[] Render(ProjectSession session, int frames)
    {
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

    [Fact]
    public async Task SplittingASectionThatWasMovedStillChangesNothing()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            // Moved to 2.0000083 s and trimmed to start 10.0000125 s into the song: fractions of a
            // frame that round in opposite directions, as a drag easily leaves them.
            var clip = track.Clips[0];
            clip.TrackStartSeconds = At(96_000, 0.4);
            clip.SourceStartSeconds = At(480_000, 0.6);
            clip.SourceEndSeconds = At(1_440_000, 0.3);
            session.Apply(track);
            var before = Render(session, Rate * 10);

            var result = TimelineEditor.Split(track, At(336_000, 0.6), session);
            Assert.True(result.Changed, result.Message);
            session.Apply(track);

            var timeline = session.VoiceFor(track)!.Timeline;
            Assert.True(timeline.Count == 2 && timeline.SeamAfter(0),
                "splitting a moved section did not leave a seam: the halves are a frame apart, so the "
              + "split point gets two declicks and a gap — a split that is audible");

            var after = Render(session, Rate * 10);
            int differing = 0;
            for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) differing++;
            Assert.True(differing == 0, $"splitting a moved section and moving neither half changed {differing} samples");
        }
    }

    [Fact]
    public async Task RepeatingASectionThatWasMovedLeavesNoGapBetweenTheRepeats()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var clip = track.Clips[0];
            clip.TrackStartSeconds = At(96_000, 0.4);
            clip.SourceStartSeconds = At(480_000, 0.6);
            clip.SourceEndSeconds = At(720_000, 0.4);

            var result = TimelineEditor.Duplicate(track, clip, session);
            Assert.True(result.Changed, result.Message);
            session.Apply(track);

            var timeline = session.VoiceFor(track)!.Timeline;
            Assert.Equal(2, timeline.Count);
            Assert.True(timeline[0].TrackEndFrame == timeline[1].TrackStartFrame,
                $"the repeat starts at frame {timeline[1].TrackStartFrame} but the section before it ends at "
              + $"{timeline[0].TrackEndFrame}: a gap that far means both sides are declicked, which is a hole "
              + "in the loop at every repeat");
        }
    }

    [Fact]
    public async Task ATrackWithEverySectionDeletedReopensEmptyNotWithTheWholeSongBack()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Delete(track, track.Clips[0]);
            Assert.Empty(track.Clips);

            var path = Path.Combine(_dir, "emptied.benchmix");
            ProjectStore.Save(session.Project, path);
            var reopened = ProjectStore.Load(path).Project;

            Assert.True(reopened.Tracks[0].Clips.Count == 0,
                "a track whose every section was deleted came back holding the whole song — "
              + "reopening a project undid an edit and put audio back into the mix");
        }
    }

    [Fact]
    public async Task PastingBeforeWhereATrackStartsLandsAtThePlayhead()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            // A song dropped 30 seconds in: its origin is at 30 s, and so is its only section.
            track.TimelineOffsetSeconds = 30;
            TimelineEditor.Split(track, 40, session);
            var copied = TimelineEditor.Copy(track.Clips[0]);

            var result = TimelineEditor.Paste(track, copied, 12.0, session);
            Assert.True(result.Changed, result.Message);

            double landed = TimelineEditor.TimelineSecondsAt(track, result.Clip!.TrackStartSeconds);
            Assert.True(Math.Abs(landed - 12.0) < 1.0 / Rate,
                $"pasted at a playhead of 12.00 s but the section landed at {landed:0.000} s — "
              + "the start of the track, not where the playhead was");

            // And nothing that was already there may have moved to make room.
            var original = track.Clips.Single(c => c.SourceStartSeconds == 0 && c.Id != result.Clip.Id);
            Assert.Equal(30.0, TimelineEditor.TimelineSecondsAt(track, original.TrackStartSeconds), 9);
        }
    }

    // ---------------------------------------------------------------- found by review, 2026-09-24

    /// <summary>Where on the timeline a section's audio sits: the moment its source frame zero would play.</summary>
    static double Anchor(TrackModel t, ClipModel c)
        => TimelineEditor.TimelineSecondsAt(t, c.TrackStartSeconds) - c.SourceStartSeconds / t.Speed;

    [Fact]
    public async Task SnappingASectionsEndNearTheStartDoesNotPushTheRestOfTheTrackLater()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            // X sits at 1.9 s. A 2 s section dragged to 0.1 s ends 8 px from X's start at 40 px/s,
            // but ending there would mean starting before 0:00. Honouring it anyway shifted every
            // section on the track 0.1 s later, out of line with every other deck.
            var x = track.Clips[0];
            x.SourceStartSeconds = 10;
            x.SourceEndSeconds = 15;
            x.TrackStartSeconds = 1.9;
            var mover = TimelineEditor.Copy(x);
            mover.SourceStartSeconds = 20;
            mover.SourceEndSeconds = 22;
            mover.TrackStartSeconds = 10;
            track.Clips.Add(mover);
            double before = Anchor(track, x);

            var move = new SectionMove(session, track, mover);
            move.DragTo(track, 0.1, new SnapSettings(true, 40, 0));
            move.Commit();

            var stillX = track.Clips.Single(c => c.Id == x.Id);
            Assert.True(Math.Abs(Anchor(track, stillX) - before) <= 1.0 / Rate,
                $"the rest of the track moved by {Anchor(track, stillX) - before:0.0000} s when a section was "
              + "snapped near 0:00 — every other section on it is now out of line with the other tracks");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrabbingAFadedLoopAgainKeepsItsFade(bool fadeIn)
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var section = track.Clips[0];
            section.SourceStartSeconds = 10;
            section.SourceEndSeconds = 12;
            section.TrackStartSeconds = 10;
            if (fadeIn) section.FadeInSeconds = 0.5; else section.FadeOutSeconds = 0.5;

            new LoopDrag(session, track, section, left: false).DragTo(16, wholeRepeats: true);     // 3 plays
            var ordered = track.Clips.OrderBy(c => c.TrackStartSeconds).ToList();

            if (fadeIn)
            {
                // Grab the loop's left end and let go where it is.
                new LoopDrag(session, track, ordered[0], left: true).DragTo(10, wholeRepeats: true);
                var first = track.Clips.OrderBy(c => c.TrackStartSeconds).First();
                Assert.True(first.FadeInSeconds == 0.5,
                    $"grabbing the loop's start dropped its 0.5 s fade-in to {first.FadeInSeconds} — it now starts dead");
            }
            else
            {
                new LoopDrag(session, track, ordered[^1], left: false).DragTo(20, wholeRepeats: true);  // 5 plays
                var last = track.Clips.OrderBy(c => c.TrackStartSeconds).Last();
                Assert.True(last.FadeOutSeconds == 0.5,
                    $"lengthening the loop dropped its 0.5 s fade-out to {last.FadeOutSeconds} — it now ends dead");
                Assert.All(track.Clips.OrderBy(c => c.TrackStartSeconds).SkipLast(1),
                    c => Assert.Equal(0, c.FadeOutSeconds));
            }
        }
    }

    [Fact]
    public async Task RemovingATrackWhileAFileLoadsOntoItLeavesNoAudioBehind()
    {
        using var session = ProjectSession.CreateOffline(Rate);
        var track = session.AddEmptyTrack();

        var loading = session.PlaceFileAsync(track, _wav, 1.0);
        session.RemoveTrack(track);
        var result = await loading;

        Assert.False(result.Changed, "the file was placed on a track that no longer exists");
        Assert.True(session.Graph.Tracks.Count == 0,
            "a removed track got its voice back when its file finished loading — audio in every render with no track to mute");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrimmingTheStartOfAVeryShortSectionNeverRunsIntoWhatIsBeforeIt(bool atTheFilesStart)
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            ClipModel tiny;
            if (atTheFilesStart)
            {
                // 30 ms from 10 ms into the file: trimming its start in place must not read from
                // before the file begins, nor move the audio.
                tiny = track.Clips[0];
                tiny.SourceStartSeconds = 0.01;
                tiny.SourceEndSeconds = 0.04;
                tiny.TrackStartSeconds = 5;
            }
            else
            {
                TimelineEditor.Split(track, 10, session);
                TimelineEditor.Split(track, 10.03, session);
                tiny = track.Clips[1];
            }
            long endBefore = TimelineEditor.EndFrameOf(tiny, session);
            long startBefore = TimelineEditor.StartFrameOf(tiny, session);

            new SectionTrim(session, track, tiny, start: true)
                .DragTo(TimelineEditor.TimelineSecondsAt(track, tiny.TrackStartSeconds), SnapSettings.Off);

            Assert.True(tiny.SourceStartSeconds >= 0, $"the section now starts {tiny.SourceStartSeconds:0.000} s into its file");
            Assert.Equal(endBefore, TimelineEditor.EndFrameOf(tiny, session));
            Assert.True(TimelineEditor.StartFrameOf(tiny, session) >= startBefore,
                $"trimming its start in place moved it from frame {startBefore} to {TimelineEditor.StartFrameOf(tiny, session)} — "
              + "into the section before it, or into audio before its file began");
        }
    }

    [Fact]
    public async Task AFileTooShortToPlaceLeavesTheEmptyTrackAlone()
    {
        var blip = Path.Combine(_dir, "blip.wav");
        AudioClip.FromSamples("blip", new float[(Rate / 100) * 2], Rate).SaveWav(blip);   // 10 ms

        using var session = ProjectSession.CreateOffline(Rate);
        var track = session.AddEmptyTrack();
        var result = await session.PlaceFileAsync(track, blip, 1.0);

        Assert.False(result.Changed);
        Assert.True(TimelineEditor.IsEmptyDeck(track),
            "a file too short to place still took the empty track over — the next section pasted onto it "
          + "will not take on its song's tempo");
        Assert.Equal("Track 1", track.Name);
    }

    [Fact]
    public async Task TrimmingTheEndOfAVeryShortSectionNeverRunsIntoTheNextOne()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            // A 30 ms section butted against the next — shorter than a trim is allowed to leave.
            TimelineEditor.Split(track, 10, session);
            TimelineEditor.Split(track, 10.03, session);
            var tiny = track.Clips[1];
            var next = track.Clips[2];

            new SectionTrim(session, track, tiny, start: false).DragTo(10.01, SnapSettings.Off);

            Assert.True(TimelineEditor.EndFrameOf(tiny, session) <= TimelineEditor.StartFrameOf(next, session),
                $"the trimmed section now ends at frame {TimelineEditor.EndFrameOf(tiny, session)}, past the next "
              + $"section's start at {TimelineEditor.StartFrameOf(next, session)} — two sections sounding at once");
        }
    }
}
