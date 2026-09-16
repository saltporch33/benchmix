using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Benchmix.Engine.Audio;

public enum TransportState { Stopped, Playing, Paused }

public sealed record OutputDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Live monitoring: one persistent WASAPI stream pulling from the <see cref="MixGraph"/>.
///
/// The stream stays open for the life of the app and simply renders silence when stopped, which
/// avoids the click and the half-second stall you get from opening a device on every play. The
/// graph it pulls from is the same object the renderer bounces, so preview and export cannot
/// drift apart.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly object _gate = new();
    private IWavePlayer? _player;
    private TransportSource? _source;
    private bool _disposed;

    private PlaybackEngine(MixGraph graph, int sampleRate, string? deviceId, string? deviceName)
    {
        Graph = graph;
        SampleRate = sampleRate;
        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    public MixGraph Graph { get; }
    public int SampleRate { get; }
    public string? DeviceId { get; }
    public string? DeviceName { get; }

    public TransportState State => _source?.State ?? TransportState.Stopped;
    public long PositionFrames => Graph.PositionFrames;
    public double PositionSeconds => PositionFrames / (double)SampleRate;

    /// <summary>Loop region in frames, or null. Playback wraps to Start when it passes End.</summary>
    public (long Start, long End)? LoopRegion
    {
        get => _source?.Loop;
        set { if (_source is not null) _source.Loop = value; }
    }

    /// <summary>Raised on the UI's clock, not the audio thread; safe to bind to.</summary>
    public event EventHandler? PlaybackStoppedUnexpectedly;

    /// <summary>
    /// Opens the default (or named) output and builds a graph at the device's own mix rate.
    /// Matching the device rate is what lets WASAPI run in its low-latency path instead of
    /// silently inserting a resampler.
    /// </summary>
    public static PlaybackEngine Open(string? deviceId = null)
    {
        MMDevice? device = null;
        var enumerator = new MMDeviceEnumerator();
        try
        {
            device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        catch
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        int rate = device.AudioClient.MixFormat.SampleRate;

        var graph = new MixGraph(rate);
        var engine = new PlaybackEngine(graph, rate, device.ID, device.FriendlyName);

        var source = new TransportSource(graph);
        engine._source = source;
        engine._player = player;

        player.Init(new SampleToWaveProvider(source));
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) engine.PlaybackStoppedUnexpectedly?.Invoke(engine, EventArgs.Empty);
        };
        player.Play();                     // the stream runs continuously; silence when stopped
        return engine;
    }

    public static IReadOnlyList<OutputDeviceInfo> ListDevices()
    {
        var list = new List<OutputDeviceInfo>();
        try
        {
            var en = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new OutputDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        catch { /* no audio hardware is a survivable state for an offline editor */ }
        return list;
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_source is null) return;
            Graph.SnapParameters();
            _source.State = TransportState.Playing;
        }
    }

    public void Pause()
    {
        lock (_gate) { if (_source is not null) _source.State = TransportState.Paused; }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_source is null) return;
            _source.State = TransportState.Stopped;
            Graph.Seek(_source.StopReturnFrame);
        }
    }

    public void TogglePlay()
    {
        if (State == TransportState.Playing) Pause(); else Play();
    }

    public void Seek(long frame)
    {
        lock (_gate)
        {
            Graph.Seek(Math.Max(0, frame));
            if (_source is not null) _source.StopReturnFrame = Math.Max(0, frame);
        }
    }

    public void SeekSeconds(double seconds) => Seek((long)(seconds * SampleRate));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        Graph.Dispose();
    }

    /// <summary>
    /// Wraps the graph with transport state. Rendering silence while stopped keeps the device
    /// stream alive, so pressing play is instant.
    /// </summary>
    private sealed class TransportSource : ISampleProvider
    {
        private readonly MixGraph _graph;

        public TransportSource(MixGraph graph)
        {
            _graph = graph;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(graph.SampleRate, graph.Channels);
        }

        public WaveFormat WaveFormat { get; }
        public volatile TransportState State = TransportState.Stopped;
        public (long Start, long End)? Loop;
        public long StopReturnFrame;

        public int Read(Span<float> buffer)
        {
            if (State != TransportState.Playing) { buffer.Clear(); return buffer.Length; }

            int channels = _graph.Channels;
            int frames = buffer.Length / channels;
            int done = 0;

            while (done < frames)
            {
                int chunk = frames - done;

                var loop = Loop;
                if (loop is { } lp && lp.End > lp.Start)
                {
                    if (_graph.PositionFrames >= lp.End) _graph.Seek(lp.Start);
                    chunk = (int)Math.Min(chunk, Math.Max(1, lp.End - _graph.PositionFrames));
                }

                _graph.Render(buffer.Slice(done * channels, chunk * channels), chunk);
                done += chunk;
            }
            return buffer.Length;
        }
    }
}
