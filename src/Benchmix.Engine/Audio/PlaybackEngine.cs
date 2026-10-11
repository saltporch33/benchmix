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

    /// <summary>True once the output device has failed; playback cannot resume without reopening.</summary>
    public bool DeviceLost { get; private set; }

    /// <summary>Raised on the UI's clock, not the audio thread; safe to bind to.</summary>
    public event EventHandler? PlaybackStoppedUnexpectedly;

    /// <summary>
    /// Opens the default (or named) output and builds a graph at the device's own mix rate.
    /// Matching the device rate is what lets WASAPI run in its low-latency path instead of
    /// silently inserting a resampler.
    /// </summary>
    /// <summary>
    /// A engine with no audio device: the graph and the transport exist, nothing is played.
    /// Used by tests, and as the fallback when a machine has no working output — rendering a file
    /// does not need speakers, so losing the device should not cost you the editor.
    /// </summary>
    public static PlaybackEngine OpenOffline(int sampleRate = 48000)
    {
        var graph = new MixGraph(sampleRate);
        var engine = new PlaybackEngine(graph, sampleRate, null, null);
        engine._source = new TransportSource(graph);
        return engine;
    }

    /// <summary>True when there is no output device — playback is a no-op but rendering works.</summary>
    public bool IsOffline => _player is null;

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
            if (e.Exception is null) return;
            // The stream is gone. Say so in the engine's own state, or the UI's 30 Hz poll will
            // read State == Playing on the next tick and cheerfully claim it is still playing.
            engine.DeviceLost = true;
            if (engine._source is not null) engine._source.State = TransportState.Stopped;
            engine.PlaybackStoppedUnexpectedly?.Invoke(engine, EventArgs.Empty);
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
            if (_source is null || DeviceLost) return;
            Graph.SnapParameters();
            // Wherever play starts is where Stop comes back to — after a pause too. Only a seek used
            // to set it, so Space after a pause and then a click of the scroll wheel went back to
            // the seek before the pause rather than to where it had just started playing.
            if (_source.State != TransportState.Playing) _source.StopReturnFrame = Graph.TargetPositionFrames;
            _source.State = TransportState.Playing;
        }
    }

    public void Pause()
    {
        lock (_gate) { if (_source is not null) _source.State = TransportState.Paused; }
    }

    /// <summary>
    /// Waits for any block the device is mid-way through to finish.
    ///
    /// Pausing only stops the <i>next</i> block from starting. The renderer seeks and then pulls
    /// the same graph, so without this handshake it can collide with a block already in flight —
    /// which is a race between two threads over every filter and stretcher in the mix.
    /// </summary>
    public bool WaitForIdle(int millisecondsTimeout = 500)
    {
        if (_source is null || _player is null) return true;

        var until = Environment.TickCount64 + millisecondsTimeout;
        while (_source.InBlock)
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>
    /// Stops and goes back to where play last started (<see cref="StopReturnFrame"/>).
    /// <para>
    /// The device may be part-way through a block when this is called. Seeking at once let that
    /// block finish afterwards and move the playhead on by its own length, so the jump back landed
    /// a block late — about 10 ms, and not even the same 10 ms twice. The seek now waits it out.
    /// </para>
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_source is null) return;
            _source.State = TransportState.Stopped;
            WaitForIdle();
            Graph.Seek(_source.StopReturnFrame);
        }
    }

    /// <summary>
    /// Where <see cref="Stop"/> goes back to: the frame play last started from, or was last moved to
    /// with <see cref="Seek"/>. Read and put back across an undo, which re-seeks to stay where it is.
    /// </summary>
    public long StopReturnFrame
    {
        get { lock (_gate) return _source?.StopReturnFrame ?? 0; }
        set { lock (_gate) { if (_source is not null) _source.StopReturnFrame = Math.Max(0, value); } }
    }

    public void TogglePlay()
    {
        if (State == TransportState.Playing) Pause(); else Play();
    }

    /// <summary>
    /// Moves the playhead. While the device is actually pulling the graph this is handed to the
    /// render thread to apply between blocks; otherwise it lands immediately.
    /// </summary>
    public void Seek(long frame)
    {
        lock (_gate)
        {
            frame = Math.Max(0, frame);
            if (_source is not null) _source.StopReturnFrame = frame;

            if (_player is not null && State == TransportState.Playing) Graph.RequestSeek(frame);
            else Graph.Seek(frame);
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
        public long StopReturnFrame;

        /// <summary>
        /// Published as a single reference so the audio thread always sees a whole region. A
        /// nullable tuple of two longs is 24 bytes: written field by field from the UI while the
        /// ruler is dragged, the audio thread can read a half-updated one and wrap to the wrong place.
        /// </summary>
        private sealed record LoopSpan(long Start, long End);
        private volatile LoopSpan? _loop;

        public (long Start, long End)? Loop
        {
            get { var l = _loop; return l is null ? null : (l.Start, l.End); }
            set => _loop = value is { } v ? new LoopSpan(v.Start, v.End) : null;
        }

        /// <summary>True while this source is inside a block, so a renderer can wait it out.</summary>
        public volatile bool InBlock;

        public int Read(Span<float> buffer)
        {
            if (State != TransportState.Playing) { buffer.Clear(); return buffer.Length; }

            InBlock = true;
            try { return ReadCore(buffer); }
            finally { InBlock = false; }
        }

        private int ReadCore(Span<float> buffer)
        {
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
