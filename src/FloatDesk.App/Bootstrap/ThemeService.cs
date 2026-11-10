using System.Windows;
using System.Windows.Media;
using FloatDesk.Core.Models;
using Microsoft.Win32;
using System.Windows.Media.Imaging;

namespace FloatDesk.App;

public static class ThemeService
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string Light, string Dark)>
        {
            ["AccentBrush"] = ("#2176E8", "#65B0FF"),
            ["AccentDarkBrush"] = ("#175FC2", "#8AC7FF"),
            ["AccentSoftBrush"] = ("#E7F2FF", "#223B60"),
            ["AccentPaleBrush"] = ("#F2F7FF", "#182943"),
            ["AccentOutlineBrush"] = ("#B7D4F8", "#476995"),
            ["SurfaceBrush"] = ("#F4F8FE", "#10151C"),
            ["SurfaceRaisedBrush"] = ("#FFFFFF", "#19222D"),
            ["SurfaceMutedBrush"] = ("#EDF5FF", "#202B38"),
            ["InkBrush"] = ("#17243B", "#F1F6FC"),
            ["MutedInkBrush"] = ("#64738C", "#AAB9CC"),
            ["SubtleInkBrush"] = ("#8190A6", "#8494A8"),
            ["ControlBorderBrush"] = ("#224C75A9", "#557FA3C9"),
            ["ControlHoverBrush"] = ("#EDF5FF", "#26364A"),
            ["ControlPressedBrush"] = ("#DCEBFF", "#30445E"),
            ["SettingsCardBrush"] = ("#F7FFFFFF", "#F21B2531"),
            ["SettingsCardSubtleBrush"] = ("#EFFFFFFF", "#E61B2531"),
            ["SettingsFieldBrush"] = ("#F8FFFFFF", "#EA202B38"),
            ["SettingsHeaderBrush"] = ("#F5FBFF", "#ED17212C"),
            ["SettingsInkBrush"] = ("#17243B", "#F1F6FC"),
            ["SettingsMutedBrush"] = ("#64738C", "#AAB9CC"),
            ["SettingsSubtleBrush"] = ("#8190A6", "#8494A8"),
            ["SettingsAccentBrush"] = ("#2176E8", "#65B0FF"),
            ["SettingsAccentDarkBrush"] = ("#175FC2", "#8AC7FF"),
            ["SettingsAccentSoftBrush"] = ("#E7F2FF", "#223B60"),
            ["SettingsAccentPaleBrush"] = ("#F2F7FF", "#182943"),
            ["SettingsAccentOutlineBrush"] = ("#B7D4F8", "#476995"),
            ["SettingsLineBrush"] = ("#1F4D78A9", "#3D7FA3C9"),
            ["SettingsHoverBrush"] = ("#EDF5FF", "#26364A"),
            ["SettingsPressedBrush"] = ("#DCEBFF", "#30445E"),
            ["SettingsButtonBrush"] = ("#F9FFFFFF", "#F21E2A37"),
            ["SettingsNavBarBrush"] = ("#C9F7FBFF", "#D9202B38"),
            ["SettingsNavBorderBrush"] = ("#72FFFFFF", "#355E83AA"),
            ["SettingsNavHoverBrush"] = ("#66FFFFFF", "#30445E"),
            ["SettingsSwitchOffBrush"] = ("#BCC7D4", "#566579"),
            ["SettingsSwitchKnobBrush"] = ("#FFFFFF", "#EAF2FC"),
            ["SettingsCaptionInkBrush"] = ("#53627A", "#D7E4F3"),
            ["SettingsCaptionHoverBrush"] = ("#80FFFFFF", "#2C3B4D"),
            ["SettingsCaptionPressedBrush"] = ("#B8E7F1FF", "#3A4E66"),
            ["SettingsPopupBrush"] = ("#FEFFFFFF", "#FF1B2531"),
            ["SettingsGridRowBrush"] = ("#F9FFFFFF", "#F21B2531"),
            ["SettingsGridAltRowBrush"] = ("#66F5F9FF", "#66233042"),
            ["SettingsGridHoverBrush"] = ("#77EFF6FF", "#7730445E"),
            ["SettingsToolbarBrush"] = ("#F2F7FF", "#1B2735"),
            ["SettingsScrollTrackBrush"] = ("#0A2176E8", "#0A65B0FF"),
            ["SettingsScrollThumbBrush"] = ("#8CB7D4F0", "#7E7F98B2"),
            ["SettingsScrollThumbHoverBrush"] = ("#697E99B8", "#A6A8BED6"),
            ["SettingsHeroBrush"] = ("#EEF7FF", "#182535"),
            ["SettingsAtmosphereBrush"] = ("#DDEBFA", "#182535"),
            ["SettingsShellBrush"] = ("#FAFCFF", "#121922"),
            ["SettingsWindowBackgroundBrush"] = ("#F4F8FE", "#10151C"),
            ["ToolWindowTitleBarBrush"] = ("#EEF6FF", "#172535"),
            ["ToolWindowContentBrush"] = ("#F8FBFF", "#121922"),
            ["ToolWindowInsetBrush"] = ("#F2F7FF", "#1B2735"),
            ["ToolWindowEditorBrush"] = ("#FFFFFF", "#18232E"),
            ["ToolWindowProxyBrush"] = ("#EAF3FD", "#202D3C"),
            ["MenuLabelBrush"] = ("#F5FFFFFF", "#E626303C"),
            ["MenuStrongBrush"] = ("#18345B", "#F4F8FF"),
            ["MenuMutedBrush"] = ("#667B92B7", "#AAB9CC"),
            ["MenuDockGlassBrush"] = ("#E9FFFFFF", "#ED202A36"),
            ["MenuActionBeamBrush"] = ("#34FFFFFF", "#26A9D8FF"),
            ["MenuDockButtonBrush"] = ("#D9FFFFFF", "#E6253040"),
            ["MenuDockButtonHoverBrush"] = ("#FFFFFFFF", "#F2384659")
        };

    public static void ApplyToResources(
        ResourceDictionary resources,
        AppTheme requested,
        bool highContrast,
        bool systemUsesDarkTheme)
    {
        if (highContrast)
            return;
        var dark = requested == AppTheme.Dark ||
                   (requested == AppTheme.System && systemUsesDarkTheme);
        foreach (var (key, colors) in Palette)
            SetBrush(resources, key, dark ? colors.Dark : colors.Light);
    }

    public static void Apply(AppTheme requested)
    {
        ApplyToResources(
            System.Windows.Application.Current.Resources,
            requested,
            SystemParameters.HighContrast,
            SystemUsesDarkTheme());
    }

    public static void ApplyCustomBackground(AppSettings settings, string assetsDirectory)
    {
        if (Application.Current is not { } app) return;
        var dark = settings.General.Theme == AppTheme.Dark || settings.General.Theme == AppTheme.System && SystemUsesDarkTheme();
        foreach (var key in new[] { "SettingsWindowBackgroundBrush", "SettingsShellBrush", "ToolWindowContentBrush", "SettingsCardBrush" })
        {
            if (SystemParameters.HighContrast) app.Resources[key] = SystemColors.WindowBrush;
            else { var colors = Palette[key]; SetBrush(app.Resources, key, dark ? colors.Dark : colors.Light); }
        }
        if (SystemParameters.HighContrast) return;
        var path = Path.Combine(assetsDirectory, "background.png");
        if (!settings.CustomBackground.Enabled || !File.Exists(path)) return;
        try
        {
            var frame = new BitmapImage(); frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad; frame.CreateOptions = BitmapCreateOptions.IgnoreImageCache; frame.UriSource = new Uri(path); frame.DecodePixelWidth = 2048; frame.EndInit(); frame.Freeze();
            foreach (var key in new[] { "SettingsWindowBackgroundBrush", "SettingsShellBrush", "ToolWindowContentBrush" })
            {
                var colors = Palette[key];
                var color = (Color)ColorConverter.ConvertFromString(dark ? colors.Dark : colors.Light);
                var bounds = new Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
                var drawing = new DrawingGroup();
                drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(bounds)));
                var image = new DrawingGroup { Opacity = Math.Clamp(settings.CustomBackground.Opacity, 0, 1) };
                image.Children.Add(new ImageDrawing(frame, bounds)); drawing.Children.Add(image);
                var brush = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill }; brush.Freeze(); app.Resources[key] = brush;
            }
            var cardColors = Palette["SettingsCardBrush"];
            var cardColor = (Color)ColorConverter.ConvertFromString(dark ? cardColors.Dark : cardColors.Light);
            cardColor.A = 190;
            var cardBrush = new SolidColorBrush(cardColor); cardBrush.Freeze(); app.Resources["SettingsCardBrush"] = cardBrush;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        { /* Missing or invalid images leave the standard theme intact. */ }
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(
                key?.GetValue("AppsUseLightTheme", 1),
                System.Globalization.CultureInfo.InvariantCulture) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        string color)
    {
        var replacement = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        replacement.Freeze();
        resources[key] = replacement;
    }
}
