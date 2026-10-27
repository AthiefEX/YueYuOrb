using System.Text.Json;
using FloatDesk.App.Translation;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class TranslationPrivacyTests
{
    [Fact]
    public void TranslationSettingsJsonCannotContainCredentialFields()
    {
        var json = JsonSerializer.Serialize(AppSettings.CreateDefault());

        Assert.DoesNotContain(
            "apiKey",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslationDiagnosticsAreContentFree()
    {
        var properties = TranslationDiagnostics.Build(
            TranslationProviderKind.MyMemory,
            TranslationPurpose.Subtitle,
            totalCharacters: 842,
            elapsedMilliseconds: 213,
            TranslationFailureKind.Network,
            retryCount: 1);

        var serialized = JsonSerializer.Serialize(properties);

        Assert.DoesNotContain(
            "Hello subtitle",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "apiKey",
            serialized,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "characterBucket",
            serialized,
            StringComparison.Ordinal);
        Assert.Contains(
            "500-1999",
            serialized,
            StringComparison.Ordinal);
        Assert.Contains(
            "Network",
            serialized,
            StringComparison.Ordinal);
        Assert.Equal(6, properties.Count);
    }

    [Fact]
    public void SettingsFreeAndPrivacyCopyIsExplicit()
    {
        var xaml = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));

        Assert.Contains("无需密钥", xaml, StringComparison.Ordinal);
        Assert.Contains("每日", xaml, StringComparison.Ordinal);
        Assert.Contains("截图不会上传", xaml, StringComparison.Ordinal);
        Assert.Contains("仅发送识别文字", xaml, StringComparison.Ordinal);
        Assert.Contains("导出或导入设置", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "测试连接会使用当前编辑值",
            xaml,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}'.");
    }
}
