using System.Security.Cryptography;
using Benchmix.Core.Model;

namespace Benchmix.Core.Persistence;

/// <summary>
/// A project follows its songs (Daniel, 2026-10-28, after moving a project and its mp3s to another
/// folder and getting a lane of "Missing:" lines: "Any way we can make these follow each other?
/// like, it looks for the source files in current folder project .benchmix file is located? …
/// if I ever rename the source sound files, would i still get this message even if we resolve
/// the folder location following issue?").
///
/// <para>
/// A project remembers each song by its full path. When nothing is at that path any more, the song
/// is looked for next to the project file, then anywhere under the project's folder — by name
/// first, then by fingerprint, so a renamed file is recognised too. A fingerprint is the file's
/// size and a hash of its first 256 KB: nothing here decodes audio, and it costs one short read
/// per song, so every song gets one when it is added and every save keeps them current. A song that
/// still exists where the project says is never touched, whatever else sits next to the project.
/// </para>
/// </summary>
public static class SourceRelinker
{
    public const string NextToTheProject = "next to the project";
    public const string InAFolderBeside = "in a folder beside the project";
    public const string ByFingerprint = "by its fingerprint";

    /// <summary>One song moved: where the project said it was, where it is now, and how it was found.</summary>
    public sealed record Found(string From, string To, string How);

    private const int HeadBytes = 256 * 1024;
    private const int MostFilesToScan = 5000;
    private static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus", ".aif", ".aiff", ".mp4", ".m4b" };

    /// <summary>
    /// The file's size and a hash of its first 256 KB — enough to tell one song from another and
    /// cheap enough to take at every save. Null when the file is not there or cannot be read.
    /// </summary>
    public static string? Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[(int)Math.Min(HeadBytes, info.Length)];
            int read = 0;
            while (read < head.Length)
            {
                int n = fs.Read(head, read, head.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return $"{info.Length}:{Convert.ToHexString(SHA256.HashData(head.AsSpan(0, read)))}";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Remembers a song's fingerprint in the project, so a rename can be recognised later. Harmless when the file cannot be read.</summary>
    public static void Remember(BenchmixProject project, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var print = Fingerprint(path);
        if (print is not null) project.SourceFingerprints[path] = print;
    }

    /// <summary>
    /// For a save: remembers every song the project uses that has no fingerprint yet, and forgets
    /// the songs it no longer uses.
    /// </summary>
    public static void RememberAll(BenchmixProject project)
    {
        var used = project.AllTracks.SelectMany(ProjectSession.SourcePathsOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in used)
            if (!project.SourceFingerprints.ContainsKey(path)) Remember(project, path);
        foreach (var key in project.SourceFingerprints.Keys.Where(k => !used.Contains(k)).ToList())
            project.SourceFingerprints.Remove(key);
    }

    /// <summary>
    /// Finds every song the project points at that is not there any more — next to the project
    /// file, then anywhere under the project's folder (its Backups left out), by name first and then
    /// by fingerprint — and moves the project's paths to where they were found, on every tab. Songs
    /// that still exist where the project says are left alone. Returns what moved, for the status bar.
    /// </summary>
    public static IReadOnlyList<Found> Resolve(BenchmixProject project, string projectPath)
    {
        var relinks = new List<Found>();
        string? folder = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (folder is null || !Directory.Exists(folder)) return relinks;

        var missing = project.AllTracks.SelectMany(ProjectSession.SourcePathsOf)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(p => !File.Exists(p))
                             .ToList();
        if (missing.Count == 0) return relinks;

        List<string>? files = null;              // listed once, and only when something is missing
        foreach (var path in missing)
        {
            string name = Path.GetFileName(path);
            string? found = null, how = null;

            string beside = Path.Combine(folder, name);
            if (File.Exists(beside)) { found = beside; how = NextToTheProject; }
            else
            {
                files ??= FilesUnder(folder);
                var sameName = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                if (sameName is not null) { found = sameName; how = InAFolderBeside; }
                else if (project.SourceFingerprints.TryGetValue(path, out var print))
                {
                    long size = SizeOf(print);
                    foreach (var f in files)
                    {
                        if (!AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                        if (new FileInfo(f).Length != size) continue;          // the cheap half first
                        if (Fingerprint(f) == print) { found = f; how = ByFingerprint; break; }
                    }
                }
            }

            if (found is null) continue;
            Relink(project, path, found);
            relinks.Add(new Found(path, found, how!));
        }
        return relinks;
    }

    /// <summary>
    /// Points every track and section that read from <paramref name="from"/> at <paramref name="to"/>,
    /// on every tab — Find missing file, and the last step of <see cref="Resolve"/>. Returns how
    /// many places changed. The fingerprint follows, taken afresh from the file now pointed at.
    /// </summary>
    public static int Relink(BenchmixProject project, string from, string to)
    {
        int changed = 0;
        foreach (var t in project.AllTracks)
        {
            if (string.Equals(t.SourcePath, from, StringComparison.OrdinalIgnoreCase)) { t.SourcePath = to; changed++; }
            foreach (var c in t.Clips)
                if (string.Equals(c.SourcePath, from, StringComparison.OrdinalIgnoreCase)) { c.SourcePath = to; changed++; }
        }
        project.SourceFingerprints.Remove(from);
        Remember(project, to);
        return changed;
    }

    private static long SizeOf(string print)
    {
        int colon = print.IndexOf(':');
        return colon > 0 && long.TryParse(print.AsSpan(0, colon), out var size) ? size : -1;
    }

    /// <summary>
    /// Every file under the folder, the project's own Backups folder left out, one folder at a time
    /// so one that cannot be read does not end the search, and capped so a project saved at the
    /// root of a drive does not walk the drive.
    /// </summary>
    private static List<string> FilesUnder(string folder)
    {
        var list = new List<string>();
        string backups = Path.Combine(folder, "Backups");
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0 && list.Count < MostFilesToScan)
        {
            string dir = pending.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    list.Add(f);
                    if (list.Count >= MostFilesToScan) break;
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!string.Equals(d, backups, StringComparison.OrdinalIgnoreCase)) pending.Push(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}
