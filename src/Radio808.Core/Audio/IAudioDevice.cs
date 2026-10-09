using System;
using System.Threading.Tasks;

namespace Radio808.Core.Audio;

/// <summary>
/// Interleaved float32 frames to play, pulled on the device's own thread. The handler fills the whole span (silence if
/// it has nothing).
/// </summary>
public delegate void AudioPullHandler(Span<float> interleaved);

/// <summary>A running output stream on the system's default device, following it when the default changes.</summary>
internal interface IAudioDevice : IDisposable
{
    string DeviceName { get; }
    /// <summary>
    /// The output stopped and started again on its own (the route changed: AirPlay picked, HDMI replugged). What
    /// queued up meanwhile would play late, so the player drops back to its target level.
    /// </summary>
    event Action? Restarted;
}

/// <summary>Opens the platform's audio output: WASAPI on Windows, CoreAudio (through miniaudio) on macOS, AVAudioEngine on tvOS.</summary>
internal static class AudioDevice
{
    public static Task<IAudioDevice> OpenAsync(int rate, int channels, AudioPullHandler pull)
    {
#if WINDOWS
        return WasapiAudioDevice.CreateAsync(rate, channels, pull);
#elif __TVOS__
        return Task.FromResult<IAudioDevice>(new AvAudioEngineDevice(rate, channels, pull));
#else
        return Task.FromResult<IAudioDevice>(new MiniAudioDevice(rate, channels, pull));
#endif
    }
}
