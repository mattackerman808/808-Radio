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
}

/// <summary>Opens the platform's audio output: WASAPI on Windows, CoreAudio (through miniaudio) on macOS.</summary>
internal static class AudioDevice
{
    public static Task<IAudioDevice> OpenAsync(int rate, int channels, AudioPullHandler pull)
    {
#if WINDOWS
        return WasapiAudioDevice.CreateAsync(rate, channels, pull);
#else
        return Task.FromResult<IAudioDevice>(new MiniAudioDevice(rate, channels, pull));
#endif
    }
}
