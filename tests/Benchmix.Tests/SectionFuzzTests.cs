using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// Hundreds of edits in a row — moves between tracks, trims, loops, pastes, splits, deletes — and
/// after every one, the rules that must always hold. Seeded, so a failure replays exactly.
///
/// <para>
/// The individual tests pin what each gesture does. This pins what none of them may ever do in
/// combination: leave two sections sounding at once on one track, put a section before a track's
/// timebase begins, or hand the engine an arrangement it has to repair (it truncates overlaps as a
/// last line of defence, which would mean audio silently cut short).
/// </para>
/// </summary>
public class SectionFuzzTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;
    private readonly ITestOutputHelper _out;

    public SectionFuzzTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-fuzz", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        int frames = Rate * 40;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)frames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task NoSequenceOfEditsLeavesATrackBroken(int seed)
    {
        var rng = new Random(seed);
        using var session = ProjectSession.CreateOffline(Rate);
        var a = await session.AddTrackAsync(_wav, 3.7);
        var b = await session.AddTrackAsync(_wav, 11.25);
        b.Speed = 0.93;
        session.AddEmptyTrack();
        session.AddEmptyTrack().Speed = 1.07;
        session.ApplyAll();

        double R(double lo, double hi) => lo + rng.NextDouble() * (hi - lo);
        TrackModel AnyTrack() => session.Project.Tracks[rng.Next(session.Project.Tracks.Count)];
        (TrackModel, ClipModel)? AnyClip()
        {
            var all = session.Project.Tracks.SelectMany(t => t.Clips.Select(c => (t, c))).ToList();
            return all.Count == 0 ? null : all[rng.Next(all.Count)];
        }
        SnapSettings Snap()
        {
            if (rng.Next(3) == 0) return SnapSettings.Off;
            var snap = new SnapSettings(true, R(5, 200));
            _ = rng.Next(2);    // drew the bar tempo before bar snapping went; still drawn so every seed replays as recorded
            return snap;
        }

        for (int step = 0; step < 250; step++)
        {
            string what;
            int op = rng.Next(7);
            var pick = AnyClip();
            if (pick is null && op != 4) op = 4;

            if (op == 4)
            {
                TimelineEditor.Split(AnyTrack(), R(0, 90), session);
                what = "split";
            }
            else
            {
                var (t, c) = pick!.Value;
                switch (op)
                {
                    case 0:
                    {
                        var move = new SectionMove(session, t, c);
                        for (int k = rng.Next(1, 4); k > 0; k--) move.DragTo(AnyTrack(), R(-5, 90), Snap());
                        move.Commit();
                        what = "move";
                        break;
                    }
                    case 1:
                    {
                        var trim = new SectionTrim(session, t, c, start: rng.Next(2) == 0);
                        for (int k = rng.Next(1, 4); k > 0; k--) trim.DragTo(R(-5, 90), Snap());
                        what = "trim";
                        break;
                    }
                    case 2:
                    {
                        bool left = rng.Next(2) == 0;
                        if (!LoopRun.Of(t, c, session).IsOuterEdge(c, left)) { what = "loop skipped"; break; }
                        var loop = new LoopDrag(session, t, c, left);
                        for (int k = rng.Next(1, 4); k > 0; k--) loop.DragTo(R(-5, 90), wholeRepeats: rng.Next(2) == 0);
                        what = "loop";
                        break;
                    }
                    case 3:
                        TimelineEditor.Paste(AnyTrack(), TimelineEditor.Copy(c), R(0, 90), session, copiedFrom: t);
                        what = "paste";
                        break;
                    case 5:
                        if (rng.Next(3) == 0) TimelineEditor.Delete(t, c);
                        what = "delete";
                        break;
                    default:
                        TimelineEditor.Duplicate(t, c, session);
                        what = "repeat";
                        break;
                }
            }

            session.ApplyAll();
            foreach (var t in session.Project.Tracks) CheckTrack(session, t, seed, step, what);
        }
    }

    private void CheckTrack(ProjectSession s, TrackModel t, int seed, int step, string what)
    {
        string where = $"seed {seed}, step {step} ({what}), track '{t.Name}'";

        Assert.True(t.TimelineOffsetSeconds >= 0, $"{where}: origin at {t.TimelineOffsetSeconds}");
        var ordered = t.Clips.OrderBy(c => TimelineEditor.StartFrameOf(c, s)).ToList();
        foreach (var c in ordered)
        {
            Assert.True(c.TrackStartSeconds >= 0, $"{where}: a section starts before the track's timebase ({c.TrackStartSeconds})");
            Assert.True(TimelineEditor.LengthFramesOf(c, s) > 0, $"{where}: a section of no length was left on the track");
        }
        for (int i = 0; i + 1 < ordered.Count; i++)
            Assert.True(TimelineEditor.EndFrameOf(ordered[i], s) <= TimelineEditor.StartFrameOf(ordered[i + 1], s),
                $"{where}: sections overlap — one ends at frame {TimelineEditor.EndFrameOf(ordered[i], s)}, "
              + $"the next starts at {TimelineEditor.StartFrameOf(ordered[i + 1], s)}");

        // The engine plays exactly what the model says: same count, same frames, nothing repaired.
        var timeline = s.VoiceFor(t)!.Timeline;
        Assert.True(timeline.Count == ordered.Count, $"{where}: the engine holds {timeline.Count} sections, the model {ordered.Count}");
        for (int i = 0; i < ordered.Count; i++)
        {
            Assert.Equal(TimelineEditor.StartFrameOf(ordered[i], s), timeline[i].TrackStartFrame);
            Assert.Equal(TimelineEditor.LengthFramesOf(ordered[i], s), timeline[i].LengthFrames);
        }
    }
}
