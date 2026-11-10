using System.Globalization;
using System.Xml.Linq;

namespace FloatDesk.Windows.DesktopSessions;

public static class HyperVUnattendedMediaBuilder
{
    private static readonly System.Buffers.SearchValues<char> InvalidAccountCharacters = System.Buffers.SearchValues.Create("\"/\\[]:;|=,+*?<>@");
    private static readonly XNamespace Namespace = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    // Return transient XML to the provisioning layer. Never serialize it into application settings or logs.
    public static string BuildAnswerFile(string userName, string password, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (userName.Length > 20 || userName.EndsWith('.') ||
            userName.AsSpan().ContainsAny(InvalidAccountCharacters) || userName.Any(char.IsControl) ||
            userName.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
            userName.Equals("Guest", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a valid new local account name.", nameof(userName));
        _ = CultureInfo.GetCultureInfo(locale);
        var shell = Component("Microsoft-Windows-Shell-Setup",
            Element("UserAccounts", Element("LocalAccounts",
                new XElement(Namespace + "LocalAccount", new XAttribute(Wcm + "action", "add"),
                    Element("Password", Element("Value", password), Element("PlainText", "true")),
                    Element("Name", userName), Element("DisplayName", userName), Element("Group", "Administrators")))),
            Element("AutoLogon", Element("Password", Element("Value", password), Element("PlainText", "true")),
                Element("Username", userName), Element("Enabled", "true"), Element("LogonCount", "1")),
            Element("OOBE", Element("HideEULAPage", "true"), Element("HideOnlineAccountScreens", "true"),
                Element("HideWirelessSetupInOOBE", "true"), Element("ProtectYourPC", "3")));
        return new XDocument(new XElement(Namespace + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
            new XElement(Namespace + "settings", new XAttribute("pass", "specialize"),
                Component("Microsoft-Windows-Shell-Setup", Element("ComputerName", "*"))),
            new XElement(Namespace + "settings", new XAttribute("pass", "oobeSystem"),
                Component("Microsoft-Windows-International-Core", Element("InputLocale", locale),
                    Element("SystemLocale", locale), Element("UILanguage", locale), Element("UserLocale", locale)), shell)))
            .ToString();
    }

    private static XElement Component(string name, params object[] children) => new(Namespace + "component",
        new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"),
        new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"),
        new XAttribute("versionScope", "nonSxS"), children);

    private static XElement Element(string name, params object[] children) => new(Namespace + name, children);
}
