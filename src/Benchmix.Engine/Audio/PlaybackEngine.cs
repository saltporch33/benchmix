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
///
/// <para>
/// It follows Windows' choice of output (Daniel, 2026-10-12): Bluetooth headphones disconnected
/// mid-song, playback carries on through the speakers from the same moment, and back again when
/// they reconnect. Until then the stream died with the device and only a restart brought sound
/// back. An output at another rate than the mix is fed through a <see cref="DeviceRateConverter"/>.
/// </para>
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly object _gate = new();
    private IWavePlayer? _player;
    private TransportSource? _source;
    private IOutputDevices? _devices;
    private bool _disposed;

    /// <summary>
    /// Which stream may pull the mix. Bumped, under <see cref="_feedGate"/>, whenever a stream is let
    /// go: its next read is silence and touches nothing, so two streams never pull the graph at once.
    /// </summary>
    private long _generation;
    private readonly object _feedGate = new();

    /// <summary>
    /// Until when (Environment.TickCount64) playing should carry on by itself once an output is back.
    /// While Windows changes outputs it can for a moment report none, or refuse to open the one it
    /// names (review, 2026-10-12); the song must not stay stopped for that. A long silence is
    /// different: sound starting again by itself minutes later would startle.
    /// </summary>
    private long _resumeUntil;

    /// <summary>How long after losing every output playing still resumes by itself.</summary>
    public const int ResumeWithinMilliseconds = 5000;

    private PlaybackEngine(MixGraph graph, int sampleRate)
    {
        Graph = graph;
        SampleRate = sampleRate;
        DeviceRate = sampleRate;
    }

    public MixGraph Graph { get; }

    /// <summary>The mix's rate: the rate of the output Benchmix started on. It never changes.</summary>
    public int SampleRate { get; }

    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }

    /// <summary>The rate of the output in use. Not <see cref="SampleRate"/> when Windows moved playback to one at another rate.</summary>
    public int DeviceRate { get; private set; }

    /// <summary>Playback moved to another output: Windows changed it, or the one in use went away. Raised on the UI's thread.</summary>
    public event EventHandler? OutputChanged;

    public TransportState State => _source?.State ?? TransportState.Stopped;
    public long PositionFrames => Graph.PositionFrames;
    public double PositionSeconds => PositionFrames / (double)SampleRate;

    /// <summary>Loop region in frames, or null. Playback wraps to Start when it passes End.</summary>
    public (long Start, long End)? LoopRegion
    {
        get => _source?.Loop;
        set { if (_source is not null) _source.Loop = value; }
    }

    /// <summary>
    /// True while there is no output to play through: the one in use went away and Windows has
    /// none to take its place. It clears by itself when one appears.
    /// </summary>
    public bool DeviceLost { get; private set; }

    /// <summary>Playback stopped because no output is left. Raised on the UI's thread, not the audio thread; safe to bind to.</summary>
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
        var engine = new PlaybackEngine(graph, sampleRate);
        engine._source = new TransportSource(graph);
        return engine;
    }

    /// <summary>True when there is no output device — playback is a no-op but rendering works.</summary>
    public bool IsOffline => _player is null;

    /// <summary>
    /// Opens the output Windows is playing through and builds the mix at that output's own rate,
    /// which lets WASAPI run in its low-latency path instead of quietly inserting a resampler, then
    /// keeps following Windows' choice of output. Throws when there is no output at all.
    /// </summary>
    public static PlaybackEngine Open(IOutputDevices? devices = null)
    {
        devices ??= new WasapiDevices();
        var output = devices.Current();
        if (output is null)
        {
            devices.Dispose();
            throw new InvalidOperationException("There is no sound output.");
        }

        var graph = new MixGraph(output.SampleRate);
        var engine = new PlaybackEngine(graph, output.SampleRate) { _devices = devices };
        engine._source = new TransportSource(graph);
        try { engine.Attach(output); }
        catch
        {
            devices.Dispose();
            throw;
        }
        devices.Changed += engine.OnOutputsChanged;
        return engine;
    }

    /// <summary>
    /// Starts a stream on an output, fed from the transport: through a rate converter when the
    /// output runs at another rate than the mix. The stream runs continuously; silence when stopped.
    /// </summary>
    private void Attach(OutputEndpoint output)
    {
        var player = _devices!.Open(output);
        try
        {
            long generation;
            lock (_feedGate) generation = ++_generation;
            ISampleProvider feed = new OwnedFeed(this, _source!, generation);
            if (output.SampleRate != SampleRate) feed = new DeviceRateConverter(feed, output.SampleRate);
            player.Init(new SampleToWaveProvider(feed));
            player.PlaybackStopped += OnStreamStopped;
            player.Play();
        }
        catch
        {
            try { player.Dispose(); } catch { }
            throw;
        }
        _player = player;
        DeviceId = output.Id;
        DeviceName = output.Name;
        DeviceRate = output.SampleRate;
        DeviceLost = false;
    }

    /// <summary>
    /// Lets go of the stream in use. A block it is part-way through is waited out, and it can
    /// start no other, before it is closed: the next stream must be the only thing pulling the mix.
    /// </summary>
    private void Detach()
    {
        var old = _player;
        _player = null;
        lock (_feedGate) _generation++;
        if (old is null) return;
        old.PlaybackStopped -= OnStreamStopped;
        try { old.Stop(); } catch { }
        try { old.Dispose(); } catch { }
    }

    private void OnOutputsChanged(object? sender, EventArgs e) => Follow(streamFailed: false);

    private void OnStreamStopped(object? sender, StoppedEventArgs e)
    {
        // A stream already let go, or a stop that was asked for, is not a failure.
        if (!ReferenceEquals(sender, _player) || e.Exception is null) return;
        Follow(streamFailed: true);
    }

    /// <summary>
    /// Moves playback to the output Windows is using now, when that is not the one in use or the one
    /// in use has failed. Playing carries on from the very moment it had reached. With no output at
    /// all, playback stops until one appears.
    /// </summary>
    private void Follow(bool streamFailed)
    {
        bool moved = false, lost = false;
        lock (_gate)
        {
            if (_disposed || _devices is null || _source is null) return;
            var now = _devices.Current();
            if (!streamFailed && !DeviceLost && _player is not null && now?.Id == DeviceId) return;
            // Mid-change Windows can name no output for a moment. A stream that still works is kept
            // until it names another one; the next notification says when.
            if (!streamFailed && !DeviceLost && _player is not null && now is null) return;

            bool wasPlaying = _source.State == TransportState.Playing;
            Detach();
            if (now is not null)
            {
                try { Attach(now); moved = true; }
                catch { now = null; }               // not ready yet: the next notification tries again
            }
            if (now is null)
            {
                lost = !DeviceLost;
                DeviceLost = true;
                if (wasPlaying) _resumeUntil = Environment.TickCount64 + ResumeWithinMilliseconds;
                _source.State = TransportState.Stopped;
            }
            else if (_resumeUntil != 0)
            {
                // Back within moments of losing it: the song carries on as if nothing had happened.
                if (Environment.TickCount64 <= _resumeUntil && _source.State != TransportState.Playing)
                {
                    Graph.SnapParameters();
                    _source.State = TransportState.Playing;
                }
                _resumeUntil = 0;
            }
        }
        if (moved) OutputChanged?.Invoke(this, EventArgs.Empty);
        if (lost) PlaybackStoppedUnexpectedly?.Invoke(this, EventArgs.Empty);
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
        // With no output a moment ago, look again: one may have appeared without a word.
        if (DeviceLost) Follow(streamFailed: false);
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
        lock (_gate)
        {
            _resumeUntil = 0;                       // paused on purpose: an output coming back must not play
            if (_source is not null) _source.State = TransportState.Paused;
        }
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
            _resumeUntil = 0;                       // stopped on purpose, likewise
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
        lock (_gate)
        {
            _disposed = true;
            if (_devices is not null) _devices.Changed -= OnOutputsChanged;
            Detach();
            try { _devices?.Dispose(); } catch { }
            _devices = null;
        }
        Graph.Dispose();
    }

    /// <summary>
    /// The transport, for one stream only. Once that stream has been let go (another output took
    /// over) it reads silence and touches nothing.
    /// </summary>
    private sealed class OwnedFeed(PlaybackEngine engine, TransportSource source, long generation) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            lock (engine._feedGate)
            {
                if (engine._generation != generation) { buffer.Clear(); return buffer.Length; }
                return source.Read(buffer);
            }
        }
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
