using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// A render holds what can be heard and ends where the last audible track ends (Daniel,
/// 2026-10-27: "mixdown should only do non-muted or selected tracks only"). Two songs of different
/// lengths, ramps running opposite ways: a short one of 4 s and a long one of 10 s. A render with
/// the long one muted, or with the short one soloed, is compared byte for byte with a render of a
/// session that holds only the short one — the same length and the same samples, or the file has
/// seconds of silence on its end from a song nobody can hear.
/// </summary>
public class RenderScopeTests : IDisposable
{
    const int Rate = 48000;
    const int ShortFrames = Rate * 4;
    const int LongFrames = Rate * 10;
    const double Tail = 0.5;
    private readonly string _dir;
    private readonly string _short;
    private readonly string _long;

    public RenderScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-render-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _short = Path.Combine(_dir, "short.wav");
        _long = Path.Combine(_dir, "long.wav");
        AudioClip.FromSamples("short", Ramp(ShortFrames, 0.4f), Rate).SaveWav(_short);
        AudioClip.FromSamples("long", Ramp(LongFrames, -0.3f), Rate).SaveWav(_long);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static float[] Ramp(int frames, float top)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = top * f / frames;
        return s;
    }

    /// <summary>Renders as the Render button does, limiter off, and reads the file back.</summary>
    (RenderResult Result, byte[] Bytes) Render(ProjectSession s, string name)
    {
        s.ApplyAll();
        string path = Path.Combine(_dir, name);
        var result = Renderer.Render(s.Graph, path, new RenderOptions { Format = RenderFormat.Pcm24, TailSeconds = Tail, ApplyLimiter = false });
        return (result, File.ReadAllBytes(path));
    }

    async Task<(ProjectSession s, TrackModel shortTrack, TrackModel longTrack)> BothAsync()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var shortTrack = await s.AddTrackAsync(_short);
        var longTrack = await s.AddTrackAsync(_long);
        return (s, shortTrack, longTrack);
    }

    /// <summary>The file a session holding only the short song renders.</summary>
    async Task<byte[]> ShortAloneAsync()
    {
        var alone = ProjectSession.CreateOffline(Rate);
        await alone.AddTrackAsync(_short);
        return Render(alone, "short-alone.wav").Bytes;
    }

    static void SameFile(byte[] want, byte[] got, string consequence)
        => Assert.True(want.AsSpan().SequenceEqual(got), $"{consequence} ({got.Length} bytes where the file should be {want.Length})");

    [Fact]
    public async Task ARenderEndsWhereTheLastAudibleTrackEnds()
    {
        var (s, _, longTrack) = await BothAsync();
        longTrack.Muted = true;

        var (result, bytes) = Render(s, "long-muted.wav");
        Assert.True(result.Frames == ShortFrames + (long)(Tail * Rate),
            $"the render is {result.Seconds:0.00}s, not 4.50s: it ran on to the end of the muted 10 s song, so the file ends in seconds of silence from a song nobody can hear");
        SameFile(await ShortAloneAsync(), bytes, "with the long song muted, the file is not the short song alone");
    }

    [Fact]
    public async Task ARenderWithASoloHoldsOnlyTheSoloedTrackAndEndsWithIt()
    {
        var (s, shortTrack, _) = await BothAsync();
        Assert.True(s.Solo(shortTrack, alongside: false).Changed);

        var (result, bytes) = Render(s, "short-soloed.wav");
        Assert.True(result.Frames == ShortFrames + (long)(Tail * Rate),
            $"the render is {result.Seconds:0.00}s, not 4.50s: a song that is not soloed set the length of the mixdown");
        SameFile(await ShortAloneAsync(), bytes, "with the short song soloed, the file is not the short song alone: the other song leaked into the mixdown");
    }

    [Fact]
    public async Task UnmutingATrackPutsItBackIntoTheRender()
    {
        var (s, _, longTrack) = await BothAsync();
        longTrack.Muted = true;
        Assert.Equal(ShortFrames + (long)(Tail * Rate), Render(s, "muted.wav").Result.Frames);

        longTrack.Muted = false;
        var (result, bytes) = Render(s, "unmuted.wav");
        Assert.True(result.Frames == LongFrames + (long)(Tail * Rate),
            $"after unmuting, the render is {result.Seconds:0.00}s, not 10.50s: the song he unmuted is still cut off");

        var both = ProjectSession.CreateOffline(Rate);
        await both.AddTrackAsync(_short);
        await both.AddTrackAsync(_long);
        SameFile(Render(both, "both.wav").Bytes, bytes, "after unmuting, the file is not both songs");
    }

    [Fact]
    public async Task WithNothingAudibleThereIsNothingToRender()
    {
        var (s, shortTrack, longTrack) = await BothAsync();
        shortTrack.Muted = true;
        longTrack.Muted = true;
        s.ApplyAll();
        Assert.True(s.Graph.AudibleLengthFrames == 0 && s.Graph.LengthFrames == LongFrames,
            "with every track muted the render still has a length: the Render button would write a file of nothing but silence");

        shortTrack.Muted = false;
        s.Solo(longTrack, alongside: false);
        longTrack.Muted = true;                     // soloed, but mute wins
        s.ApplyAll();
        Assert.True(s.Graph.AudibleLengthFrames == 0,
            "a soloed track that is also muted counts towards the render's length: the file would be as long as a track that is silent");
    }

    [Fact]
    public async Task WhatARenderLeavesOutIsNamedWithTheReason()
    {
        var (s, shortTrack, longTrack) = await BothAsync();
        Assert.Empty(s.LeftOutOfRender());

        longTrack.Muted = true;
        Assert.Equal(new[] { (longTrack, "muted") }, s.LeftOutOfRender());

        longTrack.Muted = false;
        s.Solo(shortTrack, alongside: false);
        Assert.Equal(new[] { (longTrack, "not soloed") }, s.LeftOutOfRender());

        shortTrack.Muted = true;                    // the soloed one muted too: both are left out, each for its own reason
        Assert.Equal(new[] { (shortTrack, "muted"), (longTrack, "not soloed") }, s.LeftOutOfRender());

        var empty = s.AddEmptyTrack();              // nothing on it, so nothing is left out by muting it
        empty.Muted = true;
        Assert.Equal(new[] { (shortTrack, "muted"), (longTrack, "not soloed") }, s.LeftOutOfRender());
    }
}
