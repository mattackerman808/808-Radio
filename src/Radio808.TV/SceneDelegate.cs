namespace Radio808.TV;

/// <summary>The one window scene (tvOS 26's SDK requires the scene life cycle): puts the radio screen in it.</summary>
[Register("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window = new UIWindow(windowScene) { RootViewController = new RadioViewController(AppDelegate.Controller) };
        Window.MakeKeyAndVisible();
    }
}
