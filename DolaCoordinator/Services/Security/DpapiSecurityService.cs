using System;
using System.Security.Cryptography;
using System.Text;

namespace DolaCoordinator.Services.Security;

public class DpapiSecurityService : ISecurityService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DolaCoordinator-V1-Salt");

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes = ProtectedData.Protect(
                plainBytes,
                Entropy,
                DataProtectionScope.CurrentUser
            );
            return Convert.ToBase64String(cipherBytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DPAPI encryption error: {ex.Message}");
            throw new InvalidOperationException("Không thể mã hóa dữ liệu an toàn bằng DPAPI.", ex);
        }
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] plainBytes = ProtectedData.Unprotect(
                cipherBytes,
                Entropy,
                DataProtectionScope.CurrentUser
            );
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DPAPI decryption error: {ex.Message}");
            return string.Empty;
        }
    }
}
