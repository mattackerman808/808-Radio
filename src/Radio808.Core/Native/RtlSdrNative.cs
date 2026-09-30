using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Radio808.Core.Native;

/// <summary>P/Invoke for rtlsdr.dll (RTL-SDR Blog fork of librtlsdr).</summary>
internal static unsafe class RtlSdrNative
{
    private const string Lib = "rtlsdr";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ReadAsyncCallback(byte* buf, uint len, IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint rtlsdr_get_device_count();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rtlsdr_get_device_name(uint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_device_usb_strings(uint index, byte* manufact, byte* product, byte* serial);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_open(out IntPtr dev, uint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_close(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_center_freq(IntPtr dev, uint freq);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint rtlsdr_get_center_freq(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_freq_correction(IntPtr dev, int ppm);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_type(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_gains(IntPtr dev, int* gains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain(IntPtr dev, int gain);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_gain(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain_mode(IntPtr dev, int manual);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_sample_rate(IntPtr dev, uint rate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint rtlsdr_get_sample_rate(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_agc_mode(IntPtr dev, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_bias_tee(IntPtr dev, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_reset_buffer(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_read_async(IntPtr dev, ReadAsyncCallback cb, IntPtr ctx, uint bufNum, uint bufLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_cancel_async(IntPtr dev);

    public static string DeviceName(uint index) => Marshal.PtrToStringAnsi(rtlsdr_get_device_name(index)) ?? "";

    public static (string Manufacturer, string Product, string Serial) UsbStrings(uint index)
    {
        byte* m = stackalloc byte[256], p = stackalloc byte[256], s = stackalloc byte[256];
        if (rtlsdr_get_device_usb_strings(index, m, p, s) != 0) return ("", "", "");
        return (Str(m), Str(p), Str(s));
    }

    private static string Str(byte* p)
    {
        int n = 0;
        while (n < 256 && p[n] != 0) n++;
        return Encoding.ASCII.GetString(p, n);
    }
}
