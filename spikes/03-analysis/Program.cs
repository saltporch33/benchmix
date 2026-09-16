using System.Diagnostics;
using System.Text;
using Benchmix.Analysis;

const int AnalysisRate = 22050;
string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Music\Sports DJ\audio");

var files = Directory.GetFiles(dir, "*.mp3").OrderBy(f => f).ToArray();
Console.WriteLine("=== Benchmix key detection — bass-weighted chroma bake-off ===\n");

var weights = new[] { 0.0, 0.25, 0.40, 0.55, 0.70, 1.00 };
var profiles = KeyV2.All;
var exact = new int[weights.Length, profiles.Length];
var neigh = new int[weights.Length, profiles.Length];
int total = 0;

var sw = Stopwatch.StartNew();
foreach (var f in files)
{
    var tags = Id3.Read(f);
    tags.TryGetValue("TKEY", out var tagKey);
    var gt = KeyDetector.NormalizeTag(tagKey);
    if (gt == null) continue;

    float[] mono;
    try { mono = Decode.ToMono(f, AnalysisRate, 180); } catch { continue; }
    if (mono.Length < AnalysisRate * 10) continue;
    total++;

    double tuning = KeyV2.EstimateTuning(mono, AnalysisRate);
    var full = KeyDetector.Chroma(mono, AnalysisRate);                       // the naive chroma that won earlier
    var bass = BassChroma.Compute(mono, AnalysisRate, tuning, 55, 260);      // bass register only

    for (int w = 0; w < weights.Length; w++)
    {
        var blend = BassChroma.Blend(full, bass, weights[w]);
        for (int p = 0; p < profiles.Length; p++)
        {
            var r = KeyV2.FromChroma(blend, profiles[p]);
            if (r.Name == gt) exact[w, p]++;
            else if (Neighbour(gt, r.Name)) neigh[w, p]++;
        }
    }
}
sw.Stop();

Console.WriteLine($"{total} tracks with a Serato key tag, {sw.Elapsed.TotalSeconds:F0}s\n");
Console.Write($"{"bass wt",-9}");
foreach (var p in profiles) Console.Write($"{Short(p.Name),-13}");
Console.WriteLine("\n" + new string('-', 9 + 13 * profiles.Length));

int bw = 0, bp = 0, bv = -1;
for (int w = 0; w < weights.Length; w++)
{
    Console.Write($"{weights[w],-9:F2}");
    for (int p = 0; p < profiles.Length; p++)
    {
        Console.Write($"{Pct(exact[w, p], total),-13}");
        if (exact[w, p] > bv) { bv = exact[w, p]; bw = w; bp = p; }
    }
    Console.WriteLine();
}

Console.WriteLine($"\nBEST: {profiles[bp].Name} @ bass weight {weights[bw]:F2}  =  {Pct(bv, total)} exact, "
                + $"{Pct(bv + neigh[bw, bp], total)} within a harmonic neighbour");
Console.WriteLine($"(naive full-spectrum baseline from the previous run was 47% exact)");

static string Pct(int n, int d) => d == 0 ? "-" : $"{100.0 * n / d:F0}%";
static string Short(string s) => s.Length <= 12 ? s : s[..12];

static bool Neighbour(string a, string b)
{
    if (a == b) return true;
    var ca = KeyDetector.CamelotOf(a); var cb = KeyDetector.CamelotOf(b);
    if (ca == "?" || cb == "?") return false;
    int na = int.Parse(ca[..^1]), nb = int.Parse(cb[..^1]);
    if (na == nb) return true;
    if (ca[^1] == cb[^1] && (Math.Abs(na - nb) == 1 || Math.Abs(na - nb) == 11)) return true;
    return false;
}

static class Id3
{
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[10];
            if (fs.Read(head, 0, 10) != 10) return result;
            if (head[0] != 'I' || head[1] != 'D' || head[2] != '3') return result;
            int size = 0;
            for (int i = 6; i < 10; i++) size = (size << 7) | (head[i] & 0x7F);
            var data = new byte[size];
            if (fs.Read(data, 0, size) != size) return result;
            int p = 0;
            while (p + 10 <= data.Length)
            {
                var id = Encoding.ASCII.GetString(data, p, 4);
                if (id[0] == '\0') break;
                int fsz = (data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7];
                if (fsz <= 0 || p + 10 + fsz > data.Length) break;
                if (id[0] == 'T')
                {
                    byte enc = data[p + 10];
                    var body = data.AsSpan(p + 11, fsz - 1);
                    string text = enc switch
                    {
                        1 => Encoding.Unicode.GetString(body),
                        2 => Encoding.BigEndianUnicode.GetString(body),
                        3 => Encoding.UTF8.GetString(body),
                        _ => Encoding.Latin1.GetString(body),
                    };
                    text = text.TrimEnd('\0').Trim();
                    if (text.Length > 0 && !result.ContainsKey(id)) result[id] = text;
                }
                p += 10 + fsz;
            }
        }
        catch { }
        return result;
    }
}
