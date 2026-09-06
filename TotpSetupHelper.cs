using System;
using System.Text.RegularExpressions;

namespace PinBubble;

/// <summary>
/// Helper class for setting up and managing TOTP secrets in entries.
/// Handles parsing of various TOTP secret formats and validation.
/// </summary>
internal static class TotpSetupHelper
{
    /// <summary>
    /// Attempts to extract a TOTP secret from a provisioning URI (otpauth://)
    /// </summary>
    /// <param name="uri">The provisioning URI</param>
    /// <param name="secret">The extracted base32 secret</param>
    /// <returns>True if a valid secret was extracted</returns>
    public static bool TryExtractFromUri(string uri, out string secret)
    {
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(uri))
            return false;

        try
        {
            // Parse otpauth://totp/[issuer:][label]?secret=SECRET&issuer=ISSUER
            if (!uri.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
                return false;

            var secretMatch = Regex.Match(uri, @"secret=([A-Z2-7=]+)", RegexOptions.IgnoreCase);
            if (!secretMatch.Success)
                return false;

            secret = secretMatch.Groups[1].Value;
            return IsValidBase32Secret(secret);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates if a string is a valid base32-encoded TOTP secret
    /// </summary>
    /// <param name="secret">The secret to validate</param>
    /// <returns>True if the secret is valid base32</returns>
    public static bool IsValidBase32Secret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return false;

        // Base32 alphabet: A-Z and 2-7, with optional padding (=)
        return Regex.IsMatch(secret, @"^[A-Z2-7]+=*$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Normalizes a TOTP secret to uppercase base32 format
    /// </summary>
    /// <param name="secret">The secret to normalize</param>
    /// <returns>Normalized secret or empty string if invalid</returns>
    public static string NormalizeSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return string.Empty;

        // Remove whitespace and convert to uppercase
        var normalized = Regex.Replace(secret, @"\s+", "").ToUpperInvariant();

        // Ensure padding is correct (must be multiple of 8 characters with padding)
        if (IsValidBase32Secret(normalized))
            return normalized;

        return string.Empty;
    }

    /// <summary>
    /// Generates a formatted URI for QR code generation from a label and secret
    /// </summary>
    /// <param name="label">Entry label/username</param>
    /// <param name="secret">Base32-encoded TOTP secret</param>
    /// <param name="issuer">Issuer name for the authenticator app</param>
    /// <returns>Provisioning URI suitable for QR code generation</returns>
    public static string GenerateProvisioningUri(string label, string secret, string issuer = "PinBubble")
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(secret))
            return string.Empty;

        if (!IsValidBase32Secret(secret))
            return string.Empty;

        return TotpProvider.GetProvisioningUri(secret, label, issuer);
    }

    /// <summary>
    /// Creates or updates a TOTP entry with validation
    /// </summary>
    /// <param name="row">The SnippetRow to update</param>
    /// <param name="totpSecret">The base32-encoded TOTP secret</param>
    /// <returns>True if the secret was successfully set</returns>
    public static bool TrySetTotpSecret(SnippetRow row, string? totpSecret)
    {
        if (row == null)
            return false;

        if (string.IsNullOrWhiteSpace(totpSecret))
        {
            row.TotpSecret = null;
            return true;
        }

        var normalized = NormalizeSecret(totpSecret);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        // Verify that the secret actually works by generating a code
        var testCode = TotpProvider.GetCurrentTotp(normalized);
        if (string.IsNullOrWhiteSpace(testCode))
            return false;

        row.TotpSecret = normalized;
        row.Modified = DateTime.Now;
        return true;
    }

    /// <summary>
    /// Removes TOTP from an entry
    /// </summary>
    /// <param name="row">The SnippetRow to update</param>
    public static void RemoveTotpSecret(SnippetRow row)
    {
        if (row != null)
        {
            row.TotpSecret = null;
            row.Modified = DateTime.Now;
        }
    }

    /// <summary>
    /// Gets a human-readable description of the TOTP entry status
    /// </summary>
    /// <param name="row">The SnippetRow to check</param>
    /// <returns>Status description</returns>
    public static string GetTotpStatusDescription(SnippetRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.TotpSecret))
            return "No TOTP configured";

        var totpInfo = row.TotpWithExpiry;
        if (!totpInfo.HasValue)
            return "TOTP configured but unavailable";

        var remaining = totpInfo.Value.RemainingSeconds;
        return remaining > 5 
            ? $"TOTP active: {totpInfo.Value.Code} ({remaining}s remaining)"
            : $"TOTP expiring soon: {totpInfo.Value.Code} ({remaining}s)";
    }

    /// <summary>
    /// Verifies a user-entered TOTP code against an entry
    /// </summary>
    /// <param name="row">The SnippetRow with TOTP secret</param>
    /// <param name="userCode">The code entered by the user</param>
    /// <returns>True if the code is valid</returns>
    public static bool VerifyTotpCode(SnippetRow row, string userCode)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.TotpSecret) || string.IsNullOrWhiteSpace(userCode))
            return false;

        return TotpProvider.VerifyTotp(row.TotpSecret, userCode);
    }
}
