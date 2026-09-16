using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

public class ZzTempOffsetProbe
{
    const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    public ZzTempOffsetProbe(ITestOutputHelper o) => _o = o;

    static AudioClip Ramp(int frames)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) { s[f * 2] = (float)(f / 1e6); s[f * 2 + 1] = (float)(f / 1e6); }
        return AudioClip.FromSamples("ramp", s, Rate);
    }

    static float[] RenderAll(long offset, int block, int totalFrames)
    {
        using var graph = new MixGraph(Rate) { LimiterEnabled = false };
        var t = graph.AddTrack();
        t.SetClip(Ramp(40000));
        t.TimelineOffsetFrames = offset;
        graph.Seek(0);
        graph.SnapParameters();
        var outBuf = new float[totalFrames * 2];
        int done = 0;
        while (done < totalFrames)
        {
            int n = Math.Min(block, totalFrames - done);
            graph.Render(outBuf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return outBuf;
    }

    [Fact]
    public void Probe()
    {
        foreach (var (off, blk) in new[] { (1000L, 4096), (1000L, 512), (300L, 1024) })
        {
            var buf = RenderAll(off, blk, 20000);
            int wrong = 0; double maxStep = 0; int stepAt = -1;
            for (int f = (int)off; f < 20000; f++)
            {
                double expect = (f - off) / 1e6;
                if (Math.Abs(buf[f * 2] - expect) > 1e-7) wrong++;
                if (f > off)
                {
                    double step = Math.Abs(buf[f * 2] - buf[(f - 1) * 2]);
                    if (step > maxStep) { maxStep = step; stepAt = f; }
                }
            }
            Assert.True(true); _o.WriteLine($"offset={off} block={blk} first={buf[off * 2]:F6} (expect 0.000000) wrong={wrong} maxStep={maxStep:E3} at {stepAt}");
        }
    }
}
