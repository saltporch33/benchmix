using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// A hand-built loop of one held note: a slice of a steady tone repeated by Ctrl+D. Every repeat is a
/// butt join from the end of the slice back to its own start, and on a held note the two sides of it
/// are nearly the same signal, at whatever phase the slice's length leaves them — which is where the
/// crossfade's law shows. JoinTests cut half a cycle out and look for a step; this looks at the level.
///
/// <para>
/// MEASUREMENT ONLY (2026-10-21). An audible attack at every repeat of a held note was reported, and
/// the fix waits on a decision about the join rule, so these report what the joins do and assert only
/// what the measurement rests on. The assertion about the level goes in with the fix.
/// </para>
/// <para>
/// The note is 431 Hz — never 440 near the vocoder — with a slight vibrato, ±10 cents five times a
/// second, so the two sides of a join are not even at the same frequency. The slice is one beat at
/// 98 BPM, 29388 frames: 263.88 cycles of the note, not a whole number of them.
/// </para>
/// </summary>
public class SustainedLoopTests : IDisposable
{
    const int Rate = 48000;
    const double Hz = 431, Amplitude = 0.5;
    const double VibratoHz = 5, VibratoCents = 10;
    const int SongFrames = Rate * 20;
    const long SliceStart = 4 * Rate;
    const long Beat = 29388;                // 60/98 s
    const int Repeats = 4;
    const double Stretched = 0.94;          // the kind of speed Match BPM asks for
    const int Today = 240;                  // TrackVoice.DeclickSeconds at 48 kHz: the crossfade at a butt join

    // How far the vibrato swings the phase, in radians, for ±VibratoCents at VibratoHz.
    static readonly double Beta = Hz * (Math.Pow(2, VibratoCents / 1200) - 1) / VibratoHz;
    static double PhaseAt(long frame) => 2 * Math.PI * Hz * frame / Rate + Beta * Math.Sin(2 * Math.PI * VibratoHz * frame / Rate);
    static readonly double Period = Rate / Hz;

    private readonly string _dir;
    private readonly string _wav;
    private readonly ITestOutputHelper _out;

    public SustainedLoopTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-sustained", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "held-note.wav");
        var s = new float[SongFrames * 2];
        for (int f = 0; f < SongFrames; f++) s[f * 2] = s[f * 2 + 1] = (float)(Amplitude * Math.Sin(PhaseAt(f)));
        AudioClip.FromSamples("held note", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static int Ms(double ms) => (int)Math.Round(ms * Rate / 1000);

    /// <summary>
    /// The note on a track, the slice split out of it at the playhead twice, and Ctrl+D held down on
    /// it: each press repeats the copy the last one made. Returns the track frame of every join — the
    /// repeats, then the one back into the song.
    /// </summary>
    async Task<(ProjectSession Session, TrackModel Track, long[] Joins)> LoopAsync(long length)
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 0);
        Assert.True(TimelineEditor.Split(t, SliceStart / (double)Rate, s).Changed);
        Assert.True(TimelineEditor.Split(t, (SliceStart + length) / (double)Rate, s).Changed);
        var slice = t.Clips.Single(c => TimelineEditor.StartFrameOf(c, s) == SliceStart);
        Assert.Equal(length, TimelineEditor.LengthFramesOf(slice, s));

        var joins = new long[Repeats + 1];
        for (int k = 0; k < Repeats; k++)
        {
            var r = TimelineEditor.Duplicate(t, slice, s);
            Assert.True(r.Changed, r.Message);
            slice = r.Clip!;
            joins[k] = TimelineEditor.StartFrameOf(slice, s);
        }
        joins[Repeats] = TimelineEditor.EndFrameOf(slice, s);

        s.ApplyAll();
        var timeline = s.VoiceFor(t)!.Timeline;
        int butt = Enumerable.Range(0, timeline.Count - 1).Count(timeline.ButtJoinAfter);
        Assert.True(butt == Repeats + 1, $"expected {Repeats + 1} butt joins — every repeat and the way back into the song — and found {butt}");
        return (s, t, joins);
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

    // ---------------------------------------------------------------- measuring

    /// <summary>
    /// The loudest the wave gets within a quarter cycle either side of a frame. On a steady tone that is
    /// its level, read straight off the waveform: a window half a cycle wide always holds one peak.
    /// </summary>
    static double LevelAt(Func<long, double> x, long at)
    {
        double m = 0;
        for (long f = at - 28; f <= at + 28; f++) m = Math.Max(m, Math.Abs(x(f)));
        return m;
    }

    static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-5));

    static double MeanLevel(Func<long, double> x, long from, long to)
    {
        double sum = 0;
        for (long f = from; f < to; f++) sum += LevelAt(x, f);
        return sum / (to - from);
    }

    static double WorstStep(Func<long, double> x, long from, long to)
    {
        double worst = 0;
        for (long f = from; f < to; f++) worst = Math.Max(worst, Math.Abs(x(f + 1) - x(f)));
        return worst;
    }

    /// <summary>
    /// What one join does to the note: the highest and lowest level from 5 ms before it to 10 ms after
    /// its crossfade, how long the level is more than 1 dB off in that time, the highest and lowest
    /// from then to 300 ms (a vocoder settling after its re-prime would show there), and the largest
    /// step between neighbouring samples against the largest in the steady note.
    /// </summary>
    sealed record Reading(double High, double Low, double OffMs, double LaterHigh, double LaterLow, double Step)
    {
        public override string ToString() =>
            $"level {High,6:+0.00;-0.00;0.00} / {Low,6:+0.00;-0.00;0.00} dB, {OffMs,4:0.0} ms more than 1 dB off; "
          + $"after that to 300 ms {LaterHigh:+0.00;-0.00;0.00} / {LaterLow:+0.00;-0.00;0.00} dB; worst step {Step:0.00}x steady";
    }

    static Reading Read(Func<long, double> x, long join, int fade, double level, double step)
    {
        long from = join - Ms(5), to = join + fade + Ms(10);
        double high = 0, low = double.MaxValue;
        int off = 0;
        for (long f = from; f < to; f++)
        {
            double l = LevelAt(x, f);
            high = Math.Max(high, l);
            low = Math.Min(low, l);
            if (Math.Abs(Db(l / level)) > 1) off++;
        }
        double laterHigh = 0, laterLow = double.MaxValue;
        for (long f = to; f < join + Ms(300); f++)
        {
            double l = LevelAt(x, f);
            laterHigh = Math.Max(laterHigh, l);
            laterLow = Math.Min(laterLow, l);
        }
        return new Reading(Db(high / level), Db(low / level), off * 1000.0 / Rate,
                           Db(laterHigh / level), Db(laterLow / level),
                           WorstStep(x, join - Ms(10), join + fade + Ms(10)) / step);
    }

    /// <summary>How far the incoming side's phase is from where the outgoing side would have carried on, −180° to 180°.</summary>
    static double GapDegrees(long outgoingEnd, long incomingStart)
    {
        double d = (PhaseAt(incomingStart) - PhaseAt(outgoingEnd)) * 180 / Math.PI;
        d %= 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    // ---------------------------------------------------------------- the measurements

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    // The EQ is reset and re-primed at every repeat too; with a knob moved its filters are in the path.
    [InlineData(false, 0.25)]
    public async Task MeasureTheLevelAtEveryRepeatOfAHeldNote(bool stretched, double eqLow)
    {
        var (s, t, joins) = await LoopAsync(Beat);
        using (s)
        {
            if (stretched) t.Speed = Stretched;
            t.EqLow = eqLow;
            var map = s.EngineTempoOf(t);
            long[] at = joins.Select(j => map.FirstRelAt(j)).ToArray();
            int frames = (int)(at[^1] + Rate);
            var buf = Render(s, frames);
            double X(long f) => f >= 0 && f < frames ? buf[f * 2] : 0;

            // The yardstick is the same render, in the untouched song before the first join.
            long from = map.FirstRelAt(Rate), to = at[0] - Ms(200);
            double level = MeanLevel(X, from, to);
            double step = WorstStep(X, from, to);
            var steady = Read(X, (from + to) / 2, 0, level, step);

            _out.WriteLine($"{(stretched ? $"Stretched to {Stretched}" : "Untouched")}{(eqLow != 1 ? $", LOW knob at {eqLow}" : "")}: one beat at 98 BPM ({Beat} frames, "
                         + $"{Beat * Hz / Rate:0.00} cycles of {Hz} Hz with ±{VibratoCents} cents of vibrato), {Repeats} repeats by Ctrl+D");
            _out.WriteLine($"  steady note: level {level:0.0000}, its own ripple {Math.Max(steady.High, steady.LaterHigh):+0.00;-0.00;0.00} / "
                         + $"{Math.Min(steady.Low, steady.LaterLow):+0.00;-0.00;0.00} dB over 300 ms, worst step {step:0.0000}");
            for (int k = 0; k < at.Length; k++)
            {
                long end = SliceStart + Beat;
                long next = k < Repeats ? SliceStart : end + Repeats * Beat;
                string what = k < Repeats ? $"repeat {k + 1}" : "back into the song";
                string gap = stretched ? "" : $", phase gap {GapDegrees(end, next),4:0}°";
                _out.WriteLine($"  join at {at[k] / (double)Rate:0.000} s ({what}{gap}): {Read(X, at[k], Today, level, step)}");
            }

            if (!stretched && eqLow == 1)
                Assert.True(Math.Abs(Db(level / Amplitude)) < 0.05, $"the untouched note came out at {level:0.0000}, not {Amplitude} — the measurement is off");
        }
    }

    // ---------------------------------------------------------------- what other crossfades would do

    static (double Out, double In) EqualPower(double t, double rho) => (Math.Cos(Math.PI / 2 * t), Math.Sin(Math.PI / 2 * t));

    static (double Out, double In) EqualGain(double t, double rho)
    {
        double b = 0.5 - 0.5 * Math.Cos(Math.PI * t);
        return (1 - b, b);
    }

    /// <summary>
    /// Power-complementary for two signals correlated by <paramref name="rho"/>: a² + b² + 2ρab = 1, so
    /// equal gain when the two sides are the same signal in phase and equal power when they are
    /// unrelated. Below zero it is held at equal power rather than boosting both sides to make up for
    /// cancelling.
    /// </summary>
    static (double Out, double In) Matched(double t, double rho)
    {
        double s = 0.5 - 0.5 * Math.Cos(Math.PI * t), r = Math.Clamp(rho, 0, 1);
        double g = 1 / Math.Sqrt((1 - s) * (1 - s) + s * s + 2 * r * s * (1 - s));
        return (g * (1 - s), g * s);
    }

    static double Correlation(Func<long, double> a, Func<long, double> b, int n)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int k = 0; k < n; k++) { ab += a(k) * b(k); aa += a(k) * a(k); bb += b(k) * b(k); }
        return ab / Math.Sqrt(aa * bb);
    }

    /// <summary>
    /// A repeat's join as the untouched path would play it with another crossfade, from the song
    /// itself: the outgoing side is the song carrying on past the slice's end — its handle — and the
    /// incoming side the song from the slice's start, <paramref name="shift"/> frames later. Frames are
    /// counted from the join; before it is the slice's own end. The arithmetic is the engine's, in its
    /// order, so with today's law this is the engine's output to the bit.
    /// </summary>
    static Func<long, double> Joined(float[] song, long end, long start, int fade,
                                     Func<double, double, (double Out, double In)> law, double rho, long shift = 0)
    {
        double Src(long f) => f >= 0 && f * 2 < song.Length ? song[f * 2] : 0;
        return k =>
        {
            if (k < 0) return Src(end + k);
            if (k >= fade) return Src(start + shift + k);
            var (a, b) = law((k + 0.5) / fade, rho);
            return (float)(Src(start + shift + k) * b + Src(end + k) * a);
        };
    }

    sealed record Option(string Name, int Fade, Func<double, double, (double Out, double In)> Law, bool Matching = false, bool Nudged = false);

    [Fact]
    public async Task MeasureEachCrossfadeAtEveryPhaseAHeldNoteCanMeetItself()
    {
        var options = new[]
        {
            new Option("5 ms, equal power (today)", Today, EqualPower),
            new Option("5 ms, equal gain", Today, EqualGain),
            new Option("40 ms, equal power", Ms(40), EqualPower),
            new Option("40 ms, equal gain", Ms(40), EqualGain),
            new Option("80 ms, equal power", Ms(80), EqualPower),
            new Option("80 ms, equal gain", Ms(80), EqualGain),
            new Option("5 ms, matched to how alike the sides are", Today, Matched, Matching: true),
            new Option("40 ms, matched to how alike the sides are", Ms(40), Matched, Matching: true),
            new Option("5 ms, incoming nudged into phase, equal gain", Today, EqualGain, Nudged: true),
            new Option("40 ms, incoming nudged into phase, equal gain", Ms(40), EqualGain, Nudged: true),
        };
        var worst = options.ToDictionary(o => o, _ => (High: double.MinValue, Low: double.MaxValue, Off: 0.0, Step: 0.0, Nudge: 0L));
        var stretchedReadings = new List<string>();

        _out.WriteLine($"Through the engine, today's crossfade, at the second repeat's join, for eight slice lengths an eighth of a cycle apart:");
        for (int eighth = 0; eighth < 8; eighth++)
        {
            long length = Beat + (long)Math.Round(eighth * Period / 8);
            var (s, t, joins) = await LoopAsync(length);
            using (s)
            {
                var song = s.AudioFor(t.Clips[0])!.Samples;
                long end = SliceStart + length;
                double Song(long f) => f >= 0 && f * 2 < song.Length ? song[f * 2] : 0;
                double songLevel = MeanLevel(Song, Rate, 3 * Rate), songStep = WorstStep(Song, Rate, 3 * Rate);

                // Untouched: the engine, and the same join worked out from the song with every option.
                int frames = (int)(joins[^1] + Rate);
                var buf = Render(s, frames);
                double X(long f) => f >= 0 && f < frames ? buf[f * 2] : 0;
                long join = joins[1];
                var engine = Read(X, join, Today, songLevel, songStep);

                var today = Joined(song, end, SliceStart, Today, EqualPower, 0);
                int differing = 0;
                for (long k = -Ms(10); k < Ms(30); k++) if ((float)today(k) != buf[(join + k) * 2]) differing++;
                Assert.True(differing == 0,
                    $"{differing} samples of the worked-out join differ from the engine's — the what-if numbers would not be the engine's arithmetic");

                double gap = GapDegrees(end, SliceStart);
                _out.WriteLine($"  +{eighth}/8 cycle ({length} frames), phase gap {gap,4:0}°: untouched {engine}");

                foreach (var o in options)
                {
                    Func<long, double> handle = k => Song(end + k);
                    long shift = 0;
                    if (o.Nudged)
                    {
                        // The nearest shift, within half a cycle, at which the incoming side best matches the handle.
                        double best = double.MinValue;
                        for (long d = -(long)(Period / 2); d <= (long)(Period / 2); d++)
                        {
                            double c = Correlation(handle, k => Song(SliceStart + d + k), o.Fade);
                            if (c > best) { best = c; shift = d; }
                        }
                    }
                    double rho = o.Matching ? Correlation(handle, k => Song(SliceStart + k), o.Fade) : 0;
                    var r = Read(Joined(song, end, SliceStart, o.Fade, o.Law, rho, shift), 0, o.Fade, songLevel, songStep);
                    var w = worst[o];
                    worst[o] = (Math.Max(w.High, r.High), Math.Min(w.Low, r.Low), Math.Max(w.Off, r.OffMs), Math.Max(w.Step, r.Step),
                                Math.Max(w.Nudge, Math.Abs(shift)));
                }

                // Stretched: the engine only — the vocoder's own phase decides the gap there.
                t.Speed = Stretched;
                var map = s.EngineTempoOf(t);
                long[] at = joins.Select(j => map.FirstRelAt(j)).ToArray();
                frames = (int)(at[^1] + Rate);
                buf = Render(s, frames);
                long from = map.FirstRelAt(Rate), to = at[0] - Ms(200);
                double level = MeanLevel(X, from, to), step = WorstStep(X, from, to);
                stretchedReadings.Add($"  +{eighth}/8 cycle: stretched {Read(X, at[1], Today, level, step)}");
            }
        }

        _out.WriteLine("");
        _out.WriteLine("Stretched to 0.94 through the engine, today's crossfade, the same joins:");
        foreach (var line in stretchedReadings) _out.WriteLine(line);

        _out.WriteLine("");
        _out.WriteLine("Each crossfade on the untouched path, worked out from the song, worst over the eight phases:");
        _out.WriteLine($"  {"",-48} highest   lowest   >1 dB off   worst step   nudge");
        foreach (var o in options)
        {
            var w = worst[o];
            string nudge = o.Nudged ? $"{w.Nudge * 1000.0 / Rate:0.00} ms" : "";
            _out.WriteLine($"  {o.Name,-48} {w.High,6:+0.00;-0.00;0.00}  {w.Low,7:+0.00;-0.00;0.00}  {w.Off,7:0.0} ms   {w.Step,7:0.00}x   {nudge}");
        }
    }
}
