namespace FloatDesk.Core.Tests;

public sealed class BuildBaselineTests
{
    [Fact]
    public void ProductIdentity_is_stable()
    {
        Assert.Equal("月语悬浮球", GetConstant(nameof(ProductInfo.Name)));
        Assert.Equal("YueYuOrb", GetConstant(nameof(ProductInfo.EnglishName)));
        Assert.Equal("1.3.0", GetConstant(nameof(ProductInfo.Version)));
        Assert.Equal("七月风语", GetConstant(nameof(ProductInfo.Author)));
        Assert.Equal(
            "https://github.com/AthiefEX",
            GetConstant(nameof(ProductInfo.AuthorUrl)));
        Assert.Equal(
            "https://github.com/AthiefEX/YueYuOrb",
            GetConstant(nameof(ProductInfo.RepositoryUrl)));
        Assert.Equal(
            "GPL-3.0-only",
            GetConstant(nameof(ProductInfo.License)));
    }

    private static string GetConstant(string name) =>
        (string?)typeof(ProductInfo)
            .GetField(name, System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue()
        ?? throw new Xunit.Sdk.XunitException(
            $"ProductInfo.{name} is missing.");
}
