using System.Globalization;

namespace Benchmix.Engine.Analysis;

/// <summary>What a tag write did: which values went in, and what the file's tags say now.</summary>
public sealed record TagWriteResult(bool WroteBpm, bool WroteKey, string? BpmInFile, string? KeyInFile);

/// <summary>
/// Writes Benchmix's tempo and key into a song's own tags (Daniel: *"Write
/// Benchmix's detected tempo and key into a song's own tags, so Windows Explorer (Beats-per-minute
/// and Initial key columns), Serato and djay show them. Only with your yes, per file."*; built
/// 2026-10-29). MP3 files only, for now — the one format his library is in, and the one the app's
/// own reader (<see cref="Id3"/>) reads back.
///
/// <para>
/// It fills only what the file lacks and never overwrites a value that is there: a tag written by
/// Serato or Rekordbox is as trustworthy as whatever wrote it, and the app already prefers it to its
/// own guess. The tempo is written as it is (<c>93</c>, or <c>127.5</c>), the key as the ID3 standard
/// has it (<c>Em</c>, <c>F#m</c>, <c>Bb</c>), in Latin-1, which every reader takes. The work is done
/// by TagLib# on a copy beside the file; the copy is checked — the values read back by Benchmix's own
/// reader, every tag the file had still there, the music byte for byte the same — and only then takes
/// the file's place in one step. Anything wrong and the file is as it was.
/// </para>
/// </summary>
public static class TagWriter
{
    static TagWriter()
    {
        // Digits, letters, '#' and 'b': Latin-1 holds them all, and it is what Explorer and the
        // app's own reader take without a second thought. Forced, so a 2.3 tag never gets UTF-16.
        TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.Latin1;
        TagLib.Id3v2.Tag.ForceDefaultEncoding = true;
    }

    public static bool CanWrite(string path)
        => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tempo as it goes into TBPM: <c>93</c>, or <c>127.5</c> when it is not whole.</summary>
    public static string BpmText(double bpm) => bpm.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a write would fill in: of <paramref name="bpm"/> and <paramref name="key"/>, the ones the
    /// file's tags lack. Both null means there is nothing to write.
    /// </summary>
    public static (double? Bpm, string? Key) Blanks(string path, double? bpm, string? key)
    {
        var tags = Id3.Read(path);
        bool hasBpm = tags.TryGetValue("TBPM", out var b) && !string.IsNullOrWhiteSpace(b);
        bool hasKey = tags.TryGetValue("TKEY", out var k) && !string.IsNullOrWhiteSpace(k);
        return (hasBpm || bpm is not > 0 ? null : bpm, hasKey || string.IsNullOrWhiteSpace(key) ? null : key);
    }

    /// <summary>
    /// Fills the blanks in the file's tags, as <see cref="Blanks"/> finds them. Throws, with a plain
    /// message, when the file is not an MP3, is not there, or the written copy did not check out —
    /// and then the file is as it was.
    /// </summary>
    public static TagWriteResult Write(string path, double? bpm, string? key)
    {
        if (!CanWrite(path))
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not an MP3; Benchmix writes tags into MP3 files only, for now.");
        if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} is not there.", path);

        var before = Id3.Read(path);
        var (fillBpm, fillKey) = Blanks(path, bpm, key);
        if (fillBpm is null && fillKey is null)
            return new TagWriteResult(false, false, before.GetValueOrDefault("TBPM"), before.GetValueOrDefault("TKEY"));

        byte[] original = File.ReadAllBytes(path);
        string tmp = path + ".benchmix-tags.tmp";
        try
        {
            File.WriteAllBytes(tmp, original);
            using (var file = TagLib.File.Create(tmp, "taglib/mp3", TagLib.ReadStyle.Average))   // the copy's name says .tmp; say what it is
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                if (fillBpm is { } fb) id3.SetTextFrame("TBPM", BpmText(fb));
                if (fillKey is { } fk) id3.SetTextFrame("TKEY", fk);
                file.Save();
            }

            // Checked before it takes the file's place.
            var after = Id3.Read(tmp);
            if (fillBpm is { } wb && after.GetValueOrDefault("TBPM") != BpmText(wb))
                throw new InvalidDataException("the tempo did not read back from the written copy.");
            if (fillKey is { } wk && after.GetValueOrDefault("TKEY") != wk)
                throw new InvalidDataException("the key did not read back from the written copy.");
            foreach (var (k, v) in before)
                if (after.GetValueOrDefault(k) != v) throw new InvalidDataException($"the copy's {k} tag is not what the file had.");
            if (!Music(File.ReadAllBytes(tmp)).SequenceEqual(Music(original)))
                throw new InvalidDataException("the music in the written copy is not byte for byte the file's.");

            File.Replace(tmp, path, null);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        var now = Id3.Read(path);
        return new TagWriteResult(fillBpm is not null, fillKey is not null, now.GetValueOrDefault("TBPM"), now.GetValueOrDefault("TKEY"));
    }

    /// <summary>
    /// The bytes of an MP3 that are the music: after an ID3v2 tag at the start (its header says how
    /// long it is, with a footer when flagged), and before an ID3v1 tag at the end (128 bytes from
    /// <c>TAG</c>). Tags change; these must not.
    /// </summary>
    public static ReadOnlySpan<byte> Music(byte[] file)
    {
        int start = 0;
        if (file.Length >= 10 && file[0] == 'I' && file[1] == 'D' && file[2] == '3')
        {
            int size = 0;
            for (int i = 6; i < 10; i++) size = (size << 7) | (file[i] & 0x7F);
            start = Math.Min(file.Length, 10 + size + ((file[5] & 0x10) != 0 ? 10 : 0));
        }
        int end = file.Length;
        if (end - start >= 128 && file[end - 128] == 'T' && file[end - 127] == 'A' && file[end - 126] == 'G') end -= 128;
        return file.AsSpan(start, Math.Max(0, end - start));
    }
}
