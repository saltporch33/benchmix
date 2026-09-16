using System.Diagnostics;
using Signalsmith;

const int Rate = 48000;

static float[] Sine(double f, double sec, int rate)
{
    var n = (int)(sec * rate); var b = new float[n];
    for (int i = 0; i < n; i++) b[i] = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / rate));
    return b;
}

// Largest single-sample jump, as a multiple of the largest jump a clean signal of this
// frequency would make. A click shows up as a big outlier.
static (double maxJump, int idx) WorstDiscontinuity(float[] x, int skip)
{
    double worst = 0; int at = 0;
    for (int i = skip + 1; i < x.Length; i++)
    { var d = Math.Abs(x[i] - x[i - 1]); if (d > worst) { worst = d; at = i; } }
    return (worst, at);
}

static double Rms(float[] x, int from, int len)
{
    double s = 0; int n = 0;
    for (int i = from; i < Math.Min(x.Length, from + len); i++) { s += x[i] * (double)x[i]; n++; }
    return n == 0 ? 0 : Math.Sqrt(s / n);
}

Console.WriteLine("=== Streaming / realtime behaviour ===\n");

var src = Sine(440, 10.0, Rate);

// ---------- A) block-by-block at a fixed ratio ----------
void Streamed(string label, int outBlock, double ratio, bool split)
{
    using var st = new Stretch();
    st.PresetDefault(1, Rate, split);
    var outBuf = new List<float>();
    var ob = new float[outBlock];
    int inPos = 0;
    var sw = Stopwatch.StartNew();
    long blocks = 0;
    while (inPos + (int)(outBlock / ratio) < src.Length)
    {
        int inBlock = (int)(outBlock / ratio);
        var ib = new float[inBlock];
        Array.Copy(src, inPos, ib, 0, inBlock);
        st.Process(ib, inBlock, ob, outBlock);
        outBuf.AddRange(ob);
        inPos += inBlock; blocks++;
    }
    sw.Stop();
    var y = outBuf.ToArray();
    var lat = st.OutputLatency();
    var (jump, at) = WorstDiscontinuity(y, lat + outBlock * 2);
    // reference: one-shot through the same engine
    double perBlockUs = sw.Elapsed.TotalMilliseconds * 1000 / blocks;
    double budgetUs = outBlock * 1_000_000.0 / Rate;
    Console.WriteLine($"{label}");
    Console.WriteLine($"   produced {y.Length / (double)Rate:F2}s in {blocks} blocks of {outBlock}");
    Console.WriteLine($"   worst sample-to-sample jump = {jump:F4}   (a click would be >0.15 here; clean sine steps ~{2 * Math.PI * 440 * 0.5 / Rate:F4})");
    Console.WriteLine($"   cpu {perBlockUs:F0} us/block vs {budgetUs:F0} us budget  =>  {budgetUs / perBlockUs:F0}x realtime headroom");
    Console.WriteLine($"   verdict: {(jump < 0.15 ? "CONTINUOUS - no clicks" : "*** DISCONTINUITY ***")}\n");
}

Streamed("[A1] 1024-sample blocks, ratio 1.15, splitComputation=false", 1024, 1.15, false);
Streamed("[A2]  512-sample blocks, ratio 1.15, splitComputation=true ", 512, 1.15, true);
Streamed("[A3]  256-sample blocks, ratio 0.90, splitComputation=true ", 256, 0.90, true);

// ---------- B) turning the knobs mid-stream ----------
Console.WriteLine("[B] Knob sweeps DURING playback (tempo 1.0->1.25, pitch 0->+4 semis)");
{
    using var st = new Stretch();
    st.PresetDefault(1, Rate, true);
    const int OutBlock = 512;
    var ob = new float[OutBlock];
    var outBuf = new List<float>();
    int inPos = 0, n = 0;
    double ratio = 1.0; float semis = 0;
    var sw = Stopwatch.StartNew();
    while (inPos + 2048 < src.Length)
    {
        // sweep both knobs continuously, like a hand on a control
        ratio = 1.0 + 0.25 * (n / 600.0); if (ratio > 1.25) ratio = 1.25;
        semis = (float)Math.Min(4.0, 4.0 * (n / 600.0));
        st.SetTransposeSemitones(semis, 0.5f);
        int inBlock = Math.Max(1, (int)(OutBlock / ratio));
        var ib = new float[inBlock];
        Array.Copy(src, inPos, ib, 0, inBlock);
        st.Process(ib, inBlock, ob, OutBlock);
        outBuf.AddRange(ob);
        inPos += inBlock; n++;
    }
    sw.Stop();
    var y = outBuf.ToArray();
    var (jump, at) = WorstDiscontinuity(y, st.OutputLatency() + 4096);
    Console.WriteLine($"   {n} blocks, {y.Length / (double)Rate:F2}s out");
    Console.WriteLine($"   worst jump = {jump:F4} at {at / (double)Rate:F2}s");
    Console.WriteLine($"   level stayed sane: rms start {Rms(y, 20000, 20000):F3}, mid {Rms(y, y.Length / 2, 20000):F3}, end {Rms(y, y.Length - 25000, 20000):F3}");
    Console.WriteLine($"   cpu {sw.Elapsed.TotalMilliseconds * 1000 / n:F0} us/block vs {OutBlock * 1e6 / Rate:F0} us budget");
    Console.WriteLine($"   verdict: {(jump < 0.15 ? "SMOOTH - knobs are safe to turn live" : "*** ZIPPER/CLICK ***")}\n");
}

// ---------- C) how many tracks can we run at once? ----------
Console.WriteLine("[C] Simultaneous stretcher instances (stereo tracks, 512-frame blocks)");
{
    for (int tracks = 2; tracks <= 16; tracks *= 2)
    {
        var sts = new Stretch[tracks * 2];      // one per channel
        for (int i = 0; i < sts.Length; i++) { sts[i] = new Stretch(); sts[i].PresetDefault(1, Rate, true); sts[i].SetTransposeSemitones(2f, 0.5f); }
        const int OutBlock = 512; var ob = new float[OutBlock]; var ib = new float[445];
        Array.Copy(src, 0, ib, 0, ib.Length);
        var sw = Stopwatch.StartNew(); int iter = 400;
        for (int k = 0; k < iter; k++) foreach (var s in sts) s.Process(ib, ib.Length, ob, OutBlock);
        sw.Stop();
        foreach (var s in sts) s.Dispose();
        double us = sw.Elapsed.TotalMilliseconds * 1000 / iter, budget = OutBlock * 1e6 / Rate;
        Console.WriteLine($"   {tracks,2} stereo tracks: {us,7:F0} us/block vs {budget:F0} us budget  =>  {budget / us,5:F1}x headroom  {(us < budget * 0.5 ? "OK" : us < budget ? "TIGHT" : "TOO SLOW")}");
    }
}
Console.WriteLine("\n=== done ===");
