using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Radio808.Shared;

namespace Radio808.Avalonia;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    /// <summary>Unhandled UI-thread errors, after logging (the main window shows them on the display).</summary>
    public static Action<Exception>? OnError;

    public override void OnFrameworkInitializationCompleted()
    {
        // Safety net: anything unexpected on the UI thread is logged and shown on the display, not a crash
        global::Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Write(e.Exception);
            e.Handled = true;
            try { OnError?.Invoke(e.Exception); } catch { }
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args is { Length: >= 7 } m && m[0] == "--mapsnap")
            {
                // --mapsnap <radar.png> N W S E <out.png>: the weather map window from a saved image, rendered to a file
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var bounds = new Radio808.Core.Hd.MapBounds(double.Parse(m[2], inv), double.Parse(m[3], inv), double.Parse(m[4], inv), double.Parse(m[5], inv));
                var win = new Map.MapWindow("Weather map test", new global::Avalonia.Media.Imaging.Bitmap(m[1]), bounds, DateTime.Now);
                win.Opened += async (_, _) =>
                {
                    try { await win.SnapshotTo(m[6], m.Length > 7 ? double.Parse(m[7], inv) : 15); }
                    catch (Exception ex) { Console.WriteLine("map snapshot failed: " + ex); }
                    win.Close();
                };
                desktop.MainWindow = win;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                base.OnFrameworkInitializationCompleted();
                return;
            }
            string? replay = desktop.Args is { Length: >= 2 } a && a[0] == "--replay" ? a[1] : null;
            // --snapshot <file.png> [seconds]: run the radio for a few seconds, render the faceplate to the file, exit
            // (for development: a look at the drawing without a screen)
            string? snapshot = desktop.Args is { Length: >= 2 } s && s[0] == "--snapshot" ? s[1] : null;
            if (snapshot != null) AppSettings.ReadOnly = true;
            var controller = new RadioController(AppSettings.Load()) { ReplayDirectory = replay };
            var window = new MainWindow(controller);
            if (snapshot != null)
                window.SnapshotTo(snapshot, desktop.Args!.Length > 2 ? double.Parse(desktop.Args[2], System.Globalization.CultureInfo.InvariantCulture) : 6,
                    desktop.Args.Length > 3 && desktop.Args[3] == "open");
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
