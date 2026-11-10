using System.Xml.Linq;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVUnattendedTests
{
    [Fact]
    public void AnswerFileCreatesLocalAccountAndOneTimeLoginWithoutLosingXmlCharacters()
    {
        var xml = HyperVUnattendedMediaBuilder.BuildAnswerFile("YueYu", "Test<&123", "zh-CN");
        var document = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        Assert.Equal("Test<&123", document.Descendants(ns + "Password").First().Element(ns + "Value")!.Value);
        Assert.Equal("1", document.Descendants(ns + "LogonCount").Single().Value);
        Assert.Equal("YueYu", document.Descendants(ns + "LocalAccount").Single().Element(ns + "Name")!.Value);
        Assert.Empty(document.Descendants(ns + "WillWipeDisk"));
        Assert.Contains("specialize", xml);
        Assert.Contains("oobeSystem", xml);
    }

    [Theory]
    [InlineData("bad/user")]
    [InlineData("Administrator")]
    [InlineData("")]
    public void RejectsInvalidLocalAccounts(string user)
    {
        Assert.Throws<ArgumentException>(() => HyperVUnattendedMediaBuilder.BuildAnswerFile(user, "Test1234!", "zh-CN"));
    }
}
