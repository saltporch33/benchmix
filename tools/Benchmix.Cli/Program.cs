using System.Diagnostics;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;

// Headless front end for the Benchmix engine. Useful on its own, and it is how the engine gets
// exercised on real music without the UI in the way.

if (args.Length == 0) { Usage(); return 1; }

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "analyze": return Analyze(args.Skip(1).ToArray());
        case "mashup": return Mashup(args.Skip(1).ToArray());
        default: Usage(); return 1;
    }
}
catch (ClipLoadException ex)
{
    Console.Error.WriteLine($"Could not load {Path.GetFileName(ex.SourcePath)}: {ex.Message} ({ex.Failure})");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 3;
}

static void Usage()
{
    Console.WriteLine("""
    benchmix — offline mashup engine

      benchmix analyze <file> [file...]
          Print tempo and key for each file, saying whether each came from the
          file's own tags or was detected from the audio.

      benchmix mashup <bed> <topper> <out.wav> [options]
          Lay <topper> over <bed>, tempo-matched to the bed, and render to a file.

          --seconds N        length to render (default 45)
          --bed-start S      seconds into the bed to start from (default 0)
          --top-start S      seconds into the topper to start from (default 0)
          --semitones N      pitch shift for the topper (default: from the keys)
          --linked           move the topper's pitch with its tempo (no phase vocoder)
          --no-eq            skip the mashup EQ split
          --no-tempo-match   leave the topper at its own tempo (for A/B comparison)
          --format 16|24|f32 output format (default 24)
    """);
}

static int Analyze(string[] files)
{
    if (files.Length == 0) { Console.Error.WriteLine("analyze: give me at least one file"); return 1; }

    Console.WriteLine($"{"title",-42} {"bpm",-16} {"key",-16} {"tuning"}");
    Console.WriteLine(new string('-', 92));

    foreach (var f in files)
    {
        var sw = Stopwatch.StartNew();
        var a = Analyzer.Analyze(f);
        sw.Stop();

        string name = a.Title ?? Path.GetFileNameWithoutExtension(f);
        string bpm = a.BpmSource == ValueSource.None ? "-" : $"{a.Bpm:F1} ({Tag(a.BpmSource)})";
        string key = a.Key is null ? "-" : $"{a.Key}/{a.Camelot} ({Tag(a.KeySource)})";
        string tune = Math.Abs(a.TuningCents) < 0.05 ? "-" : $"{a.TuningCents:+0;-0} cents";

        Console.WriteLine($"{Trim(name, 42),-42} {bpm,-16} {key,-16} {tune}   [{sw.Elapsed.TotalSeconds:F1}s]");
    }
    return 0;

    static string Tag(ValueSource s) => s switch
    {
        ValueSource.Tag => "tag",
        ValueSource.Detected => "est",
        ValueSource.Manual => "set",
        _ => "?",
    };
}

static int Mashup(string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("mashup: need <bed> <topper> <out.wav>"); return 1; }

    string bedPath = args[0], topPath = args[1], outPath = args[2];
    double seconds = 45, bedStart = 0, topStart = 0;
    double? semitones = null;
    bool linked = false, useEq = true, tempoMatch = true;
    var format = RenderFormat.Pcm24;

    for (int i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--seconds": seconds = double.Parse(args[++i]); break;
            case "--bed-start": bedStart = double.Parse(args[++i]); break;
            case "--top-start": topStart = double.Parse(args[++i]); break;
            case "--semitones": semitones = double.Parse(args[++i]); break;
            case "--linked": linked = true; break;
            case "--no-eq": useEq = false; break;
            case "--no-tempo-match": tempoMatch = false; break;
            case "--format":
                format = args[++i] switch { "16" => RenderFormat.Pcm16, "f32" => RenderFormat.Float32, _ => RenderFormat.Pcm24 };
                break;
            default: Console.Error.WriteLine($"unknown option {args[i]}"); return 1;
        }
    }

    const int Rate = 48000;

    Console.WriteLine("Analysing...");
    var bedInfo = Analyzer.Analyze(bedPath);
    var topInfo = Analyzer.Analyze(topPath);

    Console.WriteLine($"  bed    {Trim(bedInfo.Title ?? Path.GetFileName(bedPath), 40),-40} {bedInfo.Bpm,6:F1} BPM  {bedInfo.Key ?? "?",-4} {bedInfo.Camelot ?? "",-3}");
    Console.WriteLine($"  topper {Trim(topInfo.Title ?? Path.GetFileName(topPath), 40),-40} {topInfo.Bpm,6:F1} BPM  {topInfo.Key ?? "?",-4} {topInfo.Camelot ?? "",-3}");

    double topBpm = Analyzer.FoldToNearestOctave(topInfo.Bpm, bedInfo.Bpm);
    if (Math.Abs(topBpm - topInfo.Bpm) > 0.01)
        Console.WriteLine($"  (folded the topper's {topInfo.Bpm:F1} to {topBpm:F1} to sit in the same octave as the bed)");

    double speed = tempoMatch ? Analyzer.SpeedToMatch(topBpm, bedInfo.Bpm) : 1.0;
    int shift = semitones.HasValue ? (int)Math.Round(semitones.Value)
                                   : Keys.SemitonesBetween(topInfo.Key, bedInfo.Key) ?? 0;

    Console.WriteLine();
    Console.WriteLine(tempoMatch
        ? $"Plan: stretch the topper by {(speed - 1) * 100:+0.0;-0.0}% ({topBpm:F1} -> {bedInfo.Bpm:F1} BPM)"
        : $"Plan: tempo matching OFF - topper stays at {topBpm:F1} BPM against the bed's {bedInfo.Bpm:F1}");
    Console.WriteLine($"      {Keys.Describe(topInfo.Key, bedInfo.Key)}  ->  shift {shift:+0;-0;0} semitones");
    if (!topInfo.KeyIsTrustworthy || !bedInfo.KeyIsTrustworthy)
        Console.WriteLine("      NOTE: at least one key is an estimate, not a tag — treat the shift as a suggestion.");
    Console.WriteLine($"      mode: {(linked ? "linked (resampling, pitch follows tempo)" : "independent (tempo and pitch separate)")}");
    Console.WriteLine();

    Console.WriteLine("Loading audio...");
    var bedClip = AudioClip.Load(bedPath, Rate);
    var topClip = AudioClip.Load(topPath, Rate);

    using var graph = new MixGraph(Rate);

    var bed = graph.AddTrack();
    bed.SetClip(bedClip);
    bed.SourceStartFrame = (long)(bedStart * Rate);
    bed.Gain = 0.85;

    var top = graph.AddTrack();
    top.SetClip(topClip);
    top.SourceStartFrame = (long)(topStart * Rate);
    top.Mode = linked ? StretchMode.Linked : StretchMode.Independent;
    top.Speed = speed;
    if (!linked) top.Semitones = shift;
    top.Gain = 0.85;

    if (useEq)
    {
        // The standard mashup move: let the bed own the low end and the topper own the top, so
        // two full mixes do not fight over the same bass.
        bed.Eq.SetGains(1.0, 0.75, 0.55);
        top.Eq.SetGains(0.0, 1.0, 1.1);
        Console.WriteLine("EQ: bed keeps the bass; topper's lows are killed so they do not collide.");
    }

    graph.MasterGain = 1.0;
    graph.LimiterEnabled = true;

    long end = (long)(seconds * Rate);
    Console.Write("Rendering ");
    var progress = new Progress<double>(p => { });
    var sw = Stopwatch.StartNew();
    var result = Renderer.Render(graph, outPath,
        new RenderOptions { Region = (0, end), Format = format, TailSeconds = 0 }, progress);
    sw.Stop();

    Console.WriteLine($"done in {result.ElapsedSeconds:F1}s ({result.Seconds / Math.Max(0.001, result.ElapsedSeconds):F0}x realtime)");
    Console.WriteLine($"  {result.Path}");
    Console.WriteLine($"  {result.Seconds:F1}s, peak before limiting {20 * Math.Log10(Math.Max(1e-6, result.PeakBefore)):F1} dBFS"
                    + (result.Clipped ? ", limiter engaged" : ""));
    return 0;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
