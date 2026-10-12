using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Joining sections back into one (Daniel, 2026-10-12): "sometimes i split to extract just a part of
/// a song, and move it to a diff track, but then I just want the original song to go back to
/// normal." On a ramp source (value == position), so where every frame came from is in the audio.
/// </summary>
public class SectionJoinTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;
    private readonly string _other;

    public SectionJoinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-join", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        _other = Path.Combine(_dir, "other.wav");
        int frames = Rate * 40;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)frames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = 0.5f - f / (2f * frames);
        AudioClip.FromSamples("other", s, Rate).SaveWav(_other);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    async Task<(ProjectSession S, TrackModel T)> CutAsync(params double[] at)
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 0);
        foreach (var x in at) Assert.True(TimelineEditor.Split(t, x, s).Changed, $"could not split at {x}s");
        return (s, t);
    }

    static ClipModel At(TrackModel t, double trackSeconds) => t.Clips.Single(c => Math.Abs(c.TrackStartSeconds - trackSeconds) < 1e-6);

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

    static int Differing(float[] a, float[] b) => Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]);

    [Fact]
    public async Task JoiningASplitChangesNotOneSample()
    {
        var (s, t) = await CutAsync();
        var original = t.Clips.Single().Clone();
        var before = Render(s, Rate * 30);

        TimelineEditor.Split(t, 10, s);
        TimelineEditor.Split(t, 20, s);
        var result = TimelineEditor.Join(t, t.Clips.ToList(), s);
        var after = Render(s, Rate * 30);

        var joined = t.Clips.Single();
        Assert.True(result.Changed && joined.SourceStartSeconds == original.SourceStartSeconds && joined.SourceEndSeconds == original.SourceEndSeconds
                    && joined.TrackStartSeconds == original.TrackStartSeconds, "joining the three pieces did not give back the section that was split");
        int differing = Differing(before, after);
        Assert.True(differing == 0, $"{differing} samples changed after splitting and joining back — joining a split must change nothing");
        Assert.Contains("exactly as before", result.Message);
    }

    [Fact]
    public async Task JoiningAroundAGapPutsTheSongBackAsItWas()
    {
        var (s, t) = await CutAsync();
        var before = Render(s, Rate * 30);

        TimelineEditor.Split(t, 10, s);
        TimelineEditor.Split(t, 14, s);
        TimelineEditor.Delete(t, At(t, 10));                     // the piece taken out
        var result = TimelineEditor.Join(t, new[] { At(t, 0), At(t, 14) }, s);
        var after = Render(s, Rate * 30);

        Assert.True(result.Changed && t.Clips.Count == 1, "the two pieces either side of the gap were not joined");
        int differing = Differing(before, after);
        Assert.True(differing == 0, $"{differing} samples differ from the song before it was cut — the gap was not filled with the song again");
        Assert.Contains("4.0s that was missing", result.Message);
    }

    [Fact]
    public async Task OneSelectedSectionJoinsTheNeighbourThatLinesUp()
    {
        var (s, t) = await CutAsync(10, 14, 25);
        TimelineEditor.Delete(t, At(t, 10));
        Assert.True(TimelineEditor.JoinPartner(t, At(t, 0), s) == At(t, 14), "the piece after the gap was not taken as the one to join");
        Assert.True(TimelineEditor.JoinPartner(t, At(t, 14), s) == At(t, 25), "the next piece, which lines up, was not preferred");
        Assert.True(TimelineEditor.JoinPartner(t, At(t, 25), s) == At(t, 14), "with nothing after it, the piece before was not taken");
    }

    [Fact]
    public async Task ARepeatDoesNotLineUpAndIsNotJoined()
    {
        var (s, t) = await CutAsync(10, 14);
        var piece = At(t, 10);
        TimelineEditor.Duplicate(t, piece, s);                  // the repeat overwrites 14-18 with 10-14 of the song
        var repeat = t.Clips.Single(c => c != piece && Math.Abs(c.TrackStartSeconds - 14) < 1e-6);
        int count = t.Clips.Count;

        var result = TimelineEditor.Join(t, new[] { piece, repeat }, s);
        Assert.True(!result.Changed && t.Clips.Count == count && result.Message.Contains("do not line up"),
            "a loop's repeat was joined to what it repeats — the song would play on instead of looping");
        Assert.True(TimelineEditor.JoinPartner(t, repeat, s) is null, "a loop's repeat found a piece to join with on its own");
    }

    [Fact]
    public async Task SectionsFromAnotherSongAreNotJoined()
    {
        var (s, t) = await CutAsync(10);
        TimelineEditor.Delete(t, At(t, 10));
        var other = await s.AddTrackAsync(_other, 0);
        TimelineEditor.Paste(t, TimelineEditor.Copy(other.Clips[0]), 10, s);
        var foreign = t.Clips.Single(c => c.SourcePath == _other);

        var result = TimelineEditor.Join(t, new[] { At(t, 0), foreign }, s);
        Assert.True(!result.Changed && result.Message.Contains("different songs"), "sections of two different songs were joined");
    }

    [Fact]
    public async Task SomethingInTheGapStopsAJoin()
    {
        var (s, t) = await CutAsync(10, 20);
        var middle = At(t, 10);
        TimelineEditor.Delete(t, middle);
        var other = await s.AddTrackAsync(_other, 0);
        var put = TimelineEditor.Copy(other.Clips[0]);
        put.SourceEndSeconds = 3;
        TimelineEditor.Paste(t, put, 12, s);
        int count = t.Clips.Count;

        var result = TimelineEditor.Join(t, new[] { At(t, 0), At(t, 20) }, s);
        Assert.True(!result.Changed && t.Clips.Count == count && result.Message.Contains("in the gap"),
            "a join covered a section that had been put in the gap — it would be lost without a word");
    }

    [Fact]
    public async Task AJoinKeepsTheOuterFades()
    {
        var (s, t) = await CutAsync(10, 20);
        At(t, 0).FadeInSeconds = 1;
        At(t, 20).FadeOutSeconds = 2;
        TimelineEditor.Join(t, t.Clips.ToList(), s);
        var joined = t.Clips.Single();
        Assert.True(joined.FadeInSeconds == 1 && joined.FadeOutSeconds == 2, "joining lost the fade in at the start or the fade out at the end");
    }

    [Fact]
    public async Task AJoinUnderATempoRampPutsTheSongBackAsItWas()
    {
        var (s, t) = await CutAsync();
        t.Mode = StretchMode.Linked;
        t.Ramps.Add(new TempoRamp(5, 20, 1.1));
        var before = Render(s, Rate * 30);

        TimelineEditor.Split(t, 8, s);
        TimelineEditor.Split(t, 12, s);
        var cut = t.Clips.OrderBy(c => c.TrackStartSeconds).ElementAt(1);
        TimelineEditor.Delete(t, cut);
        var ordered = t.Clips.OrderBy(c => c.TrackStartSeconds).ToList();
        Assert.True(TimelineEditor.Join(t, ordered, s).Changed);
        int differing = Differing(before, Render(s, Rate * 30));
        Assert.True(differing == 0, $"{differing} samples differ after cutting a piece out under a tempo ramp and joining around it");
    }
}
