using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Benchmix.App.ViewModels;

/// <summary>
/// One section of a track: a block on the timeline you can select, drag, trim and copy.
///
/// Positions come in two flavours and mixing them up is the easy mistake here.
/// <see cref="TrackStartSeconds"/> lives in the track's own unstretched timebase, which is what
/// the model and the engine both speak. <see cref="TimelineStartSeconds"/> is where it actually
/// appears on screen, after the track's origin and its tempo. The UI works in the second and
/// writes the first.
/// </summary>
public sealed partial class ClipViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly TrackViewModel _track;
    private readonly Action _onChanged;

    public ClipViewModel(ProjectSession session, TrackViewModel track, ClipModel model, Action onChanged)
    {
        _session = session;
        _track = track;
        Model = model;
        _onChanged = onChanged;
        RefreshFromModel();
    }

    public ClipModel Model { get; }
    public Guid Id => Model.Id;
    public TrackViewModel Track => _track;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _trackStartSeconds;
    [ObservableProperty] private double _sourceStartSeconds;
    [ObservableProperty] private double _sourceEndSeconds = -1;
    [ObservableProperty] private double _fadeInSeconds;
    [ObservableProperty] private double _fadeOutSeconds;

    public PeakData? Peaks => _session.AudioFor(Model)?.EnsurePeaks();
    public int SampleRate => _session.SampleRate;

    /// <summary>Length of the whole file this section is cut from.</summary>
    public double SourceDurationSeconds => _session.AudioFor(Model)?.DurationSeconds ?? 0;

    /// <summary>How much of the source this section uses.</summary>
    public double LengthSeconds => TimelineEditor.LengthOf(Model, _session);

    /// <summary>Where it appears on the timeline, after the track's origin and tempo.</summary>
    public double TimelineStartSeconds => TimelineEditor.TimelineSecondsAt(_track.Model, TrackStartSeconds);

    /// <summary>How long it occupies the timeline, after the track's tempo — and its tempo ramps.</summary>
    public double TimelineLengthSeconds => TimelineEditor.TimelineLength(_track.Model, TrackStartSeconds, LengthSeconds);

    public double TimelineEndSeconds => TimelineStartSeconds + TimelineLengthSeconds;

    /// <summary>True when this section came from a different song than the track it sits on.</summary>
    public bool IsForeign => !Model.SourcePath.Equals(_track.Model.SourcePath, StringComparison.OrdinalIgnoreCase);

#pragma warning disable MVVMTK0034   // deliberate: assign backing fields, then raise once
    public void RefreshFromModel()
    {
        _trackStartSeconds = Model.TrackStartSeconds;
        _sourceStartSeconds = Model.SourceStartSeconds;
        _sourceEndSeconds = Model.SourceEndSeconds;
        _fadeInSeconds = Model.FadeInSeconds;
        _fadeOutSeconds = Model.FadeOutSeconds;
        OnPropertyChanged(string.Empty);
    }
#pragma warning restore MVVMTK0034

    partial void OnTrackStartSecondsChanged(double value)
    {
        Model.TrackStartSeconds = Math.Max(0, value);
        Push();
    }

    partial void OnSourceStartSecondsChanged(double value)
    {
        Model.SourceStartSeconds = Math.Max(0, value);
        Push();
    }

    partial void OnSourceEndSecondsChanged(double value)
    {
        Model.SourceEndSeconds = value;
        Push();
    }

    partial void OnFadeInSecondsChanged(double value)
    {
        Model.FadeInSeconds = Math.Max(0, value);
        Push();
    }

    partial void OnFadeOutSecondsChanged(double value)
    {
        Model.FadeOutSeconds = Math.Max(0, value);
        Push();
    }

    private void Push()
    {
        TimelineEditor.Normalise(_track.Model);
        _session.Apply(_track.Model);
        _track.NotifyClipsChanged();
        OnPropertyChanged(nameof(LengthSeconds));
        OnPropertyChanged(nameof(TimelineStartSeconds));
        OnPropertyChanged(nameof(TimelineLengthSeconds));
        OnPropertyChanged(nameof(TimelineEndSeconds));
        _onChanged();
    }

    public string FadeText => FadeInSeconds <= 0 && FadeOutSeconds <= 0
        ? "no fades"
        : $"fade {FadeInSeconds:0.#}s / {FadeOutSeconds:0.#}s";

    public string TrimText =>
        $"{LengthSeconds:0.0}s of {SourceDurationSeconds:0.0}s";
}
