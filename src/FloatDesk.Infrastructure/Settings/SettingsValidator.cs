using System.Text.RegularExpressions;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

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
        if (!Enum.IsDefined(value.Orb.DisplayMode))
            errors.Add("Orb.DisplayMode must be Classic or Crystal.");
        if (!KlidPattern().IsMatch(value.InputLock.TargetKlid))
            errors.Add("InputLock.TargetKlid must be an eight-digit US layout ID.");
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
        if (!Enum.IsDefined(value.BrowserTheme))
            errors.Add("BrowserTheme must be FollowApp, Light, or Dark.");
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
        if (value.WheelActions is null ||
            value.WheelActions.Distinct().Count() != value.WheelActions.Count ||
            value.WheelActions.Any(action => !Enum.IsDefined(action)))
        {
            errors.Add("WheelActions contain invalid or duplicate actions.");
        }
        var translation = value.Translation;
        ValidateAudio(value, errors);
        if (translation is null)
        {
            errors.Add("Translation settings are required.");
            return errors;
        }
        if (!Enum.IsDefined(translation.Provider))
            errors.Add("Translation.Provider is invalid.");
        if (!TranslationLanguages.IsKnown(translation.SourceLanguage))
            errors.Add("Translation.SourceLanguage must be a known language tag.");
        if (!TranslationLanguages.IsKnown(translation.TargetLanguage) ||
            translation.TargetLanguage.Equals(TranslationLanguages.Auto, StringComparison.OrdinalIgnoreCase))
            errors.Add("Translation.TargetLanguage must be a known target language.");
        if (translation.SubtitleRefreshIntervalMs is < 100 or > 1500)
            errors.Add("Translation.SubtitleRefreshIntervalMs must be between 100 and 1500.");
        if (!Enum.IsDefined(translation.SubtitleAttachment))
            errors.Add("Translation.SubtitleAttachment must be Auto, Above, or Below.");
        if (!double.IsFinite(translation.SubtitleOverlayOpacity) ||
            translation.SubtitleOverlayOpacity is < 0.6 or > 1.0)
            errors.Add("Translation.SubtitleOverlayOpacity must be between 0.60 and 1.00.");
        if (!double.IsFinite(translation.SubtitleFontSizeDip) ||
            translation.SubtitleFontSizeDip is < 14 or > 36)
            errors.Add("Translation.SubtitleFontSizeDip must be between 14 and 36.");
        if (translation.Provider is not TranslationProviderKind.MyMemory &&
            !TranslationEndpointPolicy.TryNormalizeHttps(translation.Endpoint, out _, out var endpointError))
            errors.Add($"Translation.Endpoint: {endpointError}");
        if (translation.Provider is TranslationProviderKind.OpenAiCompatible &&
            string.IsNullOrWhiteSpace(translation.Model))
            errors.Add("Translation.Model is required.");
        if (translation.SubtitleRegion is { } region &&
            (string.IsNullOrWhiteSpace(region.MonitorId) ||
             !IsRelative(region.RelativeX) || !IsRelative(region.RelativeY) ||
             !IsRelative(region.RelativeWidth) || !IsRelative(region.RelativeHeight) ||
             region.RelativeWidth == 0 || region.RelativeHeight == 0 ||
             region.RelativeX + region.RelativeWidth > 1 ||
             region.RelativeY + region.RelativeHeight > 1))
            errors.Add("Translation.SubtitleRegion has invalid coordinates.");
        if (translation.SubtitleOverlayPlacement is { } placement &&
            (string.IsNullOrWhiteSpace(placement.MonitorId) ||
             !IsRelative(placement.RelativeX) || !IsRelative(placement.RelativeY)))
            errors.Add("Translation.SubtitleOverlayPlacement has invalid coordinates.");
        return errors;
    }

    private static bool IsRelative(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    [GeneratedRegex("^[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex KlidPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}
