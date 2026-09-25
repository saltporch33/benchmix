using System.Text.Json;
using System.Text.Json.Serialization;
using Benchmix.Core.Model;

namespace Benchmix.Core.Persistence;

/// <summary>
/// Reads and writes <c>.benchmix</c> project files.
///
/// Saving is atomic and keeps the previous version: write a temp file, flush it to disk, then
/// replace the target while moving the old one aside as <c>.bak</c>. Loading falls back through
/// the main file, the backup, then the newest dated backup, and says which one it used. Losing an
/// afternoon of mashup edits to a half-written file is the kind of thing that makes someone stop
/// trusting a tool, and it is cheap to prevent.
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".benchmix";
    public const string FileFilter = "Benchmix project (*.benchmix)|*.benchmix|All files (*.*)|*.*";

    /// <summary>How many dated copies to keep beside a project.</summary>
    public const int BackupsToKeep = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record LoadResult(BenchmixProject Project, string LoadedFrom, bool UsedFallback, string? Note);

    public static void Save(BenchmixProject project, string path, string? timestampForBackup = null)
    {
        project.SchemaVersion = BenchmixProject.CurrentSchemaVersion;

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var json = JsonSerializer.Serialize(project, Options);
        var tmp = full + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.Write(json);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }

        // File.Replace throws when the destination does not exist yet, so the first save moves.
        if (File.Exists(full)) File.Replace(tmp, full, full + ".bak");
        else File.Move(tmp, full);

        if (timestampForBackup is not null) WriteDatedBackup(full, json, timestampForBackup);
    }

    public static LoadResult Load(string path)
    {
        var full = Path.GetFullPath(path);

        if (TryRead(full, out var project))
            return new LoadResult(project!, full, false, null);

        var bak = full + ".bak";
        if (TryRead(bak, out project))
            return new LoadResult(project!, bak, true, "The project file was unreadable, so the previous save was loaded instead.");

        var newest = NewestDatedBackup(full);
        if (newest is not null && TryRead(newest, out project))
            return new LoadResult(project!, newest, true,
                $"The project file and its backup were unreadable, so the dated backup from {Path.GetFileName(newest)} was loaded.");

        throw new InvalidDataException($"Could not read {Path.GetFileName(full)}, its .bak, or any dated backup.");
    }

    public static bool TryRead(string path, out BenchmixProject? project)
    {
        project = null;
        try
        {
            if (!File.Exists(path)) return false;
            var json = ReadWithRetry(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var loaded = JsonSerializer.Deserialize<BenchmixProject>(json, Options);
            if (loaded is null) return false;
            if (loaded.SchemaVersion > BenchmixProject.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"This project was written by a newer version of Benchmix (schema {loaded.SchemaVersion}).");

            Migrate(loaded);
            project = loaded;
            return true;
        }
        catch (InvalidDataException) { throw; }
        catch (System.Text.Json.JsonException) { return false; }   // genuinely malformed
        catch (IOException) { throw; }                             // locked or unreadable: not corruption
        catch (UnauthorizedAccessException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Reads the file, retrying briefly through transient locks.
    ///
    /// A sync client holding the file open for a moment is not the same thing as a corrupt file,
    /// and treating it as one silently demotes the newest save to <c>.bak</c> on the next write.
    /// </summary>
    private static string ReadWithRetry(string path, int attempts = 5)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(40);
            }
        }
    }

    /// <summary>Brings older schemas forward.</summary>
    private static void Migrate(BenchmixProject p)
    {
        if (p.SampleRate <= 0) p.SampleRate = 48000;
        if (p.MasterGain <= 0) p.MasterGain = 1.0;
        foreach (var t in p.Tracks)
        {
            if (t.Speed <= 0) t.Speed = 1.0;
            if (t.Id == Guid.Empty) t.Id = Guid.NewGuid();
            if (string.IsNullOrEmpty(t.Name)) t.Name = Path.GetFileNameWithoutExtension(t.SourcePath);

            // Version 1 had one section per track, described by fields on the track itself. It
            // becomes one clip starting at the track's own origin, which places and trims it
            // exactly where it was, then the old fields are dropped so they are not written again.
            //
            // Only for a version 1 file. A newer track with no sections is not an old one waiting
            // to be migrated: it is a track whose every section was deleted, or an empty track
            // someone made on purpose. Treating it as version 1 put the whole song back into the
            // mix every time the project was reopened.
            bool versionOne = p.SchemaVersion < 2 || t.SourceStartSeconds is not null || t.SourceEndSeconds is not null;
            if (t.Clips.Count == 0 && versionOne && !string.IsNullOrEmpty(t.SourcePath))
            {
                t.Clips.Add(new ClipModel
                {
                    SourcePath = t.SourcePath,
                    TrackStartSeconds = 0,
                    SourceStartSeconds = t.SourceStartSeconds ?? 0,
                    SourceEndSeconds = t.SourceEndSeconds ?? -1,
                    FadeInSeconds = t.FadeInSeconds ?? 0,
                    FadeOutSeconds = t.FadeOutSeconds ?? 0,
                });
            }

            t.SourceStartSeconds = null;
            t.SourceEndSeconds = null;
            t.FadeInSeconds = null;
            t.FadeOutSeconds = null;

            foreach (var c in t.Clips)
            {
                if (c.Id == Guid.Empty) c.Id = Guid.NewGuid();
                if (string.IsNullOrEmpty(c.SourcePath)) c.SourcePath = t.SourcePath;
            }
        }
        p.SchemaVersion = BenchmixProject.CurrentSchemaVersion;
    }

    private static string BackupDirectory(string projectPath)
        => Path.Combine(Path.GetDirectoryName(projectPath)!, "Backups");

    private static void WriteDatedBackup(string projectPath, string json, string timestamp)
    {
        try
        {
            var dir = BackupDirectory(projectPath);
            Directory.CreateDirectory(dir);
            var name = $"{Path.GetFileNameWithoutExtension(projectPath)}-{timestamp}{Extension}";
            File.WriteAllText(Path.Combine(dir, name), json);

            var stale = new DirectoryInfo(dir)
                .GetFiles($"{Path.GetFileNameWithoutExtension(projectPath)}-*{Extension}")
                .OrderByDescending(f => f.Name)
                .Skip(BackupsToKeep);
            foreach (var f in stale) { try { f.Delete(); } catch { } }
        }
        catch { /* a failed backup must never fail the save */ }
    }

    private static string? NewestDatedBackup(string projectPath)
    {
        try
        {
            var dir = BackupDirectory(projectPath);
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir)
                .GetFiles($"{Path.GetFileNameWithoutExtension(projectPath)}-*{Extension}")
                .OrderByDescending(f => f.Name)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }
}

/// <summary>
/// Undo by whole-project snapshot.
///
/// A mashup project is a few kilobytes of numbers, so cloning the entire thing per edit costs
/// nothing and removes every class of bug where an undo command forgets to restore one field.
/// Command-pattern undo would be the right call if the model were large; here it would only be
/// more ways to be wrong.
/// </summary>
public sealed class UndoStack
{
    private readonly List<BenchmixProject> _past = new();
    private readonly List<BenchmixProject> _future = new();
    private readonly int _limit;

    public UndoStack(int limit = 100) => _limit = Math.Max(1, limit);

    public bool CanUndo => _past.Count > 0;
    public bool CanRedo => _future.Count > 0;
    public string? NextUndoLabel { get; private set; }

    private readonly List<string> _labels = new();

    /// <summary>Call immediately BEFORE mutating, with a label describing what is about to happen.</summary>
    public void Push(BenchmixProject current, string label)
    {
        _past.Add(current.Clone());
        _labels.Add(label);
        if (_past.Count > _limit) { _past.RemoveAt(0); _labels.RemoveAt(0); }
        _future.Clear();
        NextUndoLabel = label;
    }

    public BenchmixProject? Undo(BenchmixProject current)
    {
        if (_past.Count == 0) return null;
        var previous = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        var label = _labels[^1];
        _labels.RemoveAt(_labels.Count - 1);

        _future.Add(current.Clone());
        NextUndoLabel = _labels.Count > 0 ? _labels[^1] : null;
        RedoLabel = label;
        return previous;
    }

    public BenchmixProject? Redo(BenchmixProject current)
    {
        if (_future.Count == 0) return null;
        var next = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(current.Clone());
        _labels.Add(RedoLabel ?? "change");
        NextUndoLabel = _labels[^1];
        return next;
    }

    public string? RedoLabel { get; private set; }

    public void Clear()
    {
        _past.Clear();
        _future.Clear();
        _labels.Clear();
        NextUndoLabel = null;
        RedoLabel = null;
    }
}
