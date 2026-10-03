using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Radio808.Core.Audio;

/// <summary>WASAPI shared mode on the default output device; playback follows Windows when the default changes.</summary>
internal sealed class WasapiAudioDevice : IAudioDevice
{
    private readonly WasapiPlayer _out;

    private WasapiAudioDevice(WasapiPlayer output) => _out = output;

    public static async Task<IAudioDevice> CreateAsync(int rate, int channels, AudioPullHandler pull)
    {
        var output = await new WasapiPlayerBuilder()
            .WithDefaultDeviceStreamRouting()
            .WithSharedMode()
            .WithLatency(40)
            .WithCategory(AudioStreamCategory.Media)
            .BuildAsync().ConfigureAwait(false);
        output.Init(new SampleToWaveProvider(new PullProvider(rate, channels, pull)));
        output.Play();
        return new WasapiAudioDevice(output);
    }

    public string DeviceName => _out.DeviceFriendlyName ?? "";

    public void Dispose() => _out.Dispose();

    private sealed class PullProvider : ISampleProvider
    {
        private readonly AudioPullHandler _pull;
        public PullProvider(int rate, int channels, AudioPullHandler pull)
        {
            _pull = pull;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(Span<float> buffer)
        {
            _pull(buffer);
            return buffer.Length;   // never end-of-stream
        }
    }
}
