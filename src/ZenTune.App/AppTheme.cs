using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using ZenTune.Core;

namespace ZenTune.App;

/// <summary>One color theme: an accent color (buttons, switches, icons) and a soft gradient over the whole window.</summary>
internal sealed record ThemeDef(string Id, string Name, Color Accent, Color[] Back);

/// <summary>
/// Color themes. The choice is saved in theme.txt and read when ZenTune starts, so a change needs a restart.
/// "Default" changes nothing at all.
/// </summary>
internal static class AppTheme
{
    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    public static readonly ThemeDef[] All =
    [
        new("default", "Default", C(96, 205, 255), []),
        new("gamer", "Gamer", C(43, 217, 107), [C(0, 52, 24), C(0, 22, 18)]),
        new("vibrant", "Vibrant", C(204, 92, 255), [C(96, 20, 150), C(190, 30, 120), C(20, 60, 160)]),
        new("ocean", "Ocean", C(40, 190, 235), [C(4, 48, 90), C(0, 106, 122)]),
        new("sunset", "Sunset", C(255, 146, 64), [C(140, 44, 16), C(140, 20, 90), C(48, 20, 112)]),
        new("crimson", "Crimson", C(240, 76, 88), [C(112, 10, 22), C(34, 4, 16)]),
    ];

    public static ThemeDef Current { get; private set; } = All[0];

    private static string FilePath => Path.Combine(AppPaths.Root, "theme.txt");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var id = File.ReadAllText(FilePath).Trim();
            Current = All.FirstOrDefault(t => t.Id == id) ?? All[0];
        }
        catch { Current = All[0]; }
    }

    public static void Save(ThemeDef theme)
    {
        File.WriteAllText(FilePath, theme.Id);
    }

    /// <summary>Sets the accent colors. Call before the window is created.</summary>
    public static void ApplyResources()
    {
        try
        {
            if (Current.Id == "default") return;

            var r = Application.Current.Resources;
            var a = Current.Accent;
            var light1 = Mix(a, Colors_White, 0.12);
            var light3 = Mix(a, Colors_White, 0.30);
            var dark1 = Mix(a, Colors_Black, 0.20);
            var dark2 = Mix(a, Colors_Black, 0.40);
            var dark3 = Mix(a, Colors_Black, 0.60);

            r["SystemAccentColor"] = a;
            r["SystemAccentColorLight1"] = light1;
            r["SystemAccentColorLight2"] = a;
            r["SystemAccentColorLight3"] = light3;
            r["SystemAccentColorDark1"] = dark1;
            r["SystemAccentColorDark2"] = dark2;
            r["SystemAccentColorDark3"] = dark3;

            r["AccentFillColorDefaultBrush"] = new SolidColorBrush(a);
            r["AccentFillColorSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(230, a.R, a.G, a.B));
            r["AccentFillColorTertiaryBrush"] = new SolidColorBrush(Color.FromArgb(204, a.R, a.G, a.B));
            r["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(light3);
            r["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(light3);
            r["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(a);

            // Let the gradient show through in the content area
            r["NavigationViewContentBackground"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            r["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }
        catch { /* a theme must never stop the app from starting */ }
    }

    /// <summary>Puts the soft gradient behind everything in the window.</summary>
    public static void ApplyBackground(Panel root)
    {
        try
        {
            if (Current.Back.Length == 0) return;
            root.Background = Gradient(Current, 0x78);
        }
        catch { /* the window just stays plain */ }
    }

    /// <summary>A gradient with the given opacity (0x78 over the window, 0xFF for the small previews).</summary>
    public static LinearGradientBrush Gradient(ThemeDef theme, byte alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };

        var colors = theme.Back.Length > 0 ? theme.Back : [C(60, 60, 64), C(38, 38, 42)];
        for (int i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(alpha, c.R, c.G, c.B),
                Offset = colors.Length == 1 ? 0 : (double)i / (colors.Length - 1),
            });
        }
        return brush;
    }

    private static readonly Color Colors_White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Colors_Black = Color.FromArgb(255, 0, 0, 0);

    private static Color Mix(Color from, Color to, double t) => Color.FromArgb(
        255,
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));
}
