using System;
using System.Runtime.InteropServices;

namespace Radio808.Core.Audio;

/// <summary>
/// The default output device through <c>libr808audio</c> (native/mac/r808audio.c: a thin shim over miniaudio, which
/// uses CoreAudio on macOS and follows the system default device). The shim pulls frames on the audio thread.
/// </summary>
internal sealed unsafe class MiniAudioDevice : IAudioDevice
{
    private const string Lib = "libr808audio";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReadCallback(float* output, uint frames, IntPtr user);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int r808audio_open(uint rate, uint channels, uint periodMs, ReadCallback cb, IntPtr user, out IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr r808audio_device_name(IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void r808audio_close(IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr r808audio_version();

    private readonly AudioPullHandler _pull;
    private readonly ReadCallback _callback;   // kept alive while the native side holds it
    private readonly int _channels;
    private IntPtr _device;

    public MiniAudioDevice(int rate, int channels, AudioPullHandler pull)
    {
        _pull = pull;
        _channels = channels;
        _callback = OnRead;
        int r = r808audio_open((uint)rate, (uint)channels, 20, _callback, IntPtr.Zero, out _device);
        if (r != 0 || _device == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open the audio output (miniaudio error {r}).");
    }

    public static string LibraryVersion => Marshal.PtrToStringUTF8(r808audio_version()) ?? "";

    public string DeviceName => _device == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(r808audio_device_name(_device)) ?? "";

    private void OnRead(float* output, uint frames, IntPtr user)
    {
        try { _pull(new Span<float>(output, (int)frames * _channels)); }
        catch (Exception) { /* never unwind into native code */ }
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        r808audio_close(_device);
        _device = IntPtr.Zero;
    }
}
