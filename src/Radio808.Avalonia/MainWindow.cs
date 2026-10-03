using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Radio808.Shared;

namespace Radio808.Avalonia;

/// <summary>The frameless window that is the faceplate, with its context menu and keyboard shortcuts.</summary>
internal sealed class MainWindow : Window
{
    private const double DesignW = FaceplateControl.W, DesignH = FaceplateControl.ClosedH;
    private readonly RadioController _c;
    private readonly FaceplateControl _view;
    private int _ticks;
    private bool _sizing;

    public MainWindow(RadioController controller)
    {
        _c = controller;
        Title = "808 Radio";
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = true;
        MinWidth = 620;
        MinHeight = 620 * DesignH / DesignW;
        Topmost = _c.Settings.AlwaysOnTop;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 1000; Height = DesignH;

        var b = _c.Settings.WindowBounds;
        if (b is { Length: 4 } && b[2] > 2.2 * b[3])
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(b[0], b[1]);
            Width = b[2]; Height = b[2] * DesignH / DesignW;
        }

        _view = new FaceplateControl(_c);
        Content = _view;
        _view.MenuRequested += e => BuildMenu().Open(_view);
        _view.DragRequested += e => BeginMoveDrag(e);
        _view.ResizeRequested += (edge, e) => BeginResizeDrag(edge, e);
        _view.MinimizeRequested += () => WindowState = WindowState.Minimized;
        _view.CloseRequested += Close;
        _view.ArtRequested += ShowArt;
        _view.OpenLayout += _ => FitHeight();   // the window grows downward when the faceplate opens, shrinks back after
        _view.MapRequested += ShowMap;
        App.OnError = _ => _view.Flash("ERROR - SEE LOG", 2.5);
        _view.Ticked += () =>
        {
            UpdateTitle();
            UpdateTraffic();
            if (++_ticks % 10 == 0) _c.PpmTick();
        };
        // the window keeps the faceplate's proportions while it's resized
        SizeChanged += (_, e) =>
        {
            if (_sizing) return;
            double h = Math.Round(e.NewSize.Width * _view.DesignHeight / DesignW);
            if (Math.Abs(h - e.NewSize.Height) > 1) { _sizing = true; Height = h; _sizing = false; }
        };
        Opened += async (_, _) =>
        {
            _view.Start();
            _view.Focus();
            await _c.StartAsync();
        };
        Closing += (_, _) =>
        {
            _view.Stop();
            _c.Settings.WindowBounds = new[] { Position.X, Position.Y, (int)Width, (int)Height };
            _c.Dispose();
        };
    }

    /// <summary>Development: after <paramref name="seconds"/> of running, saves the faceplate as a PNG (2x) and quits.</summary>
    public void SnapshotTo(string path, double seconds, bool open)
    {
        Opened += async (_, _) =>
        {
            if (open) { await Task.Delay(500); _view.ToggleOpen(); }
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try
            {
                var size = new PixelSize((int)(_view.Bounds.Width * 2), (int)(_view.Bounds.Height * 2));
                using var rtb = new RenderTargetBitmap(size, new Vector(192, 192));
                rtb.Render(_view);
                rtb.Save(path);
                Console.WriteLine($"snapshot: {path} {size.Width}x{size.Height}");
            }
            catch (Exception ex) { Console.WriteLine("snapshot failed: " + ex); }
            Close();
        };
    }

    private void FitHeight()
    {
        _sizing = true;
        Height = Math.Round(Width * _view.DesignHeight / DesignW);
        _sizing = false;
    }

    private string _title = "";

    private void UpdateTitle()
    {
        var eng = _c.Engine;
        string? name = null;
        if (eng != null)
        {
            var hd = eng.Hd;
            name = hd.Synced ? hd.StationName : null;
            name ??= eng.Receiver.Rds.CallSign;
        }
        string t = $"{_c.Frequency / 1e6:0.0} {name} — 808 Radio".Replace("  ", " ");
        if (t != _title) Title = _title = t;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left: if (ctrl) _c.Seek(-1); else _c.Step(-1); return;
            case Key.Right: if (ctrl) _c.Seek(1); else _c.Step(1); return;
            case Key.MediaPreviousTrack: _c.Seek(-1); return;
            case Key.MediaNextTrack: _c.Seek(1); return;
            case Key.Up: _c.SetVolume(_c.Settings.Volume + 0.025f); _view.Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1); return;
            case Key.Down: _c.SetVolume(_c.Settings.Volume - 0.025f); _view.Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1); return;
            case Key.M: _c.ToggleMute(); _view.Flash(_c.Settings.Muted ? "MUTE" : "MUTE OFF", 1); return;
            case Key.A:
                _c.SetForceAnalog(!_c.Settings.ForceAnalog);
                _view.Flash(_c.Settings.ForceAnalog ? "SOURCE FM" : "SOURCE HD");
                return;
            case Key.H: _view.NextProgram(); return;
            case Key.C: _view.CycleColor(); return;
            case Key.D: _view.NextDisplay(); return;
            case Key.O: _view.ToggleOpen(); return;
            case Key.Q when ctrl: Close(); return;
            case >= Key.D1 and <= Key.D6:
                int p = e.Key - Key.D1;
                if (ctrl)
                {
                    var e2 = _c.Engine;
                    string? n = e2 == null ? null : e2.Hd.Synced ? e2.Hd.StationName : null;
                    n ??= e2?.Receiver.Rds.CallSign;
                    _c.StorePreset(p, n);
                    _view.Flash($"P{p + 1} SAVED");
                }
                else _c.RecallPreset(p);
                return;
        }
        e.Handled = false;
        base.OnKeyDown(e);
    }

    // ------------------------------------------------------------------ the right-click menu

    private static MenuItem Item(string header, Action click, bool? isChecked = null)
    {
        var m = new MenuItem { Header = header };
        if (isChecked != null) { m.ToggleType = MenuItemToggleType.CheckBox; m.IsChecked = isChecked.Value; }
        m.Click += (_, _) => click();
        return m;
    }

    private void FillSourceMenu(MenuItem src)
    {
        var s = _c.Settings;
        src.Items.Clear();
        src.Items.Add(Item("USB dongle", () => { if (s.UseRtlTcp) _c.SetSource(null); }, !s.UseRtlTcp));
        bool savedListed = false;
        foreach (var server in _c.Discovered)
        {
            string addr = server.ConnectAddress;
            bool on = s.UseRtlTcp && string.Equals(s.RtlTcpAddress, addr, StringComparison.OrdinalIgnoreCase);
            savedListed |= string.Equals(s.RtlTcpAddress, addr, StringComparison.OrdinalIgnoreCase);
            src.Items.Add(Item($"{server.Name}  ({addr})", () => { if (!on) _c.SetSource(addr); }, on));
        }
        if (!savedListed && !string.IsNullOrWhiteSpace(s.RtlTcpAddress))
            src.Items.Add(Item($"Network: {s.RtlTcpAddress}", () => { if (!s.UseRtlTcp) _c.SetSource(s.RtlTcpAddress); }, s.UseRtlTcp));
        if (_c.Discovering) src.Items.Add(new MenuItem { Header = "Searching the network…", IsEnabled = false });
        src.Items.Add(new Separator());
        src.Items.Add(Item("Network dongle (rtl_tcp)…", async () =>
        {
            var addr = await AskAddress(s.RtlTcpAddress ?? "");
            if (addr != null) _c.SetSource(addr);
        }));
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var colors = new MenuItem { Header = "Illumination" };
        for (int i = 0; i < FaceplateControl.Illuminations.Length; i++)
        {
            int idx = i;
            colors.Items.Add(Item(FaceplateControl.Illuminations[i].Name, () => { _c.Settings.Illumination = idx; _c.Settings.Save(); _view.InvalidateVisual(); }, _c.Settings.Illumination == i));
        }
        m.Items.Add(colors);
        var disp = new MenuItem { Header = "Display" };
        for (int i = 0; i < FaceplateControl.DisplayModes.Length; i++)
        {
            int idx = i;
            string name = FaceplateControl.DisplayModes[i].Name;
            name = name[0] + name[1..].ToLowerInvariant();
            disp.Items.Add(Item(name, () => _view.SetDisplay(idx), _c.Settings.DisplayMode == i));
        }
        m.Items.Add(disp);
        m.Items.Add(Item(_view.IsOpen ? "Close faceplate" : "Open faceplate (signal details)", _view.ToggleOpen));
        m.Items.Add(new Separator());
        var src = new MenuItem { Header = "Source" };
        FillSourceMenu(src);
        void Refresh() => FillSourceMenu(src);
        _c.DiscoveryChanged += Refresh;
        m.Closed += (_, _) => { _c.DiscoveryChanged -= Refresh; _c.Settings.Save(); };
        _ = _c.DiscoverAsync();
        m.Items.Add(src);
        m.Items.Add(Item("Auto HD", () => _c.SetForceAnalog(!_c.Settings.ForceAnalog), !_c.Settings.ForceAnalog));
        m.Items.Add(Item("Multipath equalizer", () => _c.SetEqualizer(!_c.Settings.Equalizer), _c.Settings.Equalizer));
        m.Items.Add(Item("Force mono", () => _c.SetForceMono(!_c.Settings.ForceMono), _c.Settings.ForceMono));
        var gain = new MenuItem { Header = "Tuner gain" };
        gain.Items.Add(Item("Automatic (peak each station)", () => _c.SetGain(null), _c.Settings.AutoGain));
        gain.Items.Add(new Separator());
        foreach (double gdb in new[] { 8.7, 12.5, 16.6, 22.9, 29.7, 37.2, 44.5 })
        {
            double v = gdb;
            gain.Items.Add(Item($"Fixed {v} dB", () => _c.SetGain(v), !_c.Settings.AutoGain && _c.Settings.GainDb == v));
        }
        m.Items.Add(gain);
        var ppm = new MenuItem { Header = $"Frequency correction ({_c.Settings.Ppm:+0;-0;0} ppm)" };
        ppm.Items.Add(Item("Automatic (measured from FM stations)", () => { _c.Settings.AutoPpm = !_c.Settings.AutoPpm; _c.Settings.Save(); }, _c.Settings.AutoPpm));
        ppm.Items.Add(Item("Calibrate now on this station", _c.CalibratePpmNow));
        ppm.Items.Add(new Separator());
        ppm.Items.Add(Item("+1 ppm", () => _c.SetPpm(_c.Settings.Ppm + 1)));
        ppm.Items.Add(Item("−1 ppm", () => _c.SetPpm(_c.Settings.Ppm - 1)));
        ppm.Items.Add(Item("Reset to 0", () => _c.SetPpm(0)));
        m.Items.Add(ppm);
        m.Items.Add(Item("Antenna power (bias-tee) — powered antennas/LNAs only", async () =>
        {
            if (!_c.Settings.BiasTee && !await Confirm("Antenna power",
                    "This puts 4.5 V on the antenna connector, for powered antennas and LNAs.\n\n" +
                    "Don't enable it with a plain antenna that shorts the connector to ground, or with equipment that " +
                    "can't take DC on its input.\n\nTurn antenna power on?")) return;
            _c.SetBiasTee(!_c.Settings.BiasTee);
        }, _c.Settings.BiasTee));
        m.Items.Add(new Separator());
        var fpsMenu = new MenuItem { Header = "Panel frame rate" };
        foreach (int fps in AppSettings.PanelFpsChoices)
        {
            int v = fps;
            fpsMenu.Items.Add(Item(fps switch { 0 => "Display refresh rate (smoothest)", 30 => "30 fps (lightest)", _ => $"{fps} fps" },
                () => { _c.Settings.PanelFps = v; _c.Settings.Save(); _view.StartFrames(); }, _c.Settings.PanelFps == fps));
        }
        m.Items.Add(fpsMenu);
        m.Items.Add(Item("Album art on the display (off: spectrum analyzer)", () => { _c.Settings.ShowAlbumArt = !_c.Settings.ShowAlbumArt; _c.Settings.Save(); _view.InvalidateVisual(); }, _c.Settings.ShowAlbumArt));
        m.Items.Add(Item("Always on top", () => { _c.Settings.AlwaysOnTop = !_c.Settings.AlwaysOnTop; Topmost = _c.Settings.AlwaysOnTop; }, _c.Settings.AlwaysOnTop));
        m.Items.Add(Item("Minimize", () => WindowState = WindowState.Minimized));
        m.Items.Add(Item("Quit", Close));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Keyboard shortcuts", () => _ = Info("Keyboard shortcuts",
            "← / →\tstep one channel\n⌘+← / →\tseek (also media keys)\n1-6\tpreset (⌘+1-6 or hold to save)\n" +
            "↑ / ↓\tvolume\nM\tmute\nA\tHD / analog\nH\tnext HD program\nD\tdisplay mode\nC\tillumination color\n\n" +
            "Mouse: wheel over the knob = volume, over the display = tune. Click the knob to mute.")));
        return m;
    }

    // ------------------------------------------------------------------ small dialogs

    private Window Dialog(string title, Control content, double width = 460)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Thickness(18), Child = content },
        };
        return w;
    }

    private async Task Info(string title, string text)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        var w = Dialog(title, new StackPanel { Spacing = 14, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ok } });
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
    }

    private async Task<bool> Confirm(string title, string text)
    {
        bool yes = false;
        var yesB = new Button { Content = "Yes", MinWidth = 80 };
        var noB = new Button { Content = "No", MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { noB, yesB } };
        var w = Dialog(title, new StackPanel { Spacing = 14, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, buttons } });
        yesB.Click += (_, _) => { yes = true; w.Close(); };
        noB.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
        return yes;
    }

    /// <summary>Asks for an rtl_tcp server address. Null if cancelled.</summary>
    private async Task<string?> AskAddress(string current)
    {
        string? result = null;
        var box = new TextBox { Text = current, PlaceholderText = "raspberrypi.local or 192.168.1.50[:port]" };
        var okB = new Button { Content = "Connect", MinWidth = 90, IsDefault = true };
        var cancelB = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancelB, okB } };
        var w = Dialog("Network dongle", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Address of the computer running rtl_tcp with the dongle, e.g. a Raspberry Pi: a host name or IP " +
                    "address, with :port if it isn't 1234. The setup script in the 808 Radio repository (pi/install-rtl-tcp.sh) installs and starts rtl_tcp." },
                box, buttons,
            },
        });
        okB.Click += (_, _) => { result = box.Text?.Trim(); w.Close(); };
        cancelB.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ------------------------------------------------------------------ art and maps

    private ArtPopup? _artWindow;

    /// <summary>The display's album art / logo, popped out bigger from its square, with what's playing under it.</summary>
    private void ShowArt(Bitmap art, Rect square)
    {
        _artWindow?.Close();
        var eng = _c.Engine;
        var hd = eng?.Hd;
        string song = string.Join(" — ", new[] { hd?.Title, hd?.Artist }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string station = string.Join("  ·  ", new[]
        {
            hd?.StationName, $"{_c.Frequency / 1e6:0.0} MHz" + (eng != null ? $" HD{eng.Program + 1}" : ""), hd?.Album,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var lit = FaceplateControl.Illuminations[Math.Clamp(_c.Settings.Illumination, 0, FaceplateControl.Illuminations.Length - 1)].Color;
        // the square on screen (design coordinates go through the faceplate's render transform)
        var tl = _view.PointToScreen(square.TopLeft);
        var br = _view.PointToScreen(square.BottomRight);
        var from = new PixelRect(tl, br);
        _artWindow = new ArtPopup(art, from, Screens.ScreenFromPoint(tl)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080), RenderScaling,
            song.Length > 0 ? song : hd?.Slogan ?? "", station, lit) { Topmost = Topmost };
        _artWindow.Closed += (_, _) => _artWindow = null;
        _artWindow.Show(this);
    }

    private Map.MapWindow? _weather;

    private void ShowMap(string which)
    {
        var hd = _c.Engine?.Hd;
        if (hd == null) return;
        Control? content = null;
        try
        {
            if (which == "weather" && hd.WeatherMap != null)
            {
                using var ms = new MemoryStream(hd.WeatherMap);
                var overlay = new Bitmap(ms);
                if (hd.WeatherBounds is { } wb)
                {
                    // the radar over a street map of the same box
                    string title = $"Weather — {hd.StationName}";
                    if (_weather != null) { _weather.Update(overlay, hd.WeatherTime); _weather.Activate(); return; }
                    _weather = new Map.MapWindow(title, overlay, wb, hd.WeatherTime);
                    _weather.Closed += (_, _) => _weather = null;
                    _weather.Show(this);
                    return;
                }
                content = new Image { Source = overlay, Stretch = Stretch.Uniform };
            }
            else if (which == "traffic")
            {
                // the 3x3 mosaic fills in as tiles arrive (a station cycles through them over a few minutes)
                if (_traffic != null) { _traffic.Activate(); return; }
                // a fixed 600 x 600 of 200-px tiles, scaled uniformly to the window: no gaps between tiles
                var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 600, Height = 600 };
                for (int i = 0; i < 9; i++) grid.Children.Add(new Image { Stretch = Stretch.Fill });
                _trafficTiles = null;
                _trafficGrid = grid;
                UpdateTraffic();
                _traffic = new Window
                {
                    Title = $"Traffic — {hd.StationName}",
                    Width = 800, Height = 800,
                    Background = Brushes.Black,
                    Content = new Viewbox { Child = grid, Stretch = Stretch.Uniform },
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                _traffic.Closed += (_, _) => { _traffic = null; _trafficGrid = null; };
                _traffic.Show(this);
                return;
            }
        }
        catch { content = null; }
        if (content == null) return;
        new Window
        {
            Title = $"Weather — {hd.StationName}",
            Width = 800, Height = 800,
            Background = Brushes.Black,
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        }.Show(this);
    }

    private Window? _traffic;
    private UniformGrid? _trafficGrid;
    private byte[]?[]? _trafficTiles;

    /// <summary>Puts any new traffic tiles into the open traffic window (called on the tick).</summary>
    private string? _hereLogged;

    private void UpdateTraffic()
    {
        var hd = _c.Engine?.Hd;
        if (hd?.LastHereInfo is { } info && info != _hereLogged) { _hereLogged = info; AppLog.Write("HERE " + info); }   // what the station sends
        if (_trafficGrid == null || hd == null || ReferenceEquals(hd.TrafficTiles, _trafficTiles)) return;
        var tiles = hd.TrafficTiles;
        for (int i = 0; i < 9; i++)
        {
            if (_trafficGrid.Children[i] is not Image img) continue;
            var t = tiles[i];
            if (t == null) { img.Source = null; continue; }
            if (_trafficTiles != null && ReferenceEquals(_trafficTiles[i], t)) continue;   // unchanged
            try { using var ms = new MemoryStream(t); img.Source = new Bitmap(ms); } catch { }
        }
        _trafficTiles = tiles;
        if (_traffic != null) _traffic.Title = $"Traffic — {hd.StationName}  ·  {tiles.Count(t => t != null)} of 9 tiles  ·  {hd.TrafficTime:H:mm}";
    }
}
