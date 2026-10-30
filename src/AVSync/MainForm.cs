using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AVSync;

public sealed class MainForm : Form
{
    private enum Role { Discovering, Source, Sync }

    private const long FlashTicks = 100 * TimeSpan.TicksPerMillisecond;
    private const string Sample = "00:00.000";

    private static readonly Color Background = Color.Black;
    private static readonly Color Digits = Color.White;
    private static readonly Color DigitsUnlocked = Color.FromArgb(110, 110, 110);
    private static readonly Color LightOff = Color.FromArgb(38, 38, 38);
    private static readonly Color LightSecond = Color.White;
    private static readonly Color LightMinute = Color.FromArgb(255, 40, 40);
    private static readonly Color InfoText = Color.FromArgb(150, 150, 150);
    private static readonly Color InfoWarn = Color.FromArgb(255, 170, 60);

    private readonly Options _options;
    private readonly Settings _settings = Settings.Load();
    private readonly TickAudio _audio;
    private readonly ContextMenuStrip _menu = new();
    private readonly StringFormat _center = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
    };

    private volatile IClock _clock = SystemClock.Instance;
    private Role _role = Role.Discovering;
    private SourceServer? _server;
    private SyncClient? _client;

    private Font? _timeFont, _infoFont;
    private RectangleF _timeRect, _lightRect, _infoRect;

    private bool _fullScreen;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;

    private bool _cursorHidden;
    private long _lastMouseMove;
    private long _lastFlush;
    private long _frameLead = TimeSpan.TicksPerSecond / 60;

    public MainForm(Options options)
    {
        _options = options;
        _audio = new TickAudio(_clock) { OffsetMs = _settings.AudioOffsetMs };

        Text = "AVSync";
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("AVSync.ico"))
            if (icon != null) Icon = new Icon(icon);
        BackColor = Background;
        ForeColor = Digits;
        ClientSize = new Size(1100, 620);
        MinimumSize = new Size(400, 260);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        ContextMenuStrip = _menu;
        BuildMenu();
    }

    // ---------------------------------------------------------------- startup / roles

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.Idle += RenderLoop;
        _audio.Start();
        if (_options.FullScreen) SetFullScreen(true);

        try
        {
            await StartRoleAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AVSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StartRoleAsync()
    {
        int port = _options.Port;

        if (_options.SyncHost != null)
        {
            BecomeSync(await Task.Run(() => SyncClient.Connect(_options.SyncHost, port)));
            return;
        }

        if (!_options.ForceSource)
        {
            // Look for a source already running on the network.
            var client = await Task.Run(() => SyncClient.Discover(port, TimeSpan.FromSeconds(1.5)));
            while (client == null && _options.ForceSync && !IsDisposed)
                client = await Task.Run(() => SyncClient.Discover(port, TimeSpan.FromSeconds(2)));
            if (client != null)
            {
                BecomeSync(client);
                return;
            }
        }

        var server = SourceServer.TryStart(port, SystemClock.Instance);
        if (server != null)
        {
            _server = server;
            server.OlderSourceFound += (endPoint, name) => BeginInvoke(() =>
            {
                // Another source was already running (e.g. both PCs started together): defer to it.
                if (_server != server || IsDisposed) return;
                server.Dispose();
                _server = null;
                BecomeSync(SyncClient.Connect(endPoint.Address.ToString(), endPoint.Port, name));
            });
            _role = Role.Source;
            _audio.Enabled = true;
            Text = "AVSync - Source";
        }
        else
        {
            // Port already taken on this PC: a source is running here but didn't answer discovery. Use it.
            BecomeSync(SyncClient.Connect("127.0.0.1", port));
        }
    }

    private void BecomeSync(SyncClient client)
    {
        if (IsDisposed)
        {
            client.Dispose();
            return;
        }
        _client = client;
        _clock = client.Clock;
        _audio.Clock = client.Clock;
        _audio.Enabled = false; // sync side is silent by default
        _role = Role.Sync;
        Text = $"AVSync - Sync ({client.SourceName})";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Application.Idle -= RenderLoop;
        _settings.AudioOffsetMs = _audio.OffsetMs;
        _settings.Save();
        _audio.Dispose();
        _server?.Dispose();
        _client?.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- render loop

    private void RenderLoop(object? sender, EventArgs e)
    {
        while (IsApplicationIdle() && !IsDisposed)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Thread.Sleep(20);
                continue;
            }

            // Wait for the compositor's vertical blank, then draw. What we draw now appears on the *next*
            // composition, so the displayed time is advanced by one (measured) frame period.
            int hr = DwmFlush();
            long now = Mono.NowTicks;
            long dt = now - _lastFlush;
            _lastFlush = now;
            if (hr == 0 && dt > TimeSpan.TicksPerMillisecond * 3 && dt < TimeSpan.TicksPerSecond / 20)
                _frameLead = (_frameLead * 15 + dt) / 16;
            else if (dt < TimeSpan.TicksPerMillisecond * 2)
                Thread.Sleep(1); // DWM not pacing us; don't spin

            // Hide the mouse pointer in full screen once it stops moving.
            if (_fullScreen && !_cursorHidden && now - _lastMouseMove > 2 * TimeSpan.TicksPerSecond && !_menu.Visible)
                ShowCursor(false);

            Invalidate();
            Update();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastMouseMove = Mono.NowTicks;
        ShowCursor(true);
    }

    private void ShowCursor(bool show)
    {
        if (show == !_cursorHidden) return;
        _cursorHidden = !show;
        if (show) Cursor.Show(); else Cursor.Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Background);
        if (_timeFont == null) Relayout(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        long now = _clock.NowTicks + _frameLead;
        long intoSecond = now % TimeSpan.TicksPerSecond;
        long totalSeconds = now / TimeSpan.TicksPerSecond;
        int ms = (int)(intoSecond / TimeSpan.TicksPerMillisecond);
        int sec = (int)(totalSeconds % 60);
        int min = (int)(totalSeconds / 60 % 60);

        bool locked = _role == Role.Source || (_role == Role.Sync && _client!.Clock.Locked);
        using (var brush = new SolidBrush(locked ? Digits : DigitsUnlocked))
            g.DrawString($"{min:00}:{sec:00}.{ms:000}", _timeFont!, brush, _timeRect, _center);

        Color light = intoSecond < FlashTicks ? (sec == 0 ? LightMinute : LightSecond) : LightOff;
        using (var brush = new SolidBrush(light))
            g.FillEllipse(brush, _lightRect);

        if (_settings.ShowInfo) DrawInfo(g);
    }

    private void DrawInfo(Graphics g)
    {
        string line1;
        bool warn = false;
        string sound = $"Sound {(_audio.Enabled ? "ON" : "off")} (offset {_audio.OffsetMs:+0;-0;0} ms)";
        if (_audio.Error != null) sound = $"Audio error: {_audio.Error}";

        switch (_role)
        {
            case Role.Source:
                int clients = _server!.ActiveClients;
                line1 = $"SOURCE  |  {Protocol.LocalAddresses()} : {_server.Port}  |  {clients} sync app{(clients == 1 ? "" : "s")} connected  |  {sound}";
                break;
            case Role.Sync:
                var s = _client!.Stats;
                double diff = (_client.Clock.NowTicks - SystemClock.Instance.NowTicks) / (double)TimeSpan.TicksPerMillisecond;
                if (!s.Connected)
                {
                    warn = true;
                    line1 = $"SYNC  |  NO REPLY from {_client.SourceName} ({_client.SourceEndPoint})  -  free running  |  {sound}";
                }
                else
                {
                    line1 = $"SYNC <- {_client.SourceName} ({_client.SourceEndPoint.Address})  |  " +
                            $"Latency {s.MinRttMs / 2:0.00} ms one-way  (RTT best {s.MinRttMs:0.00} / last {s.LastRttMs:0.00} ms, " +
                            $"jitter {s.JitterMs:0.00} ms, loss {s.LossPercent:0}%)  |  vs this PC clock {diff:+0.0;-0.0} ms  |  {sound}";
                }
                break;
            default:
                line1 = "Searching the network for an AVSync source...";
                break;
        }

        const string line2 = "F11 / double-click: full screen    M: sound on/off    Up/Down: audio offset (Shift = 10 ms)    I: hide info    Right-click: menu";
        using var brush = new SolidBrush(warn ? InfoWarn : InfoText);
        using var hint = new SolidBrush(Color.FromArgb(90, 90, 90));
        float lh = _infoFont!.GetHeight(g);
        var fmt = new StringFormat(_center) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(line1, _infoFont, brush, new RectangleF(_infoRect.X, _infoRect.Y, _infoRect.Width, lh), fmt);
        g.DrawString(line2, _infoFont, hint, new RectangleF(_infoRect.X, _infoRect.Y + lh * 1.2f, _infoRect.Width, lh), fmt);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _timeFont?.Dispose();
        _timeFont = null; // recomputed on next paint
    }

    private void Relayout(Graphics g)
    {
        float w = Math.Max(ClientSize.Width, 1), h = Math.Max(ClientSize.Height, 1);

        float infoPx = Math.Clamp(h * 0.022f, 11, 26);
        _infoFont?.Dispose();
        _infoFont = new Font("Segoe UI", infoPx, GraphicsUnit.Pixel);
        float infoH = _settings.ShowInfo ? infoPx * 3.0f : 0;
        _infoRect = new RectangleF(10, h - infoH, w - 20, infoH);

        float avail = h - infoH;
        float lightD = Math.Min(avail * 0.26f, w * 0.3f);
        float gap = avail * 0.04f;
        float textMaxH = avail - lightD - gap * 3;

        // Size the digits to fill the width (or height, whichever runs out first).
        using var probe = new Font("Segoe UI", 100, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(Sample, probe, PointF.Empty, StringFormat.GenericTypographic);
        float scale = Math.Min(w * 0.95f / size.Width, textMaxH / size.Height);
        _timeFont = new Font("Segoe UI", Math.Max(8, 100 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        float textH = size.Height * scale;

        float top = (avail - (textH + gap + lightD)) / 2;
        _timeRect = new RectangleF(0, top, w, textH);
        _lightRect = new RectangleF((w - lightD) / 2, top + textH + gap, lightD, lightD);
    }

    // ---------------------------------------------------------------- input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = e.Shift ? 10 : 1;
        switch (e.KeyCode)
        {
            case Keys.F11:
            case Keys.F:
                SetFullScreen(!_fullScreen);
                break;
            case Keys.Escape when _fullScreen:
                SetFullScreen(false);
                break;
            case Keys.M:
            case Keys.S:
                _audio.Enabled = !_audio.Enabled;
                break;
            case Keys.Up:
            case Keys.Oemplus:
            case Keys.Add:
                _audio.OffsetMs += step;
                break;
            case Keys.Down:
            case Keys.OemMinus:
            case Keys.Subtract:
                _audio.OffsetMs -= step;
                break;
            case Keys.D0:
            case Keys.NumPad0:
                _audio.OffsetMs = 0;
                break;
            case Keys.I:
                ToggleInfo();
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Arrow keys are otherwise swallowed by dialog navigation.
        if ((keyData & Keys.KeyCode) is Keys.Up or Keys.Down)
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) SetFullScreen(!_fullScreen);
    }

    private void BuildMenu()
    {
        var fullScreen = new ToolStripMenuItem("Full screen", null, (_, _) => SetFullScreen(!_fullScreen)) { ShortcutKeyDisplayString = "F11" };
        var sound = new ToolStripMenuItem("Sound (tick)", null, (_, _) => _audio.Enabled = !_audio.Enabled) { ShortcutKeyDisplayString = "M" };
        var info = new ToolStripMenuItem("Show info", null, (_, _) => ToggleInfo()) { ShortcutKeyDisplayString = "I" };
        var offset = new ToolStripMenuItem("Audio offset");
        foreach (var delta in new[] { +10, +1, -1, -10 })
            offset.DropDownItems.Add(new ToolStripMenuItem($"{delta:+0;-0} ms", null, (_, _) => _audio.OffsetMs += delta));
        offset.DropDownItems.Add(new ToolStripSeparator());
        offset.DropDownItems.Add(new ToolStripMenuItem("Reset to 0", null, (_, _) => _audio.OffsetMs = 0));

        _menu.Items.AddRange(new ToolStripItem[]
        {
            fullScreen, sound, offset, info, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Close()),
        });
        _menu.Opening += (_, _) =>
        {
            fullScreen.Checked = _fullScreen;
            sound.Checked = _audio.Enabled;
            info.Checked = _settings.ShowInfo;
            offset.Text = $"Audio offset ({_audio.OffsetMs:+0;-0;0} ms)";
        };
    }

    private void ToggleInfo()
    {
        _settings.ShowInfo = !_settings.ShowInfo;
        _timeFont = null;
    }

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;
            _lastMouseMove = Mono.NowTicks;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            ShowCursor(true);
        }
    }

    // ---------------------------------------------------------------- interop

    private static bool IsApplicationIdle() => !PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Location;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage msg, IntPtr hWnd, uint filterMin, uint filterMax, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
