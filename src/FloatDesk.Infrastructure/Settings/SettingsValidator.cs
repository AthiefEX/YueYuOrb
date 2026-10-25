using System.Text.RegularExpressions;
using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static partial class SettingsValidator
{
    public static IReadOnlyList<string> Validate(AppSettings value)
    {
        var errors = new List<string>();
        if (value.SchemaVersion != AppSettings.CurrentSchemaVersion)
            errors.Add("SchemaVersion must be 1.");
        if (value.Orb.SizeDip is < 32 or > 128)
            errors.Add("Orb.SizeDip must be between 32 and 128.");
        if (value.Orb.Opacity is < 0.3 or > 1.0)
            errors.Add("Orb.Opacity must be between 0.3 and 1.0.");
        if (!KlidPattern().IsMatch(value.InputLock.TargetKlid))
            errors.Add("InputLock.TargetKlid must be an eight-digit US layout ID.");
        if (value.Launchers.Count > 48)
            errors.Add("At most 48 launcher items are allowed.");
        if (value.Launchers.Any(item =>
                string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > 80 ||
                string.IsNullOrWhiteSpace(item.Path)))
            errors.Add("Launcher names must contain 1-80 characters and paths cannot be empty.");
        if (value.BrowserSites.Count is < 1 or > 10)
            errors.Add("BrowserSites must contain between 1 and 10 HTTPS sites.");
        if (value.BrowserSites.Any(site => site.Url.Scheme != Uri.UriSchemeHttps))
            errors.Add("Every BrowserSites URL must use HTTPS.");
        if (value.BrowserSites.Any(site =>
                string.IsNullOrWhiteSpace(site.Name) ||
                site.Name.Length > 40))
            errors.Add("Browser site names must contain 1-40 characters.");
        if (value.BrowserZoomPercent is < 75 or > 150)
            errors.Add("BrowserZoomPercent must be between 75 and 150.");
        if (value.Capture.DelaySeconds is not (0 or 3 or 5))
            errors.Add("Capture.DelaySeconds must be 0, 3, or 5.");
        if (!ColorPattern().IsMatch(value.Capture.AnnotationColor))
            errors.Add("Capture.AnnotationColor must use #RRGGBB.");
        if (value.Capture.StrokeWidthPx is < 1 or > 12)
            errors.Add("Capture.StrokeWidthPx must be between 1 and 12.");
        if (value.Capture.FontSizePx is < 10 or > 72)
            errors.Add("Capture.FontSizePx must be between 10 and 72.");
        if (value.Windows.Any(window =>
                window.RelativeX is < 0 or > 1 ||
                window.RelativeY is < 0 or > 1 ||
                window.WidthDip <= 0 ||
                window.HeightDip <= 0))
            errors.Add("Window placements contain invalid coordinates.");
        return errors;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex KlidPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}
