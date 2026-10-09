using AVKit;
using Radio808.Core.Audio;
using Radio808.Shared;

namespace Radio808.TV;

/// <summary>
/// The faceplate on the TV: the display behind the glass, six preset keys, and two rows of keys the Siri Remote's
/// touch surface moves between. Play/pause on the remote mutes.
/// </summary>
public sealed class RadioViewController : UIViewController
{
    private readonly RadioController _c;
    private DisplayView _display = null!;
    private readonly List<KeyButton> _keys = new();
    private readonly PresetKey[] _presets = new PresetKey[AppSettings.PresetCount];
    private KeyButton _hdSeekKey = null!, _hdChKey = null!, _muteKey = null!;
    private AVRoutePickerView _picker = null!;
    private NSTimer? _timer;
    private int _ticks;
    private DateTime _lastActivity = DateTime.UtcNow;
    private VisualizerView? _saver;
    private SetupView? _setup;
    private readonly BonjourBrowser _servers = new();
    private UIView _panel = null!;
    private UIView? _lastFocused;

    public RadioViewController(RadioController controller)
    {
        _c = controller;
        _c.Changed += () => _display?.SetNeedsDisplay();
        _c.Message += m => _display?.Flash(m, 2.5);
    }

    private UIColor Lit => Theme.Lit(_c.Settings);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var v = View!;
        v.BackgroundColor = Theme.Body2;
        var bounds = v.Bounds;   // 1920 x 1080 points on an Apple TV
        v.AddSubview(new WoodView(bounds));
        _panel = new UIView(bounds);
        v.AddSubview(_panel);
        v = _panel;
        v.AddSubview(new BodyView(bounds));
        _display = new DisplayView(_c, BodyView.Glass);
        v.AddSubview(_display);
        v.AddSubview(new GlassOverlay(BodyView.Glass));

        // the presets
        const float keyW = 250, gap = 20, left = 80;
        for (int i = 0; i < _presets.Length; i++)
        {
            int idx = i;
            var k = new PresetKey(new CGRect(left + i * (keyW + gap), 640, keyW, 80), i)
            {
                Pressed = () =>
                {
                    if (idx >= _c.Settings.Presets.Count || _c.Settings.Presets[idx] == null) { _display.Flash($"HOLD {idx + 1} TO SAVE"); return; }
                    _c.RecallPreset(idx);
                },
                Held = () => StorePreset(idx),
                HeldLong = () => { _c.ClearPreset(idx); _display.Flash($"P{idx + 1} CLEARED", 2); },   // keep holding: clear it
            };
            _presets[i] = k;
            v.AddSubview(k);
        }

        // the keys
        float x = left;
        KeyButton Key(string? glyph, string label, Action pressed, float y, float w = keyW)
        {
            var k = new KeyButton(new CGRect(x, y, w, 90)) { Glyph = glyph, Label = label, Pressed = pressed };
            x += w + gap;
            _keys.Add(k);
            v.AddSubview(k);
            return k;
        }
        Key("◀◀", "SEEK", () => _c.Seek(-1), 760);
        Key("◀", "TUNE", () => _c.Step(-1), 760);
        Key("▶", "TUNE", () => _c.Step(+1), 760);
        Key("▶▶", "SEEK", () => _c.Seek(+1), 760);
        _hdChKey = Key("HD", "CH", _display.NextProgram, 760);
        _hdSeekKey = Key("HD", "SEEK", ToggleHdSeek, 760);
        x = left;
        Key(null, "DISP", _display.NextDisplay, 880);
        Key(null, "COLOR", NextColor, 880);
        _muteKey = Key(null, "MUTE", _c.ToggleMute, 880);
        Key(null, "SAVER", ShowSaver, 880);   // the visualizer, on demand
        Key(null, "SETUP", ShowSetup, 880);
        // AirPlay: our key, with Apple's route picker hidden inside it; pressing the key presses the picker's button
        _picker = new AVRoutePickerView(new CGRect(0, 0, 20, 20)) { Alpha = 0.002f, UserInteractionEnabled = false };
        var airplay = Key(null, "", PressAirPlay, 880);
        airplay.Icon = UIImage.GetSystemImage("airplayaudio");
        airplay.AddSubview(_picker);

        ApplyLit();
        KeyButton.Activity += Touch;
        _servers.Changed += () => AppLog.Write("servers on the network: " + string.Join(", ", _servers.Servers.Select(f => $"{f.Name} ({f.Address})")));
        _timer = NSTimer.CreateRepeatingScheduledTimer(0.05, _ => Tick());
        // for screenshots from the shell: launch with --setup or --saver to open one right away
        var args = NSProcessInfo.ProcessInfo.Arguments;
        if (args.Contains("--setup")) ShowSetup();
        else if (args.Contains("--saver")) _lastActivity = DateTime.MinValue;
        _ = _c.StartAsync();
    }

    private void Tick()
    {
        _ticks++;
        if (_saver == null)
        {
            _display.Tick();
            RefreshKeys();
            if (_setup != null && _ticks % 5 == 0) _setup.Refresh();
            int minutes = _c.Settings.SaverMinutes;
            if (minutes > 0 && _setup == null && DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(minutes) && _c.Engine != null) ShowSaver();
        }
        if (_ticks % 20 == 0) _c.PpmTick();
        if (_ticks % 100 == 0 && _c.Engine is { } eng)
            AppLog.Write($"dsp {eng.DspLoad * 100:0}% buffer {eng.Player.BufferedMs:0} ms underruns {eng.Player.Underruns} drift {eng.Player.DriftPpm:+0;-0} ppm hd {(eng.Hd.Synced ? "synced" : "-")} out {eng.Player.DeviceName}/{AvAudioEngineDevice.OutputPortType} ber {eng.Hd.BerAvg:0.000}{(eng.Hd.Error != null ? " hd error " + eng.Hd.Error : "")}");
    }

    private void RefreshKeys()
    {
        var s = _c.Settings;
        int current = _c.CurrentPreset;
        for (int i = 0; i < _presets.Length; i++)
        {
            var p = i < s.Presets.Count ? s.Presets[i] : null;
            var k = _presets[i];
            bool changed = k.Mhz != p?.Mhz || k.Current != (i == current) || k.Program != (p?.Program ?? 0);
            k.Mhz = p?.Mhz; k.Current = i == current; k.Program = p?.Program ?? 0;
            if (changed) k.SetNeedsDisplay();
        }
        // HD CH lights only when the station has a second program to switch to
        var hd = _c.Engine?.Hd;
        bool hdCh = hd != null && hd.Synced && hd.Programs.Count > 1;
        if (_hdChKey.Active != hdCh) { _hdChKey.Active = hdCh; _hdChKey.SetNeedsDisplay(); }
        bool hdSeek = s.SeekHd, muted = s.Muted;
        if (_hdSeekKey.Active != hdSeek) { _hdSeekKey.Active = hdSeek; _hdSeekKey.SetNeedsDisplay(); }
        string muteLabel = muted ? "UNMUTE" : "MUTE";
        if (_muteKey.Label != muteLabel) { _muteKey.Label = muteLabel; _muteKey.SetNeedsDisplay(); }
    }

    private void ApplyLit()
    {
        var lit = Lit;
        foreach (var k in _keys) { k.Lit = lit; k.SetNeedsDisplay(); }
        foreach (var k in _presets) { k.Lit = lit; k.SetNeedsDisplay(); }
        _display.SetNeedsDisplay();
    }

    private void PressAirPlay()
    {
        var button = FindButton(_picker);
        if (button == null) { _display.Flash("NO AIRPLAY PICKER"); AppLog.Write("AVRoutePickerView has no button inside"); return; }
        button.SendActionForControlEvents(UIControlEvent.PrimaryActionTriggered);
    }

    private static UIButton? FindButton(UIView v)
    {
        if (v is UIButton b) return b;
        foreach (var sub in v.Subviews) if (FindButton(sub) is { } found) return found;
        return null;
    }

    private void NextColor()
    {
        _c.Settings.Illumination = (_c.Settings.Illumination + 1) % Theme.Illuminations.Length;
        _c.Settings.Save();
        ApplyLit();
        _display.Flash(Theme.Illuminations[_c.Settings.Illumination].Name, 1.2);
    }

    private void ToggleHdSeek()
    {
        _c.SetSeekHd(!_c.Settings.SeekHd);
        _display.Flash(_c.Settings.SeekHd ? "HD SEEK ON" : "HD SEEK OFF", 1.2);
    }

    private void StorePreset(int i)
    {
        var eng = _c.Engine;
        string? name = null;
        if (eng != null)
        {
            name = eng.Hd.Synced ? eng.Hd.StationName : null;
            name ??= eng.Receiver.Rds.CallSign ?? eng.Receiver.Rds.ProgramService;
        }
        _c.StorePreset(i, name);
        _display.Flash($"P{i + 1} SAVED", 2.5);
    }

    // ---- the screen saver

    private void Touch() => _lastActivity = DateTime.UtcNow;

    public override void DidUpdateFocus(UIFocusUpdateContext context, UIFocusAnimationCoordinator coordinator)
    {
        base.DidUpdateFocus(context, coordinator);
        Touch();
        if (context.NextFocusedView is { } v && v != _saver && v is not SetupRow) _lastFocused = v;
    }

    public override IUIFocusEnvironment[] PreferredFocusEnvironments
        => _saver != null ? new IUIFocusEnvironment[] { _saver }
         : _setup != null ? new IUIFocusEnvironment[] { _setup.FirstRow }
         : _lastFocused != null ? new IUIFocusEnvironment[] { _lastFocused } : base.PreferredFocusEnvironments;

    // ---- the setup screen

    private void ShowSetup()
    {
        if (_setup != null) return;
        var setup = new SetupView(_c, View!.Bounds) { Lit = Lit, Closed = HideSetup, ServerRequested = AskServer };
        _setup = setup;
        _panel.AddSubview(setup);
        setup.Refresh();
        SetNeedsFocusUpdate();
        UpdateFocusIfNeeded();
    }

    private void HideSetup()
    {
        var setup = _setup;
        if (setup == null) return;
        _setup = null;
        setup.RemoveFromSuperview();
        setup.Dispose();
        ApplyLit();   // the display follows whatever changed
        SetNeedsFocusUpdate();
        UpdateFocusIfNeeded();
    }

    private void ShowSaver()
    {
        var saver = new VisualizerView(_c, View!.Bounds) { Dismissed = HideSaver };
        if (!saver.Setup()) { _lastActivity = DateTime.UtcNow; return; }   // no GPU shader: no saver (logged)
        _saver = saver;
        View.AddSubview(saver);   // over the wood and the panel
        _display.Hidden = true;
        SetNeedsFocusUpdate();
        UpdateFocusIfNeeded();
    }

    private void HideSaver()
    {
        var saver = _saver;
        if (saver == null) return;
        _saver = null;
        Touch();
        saver.Paused = true;
        saver.RemoveFromSuperview();
        saver.Dispose();
        _display.Hidden = false;
        SetNeedsFocusUpdate();
        UpdateFocusIfNeeded();
    }

    // ---- the Siri Remote's own buttons

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        Touch();
        foreach (UIPress p in presses)
            if (p.Type == UIPressType.PlayPause) { _c.ToggleMute(); return; }
        base.PressesBegan(presses, evt);
    }

    // ---- the rtl_tcp server

    /// <summary>The servers found on the network (a Pi, or a Mac or PC in server mode) to pick from, or an address to type.</summary>
    private void AskServer()
    {
        var found = _servers.Servers;
        var sheet = UIAlertController.Create("Dongle server", found.Count > 0 ? "Found on the network" : "None found on the network yet", UIAlertControllerStyle.ActionSheet);
        foreach (var f in found)
        {
            bool current = string.Equals(_c.Settings.RtlTcpAddress, f.Address, StringComparison.OrdinalIgnoreCase);
            sheet.AddAction(UIAlertAction.Create(current ? $"{f.Name}  ✓" : f.Name, UIAlertActionStyle.Default, _ => _c.SetSource(f.Address)));
        }
        sheet.AddAction(UIAlertAction.Create("Enter an address…", UIAlertActionStyle.Default, _ => AskServerAddress()));
        sheet.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        PresentViewController(sheet, true, null);
    }

    private void AskServerAddress()
    {
        var alert = UIAlertController.Create("rtl_tcp server", "host or host:port of the machine with the dongle", UIAlertControllerStyle.Alert);
        alert.AddTextField(tf => { tf.Text = _c.Settings.RtlTcpAddress; tf.KeyboardType = UIKeyboardType.Url; tf.AutocapitalizationType = UITextAutocapitalizationType.None; });
        alert.AddAction(UIAlertAction.Create("Connect", UIAlertActionStyle.Default, _ =>
        {
            string? addr = alert.TextFields?[0].Text?.Trim();
            if (!string.IsNullOrEmpty(addr)) _c.SetSource(addr);
        }));
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        PresentViewController(alert, true, null);
    }
}
