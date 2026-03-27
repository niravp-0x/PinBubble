using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace PinBubble;

internal static class BiometricMasterPasswordStore
{
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PinBubble",
        "master-password.bin");

    public static bool HasCachedPassword()
    {
        return File.Exists(CacheFilePath);
    }

    public static bool IsBiometricAvailable()
    {
        try
        {
            var availability = UserConsentVerifier
                .CheckAvailabilityAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();

            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RequestBiometricVerificationAsync(string message)
    {
        try
        {
            var verificationTask = UserConsentVerifier
                .RequestVerificationAsync(message)
                .AsTask();

            return await verificationTask == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryUnlockCachedPasswordAsync(string encryptedFilePath)
    {
        if (!HasCachedPassword())
            return false;

        if (!IsBiometricAvailable())
            return false;

        return await RequestBiometricVerificationAsync("Verify your fingerprint to unlock PinBubble.");
    }

    public static bool TryGetCachedPassword(string encryptedFilePath, out string password)
    {
        password = string.Empty;

        if (!HasCachedPassword())
            return false;

        if (!TryLoadCachedPassword(out var cachedPassword))
            return false;

        if (!EncryptedTextStore.TryDecrypt(encryptedFilePath, cachedPassword, out _))
        {
            ClearCachedPassword();
            return false;
        }

        password = cachedPassword;
        return true;
    }

    public static bool CachePassword(string password)
    {
        try
        {
            var cacheDirectory = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            var plainBytes = Encoding.UTF8.GetBytes(password);
            var encryptedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CacheFilePath, encryptedBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ClearCachedPassword()
    {
        try
        {
            if (File.Exists(CacheFilePath))
                File.Delete(CacheFilePath);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static bool TryLoadCachedPassword(out string password)
    {
        password = string.Empty;

        try
        {
            var encryptedBytes = File.ReadAllBytes(CacheFilePath);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            password = Encoding.UTF8.GetString(plainBytes);
            return !string.IsNullOrWhiteSpace(password);
        }
        catch
        {
            return false;
        }
    }
}
