using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Snapping to the playhead (Daniel, 2026-10-27: "should be able to snap to play marker"). A
/// section dragged, a group dragged, an end trimmed and a tempo ramp's dot all land on the playhead
/// when it is the nearest thing within the snap distance — exactly, in frames. A section edge wins
/// a tie, so a butt join is never lost to it, and Ctrl frees the drag. The song is a ramp, so a
/// rendered sample says where in the song it came from.
/// </summary>
public class PlayheadSnapTests : IDisposable
{
    const int Rate = 48000;
    const int SongFrames = Rate * 60;
    const double Pps = 28;                                   // 10 px is 0.357 s
    private readonly string _dir;
    private readonly string _wav;

    public PlayheadSnapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-playhead-snap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        var s = new float[SongFrames * 2];
        for (int f = 0; f < SongFrames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)SongFrames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static long F(double seconds) => (long)Math.Round(seconds * Rate);
    static SnapSettings Snap(double playhead) => new(true, Pps, playhead);
    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);

    async Task<(ProjectSession s, TrackModel t)> OpenAsync()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        return (s, t);
    }

    /// <summary>The track's sections in timeline order, after splitting at each of <paramref name="cuts"/>.</summary>
    static List<ClipModel> Pieces(TrackModel t, ProjectSession s, params double[] cuts)
    {
        foreach (double at in cuts) Assert.True(TimelineEditor.Split(t, at, s).Changed, $"could not split at {at}s");
        return t.Clips.OrderBy(c => Start(c, s)).ToList();
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

    [Fact]
    public async Task ASectionDraggedNearThePlayheadLandsExactlyOnIt()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var second = Pieces(t, s, 30)[1];                // the song's 30 s to 60 s
            var move = new SectionMove(s, t, second);
            move.DragTo(t, 40.2, Snap(40));
            Assert.True(move.SnappedTo == "the playhead" && move.SnapLineSeconds == 40,
                $"a section dropped 0.2 s from the playhead did not snap to it (snapped to {move.SnappedTo ?? "nothing"})");
            Assert.True(Start(second, s) == F(40),
                $"it starts at frame {Start(second, s)}, not {F(40)}: the section is not on the playhead to the frame");
            Assert.Contains("snapped to the playhead", move.Commit().Message);

            // And the audio went with it: a second into the section, what plays is what the
            // untouched song plays at 31 s — the same render, a second apart.
            var edited = Render(s, Rate * 45);
            var (fresh, _) = await OpenAsync();
            using (fresh)
            {
                var original = Render(fresh, Rate * 45);
                Assert.True(edited[F(41) * 2] == original[F(31) * 2] && edited[F(41) * 2 + 1] == original[F(31) * 2 + 1],
                    "the audio did not land with the section: at 41 s the song's 31 s is not playing");
            }
        }
    }

    [Fact]
    public async Task ASectionsEndSnapsToThePlayheadToo()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var second = Pieces(t, s, 30)[1];                // 30 s long
            var move = new SectionMove(s, t, second);
            move.DragTo(t, 19.8, Snap(50));                  // its end at 49.8, its start nowhere near
            Assert.True(move.SnappedTo == "the playhead" && move.SnapLineSeconds == 50,
                $"a section whose end was 0.2 s from the playhead did not snap to it (snapped to {move.SnappedTo ?? "nothing"})");
            Assert.True(End(second, s) == F(50) && Start(second, s) == F(20),
                $"it runs from frame {Start(second, s)} to {End(second, s)}, not {F(20)} to {F(50)}: its end is not on the playhead to the frame");
        }
    }

    [Fact]
    public async Task AnEdgeWinsATieWithThePlayheadSoAButtJoinStaysExact()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            // Three pieces; the third dragged back onto the second's place butts against the first:
            // the song's 40 s straight after its 30 s, a join the engine crossfades (not a seam).
            var pieces = Pieces(t, s, 30, 40);
            var third = pieces[2];
            var move = new SectionMove(s, t, third);
            double edge = TimelineEditor.TimelineSecondsOfFrame(t, F(30), s);
            move.DragTo(t, edge + 0.1, Snap(edge));          // the playhead sits exactly on the first piece's end
            Assert.True(move.SnappedTo == "a section",
                $"with the playhead on a section's edge, the drag snapped to {move.SnappedTo}: a tie went to the playhead, which would turn a butt join into a near miss");
            Assert.Equal(F(30), Start(third, s));
            move.Commit();                                   // covers the second piece entirely
            s.ApplyAll();
            var timeline = s.VoiceFor(t)!.Timeline;
            Assert.True(timeline.Count == 2 && timeline.ButtJoinAfter(0),
                $"the two pieces left are not a butt join the engine crossfades ({timeline.Count} pieces): the join would get two declicks and a gap");
        }
    }

    [Fact]
    public async Task ThePlayheadWinsWhenItIsStrictlyNearerThanAnEdge()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var second = Pieces(t, s, 30)[1];
            var move = new SectionMove(s, t, second);
            move.DragTo(t, 45, SnapSettings.Off);
            move.DragTo(t, 30.15, Snap(30.1));               // 0.05 s from the playhead, 0.15 s from the edge at 30
            Assert.True(move.SnappedTo == "the playhead" && Start(second, s) == F(30.1),
                $"nearer the playhead than the edge, the section went to {move.SnappedTo ?? "nothing"} at frame {Start(second, s)}, not the playhead at {F(30.1)}");
        }
    }

    [Fact]
    public async Task CtrlFreesADragFromThePlayhead()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var second = Pieces(t, s, 30)[1];
            var move = new SectionMove(s, t, second);
            move.DragTo(t, 40.2, new SnapSettings(false, Pps, 40));
            Assert.True(move.SnappedTo is null && Start(second, s) == F(40.2),
                $"with Ctrl held the section still snapped ({move.SnappedTo}, frame {Start(second, s)}): it cannot be put down 0.2 s from the playhead");
        }
    }

    [Fact]
    public async Task AGroupSnapsToThePlayheadByWhicheverOfItsEdgesIsNearest()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var pieces = Pieces(t, s, 20, 40);               // [0,20) [20,40) [40,60)
            var p2 = pieces[1];
            var p3 = pieces[2];
            var group = new GroupMove(s, new[] { (t, p2), (t, p3) }, p2);
            group.DragTo(t, 30.3, Snap(50.5));               // the third piece's start would be 0.2 s from the playhead
            Assert.True(group.SnappedTo == "the playhead" && group.SnapLineSeconds == 50.5,
                $"a group with one member's edge 0.2 s from the playhead did not snap to it (snapped to {group.SnappedTo ?? "nothing"})");
            Assert.True(Start(p3, s) == F(50.5) && Start(p2, s) == F(30.5),
                $"the pieces start at frames {Start(p2, s)} and {Start(p3, s)}, not {F(30.5)} and {F(50.5)}: the member nearest the playhead is not on it, or the group's spacing changed");
            Assert.Contains("snapped to the playhead", group.Commit().Message);
        }
    }

    [Fact]
    public async Task ATrimmedEndSnapsToThePlayhead()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            var c = t.Clips[0];
            var end = new SectionTrim(s, t, c, start: false);
            end.DragTo(45.2, Snap(45));
            Assert.True(end.SnappedTo == "the playhead" && End(c, s) == F(45),
                $"an end trimmed to 0.2 s from the playhead ended at frame {End(c, s)}, not {F(45)} (snapped to {end.SnappedTo ?? "nothing"})");

            var start = new SectionTrim(s, t, c, start: true);
            start.DragTo(9.8, Snap(10));
            Assert.True(start.SnappedTo == "the playhead" && Start(c, s) == F(10) && TimelineEditor.SourceStartFrameOf(c, s) == F(10),
                $"a start trimmed to 0.2 s from the playhead starts at frame {Start(c, s)} from the song's frame {TimelineEditor.SourceStartFrameOf(c, s)}: not on the playhead, or the audio slid");
        }
    }

    [Fact]
    public async Task ATempoRampsDotSnapsToThePlayhead()
    {
        var (s, t) = await OpenAsync();
        using (s)
        {
            double at = TempoRamps.SnapPoint(t, 25.2, isEnd: false, except: -1, Snap(25), s, out var on);
            Assert.True(on == 25 && at == 25, $"a ramp's dot placed 0.2 s from the playhead landed at {at}s (snapped to {on?.ToString() ?? "nothing"}), not on it");

            Pieces(t, s, 25);                                // an edge at 25 s, nearer than a playhead at 25.3
            at = TempoRamps.SnapPoint(t, 25.1, isEnd: false, except: -1, Snap(25.3), s, out on);
            Assert.True(on == 25 && at == 25, $"with a section edge nearer than the playhead, the dot went to {on?.ToString() ?? "nothing"} instead of the edge");

            Assert.True(TempoRamps.Add(t, 10, 20, 1.1, s).Changed);
            var drag = new RampEdgeDrag(s, t, 0, end: true);
            drag.DragTo(24.8, Snap(25));
            Assert.True(drag.SnappedTo == "the playhead" && drag.Ramp.EndSeconds == 25,
                $"a ramp's end dragged 0.2 s from the playhead ended at {drag.Ramp.EndSeconds}s (snapped to {drag.SnappedTo ?? "nothing"}), not on it");
        }
    }
}
