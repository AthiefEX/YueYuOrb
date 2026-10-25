using System.Windows;
using System.Windows.Media;
using FloatDesk.Core.Models;
using Microsoft.Win32;

namespace FloatDesk.App;

public static class ThemeService
{
    public static void Apply(AppTheme requested)
    {
        if (SystemParameters.HighContrast)
            return;
        var dark = requested == AppTheme.Dark ||
                   (requested == AppTheme.System && SystemUsesDarkTheme());
        SetBrush("SurfaceBrush", dark ? "#171A19" : "#F7F7F4");
        SetBrush("SurfaceRaisedBrush", dark ? "#232826" : "#FFFFFF");
        SetBrush("InkBrush", dark ? "#F2F4F3" : "#1D2623");
        SetBrush("MutedInkBrush", dark ? "#AAB7B2" : "#62706B");
    }

    private static bool SystemUsesDarkTheme()
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

    private static void SetBrush(string key, string color)
    {
        if (System.Windows.Application.Current.Resources[key] is not SolidColorBrush brush)
            return;
        var replacement = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        replacement.Freeze();
        System.Windows.Application.Current.Resources[key] = replacement;
    }
}
