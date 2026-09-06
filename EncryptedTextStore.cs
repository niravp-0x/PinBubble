using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PinBubble;

internal static class EncryptedTextStore
{
    private const string Header = "PBENC1";
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 100_000;

    public static bool IsEncryptedFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var firstLine = reader.ReadLine();
        return string.Equals(firstLine, Header, StringComparison.Ordinal);
    }

    public static void EncryptAndSave(string filePath, string password, string plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var lines = new[]
        {
            Header,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(cipherBytes)
        };

        File.WriteAllLines(filePath, lines, Encoding.UTF8);
    }

    public static bool TryDecrypt(string filePath, string password, out string plaintext)
    {
        plaintext = string.Empty;

        if (!File.Exists(filePath))
            return false;

        try
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            if (lines.Length < 5 || !string.Equals(lines[0], Header, StringComparison.Ordinal))
                return false;

            var salt = Convert.FromBase64String(lines[1]);
            var nonce = Convert.FromBase64String(lines[2]);
            var tag = Convert.FromBase64String(lines[3]);
            var cipherBytes = Convert.FromBase64String(lines[4]);

            using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            var key = kdf.GetBytes(32);

            var plainBytes = new byte[cipherBytes.Length];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryChangePassword(string filePath, string currentPassword, string newPassword, out string error)
    {
        error = string.Empty;

        if (!TryDecrypt(filePath, currentPassword, out var plaintext))
        {
            error = "The current vault could not be decrypted.";
            return false;
        }

        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            EncryptAndSave(temporaryPath, newPassword, plaintext);

            if (!TryDecrypt(temporaryPath, newPassword, out var verifiedPlaintext)
                || !string.Equals(plaintext, verifiedPlaintext, StringComparison.Ordinal))
            {
                error = "The new vault could not be verified.";
                return false;
            }

            File.Replace(temporaryPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch
        {
            error = "The vault could not be replaced safely. The existing vault was left unchanged.";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup; the temporary file contains encrypted data only.
            }
        }
    }
}
