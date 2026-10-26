using System.Security.Cryptography;
using System.Text;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Translation;

public static class TranslationCredentialKey
{
    public static string For(TranslationProviderKind providerKind, string endpoint)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint) ? "no-endpoint" :
            Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : endpoint.Trim();
        var bytes = Encoding.UTF8.GetBytes(providerKind + "\n" + normalized);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
