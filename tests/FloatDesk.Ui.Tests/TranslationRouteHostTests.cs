using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class TranslationRouteHostTests
{
    [Fact]
    public void DefaultDeliveryAdapterCommitsWithoutAnExplicitGuard()
    {
        var recordingHost = new RecordingRouteHost();
        ITranslationRouteHost host = recordingHost;
        var operation = new TranslationRouteHandle(
            new TranslationOverlayOperationToken(1),
            new PixelPoint(10, 20));

        var result = host.ShowSubtitleTextWithResult(
            operation,
            "translated subtitle");

        Assert.True(result.Displayed);
        Assert.Equal(
            TranslationSubtitlePlacementStatus.Ready,
            result.PlacementStatus);
        Assert.Equal("translated subtitle", recordingHost.LastStatus);
    }

    private sealed class RecordingRouteHost : ITranslationRouteHost
    {
        public string? LastStatus { get; private set; }

        public void ApplySettings(AppSettings settings)
        {
        }

        public TranslationRouteHandle ShowStatus(
            PixelPoint anchor,
            string status) =>
            new(new TranslationOverlayOperationToken(1), anchor);

        public void ShowStatus(
            TranslationRouteHandle operation,
            string status) =>
            LastStatus = status;

        public void ShowResult(
            TranslationRouteHandle operation,
            TranslationResponse response)
        {
        }

        public void ShowError(
            TranslationRouteHandle operation,
            string message)
        {
        }

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                TranslationResponse.Ok(
                    text,
                    text,
                    "",
                    "test"));

        public void DismissActiveOperation()
        {
        }
    }
}
