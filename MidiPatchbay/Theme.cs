using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MidiPatchbay;

public enum ThemeMode { System, Light, Dark }

/// <summary>All colors the UI uses, for one theme.</summary>
public sealed class Palette
{
    public bool IsDark;
    public Color Surface;      // panels, form background
    public Color Window;       // lists, grid, text boxes
    public Color Text;
    public Color Muted;
    public Color GridLine;
    public Color Accent;
    public Color AccentHover;  // outline of an unrouted cell under the mouse
    public Color HeaderHover;  // row/column header under the mouse
    public Color VirtualTint;  // header background for virtual ports
    public Color CellHover;
    public Color BoxBorder;
    public Color RoutedOffline;
    public Color Led;
    public Color LedIdle;
    public Color LedOffline;
    public Color ButtonBack;
    public Color ButtonHover;
    public Color ButtonBorder;
    public Color Selection;
    public Color SelectionText;

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Surface = Color.FromArgb(243, 243, 243),
        Window = Color.White,
        Text = Color.FromArgb(28, 28, 28),
        Muted = Color.FromArgb(110, 110, 110),
        GridLine = Color.FromArgb(225, 225, 225),
        Accent = Color.FromArgb(0, 120, 212),
        AccentHover = Color.FromArgb(0, 120, 212),
        HeaderHover = Color.FromArgb(204, 228, 247),
        VirtualTint = Color.FromArgb(240, 246, 252),
        CellHover = Color.FromArgb(240, 247, 253),
        BoxBorder = Color.FromArgb(200, 200, 200),
        RoutedOffline = Color.Silver,
        Led = Color.FromArgb(16, 185, 80),
        LedIdle = Color.FromArgb(215, 215, 215),
        LedOffline = Color.FromArgb(235, 235, 235),
        ButtonBack = Color.FromArgb(253, 253, 253),
        ButtonHover = Color.FromArgb(234, 234, 234),
        ButtonBorder = Color.FromArgb(204, 204, 204),
        Selection = Color.FromArgb(204, 228, 247),
        SelectionText = Color.FromArgb(28, 28, 28),
    };

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Surface = Color.FromArgb(32, 32, 32),
        Window = Color.FromArgb(25, 25, 25),
        Text = Color.FromArgb(235, 235, 235),
        Muted = Color.FromArgb(160, 160, 160),
        GridLine = Color.FromArgb(55, 55, 55),
        Accent = Color.FromArgb(0, 120, 212),
        AccentHover = Color.FromArgb(96, 170, 240),
        HeaderHover = Color.FromArgb(28, 58, 88),
        VirtualTint = Color.FromArgb(31, 38, 48),
        CellHover = Color.FromArgb(36, 44, 54),
        BoxBorder = Color.FromArgb(95, 95, 95),
        RoutedOffline = Color.FromArgb(90, 90, 90),
        Led = Color.FromArgb(40, 210, 100),
        LedIdle = Color.FromArgb(70, 70, 70),
        LedOffline = Color.FromArgb(48, 48, 48),
        ButtonBack = Color.FromArgb(55, 55, 55),
        ButtonHover = Color.FromArgb(70, 70, 70),
        ButtonBorder = Color.FromArgb(85, 85, 85),
        Selection = Color.FromArgb(0, 84, 153),
        SelectionText = Color.White,
    };

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Palette For(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => Dark,
        ThemeMode.Light => Light,
        _ => SystemPrefersDark() ? Dark : Light,
    };
}

/// <summary>Applies a palette to standard WinForms controls, including the parts Win32 draws itself.</summary>
public static class ThemeApplier
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    public const string MutedTag = "muted";

    public static void TitleBar(Form f, Palette p)
    {
        if (!f.IsHandleCreated) return;
        int on = p.IsDark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    /// <summary>Dark scrollbars etc. for native controls.</summary>
    public static void NativeTheme(Control c, Palette p)
    {
        if (!c.IsHandleCreated) return;
        SetWindowTheme(c.Handle, p.IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public static void Apply(Control root, Palette p)
    {
        Visit(root, p);
    }

    static void Visit(Control c, Palette p)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = p.ButtonBack;
                b.ForeColor = p.Text;
                b.FlatAppearance.BorderColor = p.ButtonBorder;
                b.FlatAppearance.MouseOverBackColor = p.ButtonHover;
                b.FlatAppearance.MouseDownBackColor = p.ButtonBorder;
                b.UseVisualStyleBackColor = false;
                break;
            case CheckBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.FlatAppearance.BorderColor = p.BoxBorder;
                cb.FlatAppearance.CheckedBackColor = p.Window;
                cb.BackColor = p.Surface;
                cb.ForeColor = p.Text;
                break;
            case TextBox tb:
                tb.BackColor = p.Window;
                tb.ForeColor = p.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                NativeTheme(tb, p);
                break;
            case ListView lv:
                lv.BackColor = p.Window;
                lv.ForeColor = p.Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                NativeTheme(lv, p);
                lv.Invalidate();
                break;
            case DataGridView g:
                g.BackgroundColor = p.Window;
                g.DefaultCellStyle.BackColor = p.Window;
                g.DefaultCellStyle.ForeColor = p.Text;
                g.GridColor = p.GridLine;
                NativeTheme(g, p);
                break;
            case StatusStrip ss:
                ss.Renderer = new ThemedToolStripRenderer(p);
                ss.BackColor = p.Surface;
                ss.ForeColor = p.Text;
                break;
            case Label l:
                l.ForeColor = Equals(l.Tag, MutedTag) ? p.Muted : p.Text;
                l.BackColor = Color.Transparent;
                break;
            case SplitContainer sc:
                sc.BackColor = p.GridLine; // the splitter bar
                sc.Panel1.BackColor = p.Surface;
                sc.Panel2.BackColor = p.Surface;
                break;
            default:
                c.BackColor = p.Surface;
                c.ForeColor = p.Text;
                break;
        }
        foreach (Control child in c.Controls) Visit(child, p);
    }
}

public sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
{
    readonly Palette _p;
    public ThemedToolStripRenderer(Palette p) : base(new ThemedColorTable(p)) { _p = p; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _p.Text : _p.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using var br = new SolidBrush(_p.Selection);
        e.Graphics.FillRectangle(br, r);
        using var pen = new Pen(_p.Text, 1.6f);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
        });
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is StatusStrip) return; // no white line on top of the status bar
        base.OnRenderToolStripBorder(e);
    }

    sealed class ThemedColorTable : ProfessionalColorTable
    {
        readonly Palette p;
        public ThemedColorTable(Palette p) { this.p = p; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => p.Window;
        public override Color ImageMarginGradientBegin => p.Window;
        public override Color ImageMarginGradientMiddle => p.Window;
        public override Color ImageMarginGradientEnd => p.Window;
        public override Color MenuBorder => p.ButtonBorder;
        public override Color MenuItemBorder => p.Selection;
        public override Color MenuItemSelected => p.IsDark ? p.ButtonHover : p.Selection;
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuItemPressedGradientBegin => p.ButtonHover;
        public override Color MenuItemPressedGradientEnd => p.ButtonHover;
        public override Color ButtonSelectedHighlight => p.ButtonHover;
        public override Color ButtonSelectedGradientBegin => p.ButtonHover;
        public override Color ButtonSelectedGradientEnd => p.ButtonHover;
        public override Color ButtonSelectedBorder => p.ButtonBorder;
        public override Color ButtonPressedGradientBegin => p.ButtonBorder;
        public override Color ButtonPressedGradientEnd => p.ButtonBorder;
        public override Color SeparatorDark => p.GridLine;
        public override Color SeparatorLight => p.GridLine;
        public override Color StatusStripGradientBegin => p.Surface;
        public override Color StatusStripGradientEnd => p.Surface;
        public override Color CheckBackground => p.Selection;
        public override Color CheckSelectedBackground => p.Selection;
        public override Color CheckPressedBackground => p.Selection;
    }
}
