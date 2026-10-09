using Radio808.Shared;

namespace Radio808.TV;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    /// <summary>The radio, created at launch; the scene (SceneDelegate) puts its screen in the window.</summary>
    public static RadioController Controller { get; private set; } = null!;

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var settings = AppSettings.Load();
        settings.UseRtlTcp = true;   // an Apple TV has no USB: the dongle is always on the network
        if (string.IsNullOrWhiteSpace(settings.RtlTcpAddress)) settings.RtlTcpAddress = "console.local";
        settings.Volume = 1;   // the TV and the speakers have the volume control; the radio runs at full level
        Controller = new RadioController(settings);
        application.IdleTimerDisabled = true;   // no screen saver while the radio plays
        return true;
    }
}
