using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// Copying, cutting, deleting and repeating a marked stretch of a track (Daniel, 2026-10-12):
/// "visually demarcate a start and end point within a track and copy, or cut, and then paste that
/// chunk elsewhere". On a ramp source (value == position), so where every frame came from is in the
/// audio, and wherever it matters a render of the edit is checked against a render of the original.
/// </summary>
public class MarkedStretchTests : IDisposable
{
    const int Rate = 48000;
    const int SongFrames = Rate * 60;
    private readonly string _dir;
    private readonly string _wav;
    private readonly ITestOutputHelper _out;

    public MarkedStretchTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-stretch", Guid.NewGuid().ToString("N"));
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

    async Task<(ProjectSession S, TrackModel T)> SongAsync(params double[] splits)
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 0);
        foreach (var x in splits) Assert.True(TimelineEditor.Split(t, x, s).Changed, $"could not split at {x}s");
        return (s, t);
    }

    static long F(double seconds) => (long)Math.Round(seconds * Rate);

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

    /// <summary>Frames of <paramref name="a"/> from <paramref name="aFrom"/> that differ from <paramref name="b"/> from <paramref name="bFrom"/>, skipping a declick's width around each guard (frames into the span).</summary>
    static int Differing(float[] a, long aFrom, float[] b, long bFrom, long frames, params long[] guards)
    {
        int guard = (int)(TrackVoice.DeclickSeconds * Rate) + 2;
        int n = 0;
        for (long k = 0; k < frames; k++)
        {
            if (guards.Any(g => Math.Abs(k - g) <= guard)) continue;
            long i = (aFrom + k) * 2, j = (bFrom + k) * 2;
            if (a[i] != b[j] || a[i + 1] != b[j + 1]) n++;
        }
        return n;
    }

    static int Loud(float[] a, long from, long frames, long guardAtEnds)
    {
        int n = 0;
        for (long k = guardAtEnds; k < frames - guardAtEnds; k++)
            if (a[(from + k) * 2] != 0 || a[(from + k) * 2 + 1] != 0) n++;
        return n;
    }

    // ================================================================ copy and paste

    [Fact]
    public async Task AStretchCopiedAcrossASeamPlaysTheSameAudioWhereverItIsPasted()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var reference = Render(s, Rate * 40);

            // 8 s to 14 s: the end of one section, the seam at 10 s, the start of the next.
            var piece = TimelineEditor.CopyStretch(a, F(8), F(14), s);
            Assert.Equal(2, piece.Clips.Count);
            Assert.Equal(F(6), piece.LengthFrames);

            var b = s.AddEmptyTrack();
            var result = TimelineEditor.Paste(b, piece, 30, s, copiedFrom: a);
            Assert.True(result.Changed, result.Message);
            Assert.Equal((F(30), F(36)), result.Span);
            a.Muted = true;

            // The seam inside the piece is not guarded: it must still play straight through.
            int differing = Differing(Render(s, Rate * 40), F(30), reference, F(8), F(6), 0, F(6));
            _out.WriteLine($"{differing} frames differ away from the two outer edges");
            Assert.True(differing == 0,
                $"{differing} frames of the pasted stretch are not the audio that was marked — or the seam inside it "
              + "now has a declick, a dip where the song should play straight through");
        }
    }

    [Fact]
    public async Task CuttingAStretchAndPastingItBackWhereItWasChangesNotOneSample()
    {
        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            var reference = Render(s, Rate * 30);

            // Inside one section at one end, across a seam, and inside another at the other end.
            long start = F(12.5), end = F(17.25);
            var piece = TimelineEditor.CopyStretch(a, start, end, s);
            Assert.True(TimelineEditor.ClearStretch(a, start, end, s).Changed);
            var back = TimelineEditor.Paste(a, piece, TimelineEditor.TimelineSecondsOfFrame(a, start, s), s);
            Assert.True(back.Changed, back.Message);

            int differing = Differing(Render(s, Rate * 30), 0, reference, 0, Rate * 30);
            Assert.True(differing == 0,
                $"{differing} frames differ: a stretch cut out and pasted straight back must be seams at both ends, "
              + "or each end gets a declick — a dip in the middle of the song where nothing was changed");
        }
    }

    [Fact]
    public async Task OnAStretchedTrackAStretchCutAndPastedBackIsExactlyASplitAtItsEnds()
    {
        // Through the stretcher a split is not bit-exact against the unsplit song: measured
        // 2026-10-12 on this ramp at 90-92% speed, a split moves samples by up to 7e-6 (-103 dB), as
        // the stretcher is fed in different blocks. So the yardstick is the engine's own seam: the
        // same track split at the stretch's two ends, which a cut pasted straight back must equal.
        long start = F(12.5), end = F(17.25);
        float[] reference;
        var (r, ra) = await SongAsync(10, 15);
        using (r)
        {
            ra.Speed = 0.92;
            ra.Mode = StretchMode.Independent;
            Assert.True(TimelineEditor.Split(ra, TimelineEditor.TimelineSecondsOfFrame(ra, start, r), r).Changed);
            Assert.True(TimelineEditor.Split(ra, TimelineEditor.TimelineSecondsOfFrame(ra, end, r), r).Changed);
            reference = Render(r, Rate * 30);
        }

        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            a.Speed = 0.92;
            a.Mode = StretchMode.Independent;
            double at = TimelineEditor.TimelineSecondsOfFrame(a, start, s);
            var piece = TimelineEditor.CopyStretch(a, start, end, s);
            TimelineEditor.ClearStretch(a, start, end, s);
            TimelineEditor.Paste(a, piece, at, s);

            Assert.Equal(start, TimelineEditor.StartFrameOf(a.Clips.First(c => TimelineEditor.StartFrameOf(c, s) >= start), s));
            int differing = Differing(Render(s, Rate * 30), 0, reference, 0, Rate * 30);
            Assert.True(differing == 0,
                $"{differing} frames differ: on a track at 92% the stretch pasted back at the moment it was cut from "
              + "is not the song split at its two ends, so its ends became joins with a declick — a dip where nothing changed");
        }
    }

    [Fact]
    public async Task CuttingAStretchAndPastingItBackIsExactUnderATempoRamp()
    {
        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            a.Bpm = 120;
            Assert.True(TempoRamps.Add(a, 5, 25, 1.08, s).Changed);
            var reference = Render(s, Rate * 30);

            long start = F(12.5), end = F(17.25);
            double at = TimelineEditor.TimelineSecondsOfFrame(a, start, s);
            var piece = TimelineEditor.CopyStretch(a, start, end, s);
            TimelineEditor.ClearStretch(a, start, end, s);
            TimelineEditor.Paste(a, piece, at, s);

            int differing = Differing(Render(s, Rate * 30), 0, reference, 0, Rate * 30);
            Assert.True(differing == 0,
                $"{differing} frames differ: under a tempo ramp the stretch pasted back where it was cut from did "
              + "not land on the frame it came from, so its ends click or leave a hole");
        }
    }

    [Fact]
    public async Task AStretchWithAGapInItPastesTheGapTooAndClearsWhatIsUnderIt()
    {
        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            // Take the middle out: sections 0-10 and 15-60, a gap between.
            TimelineEditor.Delete(a, a.Clips[1]);
            var reference = Render(s, Rate * 60);

            var piece = TimelineEditor.CopyStretch(a, F(8), F(17), s);
            Assert.Equal(F(9), piece.LengthFrames);
            Assert.Equal(new[] { 0L, F(7) }, piece.Clips.Select(c => TimelineEditor.StartFrameOf(c, s)).ToArray());

            // Pasted onto the same song further on, 40 s to 49 s: the gap lands on 42-47, where the
            // song was playing, and must clear it — what was copied is what lands.
            TimelineEditor.Paste(a, piece, 40, s);
            var after = Render(s, Rate * 60);

            Assert.Equal(0, Differing(after, F(40), reference, F(8), F(2), 0, F(2)));
            int loud = Loud(after, F(42), F(5), (long)(TrackVoice.DeclickSeconds * Rate) + 2);
            Assert.True(loud == 0, $"{loud} frames sound in the pasted gap: the song that was under it should have been cleared, as a paste overwrites");
            Assert.Equal(0, Differing(after, F(47), reference, F(15), F(2), 0, F(2)));
            Assert.Equal(0, Differing(after, F(49) + 2000, reference, F(49) + 2000, F(5)));
        }
    }

    [Fact]
    public async Task AStretchWhollyInAGapHasNothingToCopyAndDeletingItChangesNothing()
    {
        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            TimelineEditor.Delete(a, a.Clips[1]);
            int before = a.Clips.Count;
            Assert.Empty(TimelineEditor.CopyStretch(a, F(11), F(14), s).Clips);
            var result = TimelineEditor.ClearStretch(a, F(11), F(14), s);
            Assert.False(result.Changed);
            Assert.Equal(before, a.Clips.Count);
            Assert.Empty(TimelineEditor.CopyStretch(a, F(80), F(90), s).Clips);
        }
    }

    [Fact]
    public async Task ASectionsOwnFadeIsKeptButTheSideAStretchCutsHasNone()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            a.Clips[1].FadeInSeconds = 1;
            a.Clips[1].FadeOutSeconds = 2;

            var whole = TimelineEditor.CopyStretch(a, F(9), F(15), s).Clips.Single(c => c.SourceStartSeconds >= 10);
            Assert.Equal(1, whole.FadeInSeconds);       // its own start is inside the stretch
            Assert.Equal(0, whole.FadeOutSeconds);      // cut at 15 s by the stretch

            var cutIn = TimelineEditor.CopyStretch(a, F(12), F(25), s).Clips.First();
            Assert.Equal(0, cutIn.FadeInSeconds);
            Assert.Equal(2, cutIn.FadeOutSeconds);
        }
    }

    // ================================================================ delete

    [Fact]
    public async Task DeletingAStretchLeavesAHoleAndMovesNothing()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var reference = Render(s, Rate * 30);
            var starts = a.Clips.Select(c => (c.Id, c.TrackStartSeconds)).ToList();

            var result = TimelineEditor.ClearStretch(a, F(12), F(18), s);
            Assert.True(result.Changed, result.Message);
            var after = Render(s, Rate * 30);

            long guard = (long)(TrackVoice.DeclickSeconds * Rate) + 2;
            int loud = Loud(after, F(12), F(6), guard);
            Assert.True(loud == 0, $"{loud} frames still sound in the deleted stretch");
            int moved = Differing(after, 0, reference, 0, Rate * 30, F(12), F(18)) - Differing(after, F(12), reference, F(12), F(6), 0, F(6));
            Assert.True(moved == 0, $"{moved} frames outside the deleted stretch changed: something rippled, which would pull this track out of time with every other");
            foreach (var (id, at) in starts)
                if (a.Clips.FirstOrDefault(c => c.Id == id) is { } kept && at < 12)
                    Assert.Equal(at, kept.TrackStartSeconds);
        }
    }

    // ================================================================ repeat

    [Fact]
    public async Task RepeatingAStretchPutsTheCopyOnTheVeryFrameItEnds()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var reference = Render(s, Rate * 40);
            var result = TimelineEditor.RepeatStretch(a, F(8), F(12.5), s);
            Assert.True(result.Changed, result.Message);
            Assert.Equal((F(12.5), F(17)), result.Span);
            Assert.Equal(F(12.5), TimelineEditor.StartFrameOf(result.Placed[0], s));

            var after = Render(s, Rate * 40);
            // The repeat plays 8 s to 12.5 s of the song again, seam at 10 s included; the join at
            // 12.5 s is a butt join, crossfaded, so it is left out, and so is the cut at 17 s.
            int differing = Differing(after, F(12.5), reference, F(8), F(4.5), 0, F(4.5));
            Assert.True(differing == 0, $"{differing} frames of the repeat are not the marked stretch");
            Assert.Equal(0, Differing(after, 0, reference, 0, F(12.5), F(12.5)));
            Assert.Equal(0, Differing(after, F(17), reference, F(17), F(10), 0));
        }
    }

    [Fact]
    public async Task AStretchEndingInAGapRepeatsEveryStretchLengthGapAndAll()
    {
        var (s, a) = await SongAsync(10, 15);
        using (s)
        {
            TimelineEditor.Delete(a, a.Clips[1]);         // 0-10, gap, 15-60
            var result = TimelineEditor.RepeatStretch(a, F(6), F(12), s);
            Assert.True(result.Changed, result.Message);
            Assert.Equal(F(12), TimelineEditor.StartFrameOf(result.Placed.Single(), s));
            Assert.Equal((F(12), F(18)), result.Span);
            // The repeat's gap, 16-18 s, cleared what was under it — the start of the 15 s section.
            Assert.DoesNotContain(a.Clips, c => TimelineEditor.StartFrameOf(c, s) < F(18) && TimelineEditor.EndFrameOf(c, s) > F(16));
        }
    }

    // ================================================================ from the review, 2026-10-12

    [Fact]
    public async Task AMarkEndAHairPastASeamIsTidiedOntoItSoCutAndPasteBackChangesNothing()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var reference = Render(s, Rate * 30);

            // 10 ms past the seam at 20 s: Ctrl held, or a snap onto another track's edge nearby.
            long start = F(12), end = F(20.010);
            Assert.Equal((F(12), F(20)), TimelineEditor.TidyStretch(a, start, end, s));
            var piece = TimelineEditor.CopyStretch(a, start, end, s);
            TimelineEditor.ClearStretch(a, start, end, s);
            TimelineEditor.Paste(a, piece, 12, s);

            int differing = Differing(Render(s, Rate * 30), 0, reference, 0, Rate * 30);
            Assert.True(differing == 0,
                $"{differing} frames differ: the copy left out a 10 ms sliver the cut threw away, so pasting back left a gap "
              + "with a declick either side where nothing was meant to change");
        }
    }

    [Fact]
    public async Task AStretchStartingAHairBeforeASeamLeavesNoRemnantBehindIt()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            Assert.Equal((F(10), F(15)), TimelineEditor.TidyStretch(a, F(9.990), F(15), s));
            TimelineEditor.ClearStretch(a, F(9.990), F(15), s);
            Assert.Contains(a.Clips, c => TimelineEditor.EndFrameOf(c, s) == F(10) && TimelineEditor.StartFrameOf(c, s) == 0);
        }
    }

    [Fact]
    public async Task RepeatingAStretchThatEndsAHairPastAnEdgeJoinsOnTheEdge()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var result = TimelineEditor.RepeatStretch(a, F(12), F(20.010), s);
            Assert.True(result.Changed, result.Message);
            Assert.True(result.Span == (F(20), F(28)),
                $"the repeat went to {result.Span}: 10 ms after the section it repeats ends, a gap at every repeat instead of a crossfaded join");
        }
    }

    [Fact]
    public async Task PastingWhatWasCopiedFromASongNoLongerInTheProjectIsRefusedAndChangesNothing()
    {
        var other = Path.Combine(_dir, "other.wav");
        var o = new float[Rate * 30 * 2];
        for (int f = 0; f < Rate * 30; f++) o[f * 2] = o[f * 2 + 1] = 0.5f - f / (2f * Rate * 30);
        AudioClip.FromSamples("other", o, Rate).SaveWav(other);

        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            var b = await s.AddTrackAsync(other, 0);
            var piece = TimelineEditor.CopyStretch(a, F(12), F(18), s);
            s.RemoveTrack(a);                          // lets go of song A's audio
            var before = b.Clips.Select(c => (c.TrackStartSeconds, c.SourceStartSeconds, c.SourceEndSeconds)).ToList();

            var result = TimelineEditor.Paste(b, piece, 5, s);
            Assert.False(result.Changed,
                "pasted a section of a song no longer in the project: it cleared 6 s of the other song and put down silence that would play again after a reload");
            Assert.Equal(before, b.Clips.Select(c => (c.TrackStartSeconds, c.SourceStartSeconds, c.SourceEndSeconds)).ToList());
        }
    }

    // ================================================================ marking

    [Fact]
    public async Task AMarkEndSnapsToTheExactFrameOfAnEdgeOnItsOwnTrack()
    {
        var (s, a) = await SongAsync(10, 20);
        using (s)
        {
            a.Speed = 0.9;                                  // an edge's timeline time is then not a whole frame
            var snap = new SnapSettings(true, 40);          // 10 px is a quarter of a second
            double edge = TimelineEditor.TimelineSecondsOfFrame(a, F(10), s);

            long at = TimelineEditor.SnapStretchEnd(a, edge + 0.2, null, snap, s, out var on);
            Assert.True(at == F(10), $"snapped to frame {at}, not {F(10)}: a cut there would leave a sliver of the next section, or a gap");
            Assert.Equal(edge, on);

            long free = TimelineEditor.SnapStretchEnd(a, edge + 0.2, null, new SnapSettings(false, 40), s, out on);
            Assert.Null(on);
            Assert.Equal(TimelineEditor.TrackFrameAt(a, edge + 0.2, s), free);
        }
    }

    [Fact]
    public async Task AMarkEndSnapsToThePlayheadWhenThatIsNearer()
    {
        var (s, a) = await SongAsync(10);
        using (s)
        {
            var snap = new SnapSettings(true, 40);
            long at = TimelineEditor.SnapStretchEnd(a, 10.2, 10.1, snap, s, out var on);
            Assert.Equal(10.1, on);
            Assert.Equal(F(10.1), at);
            at = TimelineEditor.SnapStretchEnd(a, 10.05, 10.2, snap, s, out on);
            Assert.Equal(F(10), at);                         // the edge is nearer
        }
    }
}
