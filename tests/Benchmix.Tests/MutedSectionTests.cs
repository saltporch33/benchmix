using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Muting a section in place (Daniel, 2026-10-28: "retain looped sections on timeline but gray them
/// out to temporarily deactivate them"; built 2026-10-29). A muted section is a gap to the engine
/// and stays in the file. The source is a ramp, as everywhere else.
/// </summary>
public class MutedSectionTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public MutedSectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-muted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        int frames = Rate * 20;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = 0.5f * f / frames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
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
    public async Task AMutedSectionPlaysExactlyAsAGapWould_AndIsStillThere()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        Assert.True(TimelineEditor.Split(t, 10.0, s).Changed);
        var second = t.Clips.OrderBy(c => c.TrackStartSeconds).Last();
        int frames = Rate * 20;

        var unmuted = Render(s, frames);
        second.Muted = true;
        var muted = Render(s, frames);
        Assert.Equal(2, t.Clips.Count);

        t.Clips.Remove(second);
        var gap = Render(s, frames);

        Assert.True(muted.AsSpan().SequenceEqual(gap),
            "a muted section did not play as the gap deleting it would leave — the declick at the section before it, or the silence, differs");
        Assert.True(unmuted.AsSpan(Rate * 15 * 2, Rate * 2).ToArray().Any(v => v != 0),
            "the unmuted render is silent in the second half, so the comparison proves nothing");
        Assert.True(muted.AsSpan(Rate * 11 * 2, Rate * 8 * 2).ToArray().All(v => v == 0),
            "a muted section is still heard");
    }

    [Fact]
    public async Task MutedIsSavedInTheFile_AsVersionSeven()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        Assert.True(TimelineEditor.Split(t, 10.0, s).Changed);
        var second = t.Clips.OrderBy(c => c.TrackStartSeconds).Last();
        second.Muted = true;

        string path = Path.Combine(_dir, "muted.benchmix");
        ProjectStore.Save(s.Project, path, "20261029-000000");
        var loaded = ProjectStore.Load(path).Project;

        Assert.Equal(7, loaded.SchemaVersion);
        var back = loaded.Tracks.Single().Clips.OrderBy(c => c.TrackStartSeconds).ToList();
        Assert.False(back[0].Muted);
        Assert.True(back[1].Muted, "the muted section came back unmuted, so a project saved with one would play it again");
    }
}
