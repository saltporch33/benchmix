namespace Benchmix.Engine.Analysis;

/// <summary>
/// Musical keys, the Camelot wheel, and the harmonic-mixing rules a mashup actually uses.
///
/// Camelot notation exists because the useful question is never "what key is this" but "will
/// these two sit together". Keys sharing a number (8A / 8B) are relative minor/major and share a
/// pitch-class set; neighbours on the wheel differ by one accidental. A step around the wheel is
/// a perfect fifth, which is why <see cref="SemitonesBetween"/> multiplies by seven.
/// </summary>
public static class Keys
{
    public static readonly string[] Names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B" };

    static readonly int[] MajorWheel = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    static readonly int[] MinorWheel = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };

    static readonly Dictionary<string, string> Enharmonic = new(StringComparer.Ordinal)
    {
        ["Db"] = "C#", ["D#"] = "Eb", ["Gb"] = "F#", ["Ab"] = "G#", ["A#"] = "Bb", ["Cb"] = "B", ["Fb"] = "E", ["E#"] = "F", ["B#"] = "C",
    };

    /// <summary>Normalises "F#m", "Ebm", "1A", "8B", "F# minor" to one spelling such as "F#m".</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();

        var cam = System.Text.RegularExpressions.Regex.Match(tag, "^([0-9]{1,2})([ABab])$");
        if (cam.Success)
        {
            int n = int.Parse(cam.Groups[1].Value);
            if (n is < 1 or > 12) return null;
            bool minor = char.ToUpperInvariant(cam.Groups[2].Value[0]) == 'A';
            var table = minor ? MinorWheel : MajorWheel;
            for (int t = 0; t < 12; t++) if (table[t] == n) return Names[t] + (minor ? "m" : "");
            return null;
        }

        var m = System.Text.RegularExpressions.Regex.Match(tag, @"^([A-Ga-g][#b]?)\s*(m|min|minor)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string root = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..];
        if (Enharmonic.TryGetValue(root, out var norm)) root = norm;
        if (Array.IndexOf(Names, root) < 0) return null;
        return root + (m.Groups[2].Success ? "m" : "");
    }

    /// <summary>Camelot code such as "8A" for a normalised key name, or null.</summary>
    public static string? ToCamelot(string? normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return null;
        bool minor = normalizedKey.EndsWith("m", StringComparison.Ordinal);
        string root = minor ? normalizedKey[..^1] : normalizedKey;
        int t = Array.IndexOf(Names, root);
        if (t < 0) return null;
        return (minor ? MinorWheel[t] : MajorWheel[t]).ToString() + (minor ? "A" : "B");
    }

    /// <summary>
    /// Semitones to shift <paramref name="fromKey"/> so it shares a pitch-class set with
    /// <paramref name="toKey"/>, folded to [-6, 5]. Relative major/minor return 0, which is
    /// correct — they already agree.
    /// </summary>
    public static int? SemitonesBetween(string? fromKey, string? toKey)
    {
        var a = ToCamelot(Normalize(fromKey));
        var b = ToCamelot(Normalize(toKey));
        if (a is null || b is null) return null;

        int na = int.Parse(a[..^1]), nb = int.Parse(b[..^1]);
        int semis = ((nb - na) * 7) % 12;
        if (semis < 0) semis += 12;
        return semis > 6 ? semis - 12 : semis;
    }

    /// <summary>True when the two keys already mix without any shift at all.</summary>
    public static bool AreCompatible(string? a, string? b)
    {
        var ca = ToCamelot(Normalize(a));
        var cb = ToCamelot(Normalize(b));
        if (ca is null || cb is null) return false;
        if (ca == cb) return true;

        int na = int.Parse(ca[..^1]), nb = int.Parse(cb[..^1]);
        if (na == nb) return true;                                   // relative major/minor
        int d = Math.Abs(na - nb);
        return ca[^1] == cb[^1] && (d == 1 || d == 11);              // one step around the wheel
    }

    /// <summary>A short human explanation of the relationship, for the UI to show.</summary>
    public static string Describe(string? a, string? b)
    {
        var ca = ToCamelot(Normalize(a));
        var cb = ToCamelot(Normalize(b));
        if (ca is null || cb is null) return "key unknown";
        if (ca == cb) return "same key";

        int na = int.Parse(ca[..^1]), nb = int.Parse(cb[..^1]);
        if (na == nb) return "relative major/minor — already compatible";
        int d = Math.Abs(na - nb);
        if (ca[^1] == cb[^1] && (d == 1 || d == 11)) return "one step on the wheel — already compatible";

        var semis = SemitonesBetween(a, b);
        return semis is null ? "unrelated keys" : $"{semis:+0;-0} semitones apart";
    }
}
