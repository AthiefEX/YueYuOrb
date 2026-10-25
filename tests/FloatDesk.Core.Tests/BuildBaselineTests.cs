namespace FloatDesk.Core.Tests;

public sealed class BuildBaselineTests
{
    [Fact]
    public void ProductIdentity_is_stable()
    {
        Assert.Equal("FloatDesk", ProductInfo.Name);
        Assert.Equal("1.0.0", ProductInfo.Version);
    }
}
