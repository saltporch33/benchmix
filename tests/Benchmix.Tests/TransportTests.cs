using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Where the transport goes when it stops. A click of the scroll wheel (Daniel, 2026-10-11) stops
/// and goes back to where play last started, so Space plays the same stretch again.
/// </summary>
public class TransportTests
{
    const int Rate = 48000;

    /// <summary>The offline engine never moves by itself; pull it on the way a device would.</summary>
    static void Listen(PlaybackEngine engine, double seconds)
    {
        var block = new float[Rate / 10 * engine.Graph.Channels];
        for (int i = 0; i < (int)Math.Round(seconds * 10); i++) engine.Graph.Render(block, Rate / 10);
    }

    [Fact]
    public void StopGoesBackToWherePlayStartedNotToZeroOrWhereItGotTo()
    {
        using var engine = PlaybackEngine.OpenOffline(Rate);
        engine.Seek(30L * Rate);
        engine.Play();
        Listen(engine, 1);
        engine.Stop();

        Assert.Equal(TransportState.Stopped, engine.State);
        Assert.True(engine.PositionFrames == 30L * Rate,
            $"stopped at {engine.PositionFrames / (double)Rate:0.000}s, not 30.000s where play started: " +
            "Space would not play the same stretch again");
    }

    [Fact]
    public void SpaceAfterAPauseIsWhereTheWheelClickGoesBackTo()
    {
        using var engine = PlaybackEngine.OpenOffline(Rate);
        engine.Seek(30L * Rate);
        engine.Play();
        Listen(engine, 1);
        engine.Pause();          // Space
        engine.Play();           // Space again: plays on from 31 s
        Listen(engine, 1);
        engine.Stop();

        Assert.True(engine.PositionFrames == 31L * Rate,
            $"the wheel-click went back to {engine.PositionFrames / (double)Rate:0.000}s, not 31.000s: " +
            "it returned to the earlier start, not to where Space last started playing");
    }

    [Fact]
    public void PlayWhilePlayingDoesNotMoveWhereStopGoesBack()
    {
        using var engine = PlaybackEngine.OpenOffline(Rate);
        engine.Seek(30L * Rate);
        engine.Play();
        Listen(engine, 1);
        engine.Play();           // already playing: not a new start
        Listen(engine, 1);
        engine.Stop();

        Assert.True(engine.PositionFrames == 30L * Rate,
            $"stopped at {engine.PositionFrames / (double)Rate:0.000}s, not 30.000s: a second Play while playing " +
            "moved the point the wheel-click goes back to");
    }

    [Fact]
    public void TheReturnPointCanBePutBackAfterAReSeek()
    {
        // An undo re-seeks to stay where it is, which would make that the return point; the
        // view-model reads it first and puts it back. This pins the property it relies on.
        using var engine = PlaybackEngine.OpenOffline(Rate);
        engine.Seek(30L * Rate);
        engine.Play();
        Listen(engine, 1);
        long keep = engine.StopReturnFrame;
        engine.Seek(engine.PositionFrames);
        engine.StopReturnFrame = keep;
        engine.Stop();

        Assert.True(engine.PositionFrames == 30L * Rate,
            $"stopped at {engine.PositionFrames / (double)Rate:0.000}s, not 30.000s: an undo while playing would " +
            "move where the wheel-click goes back to");
    }
}
