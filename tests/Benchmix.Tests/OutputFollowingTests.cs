using System.Runtime.InteropServices;
using Benchmix.Engine.Audio;
using NAudio.Wave;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Following Windows' choice of sound output (Daniel, 2026-10-12): "just disconnected my headphones
/// from bluetooth while app was open and then the app stopped playing audio altogether". Driven
/// through fake outputs whose streams the test pulls by hand, so nothing here touches a real
/// device. The signal is a 431 Hz sine — never 440 near anything with a delay — and every check of
/// what is heard is against the sine written out in the test, sample by sample.
/// </summary>
public class OutputFollowingTests
{
    const int Rate = 48000;
    const double Tone = 431;

    sealed class FakePlayer(OutputEndpoint output) : IWavePlayer
    {
        public OutputEndpoint Output { get; } = output;
        private IWaveProvider? _feed;
        public bool Disposed { get; private set; }

        public void Init(IWaveProvider waveProvider) { _feed = waveProvider; OutputWaveFormat = waveProvider.WaveFormat; }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        /// <summary>What the device would ask for next: <paramref name="frames"/> stereo frames.</summary>
        public float[] Pull(int frames)
        {
            var bytes = new byte[frames * 2 * sizeof(float)];
            _feed!.Read(bytes);
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }

        /// <summary>The device goes away under the stream, as a Bluetooth headset does.</summary>
        public void Fail() => PlaybackStopped?.Invoke(this, new StoppedEventArgs(new IOException("The device was removed.")));
    }

    sealed class FakeOutputs : IOutputDevices
    {
        public OutputEndpoint? Default;
        public readonly List<FakePlayer> Opened = new();
        public bool Disposed;

        public int RefuseOpens;                  // how many opens to refuse, as an output mid-change does

        public OutputEndpoint? Current() => Default;
        public IWavePlayer Open(OutputEndpoint output)
        {
            if (RefuseOpens > 0) { RefuseOpens--; throw new IOException("The device is not ready."); }
            var p = new FakePlayer(output);
            Opened.Add(p);
            return p;
        }
        public event EventHandler? Changed;
        public void Dispose() => Disposed = true;

        /// <summary>Windows moves its output, and says so.</summary>
        public void SwitchTo(OutputEndpoint? output) { Default = output; Changed?.Invoke(this, EventArgs.Empty); }
    }

    static readonly OutputEndpoint Headphones = new("bt", "Headphones", Rate);
    static readonly OutputEndpoint Speakers = new("speakers", "Speakers", Rate);
    static readonly OutputEndpoint Headset44 = new("bt44", "Headset", 44100);

    static float Expected(long frame, int rate = Rate) => (float)(0.5 * Math.Sin(2 * Math.PI * Tone * frame / rate));

    /// <summary>An engine on the headphones, playing a 60 s 431 Hz sine through one untouched track.</summary>
    static (PlaybackEngine Engine, FakeOutputs Outputs) Playing()
    {
        var outputs = new FakeOutputs { Default = Headphones };
        var engine = PlaybackEngine.Open(outputs);
        var s = new float[Rate * 60 * 2];
        for (int f = 0; f < Rate * 60; f++) s[f * 2] = s[f * 2 + 1] = Expected(f);
        engine.Graph.AddTrack().SetClip(AudioClip.FromSamples("tone", s, Rate));
        engine.Graph.LimiterEnabled = false;
        engine.Play();
        return (engine, outputs);
    }

    /// <summary>Frames of a stereo block that are not the sine from <paramref name="firstFrame"/> on, exactly.</summary>
    static int NotTheSong(float[] block, long firstFrame)
    {
        int n = 0;
        for (int k = 0; k < block.Length / 2; k++)
            if (block[k * 2] != Expected(firstFrame + k) || block[k * 2 + 1] != Expected(firstFrame + k)) n++;
        return n;
    }

    [Fact]
    public void WhenWindowsMovesTheOutputPlaybackCarriesOnThereFromTheSameMoment()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            var headphones = outputs.Opened.Single();
            Assert.Equal(0, NotTheSong(headphones.Pull(4800), 0));

            outputs.SwitchTo(Speakers);

            Assert.Equal(2, outputs.Opened.Count);
            var speakers = outputs.Opened[1];
            Assert.True(headphones.Disposed, "the headphones' stream was not let go");
            Assert.Equal(PlaybackState.Playing, speakers.PlaybackState);
            Assert.Equal(TransportState.Playing, engine.State);
            Assert.Equal("Speakers", engine.DeviceName);

            int wrong = NotTheSong(speakers.Pull(4800), 4800);
            Assert.True(wrong == 0, $"{wrong} frames on the speakers are not the song carrying on from 0.1 s: the switch jumped, repeated or dropped audio");
        }
    }

    [Fact]
    public void AStreamLetGoNeverReadsTheMixAgain()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            var headphones = outputs.Opened.Single();
            headphones.Pull(4800);
            outputs.SwitchTo(Speakers);

            // The old device's thread asks for one more block, as a real one can on its way out.
            var stale = headphones.Pull(1024);
            Assert.True(stale.All(x => x == 0) && engine.PositionFrames == 4800,
                "a stream already let go pulled the mix: two streams reading it at once would each get every other block");
            Assert.Equal(0, NotTheSong(outputs.Opened[1].Pull(480), 4800));
        }
    }

    [Fact]
    public void AStreamThatDiesIsReplacedByTheOutputWindowsUsesNow()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            var headphones = outputs.Opened.Single();
            headphones.Pull(2400);
            bool told = false;
            engine.OutputChanged += (_, _) => told = true;

            outputs.Default = Speakers;            // Windows has moved on, but its notice has not arrived yet
            headphones.Fail();

            Assert.True(told && !engine.DeviceLost && engine.State == TransportState.Playing,
                "the stream died with the headphones and playback stopped, though the speakers were there to take over");
            Assert.Equal(0, NotTheSong(outputs.Opened[1].Pull(2400), 2400));

            // The stop from the stream already let go, arriving late, changes nothing.
            headphones.Fail();
            Assert.Equal(2, outputs.Opened.Count);
        }
    }

    [Fact]
    public void WithNoOutputLeftPlaybackStopsAndComesBackWhenOneIsConnected()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(2400);
            int stops = 0;
            engine.PlaybackStoppedUnexpectedly += (_, _) => stops++;

            outputs.Default = null;
            outputs.Opened.Single().Fail();
            outputs.SwitchTo(null);                // and Windows confirms there is nothing

            Assert.True(engine.DeviceLost && engine.State == TransportState.Stopped && stops == 1,
                $"with no output left: lost={engine.DeviceLost}, state={engine.State}, told {stops} times (once is right)");

            outputs.SwitchTo(Speakers);
            Assert.False(engine.DeviceLost, "the speakers came back and the engine did not take them");
            engine.Play();
            Assert.Equal(TransportState.Playing, engine.State);
            Assert.Equal(0, NotTheSong(outputs.Opened[^1].Pull(2400), 2400));
        }
    }

    // ================================================================ from the review, 2026-10-12

    [Fact]
    public void AMomentWithNoOutputNamedLeavesAWorkingStreamAlone()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(2400);
            outputs.SwitchTo(null);                // mid-change, Windows names nothing for a moment
            Assert.True(outputs.Opened.Count == 1 && !outputs.Opened[0].Disposed && engine.State == TransportState.Playing && !engine.DeviceLost,
                "a moment with no output named tore down the stream that was still playing, and the song stopped");
            Assert.Equal(0, NotTheSong(outputs.Opened[0].Pull(2400), 2400));
        }
    }

    [Fact]
    public void WhenTheNextOutputAppearsMomentsAfterTheOldOneDiedTheSongCarriesOn()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(2400);
            outputs.Default = null;                // the stream dies before Windows has named the speakers
            outputs.Opened[0].Fail();
            Assert.True(engine.DeviceLost);

            outputs.SwitchTo(Speakers);            // and a moment later it does
            Assert.True(engine.State == TransportState.Playing,
                "the speakers took over moments later but the song stayed stopped until Play was pressed");
            Assert.Equal(0, NotTheSong(outputs.Opened[^1].Pull(2400), 2400));
        }
    }

    [Fact]
    public void AnOutputThatWillNotOpenAtFirstIsTriedAgainAndTheSongCarriesOn()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(2400);
            outputs.RefuseOpens = 1;
            outputs.SwitchTo(Speakers);            // named, but not ready to open yet
            Assert.True(engine.DeviceLost);
            outputs.SwitchTo(Speakers);            // the next notice: ready now
            Assert.True(!engine.DeviceLost && engine.State == TransportState.Playing,
                $"the speakers opened at the second try but the song did not carry on (state {engine.State})");
            Assert.Equal(0, NotTheSong(outputs.Opened[^1].Pull(2400), 2400));
        }
    }

    [Fact]
    public void StoppedOnPurposeWhileThereIsNoOutputItStaysStoppedWhenOneComesBack()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(2400);
            outputs.Default = null;
            outputs.Opened[0].Fail();
            engine.Stop();                         // he pressed stop meanwhile
            outputs.SwitchTo(Speakers);
            Assert.True(engine.State == TransportState.Stopped, "an output coming back started playing a song that had been stopped on purpose");
        }
    }

    [Fact]
    public void ANoticeAboutTheOutputAlreadyInUseChangesNothing()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.SwitchTo(Headphones);
            Assert.Single(outputs.Opened);
            Assert.False(outputs.Opened[0].Disposed);
        }
    }

    [Fact]
    public void AnOutputAtAnotherRateGetsTheMixConvertedNotSpedUp()
    {
        var (engine, outputs) = Playing();
        using (engine)
        {
            outputs.Opened.Single().Pull(4800);
            outputs.SwitchTo(Headset44);
            Assert.Equal(44100, engine.DeviceRate);
            Assert.Equal(Rate, engine.SampleRate);

            var heard = outputs.Opened[1].Pull(44100);           // one second, as the headset counts it
            long advanced = engine.PositionFrames - 4800;
            Assert.True(Math.Abs(advanced - Rate) <= 64,
                $"one second on a 44.1 kHz headset moved the song {advanced / (double)Rate:0.0000} s: it is playing at the wrong speed");

            // Against the sine written out at the headset's rate, from where the song had got to.
            double worst = 0;
            for (int k = 64; k < 44100; k++)
            {
                double want = 0.5 * Math.Sin(2 * Math.PI * Tone * (4800 + k * (Rate / 44100.0)) / Rate);
                worst = Math.Max(worst, Math.Abs(heard[k * 2] - want));
            }
            Assert.True(worst < 2e-3, $"worst error {worst:E2} against the sine: the conversion is off in time or pitch");
        }
    }

    [Fact]
    public void DisposingLetsGoOfTheStreamAndTheNotifications()
    {
        var (engine, outputs) = Playing();
        engine.Dispose();
        Assert.True(outputs.Opened.Single().Disposed && outputs.Disposed);
        outputs.SwitchTo(Speakers);
        Assert.Single(outputs.Opened);
    }

    // ================================================================ the converter on its own

    sealed class SineSource(int rate) : ISampleProvider
    {
        private long _frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        public int Read(Span<float> buffer)
        {
            for (int k = 0; k < buffer.Length / 2; k++, _frame++)
                buffer[k * 2] = buffer[k * 2 + 1] = Expected(_frame, WaveFormat.SampleRate);
            return buffer.Length;
        }
    }

    [Theory]
    [InlineData(48000, 44100)]
    [InlineData(44100, 48000)]
    [InlineData(48000, 16000)]
    public void TheRateConverterKeepsTimeAndPitchAcrossBlocksOfAnySize(int from, int to)
    {
        var converter = new DeviceRateConverter(new SineSource(from), to);
        var heard = new List<float>();
        foreach (int block in new[] { 333, 1024, 7, 4096, 1, 2000, 512, 999 })
        {
            var buf = new float[block * 2];
            Assert.Equal(buf.Length, converter.Read(buf));
            heard.AddRange(buf);
        }
        double worst = 0;
        for (int k = 64; k < heard.Count / 2; k++)
        {
            Assert.Equal(heard[k * 2], heard[k * 2 + 1]);
            worst = Math.Max(worst, Math.Abs(heard[k * 2] - Expected(k, to)));
        }
        Assert.True(worst < 2e-3, $"{from} -> {to}: worst error {worst:E2} against the sine at the new rate — out of time, out of tune, or a seam between blocks");
    }
}
