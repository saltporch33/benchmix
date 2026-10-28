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

    /// <summary>The project exactly as it would be saved, for telling two states apart.</summary>
    internal static string Fingerprint(BenchmixProject project) => JsonSerializer.Serialize(project, Options);

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

        // Version 4 and earlier: one arrangement, written at the top of the file. It becomes the one
        // tab, "Mix", with the tracks, tempo and loop it had, and the old fields are dropped so they
        // are not written again. (The property's default is a tab already, so the version says
        // which kind of file this is, not whether the list is empty.) A version 5 file has its tabs.
        if (p.SchemaVersion < 5)
        {
            p.Tabs = new List<TabModel>
            {
                new()
                {
                    Name = "Mix",
                    Tracks = p.LegacyTracks ?? new List<TrackModel>(),
                    ProjectBpm = p.LegacyProjectBpm ?? 0,
                    LoopStartSeconds = p.LegacyLoopStartSeconds,
                    LoopEndSeconds = p.LegacyLoopEndSeconds,
                },
            };
            p.CurrentTabIndex = 0;
        }
        else if (p.Tabs is null || p.Tabs.Count == 0)
        {
            p.Tabs = new List<TabModel> { new() { Name = "Mix" } };
            p.CurrentTabIndex = 0;
        }
        p.LegacyTracks = null;
        p.LegacyProjectBpm = null;
        p.LegacyLoopStartSeconds = null;
        p.LegacyLoopEndSeconds = null;
        p.CurrentTabIndex = Math.Clamp(p.CurrentTabIndex, 0, p.Tabs.Count - 1);
        for (int i = 0; i < p.Tabs.Count; i++)
        {
            var tab = p.Tabs[i];
            tab.Tracks ??= new();
            if (tab.Id == Guid.Empty) tab.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(tab.Name)) tab.Name = i == 0 ? "Mix" : $"Tab {i + 1}";
        }

        foreach (var t in p.AllTracks)
        {
            if (t.Speed <= 0) t.Speed = 1.0;
            t.Ramps ??= new();
            TempoRamps.Normalise(t, p.SampleRate);
            t.Warps ??= new();
            WarpMarkers.Normalise(t, p.SampleRate);
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
/// Undo by whole-project snapshot, restored a tab at a time.
///
/// <para>
/// A mashup project is a few kilobytes of numbers, so cloning the entire thing per edit costs
/// nothing and removes every class of bug where an undo command forgets to restore one field.
/// Command-pattern undo would be the right call if the model were large; here it would only be
/// more ways to be wrong.
/// </para>
/// <para>
/// Since tabs (2026-10-27) each tab has a stack of its own, and a step puts back only the tabs it
/// touched (<see cref="TabMerge.Restore"/>): Daniel, verbatim, *"i dont want to hit ctrl z and have
/// something undone on an unseen tab"*. Nearly every step touches the open tab alone; moving a
/// track to another tab touches both, and adding, removing or moving a tab touches the tab row.
/// </para>
/// </summary>
public sealed class UndoStack
{
    private sealed record Entry(BenchmixProject Snapshot, string Label, Guid[] Touched, bool Order);

    private readonly List<Entry> _past = new();
    private readonly List<Entry> _future = new();
    private readonly int _limit;

    public UndoStack(int limit = 100) => _limit = Math.Max(1, limit);

    public bool CanUndo => _past.Count > 0;
    public bool CanRedo => _future.Count > 0;
    public string? NextUndoLabel { get; private set; }

    /// <summary>
    /// Call immediately BEFORE mutating, with a label describing what is about to happen.
    /// <paramref name="touched"/> names the tabs the step may change (null: the open tab only);
    /// <paramref name="order"/> says it changes the tab row — which tabs there are, or their order.
    /// </summary>
    public void Push(BenchmixProject current, string label, IEnumerable<Guid>? touched = null, bool order = false)
    {
        var ids = (touched ?? new[] { current.CurrentTab.Id }).Distinct().ToArray();
        _past.Add(new Entry(current.Clone(), label, ids, order));
        if (_past.Count > _limit) _past.RemoveAt(0);
        _future.Clear();
        NextUndoLabel = label;
    }

    /// <summary>
    /// Steps back to the last state that differs from <paramref name="current"/>, on the tabs the
    /// step touched; every other tab stays as it is now.
    ///
    /// <para>
    /// A snapshot is taken before a command knows whether it will change anything, so a refused
    /// command — a split with nothing under the playhead, a delete with nothing selected — still
    /// left one behind, identical to the project. Ctrl+Z then restored the same state and seemed
    /// to do nothing, and the press after it undid the edit actually wanted. Those steps are
    /// skipped (until 2026-10-03 they were not).
    /// </para>
    /// </summary>
    public BenchmixProject? Undo(BenchmixProject current)
    {
        string now = ProjectStore.Fingerprint(current);
        while (_past.Count > 0)
        {
            var e = _past[^1];
            _past.RemoveAt(_past.Count - 1);
            var restored = TabMerge.Restore(current, e.Snapshot, e.Touched, e.Order);
            if (ProjectStore.Fingerprint(restored) == now) continue;

            _future.Add(new Entry(current.Clone(), e.Label, e.Touched, e.Order));
            NextUndoLabel = _past.Count > 0 ? _past[^1].Label : null;
            RedoLabel = e.Label;
            return restored;
        }
        NextUndoLabel = null;
        return null;
    }

    public BenchmixProject? Redo(BenchmixProject current)
    {
        string now = ProjectStore.Fingerprint(current);
        while (_future.Count > 0)
        {
            var e = _future[^1];
            _future.RemoveAt(_future.Count - 1);
            var restored = TabMerge.Restore(current, e.Snapshot, e.Touched, e.Order);
            if (ProjectStore.Fingerprint(restored) == now) continue;

            _past.Add(new Entry(current.Clone(), e.Label, e.Touched, e.Order));
            NextUndoLabel = e.Label;
            return restored;
        }
        return null;
    }

    public string? RedoLabel { get; private set; }

    public void Clear()
    {
        _past.Clear();
        _future.Clear();
        NextUndoLabel = null;
        RedoLabel = null;
    }
}

/// <summary>How an undo step puts a snapshot back: a tab at a time, never one it did not touch.</summary>
public static class TabMerge
{
    /// <summary>
    /// The live project with only the <paramref name="touched"/> tabs put back as the snapshot
    /// had them. The open tab is put back whole. Another touched tab — the one a track was sent
    /// to — only gets its tracks' comings and goings put back: a track the step put there goes, one
    /// it took away comes back where it was, and everything done on that tab since stays, because
    /// that was done out of sight of this tab. A touched tab the snapshot lacks is taken away; one
    /// the live project lacks comes back where it was. With <paramref name="order"/>, the tabs are
    /// also put in the snapshot's order. Every other tab keeps what it has now. The open tab stays
    /// open unless it went, in which case the snapshot's is opened. The shared settings — master,
    /// limiter, EQ mode — go back with every step, as they always did.
    /// </summary>
    public static BenchmixProject Restore(BenchmixProject live, BenchmixProject snapshot, IReadOnlyCollection<Guid> touched, bool order)
    {
        var result = live.Clone();
        var openId = live.CurrentTab.Id;
        bool broughtBackTheSnapshotsOpenTab = false;
        foreach (var id in touched)
        {
            int inSnapshot = snapshot.Tabs.FindIndex(t => t.Id == id);
            int inLive = result.Tabs.FindIndex(t => t.Id == id);
            if (inSnapshot < 0)
            {
                if (inLive >= 0) result.Tabs.RemoveAt(inLive);
            }
            else if (inLive < 0)
            {
                result.Tabs.Insert(Math.Min(inSnapshot, result.Tabs.Count), snapshot.Tabs[inSnapshot].Clone());
                if (id == snapshot.CurrentTab.Id) broughtBackTheSnapshotsOpenTab = true;
            }
            else if (id == openId)
            {
                result.Tabs[inLive] = snapshot.Tabs[inSnapshot].Clone();
            }
            else
            {
                // Its name is in the tab row, in sight from every tab, so it goes back; its tracks
                // only come and go as the step moved them.
                var was = snapshot.Tabs[inSnapshot];
                var now = result.Tabs[inLive];
                now.Name = was.Name;
                var wasIds = was.Tracks.Select(t => t.Id).ToHashSet();
                now.Tracks.RemoveAll(t => !wasIds.Contains(t.Id));
                for (int i = 0; i < was.Tracks.Count; i++)
                    if (now.Tracks.All(t => t.Id != was.Tracks[i].Id))
                        now.Tracks.Insert(Math.Min(i, now.Tracks.Count), was.Tracks[i].Clone());
            }
        }
        if (order)
        {
            var wanted = snapshot.Tabs.Select(t => t.Id).ToList();
            result.Tabs = result.Tabs.OrderBy(t => { int i = wanted.IndexOf(t.Id); return i < 0 ? int.MaxValue : i; }).ToList();
        }
        if (result.Tabs.Count == 0) result.Tabs.Add(new TabModel());

        // A tab that comes back is opened, so what Ctrl+Z did is on screen; otherwise the open tab
        // stays open, unless it went, when the snapshot's is opened.
        int open = broughtBackTheSnapshotsOpenTab ? result.Tabs.FindIndex(t => t.Id == snapshot.CurrentTab.Id) : -1;
        if (open < 0) open = result.Tabs.FindIndex(t => t.Id == openId);
        if (open < 0) open = result.Tabs.FindIndex(t => t.Id == snapshot.CurrentTab.Id);
        result.CurrentTabIndex = Math.Clamp(open < 0 ? 0 : open, 0, result.Tabs.Count - 1);

        result.Name = snapshot.Name;
        result.MasterGain = snapshot.MasterGain;
        result.LimiterEnabled = snapshot.LimiterEnabled;
        result.EqMode = snapshot.EqMode;
        return result;
    }
}
