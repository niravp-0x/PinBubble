using System;
using OtpNet;

namespace PinBubble;

/// <summary>
/// Provides TOTP (Time-based One-Time Password) generation and management.
/// Generates time-based one-time passwords compatible with authenticator apps.
/// </summary>
internal static class TotpProvider
{
    private const int DefaultTimeStepSeconds = 30;
    private const int DefaultDigits = 6;

    /// <summary>
    /// Generates a TOTP code from a base32-encoded secret.
    /// </summary>
    /// <param name="base32Secret">The base32-encoded TOTP secret (from FreeIPA or authenticator setup)</param>
    /// <param name="timeStepSeconds">Time step in seconds (default 30)</param>
    /// <param name="digits">Number of digits in the code (default 6)</param>
    /// <returns>The current TOTP code, or null if secret is invalid</returns>
    public static string? GetCurrentTotp(string? base32Secret, int timeStepSeconds = DefaultTimeStepSeconds, int digits = DefaultDigits)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
            return null;

        try
        {
            var key = Base32Encoding.ToBytes(base32Secret);
            var totp = new Totp(key, timeStepSeconds, OtpHashMode.Sha1, digits);
            return totp.ComputeTotp();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the current TOTP code and the remaining seconds until it expires.
    /// </summary>
    /// <param name="base32Secret">The base32-encoded TOTP secret</param>
    /// <param name="timeStepSeconds">Time step in seconds (default 30)</param>
    /// <param name="digits">Number of digits in the code (default 6)</param>
    /// <returns>Tuple of (code, remainingSeconds), or null if secret is invalid</returns>
    public static (string Code, int RemainingSeconds)? GetTotpWithExpiry(string? base32Secret, int timeStepSeconds = DefaultTimeStepSeconds, int digits = DefaultDigits)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
            return null;

        try
        {
            var key = Base32Encoding.ToBytes(base32Secret);
            var totp = new Totp(key, timeStepSeconds, OtpHashMode.Sha1, digits);
            var code = totp.ComputeTotp();
            
            // Calculate remaining seconds
            var unixTime = (long)Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            var remainingSeconds = timeStepSeconds - (int)(unixTime % timeStepSeconds);
            
            return (code, remainingSeconds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies if a provided TOTP code is valid for the given secret.
    /// Allows for time window tolerance (±1 step).
    /// </summary>
    /// <param name="base32Secret">The base32-encoded TOTP secret</param>
    /// <param name="code">The code to verify</param>
    /// <param name="timeStepSeconds">Time step in seconds (default 30)</param>
    /// <param name="digits">Number of digits in the code (default 6)</param>
    /// <returns>True if the code is valid, false otherwise</returns>
    public static bool VerifyTotp(string? base32Secret, string? code, int timeStepSeconds = DefaultTimeStepSeconds, int digits = DefaultDigits)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            var key = Base32Encoding.ToBytes(base32Secret);
            var totp = new Totp(key, timeStepSeconds, OtpHashMode.Sha1, digits);
            
            // Verify with time window tolerance (±1 step for drift)
            // Using default verification window which allows tolerance
            return totp.VerifyTotp(code, out var window);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a provisioning URI for QR code generation.
    /// Used with authenticator apps to scan and set up TOTP.
    /// </summary>
    /// <param name="base32Secret">The base32-encoded TOTP secret</param>
    /// <param name="label">Label for the secret (e.g., "user@example.com")</param>
    /// <param name="issuer">Issuer name (e.g., "FreeIPA")</param>
    /// <returns>The provisioning URI for QR code generation</returns>
    public static string GetProvisioningUri(string base32Secret, string label, string issuer = "PinBubble")
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(label))
            return string.Empty;

        // Format: otpauth://totp/ISSUER:LABEL?secret=SECRET&issuer=ISSUER
        label = Uri.EscapeDataString(label);
        issuer = Uri.EscapeDataString(issuer);
        
        return $"otpauth://totp/{issuer}:{label}?secret={base32Secret}&issuer={issuer}";
    }
}
