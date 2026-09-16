namespace Benchmix.Analysis;

/// <summary>
/// Answers the question a mashup actually asks — "how many semitones do I move track B so it
/// sits with track A?" — without ever naming a key.
///
/// Naming keys inherits key detection's error rate for no benefit: two tracks in relative
/// major/minor (Am and C) share a pitch-class set and need <b>no</b> shift at all, which a
/// name-then-subtract approach gets wrong unless it special-cases modes. Cross-correlating the
/// two chroma vectors over the 12 rotations measures the thing we care about directly.
/// </summary>
public static class PitchMatch
{
    public readonly record struct Candidate(int Semitones, double Score);

    /// <summary>Ranks all 12 shifts of B against A, best first. Semitones folded to [-6, 5].</summary>
    public static Candidate[] Rank(double[] chromaA, double[] chromaB)
    {
        var res = new Candidate[12];
        for (int s = 0; s < 12; s++)
        {
            // rotate B up by s semitones, then correlate against A
            var rot = new double[12];
            for (int i = 0; i < 12; i++) rot[(i + s) % 12] = chromaB[i];
            int folded = s > 6 ? s - 12 : s;
            res[s] = new Candidate(folded, Pearson(chromaA, rot));
        }
        Array.Sort(res, (x, y) => y.Score.CompareTo(x.Score));
        return res;
    }

    /// <summary>
    /// Ground truth from two Camelot wheel positions. Moving one step around the wheel is a
    /// perfect fifth, so n steps is 7n semitones; relative major/minor share a number and need
    /// no shift at all.
    /// </summary>
    public static int TrueShift(string camelotA, string camelotB)
    {
        int na = int.Parse(camelotA[..^1]);
        int nb = int.Parse(camelotB[..^1]);
        int semis = ((nb - na) * 7) % 12;
        if (semis < 0) semis += 12;
        return semis > 6 ? semis - 12 : semis;
    }

    static double Pearson(double[] a, double[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sa = 0, sb = 0, sab = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sa += da * da; sb += db * db; sab += da * db;
        }
        return (sa <= 0 || sb <= 0) ? 0 : sab / Math.Sqrt(sa * sb);
    }
}
