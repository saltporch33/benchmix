using System.Text.Json;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Tabs (Daniel, 2026-10-27: separate arrangements in one file, "like an excel workbook", and "i
/// dont want to hit ctrl z and have something undone on an unseen tab"). The file: a version 4
/// project opens as one tab; tabs survive a save. The mix: only the open tab is in it, measured —
/// the render equals, byte for byte, a session holding only that tab's song. Undo: a step on one
/// tab leaves the others as they are now, and a track sent to another tab comes back from it
/// without undoing what was done there since.
/// </summary>
public class TabsTests : IDisposable
{
    const int Rate = 48000;
    const int Frames = Rate * 20;
    private readonly string _dir;
    private readonly string _up;
    private readonly string _down;

    public TabsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-tabs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _up = Path.Combine(_dir, "up.wav");
        _down = Path.Combine(_dir, "down.wav");
        var up = new float[Frames * 2];
        var down = new float[Frames * 2];
        for (int f = 0; f < Frames; f++)
        {
            up[f * 2] = up[f * 2 + 1] = 0.4f * f / Frames;
            down[f * 2] = down[f * 2 + 1] = -0.3f * (Frames - f) / Frames;
        }
        AudioClip.FromSamples("up", up, Rate).SaveWav(_up);
        AudioClip.FromSamples("down", down, Rate).SaveWav(_down);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static float[] Render(ProjectSession session)
    {
        session.ApplyAll();
        var g = session.Graph;
        g.LimiterEnabled = false;
        g.Seek(0);
        g.SnapParameters();
        var buf = new float[Frames * 2];
        for (int done = 0; done < Frames;)
        {
            int n = Math.Min(1024, Frames - done);
            g.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    /// <summary>What the mix is with only these songs in it.</summary>
    async Task<float[]> OnlyAsync(params string[] wavs)
    {
        using var s = ProjectSession.CreateOffline(Rate);
        foreach (var w in wavs) await s.AddTrackAsync(w);
        return Render(s);
    }

    static void Same(float[] want, float[] got, string consequence)
        => Assert.True(want.AsSpan().SequenceEqual(got), consequence);

    static TrackModel Track(string name) => new() { Name = name };

    // ================================================================ the model and the file

    [Fact]
    public void ANewProjectHasOneTabAndItsTracksAreThatTabs()
    {
        var p = new BenchmixProject();
        Assert.Single(p.Tabs);
        p.Tracks.Add(Track("a"));
        p.ProjectBpm = 93;
        p.LoopStartSeconds = 4;
        Assert.True(ReferenceEquals(p.Tabs[0].Tracks, p.Tracks) && p.Tabs[0].ProjectBpm == 93 && p.Tabs[0].LoopStartSeconds == 4,
            "the project's tracks, tempo and loop are not the open tab's: the editor would edit a different arrangement from the one on screen");
        var clone = p.Clone();
        Assert.True(clone.Tabs.Count == 1 && !ReferenceEquals(clone.Tabs[0], p.Tabs[0]) && !ReferenceEquals(clone.Tabs[0].Tracks[0], p.Tabs[0].Tracks[0]),
            "a clone shares a tab or a track with the original: every undo snapshot would track the present");
    }

    [Fact]
    public void AVersion4FileOpensAsOneTabWithItsTracksTempoAndLoop()
    {
        string path = Path.Combine(_dir, "old.benchmix");
        var old = new
        {
            schemaVersion = 4, name = "Old", sampleRate = Rate, masterGain = 1.0, limiterEnabled = true, eqMode = "Isolator",
            loopStartSeconds = 10.0, loopEndSeconds = 20.0, projectBpm = 93.0,
            tracks = new[]
            {
                new { id = Guid.NewGuid(), name = "Bed", sourcePath = _up, speed = 1.0,
                      clips = new[] { new { id = Guid.NewGuid(), sourcePath = _up, trackStartSeconds = 0.0, sourceStartSeconds = 0.0, sourceEndSeconds = -1.0 } } },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.True(ProjectStore.TryRead(path, out var p) && p is not null);
        Assert.True(p!.Tabs.Count == 1 && p.Tabs[0].Name == "Mix" && p.CurrentTabIndex == 0,
            $"a version 4 project did not open as one tab named Mix ({p.Tabs.Count} tabs)");
        Assert.True(p.Tracks.Count == 1 && p.Tracks[0].Name == "Bed" && p.Tracks[0].Clips.Count == 1,
            "the old project's track did not land on the tab: the project would open empty");
        Assert.True(p.ProjectBpm == 93 && p.LoopStartSeconds == 10 && p.LoopEndSeconds == 20,
            "the old project's tempo or listening loop did not land on the tab");
        Assert.True(p.LegacyTracks is null && p.LegacyProjectBpm is null && p.SchemaVersion == BenchmixProject.CurrentSchemaVersion,
            "the old top-level fields were kept: they would be written again and read back as a second copy");

        // Saved again, the file is a version 5 file: tabs, and nothing at the top that was there before.
        string again = Path.Combine(_dir, "resaved.benchmix");
        ProjectStore.Save(p, again);
        using var doc = JsonDocument.Parse(File.ReadAllText(again));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("schemaVersion").GetInt32() == 5 && root.TryGetProperty("tabs", out var tabs) && tabs.GetArrayLength() == 1
                    && !root.TryGetProperty("tracks", out _) && !root.TryGetProperty("projectBpm", out _),
            "the resaved file is not a version 5 file with its tracks inside a tab");
        Assert.True(ProjectStore.TryRead(again, out var p2) && p2!.Tracks.Count == 1 && p2.ProjectBpm == 93, "the resaved file does not open as it was");
    }

    [Fact]
    public void TabsSurviveSaveAndReopenWithTheOpenTabRemembered()
    {
        var p = new BenchmixProject { Name = "Two" };
        p.Tabs[0].Name = "Bed";
        p.Tabs[0].Tracks.Add(Track("a1"));
        p.Tabs[0].ProjectBpm = 93;
        var b = new TabModel { Name = "Bites", ProjectBpm = 120, LoopStartSeconds = 1, LoopEndSeconds = 5 };
        b.Tracks.Add(Track("b1"));
        b.Tracks.Add(Track("b2"));
        p.Tabs.Add(b);
        p.CurrentTabIndex = 1;

        string path = Path.Combine(_dir, "two.benchmix");
        ProjectStore.Save(p, path);
        var loaded = ProjectStore.Load(path).Project;

        Assert.True(loaded.Tabs.Count == 2 && loaded.CurrentTabIndex == 1, $"reopened with {loaded.Tabs.Count} tabs, open tab {loaded.CurrentTabIndex}: the tab he left open is not the one that opens");
        Assert.True(loaded.Tabs[0].Name == "Bed" && loaded.Tabs[0].Id == p.Tabs[0].Id && loaded.Tabs[0].Tracks.Select(t => t.Name).SequenceEqual(new[] { "a1" }) && loaded.Tabs[0].ProjectBpm == 93,
            "the first tab did not come back as it was");
        Assert.True(loaded.Tabs[1].Name == "Bites" && loaded.Tabs[1].Tracks.Select(t => t.Name).SequenceEqual(new[] { "b1", "b2" }) && loaded.Tabs[1].ProjectBpm == 120
                    && loaded.Tabs[1].LoopStartSeconds == 1 && loaded.Tabs[1].LoopEndSeconds == 5,
            "the second tab did not come back as it was");
        Assert.True(ReferenceEquals(loaded.Tracks, loaded.Tabs[1].Tracks) && loaded.ProjectBpm == 120, "the open tab's tracks and tempo are not the project's");
    }

    // ================================================================ the mix

    [Fact]
    public async Task OnlyTheOpenTabIsInTheMix()
    {
        using var s = ProjectSession.CreateOffline(Rate);
        var up = await s.AddTrackAsync(_up);
        s.AddTab("B");
        await s.SwitchTabAsync(1);
        var down = await s.AddTrackAsync(_down);

        Assert.True(s.Project.Tabs[0].Tracks.Contains(up) && s.Project.Tabs[1].Tracks.Contains(down), "the songs are not on the tabs they were added on");
        Assert.True(s.VoiceFor(up) is null && s.VoiceFor(down) is not null && s.Graph.Tracks.Count == 1,
            "with tab B open, tab A's song still has a voice in the mix: the tab that is not on screen would be heard");
        Same(await OnlyAsync(_down), Render(s), "with tab B open the mix is not B's song alone");

        await s.SwitchTabAsync(0);
        Assert.True(s.VoiceFor(up) is not null && s.VoiceFor(down) is null, "switching back did not swap the voices");
        Same(await OnlyAsync(_up), Render(s), "with tab A open again the mix is not A's song alone");
        Assert.True(s.HasAllAudio(up) && s.HasAllAudio(down), "switching tabs dropped a song's audio: the next switch would decode it again");
    }

    [Fact]
    public async Task ATrackSentToAnotherTabLeavesTheMixHereAndPlaysThere()
    {
        using var s = ProjectSession.CreateOffline(Rate);
        var up = await s.AddTrackAsync(_up);
        var down = await s.AddTrackAsync(_down);
        s.AddTab("B");

        Assert.False(s.MoveTrackToTab(down, 0).Changed, "sending a track to the tab it is on was not refused");
        var r = s.MoveTrackToTab(down, 1);
        Assert.True(r.Changed, r.Message);
        Assert.True(s.Project.Tabs[0].Tracks.SequenceEqual(new[] { up }) && s.Project.Tabs[1].Tracks.SequenceEqual(new[] { down }),
            "the track is not on the other tab, or still on this one");
        Same(await OnlyAsync(_up), Render(s), "a track sent to another tab is still in this tab's mix");

        await s.SwitchTabAsync(1);
        Same(await OnlyAsync(_down), Render(s), "on the other tab the track sent there does not play");
    }

    [Fact]
    public async Task RemovingTheOpenTabOpensItsNeighbourAndTheOnlyTabStays()
    {
        using var s = ProjectSession.CreateOffline(Rate);
        await s.AddTrackAsync(_up);
        s.AddTab("B");
        await s.SwitchTabAsync(1);
        await s.AddTrackAsync(_down);

        var r = await s.RemoveTabAsync(1);
        Assert.True(r.Changed && s.Project.Tabs.Count == 1 && s.Project.CurrentTabIndex == 0, r.Message);
        Same(await OnlyAsync(_up), Render(s), "after removing the open tab, the tab opened instead does not play");
        Assert.False((await s.RemoveTabAsync(0)).Changed, "the only tab could be removed, leaving nothing to put a song on");
    }

    // ================================================================ undo, a tab at a time

    static (BenchmixProject p, TabModel a, TabModel b) TwoTabs()
    {
        var p = new BenchmixProject();
        var a = p.Tabs[0];
        a.Name = "A";
        a.Tracks.Add(Track("a1"));
        a.Tracks.Add(Track("a2"));
        var b = new TabModel { Name = "B" };
        b.Tracks.Add(Track("b1"));
        p.Tabs.Add(b);
        return (p, a, b);
    }

    [Fact]
    public void AnUndoOnOneTabLeavesTheOtherTabsAsTheyAreNow()
    {
        var (p, a, b) = TwoTabs();
        var stackA = new UndoStack();
        stackA.Push(p, "rename a1");
        a.Tracks[0].Name = "a1 renamed";
        b.Tracks[0].Name = "b1 renamed";                  // done on tab B since, out of sight of A

        var restored = stackA.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs[0].Tracks[0].Name == "a1", "the step on tab A was not undone");
        Assert.True(restored.Tabs[1].Tracks[0].Name == "b1 renamed",
            "undoing on tab A undid something on tab B, which was not on screen");
        Assert.True(restored.CurrentTabIndex == 0, "the undo changed which tab is open");

        var redone = stackA.Redo(restored);
        Assert.True(redone is not null && redone.Tabs[0].Tracks[0].Name == "a1 renamed" && redone.Tabs[1].Tracks[0].Name == "b1 renamed",
            "redo did not put the step back on tab A alone");
    }

    [Fact]
    public void UndoingAMoveToAnotherTabBringsTheTrackBackWithoutUndoingThatTabsOwnChanges()
    {
        var (p, a, b) = TwoTabs();
        var a2 = a.Tracks[1];
        var stackA = new UndoStack();
        stackA.Push(p, "send a2 to B", touched: new[] { a.Id, b.Id });
        a.Tracks.Remove(a2);
        b.Tracks.Add(a2);
        b.Tracks[0].Name = "b1 renamed";                  // on tab B, after the move

        var restored = stackA.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs[0].Tracks.Select(t => t.Name).SequenceEqual(new[] { "a1", "a2" }), "the track did not come back to tab A where it was");
        Assert.True(restored.Tabs[1].Tracks.Select(t => t.Name).SequenceEqual(new[] { "b1 renamed" }),
            "undoing the move from tab A either left the track on tab B or undid B's own later change");
    }

    [Fact]
    public void UndoingARemovedTabBringsItBackWhereItWas()
    {
        var (p, a, b) = TwoTabs();
        var c = new TabModel { Name = "C" };
        p.Tabs.Add(c);
        p.CurrentTabIndex = 1;                            // B open, about to go; A is where he lands
        var stackA = new UndoStack();
        stackA.Push(p, "remove tab B", touched: new[] { b.Id }, order: true);
        p.Tabs.RemoveAt(1);
        p.CurrentTabIndex = 0;
        a.Tracks[0].Name = "a1 renamed";                  // on A, after the removal

        var restored = stackA.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs.Select(t => t.Name).SequenceEqual(new[] { "A", "B", "C" }), $"the tab did not come back where it was ({string.Join(",", restored.Tabs.Select(t => t.Name))})");
        Assert.True(restored.Tabs[1].Tracks.Select(t => t.Name).SequenceEqual(new[] { "b1" }), "the tab came back without its tracks");
        Assert.True(restored.Tabs[0].Tracks[0].Name == "a1 renamed", "bringing a tab back undid a change on the tab he was on");
        Assert.True(restored.CurrentTabIndex == 1, "the tab brought back was not opened: what Ctrl+Z did would be off screen");
    }

    [Fact]
    public void RenamingAnotherTabIsUndoneFromTheTabHeWasOnWithoutUndoingThatTabsOwnChanges()
    {
        var (p, a, b) = TwoTabs();                        // A open; B renamed from A, by its chip
        var stackA = new UndoStack();
        stackA.Push(p, "rename tab B", touched: new[] { b.Id });
        b.Name = "Bites";
        b.Tracks[0].Name = "b1 renamed";                  // on tab B itself, after

        var restored = stackA.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs[1].Name == "B", "the tab's old name did not come back");
        Assert.True(restored.Tabs[1].Tracks[0].Name == "b1 renamed", "undoing the rename from tab A undid a change made on tab B since");
        Assert.True(restored.CurrentTabIndex == 0, "undoing a tab's rename changed which tab is open");
    }

    [Fact]
    public void UndoingAnAddedTabTakesItAwayAndOpensWhereHeWas()
    {
        var (p, a, b) = TwoTabs();
        var c = new TabModel { Name = "C" };
        var stackC = new UndoStack();
        stackC.Push(p, "add tab C", touched: new[] { c.Id }, order: true);   // before it exists
        p.Tabs.Add(c);
        p.CurrentTabIndex = 2;

        var restored = stackC.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs.Select(t => t.Name).SequenceEqual(new[] { "A", "B" }) && restored.CurrentTabIndex == 0,
            $"undoing the new tab left {string.Join(",", restored.Tabs.Select(t => t.Name))} with tab {restored.CurrentTabIndex} open, not A and B with A open");
    }

    [Fact]
    public void UndoingAMovedTabPutsTheRowBackWithTheSameTabOpen()
    {
        var (p, a, b) = TwoTabs();
        var c = new TabModel { Name = "C" };
        p.Tabs.Add(c);
        p.CurrentTabIndex = 2;
        var stackC = new UndoStack();
        stackC.Push(p, "move tab C left", touched: Array.Empty<Guid>(), order: true);
        p.Tabs.RemoveAt(2);
        p.Tabs.Insert(0, c);
        p.CurrentTabIndex = 0;

        var restored = stackC.Undo(p);
        Assert.NotNull(restored);
        Assert.True(restored!.Tabs.Select(t => t.Name).SequenceEqual(new[] { "A", "B", "C" }) && restored.CurrentTab.Id == c.Id,
            $"the row is {string.Join(",", restored.Tabs.Select(t => t.Name))} with tab {restored.CurrentTabIndex} open: not A, B, C with C still open");
    }

    [Fact]
    public void AStepThatChangedNothingIsSkipped()
    {
        var (p, _, _) = TwoTabs();
        var stack = new UndoStack();
        stack.Push(p, "refused");
        Assert.Null(stack.Undo(p));
    }
}
