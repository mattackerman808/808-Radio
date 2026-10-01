using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Radio808.App;

internal sealed class MainForm : Form
{
    private readonly RadioController _c;
    private readonly RadioView _view;
    private readonly Timer _timer = new() { Interval = 100 };
    private string _title = "";

    public MainForm(RadioController controller)
    {
        _c = controller;
        Text = "808 Radio";
        BackColor = Color.FromArgb(0x0C, 0x0F, 0x14);
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var b = _c.Settings.WindowBounds;
        if (b is { Length: 4 } && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(b[0], b[1], b[2], b[3]))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(b[0], b[1], b[2], b[3]);
        }

        _view = new RadioView(_c) { Dock = DockStyle.Fill };
        _view.MapRequested += ShowMap;
        Controls.Add(_view);

        _c.Changed += () => _view.Invalidate();
        _timer.Tick += (_, _) =>
        {
            _view.PullSpectrum();
            _view.Invalidate();
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
            _c.Settings.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
            _c.Dispose();
        };
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
            case Keys.Up: _c.SetVolume(_c.Settings.Volume + 0.05f); return true;
            case Keys.Down: _c.SetVolume(_c.Settings.Volume - 0.05f); return true;
            case Keys.M: _c.ToggleMute(); return true;
            case Keys.A: _c.SetForceAnalog(!_c.Settings.ForceAnalog); return true;
            case Keys.H:
                if (_c.Engine is { } eng && eng.Hd.Programs.Count > 0)
                {
                    var progs = eng.Hd.Programs.Keys.ToList();
                    int i = progs.IndexOf(eng.Program);
                    _c.SetProgram(progs[(i + 1) % progs.Count]);
                }
                return true;
            case >= Keys.D1 and <= Keys.D6:
                int p = key - Keys.D1;
                if (ctrl)
                {
                    var e2 = _c.Engine;
                    string? n = e2 == null ? null : e2.Hd.Synced ? e2.Hd.StationName : null;
                    n ??= e2?.Receiver.Rds.CallSign;
                    _c.StorePreset(p, n);
                }
                else _c.RecallPreset(p);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
