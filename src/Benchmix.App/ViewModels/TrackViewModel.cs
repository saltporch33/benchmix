using System.Windows.Media;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Benchmix.App.ViewModels;

/// <summary>
/// One track's strip and lane.
///
/// Every setter writes the model and then pushes that one track into the audio graph. The push is
/// a handful of field stores, so it is safe and cheap to do on every pixel of a knob drag — which
/// is what makes the knobs feel live rather than stepped.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly Action _onChanged;
    private readonly Action<string, string?>? _pushUndo;

    public TrackViewModel(ProjectSession session, TrackModel model, Action onChanged,
                          Action<string, string?>? pushUndo = null)
    {
        _session = session;
        Model = model;
        _onChanged = onChanged;
        _pushUndo = pushUndo;
        RefreshFromModel();
    }

    /// <summary>
    /// Records an undo point before a parameter changes. A continuous knob drag fires this on every
    /// pixel, so the key coalesces the whole drag into one step — otherwise a single twist of the
    /// LOW knob would need two hundred presses of Ctrl+Z to undo.
    /// </summary>
    private void Snapshot(string what) => _pushUndo?.Invoke($"{Name}: {what}", $"{Id}:{what}");

    public TrackModel Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameTip))] private string _name = "";

    /// <summary>
    /// True while the name is open for typing. What is typed goes to <see cref="RenameDraft"/>, never
    /// straight to <see cref="Name"/>: any edit elsewhere refreshes this row from the model, and a
    /// box bound to the name itself would be wiped mid-word.
    /// </summary>
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameDraft = "";

    /// <summary>The name's tooltip. A view-model string, because StringFormat does nothing on a ToolTip.</summary>
    public string NameTip => $"{Name} — double-click to rename";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string? _loadError;

    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private double _semitones;
    [ObservableProperty] private double _eqLow = 1.0;
    [ObservableProperty] private double _eqMid = 1.0;
    [ObservableProperty] private double _eqHigh = 1.0;
    [ObservableProperty] private double _gain = 1.0;
    [ObservableProperty] private bool _muted;

    /// <summary>
    /// Read-only here: a click on S can change every track's solo, so it goes through
    /// <see cref="MainViewModel.Solo"/>, which refreshes every row.
    /// </summary>
    public bool Soloed => Model.Soloed;
    [ObservableProperty] private double _timelineOffsetSeconds;
    [ObservableProperty] private bool _linkPitchToTempo;
    [ObservableProperty] private bool _preserveFormants = true;
    [ObservableProperty] private float _meterLevel;

    /// <summary>This track's sections, in playing order.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ClipViewModel> Clips { get; } = new();

    /// <summary>
    /// What the lane draws, as one immutable array.
    ///
    /// It is rebuilt rather than mutated because WPF only repaints a dependency property when the
    /// value it is bound to CHANGES — editing a list in place leaves the reference identical and
    /// the waveform stale.
    /// </summary>
    public IReadOnlyList<Controls.ClipVisual> ClipVisuals { get; private set; } = Array.Empty<Controls.ClipVisual>();

    /// <summary>Rebuilds the section view-models from the model, keeping the selected ones selected.</summary>
    public void RebuildClips()
    {
        var selected = Clips.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        Clips.Clear();
        foreach (var m in Model.Clips)
        {
            var vm = new ClipViewModel(_session, this, m, _onChanged);
            if (selected.Contains(m.Id)) vm.IsSelected = true;
            Clips.Add(vm);
        }
        NotifyClipsChanged();
    }

    /// <summary>Republishes what the lane draws. Called after anything structural.</summary>
    public void NotifyClipsChanged()
    {
        // The loop the selected section belongs to, so loop mode can mark its two outer ends.
        var selected = Clips.FirstOrDefault(c => c.IsSelected);
        var run = selected is null ? null : LoopRun.Of(Model, selected.Model, _session);
        var tempo = TimelineEditor.SecondsMapOf(Model);         // once for the lot; null with no tempo ramp or moved warp marker

        ClipVisuals = Clips.Select(c => new Controls.ClipVisual(
            c.Peaks, c.SampleRate, c.TimelineStartSeconds, c.SourceStartSeconds,
            c.SourceEndSeconds < 0 ? c.SourceDurationSeconds : c.SourceEndSeconds,
            Speed, c.IsSelected, c.IsForeign,
            LoopHandleLeft: run is not null && run.IsOuterEdge(c.Model, left: true),
            LoopHandleRight: run is not null && run.IsOuterEdge(c.Model, left: false),
            Tempo: tempo,
            TimelineEndSeconds: c.TimelineEndSeconds,
            OriginSeconds: Model.TimelineOffsetSeconds,
            TrackStartSeconds: c.TrackStartSeconds,
            CrossfadeSeconds: TimelineEditor.CrossfadeAt(Model, c.Model, _session))).ToArray();

        OnPropertyChanged(nameof(ClipVisuals));
        NotifyMarkChanged();                                    // a turn of TEMPO moves the audio a mark covers
        NotifyRampsChanged();                                   // a turn of TEMPO changes where the first ramp starts from
        NotifyWarpsChanged();                                   // and moves the beats the warp markers are on
        OnPropertyChanged(nameof(TimelineDurationSeconds));
        OnPropertyChanged(nameof(TimelineEndSeconds));
        OnPropertyChanged(nameof(TrimText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---- a marked stretch (Daniel, 2026-10-12) ----

    /// <summary>
    /// The stretch marked on this track in MARK MODE, in its own frames, [Start, End); null when
    /// there is none. In frames, like the sections, so it stays on the audio it covers when TEMPO
    /// turns. Only one track has one at a time; the main view-model sees to that.
    /// </summary>
    public (long Start, long End)? Mark { get; private set; }

    /// <summary>The track's origin when <see cref="Mark"/> was last worked out.</summary>
    public double MarkOrigin { get; private set; }

    /// <summary>What the lane draws for the mark: where it starts and ends on the timeline.</summary>
    public Controls.MarkVisual? MarkVisual { get; private set; }

    /// <summary>Marks a stretch of this track, or with null takes the mark away.</summary>
    public void SetMark((long Start, long End)? span) => RestoreMark(span, Model.TimelineOffsetSeconds);

    /// <summary>Puts back a mark as it was worked out against an origin, after undo rebuilt the rows.</summary>
    public void RestoreMark((long Start, long End)? span, double origin)
    {
        Mark = span;
        MarkOrigin = origin;
        NotifyMarkChanged();
    }

    private void NotifyMarkChanged()
    {
        // Something put before the track's origin moves the origin back and every frame on the
        // track forward by as much, so that nothing is heard to move (TimelineEditor.ShiftOrigin).
        // The mark moves with the frames, or it would slide off the audio it was put on.
        if (Mark is { } m && Model.TimelineOffsetSeconds != MarkOrigin)
        {
            long shift = TimelineEditor.TrackFrameAt(Model, MarkOrigin, _session);
            Mark = (m.Start + shift, m.End + shift);
            MarkOrigin = Model.TimelineOffsetSeconds;
        }
        MarkVisual = Mark is { } k
            ? new Controls.MarkVisual(TimelineEditor.TimelineSecondsOfFrame(Model, k.Start, _session),
                                      TimelineEditor.TimelineSecondsOfFrame(Model, k.End, _session))
            : null;
        OnPropertyChanged(nameof(MarkVisual));
    }

    // ---- tempo ramps (Daniel, 2026-10-11) ----

    /// <summary>What the lane draws for this track's tempo ramps, as one immutable array: placed ones in order, then one being placed.</summary>
    public IReadOnlyList<Controls.RampVisual> RampVisuals { get; private set; } = Array.Empty<Controls.RampVisual>();

    /// <summary>True while the box for a ramp's end tempo is open on this track.</summary>
    [ObservableProperty] private bool _isTypingRampBpm;

    /// <summary>What is typed in the box. The dashed line follows it as it is typed.</summary>
    [ObservableProperty] private string _rampBpmDraft = "";

    /// <summary>Where on the timeline the box sits: beside the ramp's end.</summary>
    [ObservableProperty] private double _rampBoxSeconds;

    /// <summary>The ramp being placed or retyped on this track, drawn on top. Set by the main view-model.</summary>
    public (double Start, double FromBpm, double? End, int? Retype)? RampDraft { get; set; }

    partial void OnRampBpmDraftChanged(string value) => NotifyRampsChanged();

    /// <summary>Republishes what the lane draws for the ramps. Never throws: it runs on every repaint path.</summary>
    public void NotifyRampsChanged()
    {
        var list = new List<Controls.RampVisual>(Model.Ramps.Count + 1);
        double? typed = TempoRamps.ParseBpm(Model, RampBpmDraft) is (true, var v, _) ? Model.Bpm * v : null;
        for (int i = 0; i < Model.Ramps.Count; i++)
        {
            var r = Model.Ramps[i];
            bool retyping = RampDraft is { Retype: int k } && k == i;
            double to = retyping && typed is { } t ? t : Model.Bpm * r.ToSpeed;
            list.Add(new Controls.RampVisual(r.StartSeconds, r.EndSeconds, TempoRamps.BpmAt(Model, r.StartSeconds), to,
                                             retyping ? Controls.RampLook.Typing : Controls.RampLook.Placed));
        }
        if (RampDraft is { Retype: null } d)
            list.Add(d.End is double e
                ? new Controls.RampVisual(d.Start, e, d.FromBpm, typed ?? d.FromBpm, Controls.RampLook.Typing)
                : new Controls.RampVisual(d.Start, d.Start, d.FromBpm, d.FromBpm, Controls.RampLook.StartOnly));
        RampVisuals = list.ToArray();
        OnPropertyChanged(nameof(RampVisuals));
    }

    // ---- warp markers (Daniel, 2026-10-18) ----

    /// <summary>
    /// What the lane draws for this track's warp markers, as one immutable array, in order. A marker
    /// is on the audio, not on the timeline, so where each is drawn is worked out again with the
    /// sections: a turn of TEMPO, a tempo ramp, the origin moving or the warp itself all move them.
    /// </summary>
    public IReadOnlyList<Controls.WarpVisual> WarpVisuals { get; private set; } = Array.Empty<Controls.WarpVisual>();

    /// <summary>Republishes what the lane draws for the warp markers. Never throws: it runs on every repaint path.</summary>
    public void NotifyWarpsChanged()
    {
        var list = new Controls.WarpVisual[Model.Warps.Count];
        // The first stretch runs from the track's start, where its origin is.
        double from = TimelineEditor.TimelineSecondsAt(Model, 0);
        for (int i = 0; i < list.Length; i++)
        {
            double at = WarpMarkers.TimelineSecondsOf(Model, i);
            double spread = WarpMarkers.SpreadBefore(Model, i);
            list[i] = new Controls.WarpVisual(at, from, double.IsFinite(spread) ? WarpMarkers.Label(spread) : null);
            from = at;
        }
        WarpVisuals = list;
        OnPropertyChanged(nameof(WarpVisuals));
    }

    /// <summary>Nothing on this track yet — the lane says how to put something there.</summary>
    public bool IsEmpty => Model.Clips.Count == 0;

    /// <summary>The whole song this track is about, for the strip's readouts.</summary>
    public double SourceDurationSeconds => _session.ClipFor(Model)?.DurationSeconds ?? 0;

    /// <summary>How far this track reaches on the timeline, sections and gaps included.</summary>
    public double TimelineDurationSeconds
        => Model.Clips.Count == 0 || Clips.Count == 0 ? 0 : TimelineEndSeconds - Model.TimelineOffsetSeconds;

    /// <summary>
    /// Where this track's last section ends on the timeline. Read from the model rather than from
    /// <see cref="TimelineOffsetSeconds"/>: the track's origin moves back when a section is put
    /// before it, and the length of the mix must not depend on whether this view has caught up.
    /// </summary>
    public double TimelineEndSeconds => Clips.Count == 0 ? 0 : Clips.Max(c => c.TimelineEndSeconds);

    public bool IsTrimmed => Model.Clips.Count != 1
        || Model.Clips[0].SourceStartSeconds > 0.0001
        || (Model.Clips[0].SourceEndSeconds >= 0
            && Model.Clips[0].SourceEndSeconds < SourceDurationSeconds - 0.0001);

    public string TrimText => Model.Clips.Count switch
    {
        0 => "empty",
        1 => IsTrimmed ? $"trimmed to {Clips.FirstOrDefault()?.LengthSeconds ?? 0:0.0}s" : $"{SourceDurationSeconds:0.0}s",
        var n => $"{n} sections",
    };

    public int SampleRate => _session.SampleRate;

    public Brush ColorBrush => Brushed(Model.ColorHex, 255);
    public Brush ClipBackground => Brushed(Model.ColorHex, 26);

    // ---- read-only display ----
    public double OriginalBpm => Model.Bpm;
    public double PlayingBpm => Model.Bpm * Speed;
    public string BpmText => Model.Bpm > 0
        ? (Math.Abs(Speed - 1) < 0.0005 ? $"{Model.Bpm:F1} BPM" : $"{PlayingBpm:F1} BPM  (was {Model.Bpm:F1})")
        : "BPM unknown";
    public string BpmSourceText => Model.BpmSource == nameof(Engine.Analysis.ValueSource.Detected) ? "estimated" : "from tag";
    public bool BpmIsEstimate => Model.BpmSource == nameof(Engine.Analysis.ValueSource.Detected);

    public string KeyText => Model.Key is null ? "key unknown" : $"{Model.Key}  ({Model.Camelot})";
    public bool KeyIsEstimate => Model.KeySource == nameof(Engine.Analysis.ValueSource.Detected);
    public string KeySourceText => KeyIsEstimate ? "estimated" : "from tag";

    public string SpeedText => $"{(Speed - 1) * 100:+0.00;-0.00;0.00}%";
    public string SemitonesText => LinkPitchToTempo
        ? $"{12 * Math.Log2(Math.Max(0.01, Speed)):+0.00;-0.00;0.00} st (linked)"
        : $"{Semitones:+0.00;-0.00;0} st";
    public string GainText => Gain <= 0.0001 ? "-∞ dB" : $"{20 * Math.Log10(Gain):+0.0;-0.0;0.0} dB";
    public string EqLowText => EqText(EqLow);
    public string EqMidText => EqText(EqMid);
    public string EqHighText => EqText(EqHigh);

    private static string EqText(double g) => g <= 0.0001 ? "KILL" : $"{20 * Math.Log10(g):+0.0;-0.0;0.0}";

    private static Brush Brushed(string hex, byte alpha)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
        catch
        {
            return Brushes.SteelBlue;
        }
    }

    /// <summary>
    /// Reloads every field from the model without firing the property setters, because those
    /// setters push into the audio graph and mark the project dirty — neither of which should
    /// happen when we are only catching the view up to a model that already changed.
    /// </summary>
#pragma warning disable MVVMTK0034   // deliberate: assign backing fields, then raise once
    public void RefreshFromModel()
    {
        _name = Model.Name;
        _speed = Model.Speed;
        _semitones = Model.Semitones;
        _eqLow = Model.EqLow;
        _eqMid = Model.EqMid;
        _eqHigh = Model.EqHigh;
        _gain = Model.Gain;
        _muted = Model.Muted;
        _timelineOffsetSeconds = Model.TimelineOffsetSeconds;
        _linkPitchToTempo = Model.Mode == StretchMode.Linked;
        _preserveFormants = Model.PreserveFormants;
        OnPropertyChanged(string.Empty);
    }
#pragma warning restore MVVMTK0034

    public void NotifyAudioLoaded()
    {
        IsLoaded = true;
        LoadError = null;
        RebuildClips();
        OnPropertyChanged(nameof(SourceDurationSeconds));
        OnPropertyChanged(nameof(TimelineDurationSeconds));
    }

    // ---- change handlers: model first, then the graph, then tell the shell ----

    partial void OnNameChanged(string value) { Model.Name = value; _onChanged(); }

    partial void OnSpeedChanged(double value)
    {
        Snapshot("tempo");
        Model.Speed = value;
        Push();
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(PlayingBpm));
        OnPropertyChanged(nameof(BpmText));
        OnPropertyChanged(nameof(SemitonesText));
        NotifyClipsChanged();
    }

    partial void OnSemitonesChanged(double value)
    {
        Snapshot("pitch");
        Model.Semitones = value;
        Push();
        OnPropertyChanged(nameof(SemitonesText));
    }

    partial void OnEqLowChanged(double value) { Snapshot("low"); Model.EqLow = Gainish(value); Push(); OnPropertyChanged(nameof(EqLowText)); }
    partial void OnEqMidChanged(double value) { Snapshot("mid"); Model.EqMid = Gainish(value); Push(); OnPropertyChanged(nameof(EqMidText)); }
    partial void OnEqHighChanged(double value) { Snapshot("high"); Model.EqHigh = Gainish(value); Push(); OnPropertyChanged(nameof(EqHighText)); }
    partial void OnGainChanged(double value) { Snapshot("volume"); Model.Gain = Gainish(value); Push(); OnPropertyChanged(nameof(GainText)); }

    /// <summary>
    /// Clamps a gain into the range the engine actually accepts, before it reaches the model.
    ///
    /// The knob coerces its own Value into its Minimum..Maximum, but a WPF two-way binding writes
    /// the <b>un-coerced</b> number to its source — so dragging a knob past the bottom left the
    /// knob correctly at zero while the model, and then the saved project, kept a small negative
    /// gain. It sounded right because the engine clamps too, but a .benchmix file should not
    /// contain a negative gain for a knob that reads KILL.
    /// </summary>
    private static double Gainish(double value)
        => double.IsNaN(value) ? 1.0 : Math.Clamp(value, 0, 4);

    partial void OnMutedChanged(bool value) { _pushUndo?.Invoke($"{Name}: mute", null); Model.Muted = value; PushAll(); }

    partial void OnTimelineOffsetSecondsChanged(double value)
    {
        Snapshot("position");
        Model.TimelineOffsetSeconds = Math.Max(0, value);
        Push();
        NotifyClipsChanged();
    }





    partial void OnLinkPitchToTempoChanged(bool value)
    {
        Model.Mode = value ? StretchMode.Linked : StretchMode.Independent;
        Push();
        OnPropertyChanged(nameof(SemitonesText));
    }

    partial void OnPreserveFormantsChanged(bool value) { Model.PreserveFormants = value; Push(); }

    private void Push()
    {
        _session.Apply(Model);
        _onChanged();
    }

    /// <summary>Mute and solo change what every other track hears, so they all get re-pushed.</summary>
    private void PushAll()
    {
        _session.ApplyAll();
        _onChanged();
    }
}
