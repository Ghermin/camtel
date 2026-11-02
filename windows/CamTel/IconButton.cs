using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CamTel;

/// <summary>Round button drawing a Material icon (or a short caption), like the Android app's buttons.</summary>
public sealed class IconButton : Control
{
    public static Color Accent => Theme.Accent;

    private GraphicsPath? _icon;
    private bool _active;
    private bool _hover;
    private bool _pressed;

    public IconButton(string? iconPath = null, string? caption = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 46);
        Margin = new Padding(3);
        Cursor = Cursors.Hand;
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 9.5f);
        IconPath = iconPath;
        Text = caption ?? "";
    }

    public string? IconPath
    {
        set
        {
            _icon?.Dispose();
            _icon = value == null ? null : SvgPath.Parse(value);
            Invalidate();
        }
    }

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var alpha = Enabled ? 255 : 90;

        Color bg;
        if (_active) bg = Color.FromArgb(alpha, Accent);
        else if (_pressed && Enabled) bg = Theme.Overlay(0x50);
        else if (_hover && Enabled) bg = Theme.Overlay(0x30);
        else bg = Theme.Overlay(Enabled ? 0x1C : 0x0C);
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);

        // White on the blue "active" state, theme text colour otherwise (same as the phone).
        var ink = _active ? Color.White : Theme.Text;
        using var fg = new SolidBrush(Color.FromArgb(alpha, ink));
        if (_icon != null)
        {
            var state = g.Save();
            var scale = Width * 0.5f / 24f;
            g.TranslateTransform(Width / 2f - 12 * scale, Height / 2f - 12 * scale);
            g.ScaleTransform(scale, scale);
            g.FillPath(fg, _icon);
            g.Restore(state);
        }
        else if (Text.Length > 0)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.FromArgb(alpha, ink),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Minimal SVG path-data parser (M, L, H, V, C, S, Z; absolute and relative).</summary>
public static class SvgPath
{
    private static readonly Regex Token = new(@"[MmLlHhVvCcSsZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");

    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = Token.Matches(d).Select(m => m.Value).ToList();
        var i = 0;
        var cmd = 'M';
        PointF cur = default, start = default, lastCtrl = default;
        var prevWasCurve = false;

        float Num() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        bool HasNum() => i < tokens.Count && !char.IsLetter(tokens[i][0]);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            var rel = char.IsLower(cmd);
            PointF P(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    cur = P(Num(), Num());
                    start = cur;
                    path.StartFigure();
                    cmd = rel ? 'l' : 'L'; // subsequent pairs are implicit line-tos
                    prevWasCurve = false;
                    break;
                case 'L':
                {
                    var p = P(Num(), Num());
                    path.AddLine(cur, p);
                    cur = p;
                    prevWasCurve = false;
                    break;
                }
                case 'H':
                {
                    var x = Num();
                    var p = new PointF(rel ? cur.X + x : x, cur.Y);
                    path.AddLine(cur, p);
                    cur = p;
                    prevWasCurve = false;
                    break;
                }
                case 'V':
                {
                    var y = Num();
                    var p = new PointF(cur.X, rel ? cur.Y + y : y);
                    path.AddLine(cur, p);
                    cur = p;
                    prevWasCurve = false;
                    break;
                }
                case 'C':
                {
                    var c1 = P(Num(), Num());
                    var c2 = P(Num(), Num());
                    var p = P(Num(), Num());
                    path.AddBezier(cur, c1, c2, p);
                    lastCtrl = c2;
                    cur = p;
                    prevWasCurve = true;
                    break;
                }
                case 'S':
                {
                    var c1 = prevWasCurve ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                    var c2 = P(Num(), Num());
                    var p = P(Num(), Num());
                    path.AddBezier(cur, c1, c2, p);
                    lastCtrl = c2;
                    cur = p;
                    prevWasCurve = true;
                    break;
                }
                case 'Z':
                    path.CloseFigure();
                    cur = start;
                    prevWasCurve = false;
                    // A command letter must follow; guard against malformed data.
                    if (HasNum()) cmd = rel ? 'l' : 'L';
                    break;
                default:
                    i++;
                    break;
            }
        }
        return path;
    }
}
