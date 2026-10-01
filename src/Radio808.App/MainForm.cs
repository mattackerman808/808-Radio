using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Radio808.App;

internal sealed class MainForm : Form
{
    private readonly RadioController _c;
    private readonly FaceplateView _view;
    private readonly Timer _timer = new() { Interval = 100 };
    private string _title = "";

    public MainForm(RadioController controller)
    {
        _c = controller;
        Text = "808 Radio";
        BackColor = Color.FromArgb(0x05, 0x06, 0x07);
        ClientSize = new Size(1000, 300);
        MinimumSize = new Size(620, 230);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // restore the window, if it was saved with the faceplate's wide shape (older layouts were taller)
        var b = _c.Settings.WindowBounds;
        if (b is { Length: 4 } && b[2] > 2.2 * b[3] && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(b[0], b[1], b[2], b[3]))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(b[0], b[1], b[2], b[3]);
        }

        // no window frame: the faceplate is the window
        FormBorderStyle = FormBorderStyle.None;
        TopMost = _c.Settings.AlwaysOnTop;

        _view = new FaceplateView(_c) { Dock = DockStyle.Fill };
        _view.MapRequested += ShowMap;
        _view.MenuRequested += p => BuildMenu().Show(_view, p);
        _view.DragRequested += () =>
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        };
        _view.MinimizeRequested += () => WindowState = FormWindowState.Minimized;
        _view.CloseRequested += Close;
        _view.IsResizeBorder = p => EdgeHit(p) != 0;
        _view.OpenLayout += _ =>
        {
            // the window grows downward when the faceplate opens and shrinks back when it closes
            ClientSize = new Size(ClientSize.Width, (int)Math.Round(ClientSize.Width * DesignH / DesignW));
            UpdateShape();
        };
        Controls.Add(_view);
        Resize += (_, _) => UpdateShape();

        _c.Changed += () => _view.Invalidate();
        _timer.Tick += (_, _) =>
        {
            _view.Tick();
            UpdateTitle();
        };
        Load += async (_, _) =>
        {
            _timer.Start();
            await _c.StartAsync();
        };
        FormClosing += (_, _) =>
        {
            _timer.Stop();
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            // always remember the closed faceplate's shape (the app starts with the faceplate up)
            int h = _view.IsOpen ? (int)Math.Round(r.Width * 300 / DesignW) : r.Height;
            _c.Settings.WindowBounds = new[] { r.X, r.Y, r.Width, h };
            _c.Dispose();
        };
    }

    // ---- frameless window: shape, move, resize with a locked aspect ratio ----

    private const int WM_NCLBUTTONDOWN = 0xA1, WM_NCHITTEST = 0x84, WM_SIZING = 0x214, HTCAPTION = 2;
    private const float DesignW = 1000;
    private float DesignH => _view.DesignHeight;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private float FaceScale => ClientSize.Width / DesignW;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x20000;   // WS_MINIMIZEBOX: lets the taskbar minimize/restore a frameless window
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ClientSize = new Size(ClientSize.Width, (int)Math.Round(ClientSize.Width * DesignH / DesignW));   // exact faceplate shape
        UpdateShape();
    }

    /// <summary>The window region follows the faceplate's rounded outline.</summary>
    private void UpdateShape()
    {
        if (WindowState == FormWindowState.Minimized || ClientSize.Width == 0) return;
        float s = FaceScale;
        var o = _view.CurrentOutline;
        using var path = FaceplateView.Rounded(new RectangleF(o.X * s, o.Y * s, o.Width * s, o.Height * s), FaceplateView.OutlineRadius * s);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>Resize hit-test code for a client point on the faceplate's edge band, or 0.</summary>
    private int EdgeHit(Point p)
    {
        float s = FaceScale, band = 9 * s;
        var o = _view.CurrentOutline;
        float l = o.Left * s, t = o.Top * s, r = o.Right * s, b = o.Bottom * s;
        bool left = p.X < l + band, right = p.X > r - band, top = p.Y < t + band, bottom = p.Y > b - band;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            var screen = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
            int hit = EdgeHit(PointToClient(screen));
            if (hit != 0) { m.Result = (IntPtr)hit; return; }
        }
        else if (m.Msg == WM_SIZING)
        {
            // keep the faceplate's proportions while the user drags an edge
            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(m.LParam);
            int edge = (int)m.WParam;   // 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (edge is 3 or 6) w = (int)Math.Round(h * DesignW / DesignH);
            else h = (int)Math.Round(w * DesignH / DesignW);
            if (edge is 1 or 4 or 7) r.Left = r.Right - w; else r.Right = r.Left + w;
            if (edge is 3 or 4 or 5) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
            System.Runtime.InteropServices.Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

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
        if (t != _title) Text = _title = t;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        switch (key)
        {
            case Keys.Left: if (ctrl) _c.Seek(-1); else _c.Step(-1); return true;
            case Keys.Right: if (ctrl) _c.Seek(1); else _c.Step(1); return true;
            case Keys.MediaPreviousTrack: _c.Seek(-1); return true;
            case Keys.MediaNextTrack: _c.Seek(1); return true;
            case Keys.Up: _c.SetVolume(_c.Settings.Volume + 0.025f); _view.Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1); return true;
            case Keys.Down: _c.SetVolume(_c.Settings.Volume - 0.025f); _view.Flash($"VOL {Math.Round(_c.Settings.Volume * 40):0}", 1); return true;
            case Keys.M: _c.ToggleMute(); _view.Flash(_c.Settings.Muted ? "MUTE" : "MUTE OFF", 1); return true;
            case Keys.A:
                _c.SetForceAnalog(!_c.Settings.ForceAnalog);
                _view.Flash(_c.Settings.ForceAnalog ? "SOURCE FM" : "SOURCE HD");
                return true;
            case Keys.H: _view.NextProgram(); return true;
            case Keys.O: _view.ToggleOpen(); return true;
            case Keys.C: _view.CycleColor(); return true;
            case Keys.D:
                _c.Settings.DisplayMode = (_c.Settings.DisplayMode + 1) % 3;
                _view.Flash(_c.Settings.DisplayMode switch { 0 => "NOW PLAYING", 1 => "STATION", _ => "FREQUENCY" });
                return true;
            case >= Keys.D1 and <= Keys.D6:
                int p = key - Keys.D1;
                if (ctrl)
                {
                    var e2 = _c.Engine;
                    string? n = e2 == null ? null : e2.Hd.Synced ? e2.Hd.StationName : null;
                    n ??= e2?.Receiver.Rds.CallSign;
                    _c.StorePreset(p, n);
                    _view.Flash($"P{p + 1} SAVED");
                }
                else _c.RecallPreset(p);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Right-click menu: the settings that don't deserve a key on the faceplate.</summary>
    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var colors = new ToolStripMenuItem("Illumination");
        for (int i = 0; i < FaceplateView.Illuminations.Length; i++)
        {
            int idx = i;
            colors.DropDownItems.Add(new ToolStripMenuItem(FaceplateView.Illuminations[i].Name, null, (_, _) =>
            {
                _c.Settings.Illumination = idx;
                _c.Settings.Save();
                _view.Invalidate();
            }) { Checked = _c.Settings.Illumination == i });
        }
        m.Items.Add(colors);
        var disp = new ToolStripMenuItem("Display");
        string[] modes = { "Now playing", "Station name", "Frequency" };
        for (int i = 0; i < modes.Length; i++)
        {
            int idx = i;
            disp.DropDownItems.Add(new ToolStripMenuItem(modes[i], null, (_, _) => _c.Settings.DisplayMode = idx) { Checked = _c.Settings.DisplayMode == i });
        }
        m.Items.Add(disp);
        m.Items.Add(new ToolStripMenuItem(_view.IsOpen ? "Close faceplate" : "Open faceplate (signal details)", null, (_, _) => _view.ToggleOpen()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Auto HD", null, (_, _) => _c.SetForceAnalog(!_c.Settings.ForceAnalog)) { Checked = !_c.Settings.ForceAnalog });
        m.Items.Add(new ToolStripMenuItem("Multipath equalizer", null, (_, _) => _c.SetEqualizer(!_c.Settings.Equalizer)) { Checked = _c.Settings.Equalizer });
        m.Items.Add(new ToolStripMenuItem("Force mono", null, (_, _) => _c.SetForceMono(!_c.Settings.ForceMono)) { Checked = _c.Settings.ForceMono });
        var gain = new ToolStripMenuItem("Tuner gain");
        gain.DropDownItems.Add(new ToolStripMenuItem("Automatic (peak each station)", null, (_, _) => _c.SetGain(null)) { Checked = _c.Settings.AutoGain });
        gain.DropDownItems.Add(new ToolStripSeparator());
        foreach (double gdb in new[] { 8.7, 12.5, 16.6, 22.9, 29.7, 37.2, 44.5 })
        {
            double v = gdb;
            gain.DropDownItems.Add(new ToolStripMenuItem($"Fixed {v} dB", null, (_, _) => _c.SetGain(v))
            { Checked = !_c.Settings.AutoGain && _c.Settings.GainDb == v });
        }
        m.Items.Add(gain);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Always on top", null, (_, _) =>
        {
            _c.Settings.AlwaysOnTop = !_c.Settings.AlwaysOnTop;
            TopMost = _c.Settings.AlwaysOnTop;
        }) { Checked = _c.Settings.AlwaysOnTop });
        m.Items.Add(new ToolStripMenuItem("Minimize", null, (_, _) => WindowState = FormWindowState.Minimized));
        m.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Keyboard shortcuts", null, (_, _) => MessageBox.Show(this,
            "← / →\tstep one channel\nCtrl+← / →\tseek (also media keys)\n1-6\tpreset (Ctrl+1-6 or hold to save)\n" +
            "↑ / ↓\tvolume\nM\tmute\nA\tHD / analog\nH\tnext HD program\nD\tdisplay mode\nC\tillumination color\n\n" +
            "Mouse: wheel over the knob = volume, over the display = tune. Click the knob to mute.",
            "808 Radio", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        m.Closed += (_, _) => _c.Settings.Save();
        return m;
    }

    private void ShowMap(string which)
    {
        var hd = _c.Engine?.Hd;
        if (hd == null) return;
        Image? img = null;
        try
        {
            if (which == "weather" && hd.WeatherMap != null)
            {
                using var ms = new System.IO.MemoryStream(hd.WeatherMap);
                img = new Bitmap(Image.FromStream(ms));
            }
            else if (which == "traffic")
            {
                // 3x3 tiles
                var tiles = hd.TrafficTiles.Select(t =>
                {
                    if (t == null) return null;
                    using var ms = new System.IO.MemoryStream(t);
                    return (Image)new Bitmap(Image.FromStream(ms));
                }).ToArray();
                var first = tiles.FirstOrDefault(t => t != null);
                if (first != null)
                {
                    int tw = first.Width, th = first.Height;
                    var bmp = new Bitmap(tw * 3, th * 3);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(0x1B, 0x21, 0x2B));
                        for (int i = 0; i < 9; i++) if (tiles[i] != null) g.DrawImage(tiles[i]!, (i % 3) * tw, (i / 3) * th, tw, th);
                    }
                    foreach (var t in tiles) t?.Dispose();
                    img = bmp;
                }
            }
        }
        catch { img = null; }
        if (img == null) return;

        var f = new Form
        {
            Text = (which == "weather" ? "Weather" : "Traffic") + $" — {hd.StationName}",
            ClientSize = new Size(Math.Min(img.Width, 900), Math.Min(img.Height, 900)),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.Black,
            Icon = Icon,
        };
        var pb = new PictureBox { Dock = DockStyle.Fill, Image = img, SizeMode = PictureBoxSizeMode.Zoom };
        f.Controls.Add(pb);
        f.FormClosed += (_, _) => img.Dispose();
        f.Show(this);
    }
}
