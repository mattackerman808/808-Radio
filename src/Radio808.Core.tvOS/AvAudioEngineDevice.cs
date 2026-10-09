using System;
using System.Linq;
using AudioToolbox;
using AVFoundation;
using Foundation;

namespace Radio808.Core.Audio;

/// <summary>
/// The Apple TV's audio output through AVAudioEngine, which goes wherever tvOS routes it (the TV over HDMI, HomePods,
/// AirPods). A source node pulls interleaved float32 frames from the handler on the render thread.
/// </summary>
public sealed class AvAudioEngineDevice : IAudioDevice
{
    private readonly AudioPullHandler _pull;
    private readonly int _channels;
    private readonly AVAudioEngine _engine;
    private readonly AVAudioSourceNode _source;
    private readonly AVAudioSourceNodeRenderHandler3 _render;   // kept alive while the node holds it
    private float[] _tmp = Array.Empty<float>();

    public AvAudioEngineDevice(int rate, int channels, AudioPullHandler pull)
    {
        _pull = pull;
        _channels = channels;
        var session = AVAudioSession.SharedInstance();
        // "long-form" routing: the app's audio can be sent to AirPlay 2 speakers (HomePods) on their own, picked with
        // an AVRoutePickerView in the app, while the TV's other sound stays on HDMI
        if (!session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default, AVAudioSessionRouteSharingPolicy.LongForm, 0, out var catErr))
            session.SetCategory(AVAudioSessionCategory.Playback);
        session.SetActive(true);

        // the mixer takes only non-interleaved float32 (interleaved: -10868 kAudioUnitErr_FormatNotSupported)
        var format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, rate, (uint)channels, interleaved: false);
        _render = Render;
        _source = new AVAudioSourceNode(format, _render);
        _engine = new AVAudioEngine();
        _engine.AttachNode(_source);
        try
        {
            _engine.Connect(_source, _engine.MainMixerNode, format);
        }
        catch (Exception ex)
        {
            // seen on the simulator right after launch (kAudioUnitErr_FormatNotSupported, then fine seconds later)
            string outFmt, sessionInfo;
            try { outFmt = _engine.OutputNode.GetBusOutputFormat(0).Description; } catch (Exception e) { outFmt = e.Message; }
            try { var ss = AVAudioSession.SharedInstance(); sessionInfo = $"{ss.Category} {ss.SampleRate} Hz io {ss.IOBufferDuration * 1000:0} ms"; } catch (Exception e) { sessionInfo = e.Message; }
            throw new InvalidOperationException($"Could not connect the audio source ({ex.Message.Split('\n')[0]}); format {format.Description}; output {outFmt}; session {sessionInfo}", ex);
        }
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var err))
            throw new InvalidOperationException($"Could not start the audio engine: {err?.LocalizedDescription}");
        // the output route changed (AirPlay picked or dropped, HDMI replugged): the engine stops itself and must be restarted
        _configChange = AVAudioEngine.Notifications.ObserveConfigurationChange(_engine, (_, _) =>
        {
            try { if (!_engine.Running) _engine.StartAndReturnError(out _); } catch (Exception) { }
            Restarted?.Invoke();
        });
    }

    private NSObject? _configChange;

    public event Action? Restarted;

    /// <summary>The route the sound is going out on: "AirPlay" for a HomePod or other AirPlay speaker, "HDMI" for the TV, …</summary>
    public static string OutputPortType
    {
        get
        {
            try { return AVAudioSession.SharedInstance().CurrentRoute?.Outputs?.FirstOrDefault()?.PortType ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    public string DeviceName
    {
        get
        {
            try { return AVAudioSession.SharedInstance().CurrentRoute?.Outputs?.FirstOrDefault()?.PortName ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    private unsafe int Render(ref bool isSilence, ref AudioTimeStamp timestamp, uint frameCount, AudioBuffers output)
    {
        int frames = (int)frameCount;
        try
        {
            if (output.Count == 1)
            {
                // interleaved, as asked for
                var b = output[0];
                int floats = Math.Min(frames * _channels, b.DataByteSize / sizeof(float));
                _pull(new Span<float>((void*)b.Data, floats));
            }
            else
            {
                // one buffer per channel: pull interleaved, then split
                if (_tmp.Length < frames * _channels) _tmp = new float[frames * _channels];
                var mixed = _tmp.AsSpan(0, frames * _channels);
                _pull(mixed);
                for (int c = 0; c < output.Count && c < _channels; c++)
                {
                    var b = output[c];
                    var dst = new Span<float>((void*)b.Data, Math.Min(frames, b.DataByteSize / sizeof(float)));
                    for (int i = 0; i < dst.Length; i++) dst[i] = mixed[i * _channels + c];
                }
            }
        }
        catch (Exception) { /* never unwind into native code */ }
        isSilence = false;
        return 0;
    }

    public void Dispose()
    {
        _configChange?.Dispose();
        try { _engine.Stop(); } catch (Exception) { }
        _source.Dispose();
        _engine.Dispose();
    }
}
