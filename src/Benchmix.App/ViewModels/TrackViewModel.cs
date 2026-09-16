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

    public TrackViewModel(ProjectSession session, TrackModel model, Action onChanged)
    {
        _session = session;
        Model = model;
        _onChanged = onChanged;
        RefreshFromModel();
    }

    public TrackModel Model { get; }
    public Guid Id => Model.Id;

    [ObservableProperty] private string _name = "";
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
    [ObservableProperty] private bool _soloed;
    [ObservableProperty] private double _timelineOffsetSeconds;
    [ObservableProperty] private bool _linkPitchToTempo;
    [ObservableProperty] private bool _preserveFormants = true;
    [ObservableProperty] private float _meterLevel;

    public PeakData? Peaks => _session.ClipFor(Model)?.EnsurePeaks();
    public double SourceDurationSeconds => _session.ClipFor(Model)?.DurationSeconds ?? 0;
    public double TimelineDurationSeconds => SourceDurationSeconds / Math.Max(0.01, Speed);
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

    public string SpeedText => $"{(Speed - 1) * 100:+0.0;-0.0;0.0}%";
    public string SemitonesText => LinkPitchToTempo
        ? $"{12 * Math.Log2(Math.Max(0.01, Speed)):+0.00;-0.00;0.00} st (linked)"
        : $"{Semitones:+0.0;-0.0;0} st";
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
        _soloed = Model.Soloed;
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
        OnPropertyChanged(nameof(Peaks));
        OnPropertyChanged(nameof(SourceDurationSeconds));
        OnPropertyChanged(nameof(TimelineDurationSeconds));
    }

    // ---- change handlers: model first, then the graph, then tell the shell ----

    partial void OnNameChanged(string value) { Model.Name = value; _onChanged(); }

    partial void OnSpeedChanged(double value)
    {
        Model.Speed = value;
        Push();
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(PlayingBpm));
        OnPropertyChanged(nameof(BpmText));
        OnPropertyChanged(nameof(SemitonesText));
        OnPropertyChanged(nameof(TimelineDurationSeconds));
    }

    partial void OnSemitonesChanged(double value)
    {
        Model.Semitones = value;
        Push();
        OnPropertyChanged(nameof(SemitonesText));
    }

    partial void OnEqLowChanged(double value) { Model.EqLow = value; Push(); OnPropertyChanged(nameof(EqLowText)); }
    partial void OnEqMidChanged(double value) { Model.EqMid = value; Push(); OnPropertyChanged(nameof(EqMidText)); }
    partial void OnEqHighChanged(double value) { Model.EqHigh = value; Push(); OnPropertyChanged(nameof(EqHighText)); }
    partial void OnGainChanged(double value) { Model.Gain = value; Push(); OnPropertyChanged(nameof(GainText)); }

    partial void OnMutedChanged(bool value) { Model.Muted = value; PushAll(); }
    partial void OnSoloedChanged(bool value) { Model.Soloed = value; PushAll(); }

    partial void OnTimelineOffsetSecondsChanged(double value)
    {
        Model.TimelineOffsetSeconds = Math.Max(0, value);
        Push();
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
