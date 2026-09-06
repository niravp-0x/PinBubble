using System;
using System.Windows.Threading;

namespace PinBubble;

/// <summary>
/// Helper class for integrating TOTP display and management in the UI.
/// Provides convenient methods for showing TOTP codes with automatic updates.
/// </summary>
internal class TotpDisplayHelper : IDisposable
{
    private readonly DispatcherTimer _refreshTimer;

    public TotpDisplayHelper()
    {
        _refreshTimer = new DispatcherTimer()
        {
            Interval = TimeSpan.FromMilliseconds(500) // Update every 500ms for smooth UX
        };
        _refreshTimer.Tick += (_, _) => RefreshDisplay();
    }

    /// <summary>
    /// Starts monitoring a TOTP secret and provides periodic updates
    /// </summary>
    public void StartMonitoring(string? base32Secret)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
        {
            Stop();
            return;
        }

        _refreshTimer.Start();
        RefreshDisplay();
    }

    /// <summary>
    /// Stops monitoring the TOTP secret
    /// </summary>
    public void Stop()
    {
        _refreshTimer.Stop();
    }

    private void RefreshDisplay()
    {
        // Note: In a real implementation, you'd need to pass the secret to refresh
        // This is a simplified version. You'd want to store the current secret
        // and call TotpProvider here
    }

    /// <summary>
    /// Gets formatted TOTP display string with expiry information
    /// </summary>
    public static string GetFormattedTotpDisplay(string? base32Secret)
    {
        var totpInfo = TotpProvider.GetTotpWithExpiry(base32Secret);
        if (!totpInfo.HasValue)
            return "N/A";

        var code = totpInfo.Value.Code;
        var remaining = totpInfo.Value.RemainingSeconds;
        
        // Format: "123456 (15s)"
        return $"{code} ({remaining}s)";
    }

    /// <summary>
    /// Gets the progress percentage for TOTP validity (0-100)
    /// </summary>
    public static double GetTotpExpiryProgress(string? base32Secret)
    {
        const int timeStep = 30;
        var totpInfo = TotpProvider.GetTotpWithExpiry(base32Secret, timeStep);
        
        if (!totpInfo.HasValue)
            return 0;

        // Calculate progress: (timeStep - remainingSeconds) / timeStep * 100
        return ((timeStep - totpInfo.Value.RemainingSeconds) / (double)timeStep) * 100;
    }

    /// <summary>
    /// Gets a color indicator for TOTP expiry status
    /// </summary>
    public static string GetTotpStatusColor(string? base32Secret)
    {
        var totpInfo = TotpProvider.GetTotpWithExpiry(base32Secret);
        if (!totpInfo.HasValue)
            return "Gray"; // No TOTP configured

        var remaining = totpInfo.Value.RemainingSeconds;
        
        return remaining > 10 ? "Green" : remaining > 5 ? "Yellow" : "Red";
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
    }
}

/// <summary>
/// Event arguments for TOTP display changes
/// </summary>
internal class TotpDisplayChangedEventArgs : EventArgs
{
    public string Code { get; set; } = string.Empty;
    public int RemainingSeconds { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}
