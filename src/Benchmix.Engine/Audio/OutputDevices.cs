using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Benchmix.Engine.Audio;

/// <summary>A sound output the engine can play through, and the rate Windows mixes it at.</summary>
public sealed record OutputEndpoint(string Id, string Name, int SampleRate);

/// <summary>
/// What the engine needs from Windows' sound outputs: which one Windows is playing through now, a
/// stream on it, and word when that changes. A seam, so that following a change — headphones
/// unplugged mid-song, Bluetooth connecting — can be tested without touching anyone's speakers.
/// </summary>
public interface IOutputDevices : IDisposable
{
    /// <summary>The output Windows plays through now, or null when there is none.</summary>
    OutputEndpoint? Current();

    /// <summary>A shared-mode stream on that output, not yet started.</summary>
    IWavePlayer Open(OutputEndpoint output);

    /// <summary>
    /// Windows' choice of output changed, or an output came or went. Raised on the thread that made
    /// this object when it has a synchronisation context (the UI's), and must not be acted on from
    /// Windows' own notification thread, which holds an audio-system lock while it calls.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>The real outputs, through Windows Core Audio (WASAPI).</summary>
public sealed class WasapiDevices : IOutputDevices
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    public WasapiDevices()
    {
        // Marshalled to the creating thread's context: the handlers open and close streams, which
        // must never happen on Windows' notification thread.
        _notifications = _enumerator.CreateNotificationClient(true);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) Changed?.Invoke(this, EventArgs.Empty);
        };
        _notifications.DeviceStateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceRemoved += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceAdded += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public OutputEndpoint? Current()
    {
        try
        {
            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null) return null;
#pragma warning disable CS0618   // the property the rest of the engine has always read the mix rate from
            return new OutputEndpoint(device.ID, device.FriendlyName, device.AudioClient.MixFormat.SampleRate);
#pragma warning restore CS0618
        }
        catch
        {
            return null;                    // mid-change, Windows can refuse to say; there will be another notification
        }
    }

    public IWavePlayer Open(OutputEndpoint output)
    {
#pragma warning disable CS0618   // WasapiOut, as the engine has always used: 30 ms, event-driven, shared
        return new WasapiOut(_enumerator.GetDevice(output.Id), AudioClientShareMode.Shared, true, 30);
#pragma warning restore CS0618
    }

    public void Dispose()
    {
        try { _notifications.Dispose(); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
