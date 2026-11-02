using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CamTel;

/// <summary>Light/dark palette that follows the Windows "app mode" setting, live.</summary>
public static class Theme
{
    /// <summary>CAMTEL_THEME=light|dark forces a theme (screenshots, testing).</summary>
    private static readonly bool? Forced = Environment.GetEnvironmentVariable("CAMTEL_THEME") switch
    {
        "dark" => true,
        "light" => false,
        _ => null,
    };

    public static bool IsDark { get; private set; } = Forced ?? ReadSystemDark();

    /// <summary>Raised (on the UI thread that started the app) when Windows switches theme.</summary>
    public static event Action? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle)) return;
            if (Forced != null) return;
            var dark = ReadSystemDark();
            if (dark == IsDark) return;
            IsDark = dark;
            Changed?.Invoke();
        };
    }

    public static readonly Color Accent = Color.FromArgb(47, 128, 237);

    public static Color Bg => IsDark ? Color.FromArgb(22, 26, 36) : Color.FromArgb(243, 245, 249);
    public static Color Panel => IsDark ? Color.FromArgb(32, 37, 50) : Color.White;
    public static Color Card => IsDark ? Color.FromArgb(28, 33, 46) : Color.White;
    public static Color Text => IsDark ? Color.FromArgb(238, 241, 247) : Color.FromArgb(24, 28, 38);
    public static Color Muted => IsDark ? Color.FromArgb(150, 160, 180) : Color.FromArgb(93, 102, 121);
    public static Color Hover => IsDark ? Color.FromArgb(45, 52, 70) : Color.FromArgb(228, 233, 242);

    /// <summary>Neutral overlay used for idle round buttons (white on dark, black on light).</summary>
    public static Color Overlay(int alpha) => Color.FromArgb(alpha, IsDark ? Color.White : Color.Black);

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Dark or light title bar to match the window content (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(IntPtr hwnd)
    {
        var value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Menu colours for the current theme.</summary>
    public sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => IsDark ? Color.FromArgb(55, 62, 80) : Color.FromArgb(214, 219, 230);
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => IsDark ? Color.FromArgb(55, 62, 80) : Color.FromArgb(214, 219, 230);
        public override Color SeparatorLight => SeparatorDark;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
