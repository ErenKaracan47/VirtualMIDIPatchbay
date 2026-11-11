using System.Drawing.Drawing2D;
using System.Text;
using Microsoft.Win32;

namespace MidiPatchbay;

public sealed class MainForm : Form
{
    readonly PatchbayEngine _engine;
    readonly bool _startMinimized;
    bool _reallyExit;
    bool _shownTrayHint;

    // left: virtual ports
    readonly TextBox _newName = new() { PlaceholderText = "New port name", Dock = DockStyle.Fill };
    readonly Button _add = new() { Text = "Add port", AutoSize = true };
    readonly ListView _ports = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true };
    readonly Button _up = new() { Text = "Move up", AutoSize = true };
    readonly Button _down = new() { Text = "Move down", AutoSize = true };

    // right: matrix + monitor
    readonly DataGridView _grid = new();
    readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f), WordWrap = false,
    };
    readonly CheckBox _monitor = new() { Text = "Monitor messages", AutoSize = true };
    readonly CheckBox _hideClock = new() { Text = "Hide clock / active sensing", AutoSize = true };
    readonly Button _clearLog = new() { Text = "Clear", AutoSize = true };
    readonly Button _clearRoutes = new() { Text = "Disconnect all", AutoSize = true };
    readonly Button _refresh = new() { Text = "Rescan devices", AutoSize = true };

    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 60 };

    List<Node> _shown = new();
    SplitContainer _split, _rightSplit;
    float _s = 1f;
    int Sc(int v) => (int)Math.Round(v * _s);
    int _hoverRow = -1, _hoverCol = -1;
    int _tick;

    Palette P = Palette.Light;
    readonly ToolStripDropDownButton _themeButton = new() { Text = "Theme" };
    readonly ContextMenuStrip _trayMenu = new();
    ToolStripMenuItem _trayTheme;

    public MainForm(PatchbayEngine engine, bool startMinimized)
    {
        _engine = engine;
        _startMinimized = startMinimized;

        Text = "MIDI Patchbay";
        Icon = MakeIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 720);
        MinimumSize = new Size(760, 480);
        Font = new Font("Segoe UI", 9f);
        if (startMinimized) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

        BuildLayout();
        BuildTray();

        _engine.Changed += () => BeginInvokeSafe(RebuildAll);
        _engine.Log += s => BeginInvokeSafe(() => SetStatus(s));
        _uiTimer.Tick += (_, _) => OnUiTick();
    }

    // ------------------------------------------------------------------ layout

    void BuildLayout()
    {
        var split = _split = new SplitContainer
        {
            Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 300, Panel1MinSize = 240,
        };

        // ---- virtual ports panel
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        left.Controls.Add(Title("Virtual MIDI ports"), 0, 0);

        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 4, 0, 6) };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _newName.Margin = new Padding(0, 1, 6, 0);
        _add.Margin = new Padding(0);
        addRow.Controls.Add(_newName, 0, 0);
        addRow.Controls.Add(_add, 1, 0);
        left.Controls.Add(addRow, 0, 1);

        _ports.OwnerDraw = true;
        _ports.DrawItem += (_, e) => e.DrawDefault = true;
        _ports.DrawSubItem += (_, e) => e.DrawDefault = true;
        _ports.DrawColumnHeader += DrawPortsHeader;
        _ports.Columns.Add("Name", 150);
        _ports.Columns.Add("Received", 64, HorizontalAlignment.Right);
        _ports.Columns.Add("Sent", 56, HorizontalAlignment.Right);
        left.Controls.Add(_ports, 0, 2);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        btnRow.Controls.AddRange(new Control[] { _remove, _up, _down });
        left.Controls.Add(btnRow, 0, 3);

        left.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(280, 0), Tag = ThemeApplier.MutedTag, Margin = new Padding(0, 8, 0, 0),
            Text = "Every app sees each port as a MIDI input and output with the same name. " +
                   "New ports are connected to themselves, so they work like loopMIDI ports: " +
                   "whatever one app sends to the port, other apps receive from it. " +
                   "Use the matrix to send it somewhere else as well, or instead.\n\n" +
                   "Ports exist while MIDI Patchbay runs. Closing the window keeps it running in the notification area.",
        }, 0, 4);
        split.Panel1.Controls.Add(left);

        // ---- matrix + monitor
        var rightSplit = _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 430 };

        var matrixPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 8, 8, 0) };
        matrixPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        matrixPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var matrixHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        matrixHeader.Controls.Add(Title("Routing"));
        matrixHeader.Controls.Add(new Label
        {
            AutoSize = true, Tag = ThemeApplier.MutedTag, Margin = new Padding(8, 5, 16, 0),
            Text = "Rows send to columns. Click a cell to connect or disconnect.",
        });
        matrixHeader.Controls.Add(_refresh);
        matrixHeader.Controls.Add(_clearRoutes);
        matrixPanel.Controls.Add(matrixHeader, 0, 0);
        SetupGrid();
        matrixPanel.Controls.Add(_grid, 0, 1);
        rightSplit.Panel1.Controls.Add(matrixPanel);

        var monPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 4, 8, 8) };
        monPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        monPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var monHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        monHeader.Controls.AddRange(new Control[] { _monitor, _hideClock, _clearLog });
        monPanel.Controls.Add(monHeader, 0, 0);
        monPanel.Controls.Add(_log, 0, 1);
        rightSplit.Panel2.Controls.Add(monPanel);

        split.Panel2.Controls.Add(rightSplit);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_themeButton);
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            var m = mode;
            _themeButton.DropDownItems.Add(new ToolStripMenuItem(m.ToString(), null, (_, _) => SetTheme(m)) { Tag = m });
        }

        Controls.Add(split);
        Controls.Add(statusStrip);

        // ---- events
        _add.Click += (_, _) => AddPort();
        _newName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddPort(); } };
        _remove.Click += (_, _) => RemoveSelectedPort();
        _ports.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedPort(); };
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(+1);
        _ports.SelectedIndexChanged += (_, _) => UpdateButtons();
        _monitor.CheckedChanged += (_, _) => _engine.MonitorEnabled = _monitor.Checked;
        _hideClock.Checked = _engine.Config.HideClockInMonitor;
        _hideClock.CheckedChanged += (_, _) => { _engine.Config.HideClockInMonitor = _hideClock.Checked; _engine.SaveConfig(); };
        _clearLog.Click += (_, _) => _log.Clear();
        _refresh.Click += (_, _) => { Cursor = Cursors.WaitCursor; try { _engine.RefreshEndpoints(); SetStatus("Device list refreshed"); } finally { Cursor = Cursors.Default; } };
        _clearRoutes.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove every connection in the matrix, including each port's connection to itself?",
                    "Disconnect all", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _engine.ClearRoutes();
        };
    }

    static Label Title(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, 2),
    };

    void SetupGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.AllowUserToResizeColumns = false;
        g.AllowUserToOrderColumns = false;
        g.ReadOnly = true;
        g.MultiSelect = false;
        g.SelectionMode = DataGridViewSelectionMode.CellSelect;
                g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        g.RowHeadersWidth = 240;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 160;
        g.RowTemplate.Height = 28;
        g.ShowCellToolTips = true;
        g.StandardTab = true;
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(g, true);

        g.CellPainting += PaintCell;
        g.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0) ToggleRoute(e.RowIndex, e.ColumnIndex);
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && g.CurrentCell != null) { ToggleRoute(g.CurrentCell.RowIndex, g.CurrentCell.ColumnIndex); e.Handled = true; }
        };
        g.CellMouseEnter += (_, e) => { _hoverRow = e.RowIndex; _hoverCol = e.ColumnIndex; g.Invalidate(); };
        g.MouseLeave += (_, _) => { _hoverRow = _hoverCol = -1; g.Invalidate(); };
        g.SelectionChanged += (_, _) => g.Invalidate();
    }

    void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "MIDI Patchbay";
        _tray.Visible = true;
        var menu = _trayMenu;
        menu.Items.Add("Open MIDI Patchbay", null, (_, _) => ShowFromTray());
        _trayTheme = new ToolStripMenuItem("Theme");
        foreach (var mode in Enum.GetValues<ThemeMode>())
        {
            var m = mode;
            _trayTheme.DropDownItems.Add(new ToolStripMenuItem(m.ToString(), null, (_, _) => SetTheme(m)) { Tag = m });
        }
        menu.Items.Add(_trayTheme);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (removes the virtual ports)", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ------------------------------------------------------------------ theme

    ThemeMode CurrentMode => Enum.TryParse<ThemeMode>(_engine.Config.Theme, out var m) ? m : ThemeMode.System;

    void SetTheme(ThemeMode mode)
    {
        _engine.Config.Theme = mode.ToString();
        _engine.SaveConfig();
        ApplyTheme();
    }

    void ApplyTheme()
    {
        P = Palette.For(CurrentMode);
        SuspendLayout();
        BackColor = P.Surface;
        ForeColor = P.Text;
        ThemeApplier.Apply(this, P);
        ThemeApplier.TitleBar(this, P);
        _trayMenu.Renderer = new ThemedToolStripRenderer(P);
        _themeButton.Text = "Theme: " + CurrentMode;
        foreach (ToolStripMenuItem it in _themeButton.DropDownItems) it.Checked = (ThemeMode)it.Tag == CurrentMode;
        foreach (ToolStripMenuItem it in _trayTheme.DropDownItems) it.Checked = (ThemeMode)it.Tag == CurrentMode;
        ResumeLayout();
        Invalidate(true);
    }

    void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
            BeginInvokeSafe(ApplyTheme);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        base.OnHandleDestroyed(e);
    }

    /// <summary>Name column takes the rest, so no unpainted header area is left on the right.</summary>
    void FitPortColumns()
    {
        if (_ports.Columns.Count < 3) return;
        int w = _ports.ClientSize.Width - _ports.Columns[1].Width - _ports.Columns[2].Width;
        if (w > Sc(60) && _ports.Columns[0].Width != w) _ports.Columns[0].Width = w;
    }

    void DrawPortsHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(P.Window)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var pen = new Pen(P.GridLine))
        {
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        var r = Rectangle.Inflate(e.Bounds, -Sc(6), 0);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, _ports.Font, r, P.Muted, flags);
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _s = DeviceDpi / 96f;
        _split.Panel1MinSize = Sc(240);
        _split.SplitterDistance = Sc(300);
        _rightSplit.SplitterDistance = (int)(_rightSplit.Height * 0.72);
        _grid.RowTemplate.Height = Sc(28);
        _ports.Columns[1].Width = Sc(76); _ports.Columns[2].Width = Sc(56);
        FitPortColumns();
        _ports.Resize += (_, _) => FitPortColumns();
        Cursor = Cursors.WaitCursor;
        try
        {
            _engine.Start();
            SetStatus($"Ready: {_engine.Config.Ports.Count} virtual port(s)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to start: " + ex.Message, "MIDI Patchbay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
        RebuildAll();
        _uiTimer.Start();
        if (_startMinimized) BeginInvoke(() => Hide());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_shownTrayHint)
            {
                _shownTrayHint = true;
                _tray.ShowBalloonTip(3000, "MIDI Patchbay is still running",
                    "Your virtual ports stay available. Right-click the tray icon to exit.", ToolTipIcon.Info);
            }
            return;
        }
        _uiTimer.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ ports

    void AddPort()
    {
        var name = _newName.Text.Trim();
        var err = PatchbayEngine.ValidatePortName(name, _engine.Config.Ports.Select(p => p.Name));
        if (err != null) { SetStatus(err); System.Media.SystemSounds.Beep.Play(); _newName.Focus(); return; }
        Cursor = Cursors.WaitCursor;
        try
        {
            _engine.AddVirtualPort(name);
            _newName.Clear();
            SetStatus($"Added '{name}'. Apps may need to rescan their MIDI devices to see it.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not add port", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { Cursor = Cursors.Default; }
    }

    Node SelectedPort => _ports.SelectedItems.Count == 1 ? _ports.SelectedItems[0].Tag as Node : null;

    void RemoveSelectedPort()
    {
        var n = SelectedPort;
        if (n == null) return;
        if (MessageBox.Show(this, $"Remove the virtual port '{n.Name}'? Apps using it will lose the connection.",
                "Remove port", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _engine.RemoveVirtualPort(n);
    }

    void MoveSelected(int delta)
    {
        var n = SelectedPort;
        if (n == null) return;
        _engine.MoveVirtualPort(n, delta);
        foreach (ListViewItem it in _ports.Items) if (it.Tag == n) { it.Selected = true; it.Focused = true; }
    }

    void UpdateButtons()
    {
        var n = SelectedPort;
        int i = n == null ? -1 : _ports.SelectedIndices[0];
        _remove.Enabled = n != null;
        _up.Enabled = n != null && i > 0;
        _down.Enabled = n != null && i < _ports.Items.Count - 1;
    }

    // ------------------------------------------------------------------ rebuild

    void RebuildAll()
    {
        var nodes = _engine.Nodes.ToList();
        var selected = SelectedPort?.Key;

        _ports.BeginUpdate();
        _ports.Items.Clear();
        foreach (var n in nodes.Where(n => n.IsVirtual))
        {
            var it = new ListViewItem(new[] { n.Name, n.InCount.ToString("N0"), n.OutCount.ToString("N0") }) { Tag = n };
            _ports.Items.Add(it);
            if (n.Key == selected) it.Selected = true;
        }
        _ports.EndUpdate();
        UpdateButtons();

        bool sameShape = nodes.Count == _shown.Count && nodes.Zip(_shown).All(p => p.First == p.Second);
        _shown = nodes;
        if (!sameShape)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            foreach (var n in nodes)
            {
                var col = new DataGridViewTextBoxColumn { Name = n.Key, HeaderText = n.Name, Width = Sc(30), SortMode = DataGridViewColumnSortMode.NotSortable, Tag = n };
                _grid.Columns.Add(col);
            }
            foreach (var n in nodes)
            {
                int r = _grid.Rows.Add();
                _grid.Rows[r].Tag = n;
                _grid.Rows[r].Height = Sc(28);
                for (int c = 0; c < nodes.Count; c++)
                    _grid.Rows[r].Cells[c].ToolTipText = $"{n.Name}  →  {nodes[c].Name}";
            }
            using (var gfx = _grid.CreateGraphics())
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                float longest = nodes.Count == 0 ? 0 : nodes.Max(n => gfx.MeasureString(n.Online ? n.Name : n.Name + " (offline)", bold).Width);
                _grid.ColumnHeadersHeight = Math.Clamp((int)longest + Sc(36), Sc(70), Sc(220));
                float widest = nodes.Count == 0 ? 0 : nodes.Max(n => gfx.MeasureString(n.Online ? n.Name : n.Name + " (offline)", bold).Width);
                _grid.RowHeadersWidth = Math.Clamp((int)widest + Sc(40), Sc(160), Sc(320));
            }
            _grid.ClearSelection();
            _grid.ResumeLayout();
        }
        _grid.Invalidate();
    }

    void ToggleRoute(int row, int col)
    {
        if (row < 0 || col < 0 || row >= _shown.Count || col >= _shown.Count) return;
        var from = _shown[row];
        var to = _shown[col];
        bool on = !_engine.HasRoute(from.Key, to.Key);
        _engine.SetRoute(from.Key, to.Key, on);
        SetStatus(on ? $"Connected {from.Name} → {to.Name}" : $"Disconnected {from.Name} → {to.Name}");
    }

    // ------------------------------------------------------------------ painting

    void PaintCell(object sender, DataGridViewCellPaintingEventArgs e)
    {
        var g = e.Graphics;
        var b = e.CellBounds;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        long now = Environment.TickCount64;

        if (e.RowIndex == -1 && e.ColumnIndex == -1)
        {
            using (var bg = new SolidBrush(P.Window)) g.FillRectangle(bg, b);
            TextRenderer.DrawText(g, "From  ↓       To  →", Font, new Rectangle(b.X + Sc(8), b.Bottom - Sc(26), b.Width - Sc(8), Sc(22)),
                P.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.Handled = true;
            return;
        }

        if (e.RowIndex == -1) // column header: destination
        {
            var n = _shown[e.ColumnIndex];
            g.FillRectangle(new SolidBrush(e.ColumnIndex == _hoverCol ? P.HeaderHover : n.IsVirtual ? P.VirtualTint : P.Window), b);
            DrawLed(g, new Rectangle(b.X + b.Width / 2 - Sc(4), b.Bottom - Sc(14), Sc(8), Sc(8)), now - n.LastOutTick < 150, n.Online);
            var state = g.Save();
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var color = n.Online ? P.Text : P.Muted;
            var text = n.Online ? n.Name : n.Name + " (offline)";
            using (var font = n.IsVirtual ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular))
            {
                var label = Ellipsize(g, text, font, b.Height - Sc(30));
                var sz = g.MeasureString(label, font);
                g.TranslateTransform(b.X + (b.Width - sz.Height) / 2f, b.Bottom - Sc(22));
                g.RotateTransform(-90);
                using var br = new SolidBrush(color);
                g.DrawString(label, font, br, 0, 0);
            }
            g.Restore(state);
            using var p = new Pen(P.GridLine);
            g.DrawLine(p, b.Right - 1, b.Top + 4, b.Right - 1, b.Bottom);
            e.Handled = true;
            return;
        }

        if (e.ColumnIndex == -1) // row header: source
        {
            var n = _shown[e.RowIndex];
            g.FillRectangle(new SolidBrush(e.RowIndex == _hoverRow ? P.HeaderHover : n.IsVirtual ? P.VirtualTint : P.Window), b);
            DrawLed(g, new Rectangle(b.Right - Sc(16), b.Y + b.Height / 2 - Sc(4), Sc(8), Sc(8)), now - n.LastInTick < 150, n.Online);
            var color = n.Online ? P.Text : P.Muted;
            var text = n.Online ? n.Name : n.Name + " (offline)";
            using (var font = n.IsVirtual ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular))
                TextRenderer.DrawText(g, text, font, new Rectangle(b.X + Sc(8), b.Y, b.Width - Sc(30), b.Height), color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var p = new Pen(P.GridLine);
            g.DrawLine(p, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            e.Handled = true;
            return;
        }

        // matrix cell
        var src = _shown[e.RowIndex];
        var dst = _shown[e.ColumnIndex];
        bool hover = e.RowIndex == _hoverRow || e.ColumnIndex == _hoverCol;
        bool focused = _grid.CurrentCell != null && _grid.Focused &&
                       _grid.CurrentCell.RowIndex == e.RowIndex && _grid.CurrentCell.ColumnIndex == e.ColumnIndex;
        g.FillRectangle(new SolidBrush(hover ? P.CellHover : P.Window), b);
        using (var p = new Pen(P.GridLine))
        {
            g.DrawLine(p, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            g.DrawLine(p, b.Right - 1, b.Top, b.Right - 1, b.Bottom);
        }

        int bs = Sc(18);
        var box = new Rectangle(b.X + (b.Width - bs) / 2, b.Y + (b.Height - bs) / 2, bs, bs);
        bool routed = _engine.HasRoute(src.Key, dst.Key);
        bool live = src.Online && dst.Online;
        if (routed)
        {
            var fill = live ? P.Accent : P.RoutedOffline;
            if (live && now - src.LastInTick < 150) fill = ControlPaint.Light(P.Accent, 0.4f);
            using var path = Rounded(box, Sc(4));
            using var br = new SolidBrush(fill);
            g.FillPath(br, path);
            if (src == dst) // self-loop marker
            {
                using var pen = new Pen(Color.White, 1.6f * _s);
                g.DrawArc(pen, box.X + Sc(4), box.Y + Sc(4), Sc(10), Sc(10), 30, 300);
            }
            else
            {
                using var pen = new Pen(Color.White, 2f * _s);
                g.DrawLines(pen, new[] { new Point(box.X + Sc(4), box.Y + Sc(9)), new Point(box.X + Sc(8), box.Y + Sc(13)), new Point(box.X + Sc(14), box.Y + Sc(5)) });
            }
        }
        else
        {
            using var path = Rounded(box, Sc(4));
            using var pen = new Pen(hover ? P.AccentHover : P.BoxBorder);
            g.DrawPath(pen, path);
        }
        if (focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(box, Sc(3), Sc(3)));
        e.Handled = true;
    }

    void DrawLed(Graphics g, Rectangle r, bool on, bool online)
    {
        using var br = new SolidBrush(on ? P.Led : online ? P.LedIdle : P.LedOffline);
        g.FillEllipse(br, r);
    }

    static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static string Ellipsize(Graphics g, string s, Font f, int maxWidth)
    {
        if (g.MeasureString(s, f).Width <= maxWidth) return s;
        while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxWidth) s = s[..^1];
        return s + "…";
    }

    // ------------------------------------------------------------------ timers

    void OnUiTick()
    {
        if (!Visible) { while (_engine.MonitorQueue.TryDequeue(out _)) { } return; }
        _grid.Invalidate();

        if (++_tick % 8 == 0)
        {
            foreach (ListViewItem it in _ports.Items)
            {
                var n = (Node)it.Tag;
                var a = n.InCount.ToString("N0");
                var s = n.OutCount.ToString("N0");
                if (it.SubItems[1].Text != a) it.SubItems[1].Text = a;
                if (it.SubItems[2].Text != s) it.SubItems[2].Text = s;
            }
        }

        if (!_engine.MonitorQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            int lines = 0;
            while (lines < 400 && _engine.MonitorQueue.TryDequeue(out var line)) { sb.Append(line).Append("\r\n"); lines++; }
            if (_log.TextLength > 300_000) _log.Text = _log.Text[^150_000..];
            _log.AppendText(sb.ToString());
        }
    }

    void SetStatus(string s) => _status.Text = s;

    // ------------------------------------------------------------------ autostart

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("MidiPatchbay") != null;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k == null) return;
        if (on) k.SetValue("MidiPatchbay", $"\"{Application.ExecutablePath}\" --minimized");
        else k.DeleteValue("MidiPatchbay", false);
    }

    // ------------------------------------------------------------------ icon

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Palette.Light.Accent);
            using (var path = Rounded(new Rectangle(1, 1, 30, 30), 7)) g.FillPath(bg, path);
            using var dot = new SolidBrush(Color.White);
            // 5-pin DIN style
            g.FillEllipse(dot, 7, 13, 5, 5);
            g.FillEllipse(dot, 20, 13, 5, 5);
            g.FillEllipse(dot, 10, 7, 5, 5);
            g.FillEllipse(dot, 17, 7, 5, 5);
            g.FillEllipse(dot, 13.5f, 20, 5, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
