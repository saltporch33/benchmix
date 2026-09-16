using Benchmix.Engine.Dsp;
using Xunit;

namespace Benchmix.Tests;

public class FilterDesignTests
{
    [Fact]
    public void BesselPoleTable_RebuildsItsOwnPolynomial()
    {
        // Throws if a pole constant was mistyped. Cheap insurance against a silently detuned crossover.
        var ex = Record.Exception(() => FilterDesign.Bessel4LowPass(1000, 48000));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(246.0)]
    [InlineData(2500.0)]
    [InlineData(80.0)]
    public void Bessel4_IsMinusThreeDbAtItsCutoff(double cutoff)
    {
        var cascade = new BiquadCascade(FilterDesign.Bessel4LowPass(cutoff, 48000), 1);
        double mag = cascade.MagnitudeAt(cutoff, 48000);
        double db = 20 * Math.Log10(mag);
        Assert.InRange(db, -3.3, -2.7);
    }

    [Fact]
    public void Bessel4_RollsOffAboutTwentyFourDbPerOctave()
    {
        var cascade = new BiquadCascade(FilterDesign.Bessel4LowPass(500, 48000), 1);
        double at2k = 20 * Math.Log10(cascade.MagnitudeAt(2000, 48000));
        double at4k = 20 * Math.Log10(cascade.MagnitudeAt(4000, 48000));
        Assert.InRange(at2k - at4k, 20, 28);       // one octave, fourth order
    }

    [Fact]
    public void Bessel4_HasFlatterGroupDelayThanButterworth()
    {
        // This is the entire reason Bessel is used for the subtractive crossover.
        var bessel = FilterDesign.Bessel4LowPass(1000, 48000);
        var butter = FilterDesign.ButterworthLowPass(4, 1000, 48000);
        double bDc = ThreeBandEq.GroupDelayAtDc(bessel);
        double wDc = ThreeBandEq.GroupDelayAtDc(butter);
        Assert.True(bDc > 0 && wDc > 0);
        Assert.True(bDc < wDc, $"expected Bessel ({bDc:F2}) to delay less at DC than Butterworth ({wDc:F2})");
    }
}

public class ThreeBandEqTests
{
    const int Rate = 48000;

    static float[] WhiteNoise(int n, int seed = 12345)
    {
        var rnd = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.5f;
        return x;
    }

    static float[] Sine(double hz, int n, int rate = Rate)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        return x;
    }

    static double Rms(ReadOnlySpan<float> x, int from = 0)
    {
        double s = 0;
        int n = 0;
        for (int i = from; i < x.Length; i++) { s += x[i] * (double)x[i]; n++; }
        return n == 0 ? 0 : Math.Sqrt(s / n);
    }

    [Fact]
    public void Isolator_AtUnity_IsBitExactPassthrough()
    {
        // THE load-bearing test. Benchmix renders finished audio files; a track whose EQ knobs were
        // never touched must come out the far side unchanged, not merely "close".
        var eq = new ThreeBandEq(Rate, 2, EqMode.Isolator);
        eq.SetGains(1, 1, 1);
        eq.SnapGains();

        const int Frames = 20000;
        var input = WhiteNoise(Frames * 2);
        var work = (float[])input.Clone();
        eq.Process(work);

        int lat = eq.LatencySamples;
        Assert.True(lat > 0, "the subtractive path should report a real latency");

        int compared = 0;
        for (int f = lat; f < Frames; f++)
            for (int c = 0; c < 2; c++)
            {
                float got = work[f * 2 + c];
                float want = input[(f - lat) * 2 + c];
                Assert.True(got == want,
                    $"frame {f} ch {c}: got {got:R}, want {want:R} — unity passthrough is not exact");
                compared++;
            }
        Assert.True(compared > 30000);
    }

    [Fact]
    public void Isolator_KillingLow_RemovesBassAndKeepsTreble()
    {
        var eq = new ThreeBandEq(Rate, 1, EqMode.Isolator);
        eq.SetGains(0, 1, 1);
        eq.SnapGains();

        var bass = Sine(60, 48000);
        var treble = Sine(8000, 48000);
        var bassOut = (float[])bass.Clone();
        var trebleOut = (float[])treble.Clone();

        eq.Process(bassOut);
        eq.Reset();
        eq.SnapGains();
        eq.Process(trebleOut);

        double bassAtten = 20 * Math.Log10(Rms(bassOut, 4000) / Rms(bass, 4000));
        double trebleAtten = 20 * Math.Log10(Rms(trebleOut, 4000) / Rms(treble, 4000));

        Assert.True(bassAtten < -30, $"60 Hz should be gone, was {bassAtten:F1} dB");
        Assert.InRange(trebleAtten, -1.0, 1.0);
    }

    [Fact]
    public void Isolator_KillingHigh_RemovesTrebleAndKeepsBass()
    {
        var eq = new ThreeBandEq(Rate, 1, EqMode.Isolator);
        eq.SetGains(1, 1, 0);
        eq.SnapGains();

        var bass = Sine(60, 48000);
        var treble = Sine(10000, 48000);
        var bassOut = (float[])bass.Clone();
        var trebleOut = (float[])treble.Clone();

        eq.Process(bassOut);
        eq.Reset();
        eq.SnapGains();
        eq.Process(trebleOut);

        double bassAtten = 20 * Math.Log10(Rms(bassOut, 4000) / Rms(bass, 4000));
        double trebleAtten = 20 * Math.Log10(Rms(trebleOut, 4000) / Rms(treble, 4000));

        Assert.InRange(bassAtten, -1.0, 1.0);
        Assert.True(trebleAtten < -30, $"10 kHz should be gone, was {trebleAtten:F1} dB");
    }

    [Fact]
    public void Isolator_KillingAllThree_IsSilence()
    {
        var eq = new ThreeBandEq(Rate, 2, EqMode.Isolator);
        eq.SetGains(0, 0, 0);
        eq.SnapGains();

        var work = WhiteNoise(40000);
        eq.Process(work);

        double rms = Rms(work.AsSpan(8000));
        Assert.True(rms < 1e-6, $"a full kill should be silent, rms was {rms:E2}");
    }

    [Fact]
    public void TurningAKnobDuringPlayback_DoesNotClick()
    {
        var eq = new ThreeBandEq(Rate, 1, EqMode.Isolator);
        eq.SetGains(1, 1, 1);
        eq.SnapGains();

        var sine = Sine(1000, Rate * 2);
        var work = (float[])sine.Clone();

        // Sweep the low knob from unity to kill and back, a block at a time, mid-playback.
        const int Block = 256;
        int blocks = work.Length / Block;
        for (int b = 0; b < blocks; b++)
        {
            double t = b / (double)blocks;
            double g = t < 0.5 ? 1 - 2 * t : 2 * (t - 0.5);
            eq.SetGains(g, 1, 1);
            eq.Process(work.AsSpan(b * Block, Block));
        }

        double worstJump = 0;
        for (int i = 8000 + 1; i < work.Length; i++)
            worstJump = Math.Max(worstJump, Math.Abs(work[i] - work[i - 1]));

        // A clean 1 kHz sine at 0.5 amplitude steps by at most ~0.065 per sample.
        Assert.True(worstJump < 0.15, $"worst sample-to-sample jump {worstJump:F4} suggests a click");
    }

    [Fact]
    public void Shelving_KillFloorIsClampedNotSilent()
    {
        var eq = new ThreeBandEq(Rate, 1, EqMode.Shelving);
        eq.SetGains(0, 1, 1);
        eq.SnapGains();

        var bass = Sine(50, 48000);
        var work = (float[])bass.Clone();
        eq.Process(work);

        double atten = 20 * Math.Log10(Rms(work, 4000) / Rms(bass, 4000));
        // Shelves cannot reach -inf; Mixxx clamps at -23 dB and so do we.
        Assert.InRange(atten, -30, -12);
    }

    [Fact]
    public void GainConversionRoundTrips()
    {
        Assert.Equal(0.0, ThreeBandEq.FromDb(double.NegativeInfinity));
        Assert.Equal(double.NegativeInfinity, ThreeBandEq.ToDb(0));
        Assert.Equal(1.0, ThreeBandEq.FromDb(0), 12);
        Assert.Equal(6.0, ThreeBandEq.ToDb(ThreeBandEq.FromDb(6.0)), 9);
    }
}
