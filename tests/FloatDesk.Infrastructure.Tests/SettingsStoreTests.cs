using System.Text.Json;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using FloatDesk.Core.Contracts;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "FloatDeskTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsMatchSpecification()
    {
        var settings = AppSettings.CreateDefault(@"C:\Users\Test\Pictures");

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(56, settings.Orb.SizeDip);
        Assert.Equal(0.9, settings.Orb.Opacity);
        Assert.True(settings.InputLock.AutoEnabled);
        Assert.False(settings.Orb.ShowClock);
        Assert.Equal(0, settings.Capture.DelaySeconds);
        Assert.True(settings.Capture.OpenEditor);
        Assert.True(settings.Overlay.AlwaysOnTop);
        Assert.Equal(
            Enum.GetValues<WheelActionKind>(),
            settings.WheelActions);
    }

    [Fact]
    public async Task CorruptPrimaryLoadsBackup()
    {
        Directory.CreateDirectory(_root);
        var expected = AppSettings.CreateDefault();
        var store = new JsonSettingsStore(_root);
        await store.SaveAsync(expected, CancellationToken.None);
        await store.SaveAsync(expected with
        {
            Orb = expected.Orb with { ShowClock = true }
        }, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "settings.json"),
            "{ broken",
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Orb.ShowClock);
    }

    [Fact]
    public async Task BothCorruptReturnsValidDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "bad");
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json.bak"), "also bad");
        var store = new JsonSettingsStore(_root);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.NotEmpty(loaded.BrowserSites);
    }

    [Fact]
    public async Task FutureSchemaIsLoadedReadOnlyAndCannotBeOverwritten()
    {
        Directory.CreateDirectory(_root);
        var primary = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(primary, """{"schemaVersion":2}""");
        var store = new JsonSettingsStore(_root);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, loaded.SchemaVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(loaded, CancellationToken.None));
        Assert.Equal(
            """{"schemaVersion":2}""",
            await File.ReadAllTextAsync(primary));
    }

    [Fact]
    public void ValidatorAllowsMoreThan48WheelLaunchers()
    {
        var launchers = Enumerable.Range(0, 64)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                $"轮盘应用 {index + 1}",
                LauncherKind.Exe,
                $@"C:\Apps\wheel{index}.exe",
                [],
                null,
                index,
                ShowInWheel: true))
            .ToArray();
        var settings = AppSettings.CreateDefault() with
        {
            Launchers = launchers
        };

        var errors = SettingsValidator.Validate(settings);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatorRejectsOutOfRangeValues()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Orb = AppSettings.CreateDefault().Orb with
            {
                SizeDip = 8,
                Opacity = 1.2
            }
        };

        var errors = SettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("SizeDip", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Opacity", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacySettingsWithoutWheelActionsShowAllBuiltIns()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.SerializeToNode(
            AppSettings.CreateDefault(),
            options)!.AsObject();
        json.Remove("wheelActions");

        var migrated = SettingsMigrator.Deserialize(json.ToJsonString(), options);

        Assert.Equal(
            Enum.GetValues<WheelActionKind>(),
            migrated.WheelActions);
    }

    [Fact]
    public void LegacySettingsWithoutBrowserThemeFollowTheApp()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.SerializeToNode(
            AppSettings.CreateDefault(),
            options)!.AsObject();
        json.Remove("browserTheme");

        var migrated = SettingsMigrator.Deserialize(
            json.ToJsonString(),
            options);

        Assert.Equal(BrowserThemeMode.FollowApp, migrated.BrowserTheme);
    }

    [Fact]
    public async Task BrowserThemeRoundTripsWithoutSchemaChange()
    {
        using var store = new JsonSettingsStore(_root);
        var expected = AppSettings.CreateDefault() with
        {
            BrowserTheme = BrowserThemeMode.Dark
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(BrowserThemeMode.Dark, loaded.BrowserTheme);
    }

    [Fact]
    public async Task TranslationSettingsRoundTripWithoutSchemaChange()
    {
        using var store = new JsonSettingsStore(_root);
        var original = AppSettings.CreateDefault();
        var expected = original with
        {
            Translation = original.Translation with
            {
                SourceLanguage = "en",
                TargetLanguage = "ja",
                SubtitleRegion = new TranslationCaptureRegion("monitor-1", 0.1, 0.2, 0.5, 0.4),
                SubtitleOverlayPlacement = new TranslationOverlayPlacement("monitor-1", 0.2, 0.7)
            }
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(expected.Translation, loaded.Translation);
    }

    [Fact]
    public async Task TranslationSettingsRoundTripIncludesSubtitleAttachment()
    {
        using var store = new JsonSettingsStore(_root);
        var original = AppSettings.CreateDefault();
        var expected = original with
        {
            Translation = original.Translation with
            {
                SubtitleAttachment = SubtitleAttachmentMode.Below
            }
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(expected.Translation, loaded.Translation);
    }

    [Fact]
    public void ValidatorRejectsUndefinedBrowserTheme()
    {
        var settings = AppSettings.CreateDefault() with
        {
            BrowserTheme = (BrowserThemeMode)99
        };

        var errors = SettingsValidator.Validate(settings);

        Assert.Contains(
            errors,
            error => error.Contains("BrowserTheme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PositionAndRuleUpdatesBothSurviveInterleaving()
    {
        var store = new MemoryStore(AppSettings.CreateDefault());
        var coordinator = new SettingsCoordinator(store);
        await coordinator.InitializeAsync(CancellationToken.None);
        var rule = new AppRule(
            Guid.NewGuid(),
            @"C:\Apps\editor.exe",
            InputAction.LockEnglish,
            true);

        var position = coordinator.UpdateAsync(
            current => current with
            {
                Windows =
                [
                    new WindowPlacement("orb", null, 0.4, 0.7, 56, 56)
                ]
            },
            CancellationToken.None);
        var input = coordinator.UpdateAsync(
            current => current with
            {
                InputLock = current.InputLock with
                {
                    Rules = [rule]
                }
            },
            CancellationToken.None);
        await Task.WhenAll(position, input);

        Assert.Single(coordinator.Current.Windows);
        Assert.Equal(rule, Assert.Single(coordinator.Current.InputLock.Rules));
    }

    [Fact]
    public async Task FailedWriteDoesNotPublishNewSnapshot()
    {
        var original = AppSettings.CreateDefault();
        var store = new MemoryStore(original) { FailNextSave = true };
        var coordinator = new SettingsCoordinator(store);
        await coordinator.InitializeAsync(CancellationToken.None);

        var result = await coordinator.UpdateAsync(
            current => current with
            {
                Orb = current.Orb with { ShowClock = true }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(coordinator.Current.Orb.ShowClock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MemoryStore(AppSettings initial) : ISettingsStore
    {
        private AppSettings _value = initial;
        public bool FailNextSave { get; set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_value);

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("disk full");
            }

            _value = value;
            return Task.CompletedTask;
        }
    }
}
