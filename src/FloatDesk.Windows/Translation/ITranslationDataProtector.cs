using System.Security.Cryptography;

namespace FloatDesk.Windows.Translation;

public interface ITranslationDataProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

internal sealed class DpapiTranslationDataProtector : ITranslationDataProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
