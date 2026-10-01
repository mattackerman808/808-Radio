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
        // one instance: a second copy couldn't open the dongle anyway
        using var mutex = new Mutex(true, "808Radio-single-instance", out bool first);
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
        var controller = new RadioController(AppSettings.Load()) { ReplayDirectory = replay };
        Application.Run(new MainForm(controller));
    }
}
