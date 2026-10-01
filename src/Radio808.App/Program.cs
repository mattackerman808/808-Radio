using System;
using System.Threading;
using System.Windows.Forms;

namespace Radio808.App;

internal static class Program
{
    /// <summary>Unhandled UI-thread errors, after logging (the main form shows them on the display).</summary>
    public static Action<Exception>? OnError;

    [STAThread]
    private static void Main(string[] args)
    {
        string? replay = args.Length >= 2 && args[0] == "--replay" ? args[1] : null;
        // --bench <recordings> <seconds> <report.txt> [width] [fps]: times painting with the instrument panel open, playing recordings
        // muted, without touching the settings file, then exits (for development; runs beside a normal copy)
        bool bench = args.Length >= 4 && args[0] == "--bench";
        if (bench)
        {
            replay = args[1];
            MainForm.Bench = (double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3],
                args.Length > 4 ? int.Parse(args[4]) : 0);   // optional window width in pixels
            AppSettings.ReadOnly = true;
        }
        // one instance: a second copy couldn't open the dongle anyway
        using var mutex = new Mutex(true, bench ? "808Radio-bench" : "808Radio-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("808 Radio is already running.", "808 Radio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Safety net: anything unexpected on the UI thread is logged and shown on the display, not a crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Write(e.Exception);
            OnError?.Invoke(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write(e.ExceptionObject?.ToString() ?? "unknown error");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = AppSettings.Load();
        if (bench)
        {
            settings.Muted = false;
            settings.Volume = 0;   // silent but not muted: the faceplate analyzer goes blank when muted
            settings.FrequencyMhz = 97.3;
            if (args.Length > 5) settings.PanelFps = int.Parse(args[5]);   // optional frame rate setting (0 = display)
        }
        var controller = new RadioController(settings) { ReplayDirectory = replay };
        // WinForms double buffering keeps its back buffer only up to MaximumBuffer (225 x 96 by default); a bigger window
        // got a new full-size buffer allocated, cleared and freed on every paint (~4-5 ms per frame on a 4K screen)
        System.Drawing.BufferedGraphicsManager.Current.MaximumBuffer = new System.Drawing.Size(8192, 8192);
        Application.Run(new MainForm(controller));
    }
}
