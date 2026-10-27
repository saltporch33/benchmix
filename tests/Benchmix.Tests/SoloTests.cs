using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// The S button (Daniel, 2026-10-27): a click solos that track alone, Ctrl+click adds it to the
/// ones already soloed. What plays is measured: a solo render is compared, sample for sample, with
/// a session that holds only the tracks that should be heard. The two songs are ramps running
/// opposite ways, so any of the other one leaking in shows.
/// </summary>
public class SoloTests : IDisposable
{
    const int Rate = 48000;
    const int Frames = Rate * 4;
    private readonly string _dir;
    private readonly string _up;
    private readonly string _down;

    public SoloTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-solo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _up = Path.Combine(_dir, "up.wav");
        _down = Path.Combine(_dir, "down.wav");
        var up = new float[Frames * 2];
        var down = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            up[f * 2] = up[f * 2 + 1] = 0.4f * f / Frames;
            down[f * 2] = down[f * 2 + 1] = -0.3f * (Frames - f) / Frames;
        }
        AudioClip.FromSamples("up", up, Rate).SaveWav(_up);
        AudioClip.FromSamples("down", down, Rate).SaveWav(_down);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static float[] Render(ProjectSession session)
    {
        session.ApplyAll();
        var g = session.Graph;
        g.LimiterEnabled = false;
        g.Seek(0);
        g.SnapParameters();
        var buf = new float[Frames * 2];
        for (int done = 0; done < Frames;)
        {
            int n = Math.Min(1024, Frames - done);
            g.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    /// <summary>What the mix is with only these songs in it, nothing soloed.</summary>
    async Task<float[]> OnlyAsync(params string[] wavs)
    {
        var s = ProjectSession.CreateOffline(Rate);
        foreach (var w in wavs) await s.AddTrackAsync(w);
        return Render(s);
    }

    async Task<(ProjectSession s, TrackModel bed, TrackModel topper)> TwoAsync()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var bed = await s.AddTrackAsync(_up);
        var topper = await s.AddTrackAsync(_down);
        return (s, bed, topper);
    }

    static void Same(float[] want, float[] got, string consequence)
    {
        for (int i = 0; i < want.Length; i++)
            if (want[i] != got[i])
                Assert.Fail($"{consequence} (sample {i / 2}: {got[i]} where it should be {want[i]})");
    }

    [Fact]
    public async Task AClickOnSPlaysThatTrackAlone()
    {
        var (s, bed, topper) = await TwoAsync();
        Assert.True(s.Solo(bed, alongside: false).Changed);
        Assert.True(bed.Soloed && !topper.Soloed);
        Same(await OnlyAsync(_up), Render(s), "the soloed bed did not play alone: the topper leaked into the solo");
    }

    [Fact]
    public async Task AClickOnAnotherSMovesTheSoloInsteadOfAddingIt()
    {
        var (s, bed, topper) = await TwoAsync();
        s.Solo(bed, alongside: false);
        s.Solo(topper, alongside: false);
        Assert.True(!bed.Soloed && topper.Soloed,
            "clicking the topper's S left the bed soloed too: two tracks play when he asked to hear one");
        Same(await OnlyAsync(_down), Render(s), "after moving the solo to the topper, the bed still played");
    }

    [Fact]
    public async Task CtrlClickAddsATrackAndTakesItAwayWithoutTouchingTheOthers()
    {
        var (s, bed, topper) = await TwoAsync();
        s.Solo(bed, alongside: false);
        s.Solo(topper, alongside: true);
        Assert.True(bed.Soloed && topper.Soloed, "Ctrl+click did not add the topper: two tracks can no longer be soloed together");
        Same(await OnlyAsync(_up, _down), Render(s), "with both soloed, the mix is not both songs");

        s.Solo(bed, alongside: true);
        Assert.True(!bed.Soloed && topper.Soloed, "Ctrl+click on a soloed track changed another track's solo");
        Same(await OnlyAsync(_down), Render(s), "after Ctrl+click took the bed out of the solo, it still played");
    }

    [Fact]
    public async Task AClickOnOneOfSeveralSoloedTracksLeavesOnlyThatOne()
    {
        var (s, bed, topper) = await TwoAsync();
        s.Solo(bed, alongside: false);
        s.Solo(topper, alongside: true);
        s.Solo(topper, alongside: false);
        Assert.True(!bed.Soloed && topper.Soloed,
            "a plain click on a lit S, with another track soloed too, did not leave just that track playing");
    }

    [Fact]
    public async Task AClickOnTheOnlySoloedTrackTurnsSoloOff()
    {
        var (s, bed, topper) = await TwoAsync();
        s.Solo(bed, alongside: false);
        s.Solo(bed, alongside: false);
        Assert.True(!bed.Soloed && !topper.Soloed, "the S button no longer toggles off: once soloed, a track stays soloed");
        Same(await OnlyAsync(_up, _down), Render(s), "with solo off, the mix is not both songs");
    }

    [Fact]
    public async Task AMutedTrackSoloedStaysSilentAndTheMessageSaysWhy()
    {
        var (s, bed, topper) = await TwoAsync();
        bed.Muted = true;
        var r = s.Solo(bed, alongside: false);
        Assert.Contains("muted", r.Message);
        var silence = Render(s);
        Assert.True(silence.All(v => v == 0), "a muted, soloed track played: mute no longer wins over solo");
    }
}
