using FloatDesk.App.Browser;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class BrowserThemeLifecycleTests
{
    [Fact]
    public async Task RequestDuringInitializationAppliesNewestSchemeBeforeAttachReturns()
    {
        var firstReplacementStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReplacement = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new RecordingThemeSurface
        {
            ReplaceScriptAsync = async (_, scheme) =>
            {
                if (scheme == BrowserColorScheme.Light)
                {
                    firstReplacementStarted.TrySetResult(true);
                    await releaseFirstReplacement.Task;
                    return "script-Light";
                }

                return $"script-{scheme}";
            }
        };
        using var lifecycle = new BrowserThemeLifecycle(
            BrowserColorScheme.Light);

        var attachTask = lifecycle.AttachAsync(surface);
        await firstReplacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.Request(BrowserColorScheme.Dark);
        releaseFirstReplacement.TrySetResult(true);
        await attachTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [BrowserColorScheme.Light, BrowserColorScheme.Dark],
            surface.PreferredSchemes);
        Assert.Equal(
            [BrowserColorScheme.Light, BrowserColorScheme.Dark],
            surface.ReplacementSchemes);
        Assert.Equal("script-Light", surface.Replacements[1].ExistingScriptId);
        Assert.Equal(BrowserColorScheme.Dark, surface.LastPreferredScheme);
        Assert.Equal(BrowserColorScheme.Dark, surface.LastReplacementScheme);
    }

    [Fact]
    public async Task DetachDuringReplacementPreventsOldSurfaceWorkAndSerializesNewAttach()
    {
        var replacementStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSurface = new RecordingThemeSurface();
        using var lifecycle = new BrowserThemeLifecycle(
            BrowserColorScheme.Light);
        await lifecycle.AttachAsync(oldSurface);

        oldSurface.ReplaceScriptAsync = (_, scheme) =>
        {
            if (scheme == BrowserColorScheme.Dark)
            {
                replacementStarted.TrySetResult(true);
                return AwaitReplacementAsync(oldSurface, releaseReplacement.Task);
            }

            return Task.FromResult($"old-{scheme}");
        };
        lifecycle.Request(BrowserColorScheme.Dark);
        var applyTask = lifecycle.ApplyAsync();
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.Detach();
        var newSurface = new RecordingThemeSurface();
        var attachNewSurfaceTask = lifecycle.AttachAsync(newSurface);

        Assert.False(attachNewSurfaceTask.IsCompleted);
        releaseReplacement.TrySetResult(true);
        await Task.WhenAll(applyTask, attachNewSurfaceTask)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            BrowserColorScheme.Dark,
            oldSurface.DocumentSchemes);
        Assert.Contains(
            oldSurface.Replacements,
            replacement =>
                replacement.ExistingScriptId == "script-Light" &&
                replacement.Scheme == BrowserColorScheme.Dark);
        Assert.Equal(BrowserColorScheme.Dark, newSurface.LastPreferredScheme);
        Assert.Equal(BrowserColorScheme.Dark, newSurface.LastReplacementScheme);
        Assert.Null(newSurface.Replacements.Single().ExistingScriptId);
    }

    [Fact]
    public async Task RequestAfterAttachAppliesNewestSchemeToCurrentDocument()
    {
        var surface = new RecordingThemeSurface();
        using var lifecycle = new BrowserThemeLifecycle(
            BrowserColorScheme.Light);
        await lifecycle.AttachAsync(surface);

        lifecycle.Request(BrowserColorScheme.Dark);
        await lifecycle.ApplyAsync();

        Assert.Equal(BrowserColorScheme.Dark, surface.LastPreferredScheme);
        Assert.Equal(BrowserColorScheme.Dark, surface.LastReplacementScheme);
        Assert.Contains(BrowserColorScheme.Dark, surface.DocumentSchemes);
    }

    [Fact]
    public async Task RepeatedApplyWithoutThemeChangeDoesNotReinjectDocumentScript()
    {
        var surface = new RecordingThemeSurface();
        using var lifecycle = new BrowserThemeLifecycle(
            BrowserColorScheme.Light);
        await lifecycle.AttachAsync(surface);
        lifecycle.Request(BrowserColorScheme.Dark);
        await lifecycle.ApplyAsync();
        surface.DocumentSchemes.Clear();

        await lifecycle.ApplyAsync();

        Assert.Empty(surface.DocumentSchemes);
    }

    [Fact]
    public async Task DisposeDuringReplacementStopsFurtherSurfaceWorkWithoutThrowing()
    {
        var replacementStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new RecordingThemeSurface();
        var lifecycle = new BrowserThemeLifecycle(BrowserColorScheme.Light);
        await lifecycle.AttachAsync(surface);

        surface.ReplaceScriptAsync = (_, scheme) =>
        {
            if (scheme == BrowserColorScheme.Dark)
            {
                replacementStarted.TrySetResult(true);
                return AwaitReplacementAsync(surface, releaseReplacement.Task);
            }

            return Task.FromResult($"script-{scheme}");
        };
        lifecycle.Request(BrowserColorScheme.Dark);
        var applyTask = lifecycle.ApplyAsync();
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifecycle.Dispose();
        releaseReplacement.TrySetResult(true);
        await applyTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            BrowserColorScheme.Dark,
            surface.DocumentSchemes);
        lifecycle.Request(BrowserColorScheme.Light);
        await lifecycle.ApplyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(
            BrowserColorScheme.Light,
            surface.DocumentSchemes);
    }

    private static async Task<string> AwaitReplacementAsync(
        RecordingThemeSurface surface,
        Task releaseTask)
    {
        await releaseTask;
        return $"script-{surface.Replacements[^1].Scheme}";
    }

    private sealed class RecordingThemeSurface : IBrowserThemeSurface
    {
        public List<BrowserColorScheme> PreferredSchemes { get; } = [];

        public List<BrowserColorScheme> DocumentSchemes { get; } = [];

        public IEnumerable<BrowserColorScheme> ReplacementSchemes =>
            Replacements.Select(replacement => replacement.Scheme);

        public List<(string? ExistingScriptId, BrowserColorScheme Scheme)>
            Replacements { get; } = [];

        public Func<string?, BrowserColorScheme, Task<string>>
            ReplaceScriptAsync { get; set; } =
                (_, scheme) => Task.FromResult($"script-{scheme}");

        public BrowserColorScheme? LastPreferredScheme =>
            PreferredSchemes.LastOrDefault() is var scheme &&
            PreferredSchemes.Count > 0
                ? scheme
                : null;

        public BrowserColorScheme? LastReplacementScheme =>
            Replacements.Count > 0 ? Replacements[^1].Scheme : null;

        public void ApplyPreferredColorScheme(BrowserColorScheme scheme) =>
            PreferredSchemes.Add(scheme);

        public async Task<string> ReplaceDocumentScriptAsync(
            string? existingScriptId,
            BrowserColorScheme scheme)
        {
            Replacements.Add((existingScriptId, scheme));
            return await ReplaceScriptAsync(existingScriptId, scheme);
        }

        public Task ApplyCurrentDocumentAsync(BrowserColorScheme scheme)
        {
            DocumentSchemes.Add(scheme);
            return Task.CompletedTask;
        }
    }
}
