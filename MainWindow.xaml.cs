using System.Diagnostics;
using System.Windows.Interop;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using QRCoder;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinDataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using WinClipboardContentOptions = Windows.ApplicationModel.DataTransfer.ClipboardContentOptions;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PinBubble;

// Row data class to track encrypted state per row
class SnippetRow
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string ActualValue { get; set; } = string.Empty; // Always stores the real value
    public bool IsEncrypted { get; set;} = true;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime ExpiryDate { get; set; } = DateTime.Now.AddDays(30);
    
    // TOTP (Time-based One-Time Password) support
    public string? TotpSecret { get; set; } // Base32-encoded TOTP secret from FreeIPA
    
    /// <summary>Gets the current TOTP code if a secret is configured</summary>
    public string? CurrentTotp => TotpProvider.GetCurrentTotp(TotpSecret);
    
    /// <summary>Gets the current TOTP code with expiry info if a secret is configured</summary>
    public (string Code, int RemainingSeconds)? TotpWithExpiry => TotpProvider.GetTotpWithExpiry(TotpSecret);
    
    /// <summary>Gets the provisioning URI for QR code generation</summary>
    public string GetTotpProvisioningUri(string issuer = "PinBubble") => 
        string.IsNullOrWhiteSpace(TotpSecret) ? string.Empty : TotpProvider.GetProvisioningUri(TotpSecret, Label, issuer);
}

// Lightweight DTO for JSON persistence (no UI-only fields)
class SnippetEntry
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime ExpiryDate { get; set; } = DateTime.Now.AddDays(30);
    
    // TOTP (Time-based One-Time Password) support
    public string? TotpSecret { get; set; } // Base32-encoded TOTP secret from FreeIPA
}

// Maps a global hotkey to a pinned snippet
class ShortcutEntry
{
    public string SnippetLabel { get; set; } = string.Empty;
    public int Modifiers { get; set; }
    public int VirtualKey { get; set; }
    public string DisplayShortcut { get; set; } = string.Empty;
}

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string SerializeSnippetsToJson(IEnumerable<SnippetRow> rows)
    {
        var entries = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Label))
            .Select(r => new SnippetEntry
            {
                Label = r.Label,
                Value = r.ActualValue,
                Created = r.Created,
                Modified = r.Modified,
                ExpiryDate = r.ExpiryDate,
                TotpSecret = r.TotpSecret
            })
            .ToList();
        return JsonSerializer.Serialize(entries, s_jsonOptions);
    }

    private static List<SnippetRow> ParseSnippets(string plaintext)
    {
        var trimmed = plaintext.TrimStart();
        if (trimmed.StartsWith('['))
        {
            // JSON format
            var entries = JsonSerializer.Deserialize<List<SnippetEntry>>(trimmed, s_jsonOptions) ?? new();
            return entries.Select(e => new SnippetRow
            {
                Label = e.Label,
                Value = "••••••••",
                ActualValue = e.Value,
                IsEncrypted = true,
                Created = e.Created,
                Modified = e.Modified,
                ExpiryDate = e.ExpiryDate,
                TotpSecret = e.TotpSecret
            }).ToList();
        }

        // Legacy comma-delimited format – split on first comma for the label,
        // then try to extract ISO-8601 timestamps from the end.
        var rows = new List<SnippetRow>();
        foreach (var line in plaintext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var l = line.Trim();
            if (string.IsNullOrWhiteSpace(l) || l.StartsWith('#'))
                continue;

            var commaIdx = l.IndexOf(',');
            if (commaIdx < 0) continue;

            var label = l[..commaIdx].Trim();
            var rest = l[(commaIdx + 1)..].Trim();

            DateTime created = DateTime.Now;
            DateTime modified = DateTime.Now;
            DateTime expiry = DateTime.Now.AddDays(30);
            string value = rest;

            // Try to peel off 3 ISO timestamps from the end
            var segments = rest.Split(',');
            if (segments.Length >= 4 &&
                DateTime.TryParse(segments[^1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var eDate) &&
                DateTime.TryParse(segments[^2].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var mDate) &&
                DateTime.TryParse(segments[^3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cDate))
            {
                created = cDate;
                modified = mDate;
                expiry = eDate;
                value = string.Join(",", segments[..^3]).Trim();
            }

            rows.Add(new SnippetRow
            {
                Label = label,
                Value = "••••••••",
                ActualValue = value,
                IsEncrypted = true,
                Created = created,
                Modified = modified,
                ExpiryDate = expiry
            });
        }
        return rows;
    }

    private static string BuildGridSaveJson(WinForms.DataGridView grid)
    {
        var rows = grid.Rows.Cast<WinForms.DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Tag is SnippetRow)
            .Select(r => (SnippetRow)r.Tag!);
        return SerializeSnippetsToJson(rows);
    }

    private const double DefaultBackdropOpacity = 0.80;
    private const bool DefaultIsPinned = true;
    private const bool DefaultIsDarkTheme = true;
    private const bool DefaultShowInTaskbar = true;
    private const int DefaultClipboardClearSeconds = 60;

    // Hotkey constants
    private const int WmHotkey = 0x0312;
    private const int HotkeyIdQwertyPicker = 1;
    private const int HotkeyIdUserBase = 100;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModNoRepeat = 0x4000;

    // QWERTY keyboard order for the quick-paste picker
    private static readonly char[] QwertyOrder =
    {
        'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P',
        'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L',
        'Z', 'X', 'C', 'V', 'B', 'N', 'M'
    };

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_PASTE = 0x0302;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // Pad to match the largest union member (MOUSEINPUT = 28 bytes on x64)
        [FieldOffset(0)] private long _pad0;
        [FieldOffset(8)] private long _pad1;
        [FieldOffset(16)] private long _pad2;
        [FieldOffset(24)] private int _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;   // 1 = KEYBOARD
        public InputUnion u;
    }

    private static void SendPaste()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V       = 0x56;
        const uint KEYEVENTF_KEYUP = 0x0002;

        var inputs = new INPUT[]
        {
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V,       dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // Pastes the clipboard content into hWnd using the most reliable method for
    // the target control type.
    //
    // Win32 Edit / RichEdit / ComboBox controls (e.g. RDCMan credential prompts,
    // Windows Security dialogs, classic apps): WM_PASTE is sent directly to the
    // focused child control. This bypasses the input queue entirely and works
    // even when synthesized keystrokes are blocked.
    //
    // Modern apps (browsers, terminals, Electron): they don't process WM_PASTE,
    // so we fall back to SendInput Ctrl+V which their message loops handle.
    private static void SendPasteToWindow(IntPtr hWnd)
    {
        var targetTid  = GetWindowThreadProcessId(hWnd, out _);
        var currentTid = GetCurrentThreadId();
        bool attached  = false;
        bool usedWmPaste = false;
        try
        {
            if (targetTid != 0 && targetTid != currentTid)
                attached = AttachThreadInput(currentTid, targetTid, true);

            SetForegroundWindow(hWnd);

            // With input queues attached, GetFocus() returns the child control
            // that actually has keyboard focus (e.g. the username text field).
            IntPtr focusedCtrl = GetFocus();
            IntPtr pasteTarget = focusedCtrl != IntPtr.Zero ? focusedCtrl : hWnd;

            var sb = new System.Text.StringBuilder(256);
            GetClassName(pasteTarget, sb, sb.Capacity);
            string cls = sb.ToString();

            // Standard Win32 edit-type controls all accept WM_PASTE directly.
            bool isEditClass = cls.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                            || cls.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
                            || cls.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isEditClass)
            {
                SendMessage(pasteTarget, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                usedWmPaste = true;
            }
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentTid, targetTid, false);
        }

        // Fall back to synthesized Ctrl+V for apps that don't use standard edit
        // controls (browsers, terminals, Electron-based apps).
        if (!usedWmPaste)
            SendPaste();
    }

    // Polls until hWnd is the foreground window, or the timeout elapses.
    private static async Task WaitForForegroundAsync(IntPtr hWnd, int timeoutMs = 800)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (GetForegroundWindow() == hWnd) return;
            await Task.Delay(25);
        }
    }

    private readonly string _textFilePath;
    private readonly string _settingsFilePath;
    private string? _masterPassword;
    private string[] _labels = Array.Empty<string>();
    private string[] _fullLabels = Array.Empty<string>();
    private string[] _snippets = Array.Empty<string>();
    private SnippetRow[] _snippetRows = Array.Empty<SnippetRow>(); // Store full SnippetRow for TOTP access
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadDebounce;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private bool _expanded;
    private bool _isDrag;
    private System.Windows.Point _dragStartCursor;
    private System.Windows.Point _dragStartWindow;
    private bool _isPinned = DefaultIsPinned;
    private bool _isDarkTheme = DefaultIsDarkTheme;
    private double _backdropOpacity = DefaultBackdropOpacity;
    private double? _savedWindowLeft;
    private double? _savedWindowTop;
    private string? _savedMonitorDeviceName;
    private double? _preExpandWindowLeft;
    private double? _preExpandWindowTop;

    // Clipboard clear setting (0 = disabled)
    private int _clipboardClearSeconds = DefaultClipboardClearSeconds;

    // Quick Enter: auto-paste into previously focused window after QWERTY pick
    private bool _quickEnterEnabled = false;

    // TOTP copy behavior: when true, copy value + TOTP code together
    private bool _copyTotpTogether = true;

    // Default expiry for new/updated entries.
    private int _defaultExpiryDays = 30;

    private static readonly string s_appVersion = typeof(MainWindow).Assembly.GetName().Version is { } version
        ? version.ToString(3)
        : "2.1.0";

    // Shortcut hotkey state
    private string _shortcutsFilePath = string.Empty;
    private List<ShortcutEntry> _shortcuts = new();
    private readonly List<int> _registeredHotkeyIds = new();
    private HwndSource? _hwndSource;

    private static readonly WpfColor BubbleDefaultColorDark = WpfColor.FromRgb(45, 45, 48);
    private static readonly WpfColor BubbleDefaultColorLight = WpfColor.FromRgb(230, 233, 237);
    private static readonly WpfColor BubbleBorderColorDark = WpfColor.FromRgb(80, 80, 80);
    private static readonly WpfColor BubbleBorderColorLight = WpfColor.FromRgb(176, 181, 188);
    private static readonly WpfColor BubbleHoverColorDark = WpfColor.FromRgb(102, 185, 51);
    private static readonly WpfColor BubbleHoverColorLight = WpfColor.FromRgb(125, 190, 90);
    private static readonly SolidColorBrush BubbleClicked = new SolidColorBrush(WpfColor.FromRgb(0, 255, 0));
    private static readonly WpfColor BackdropBaseColorDark = WpfColor.FromRgb(16, 18, 22);
    private static readonly WpfColor BackdropBaseColorLight = WpfColor.FromRgb(245, 247, 250);

    private sealed class UiSettings
    {
        public double BackdropOpacity { get; set; } = DefaultBackdropOpacity;
        public bool IsPinned { get; set; } = DefaultIsPinned;
        public bool IsDarkTheme { get; set; } = DefaultIsDarkTheme;
        public bool ShowInTaskbar { get; set; } = DefaultShowInTaskbar;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public string? MonitorDeviceName { get; set; }
        // 0 = disabled; 30 / 60 / 120 = clear after N seconds
        public int ClipboardClearSeconds { get; set; } = DefaultClipboardClearSeconds;
        // When true, QWERTY picker auto-pastes into the previously focused window
        public bool QuickEnterEnabled { get; set; } = false;
        // When true, copying a snippet with TOTP will copy both value and TOTP code together
        public bool CopyTotpTogether { get; set; } = true;
        // Default number of days before a snippet is considered expired.
        public int DefaultExpiryDays { get; set; } = 30;
    }

    public MainWindow()
    {
        InitializeComponent();
        _settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PinBubble",
            "settings.json");

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PinBubble");
        Directory.CreateDirectory(appDataDir);
        _textFilePath = Path.Combine(appDataDir, "snippets.bin");
        _shortcutsFilePath = Path.Combine(appDataDir, "shortcuts.json");

        // One-time migration: move old text.text from the install directory if present
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "text.text");
        if (!File.Exists(_textFilePath) && File.Exists(legacyPath))
        {
            try { File.Move(legacyPath, _textFilePath); }
            catch { /* non-fatal; a new file will be created on first save */ }
        }

        LoadUiSettings();
        ApplyExpandedPanelTheme();
        UpdateBackdropOpacityMenuChecks();
        UpdateClearClipMenuChecks();
        UpdateBiometricUi();
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
        QuickEnterMenuItem.IsChecked = _quickEnterEnabled;
        CopyTotpTogetherMenuItem.IsChecked = _copyTotpTogether;
        
        // Ensure window topmost behavior follows saved pin state.
        Topmost = _isPinned;
        
        if (!InitializeSecureStore())
        {
            Close();
            return;
        }

        LoadSnippets();
        LoadShortcuts();
        BuildBubbles();
        SetupWatcher();
        SetupTrayIcon();
        SourceInitialized += (_, _) => SetupGlobalHotkeys();
        Loaded += (_, _) => RestoreWindowPlacement();
        Loaded += (_, _) => UpdateTaskbarMenuText();
        Loaded += (_, _) => UpdatePinMenuText();
        Loaded += (_, _) => UpdateBiometricUi();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _expanded) Collapse(); };
    }

    private bool InitializeSecureStore()
    {
        if (File.Exists(_textFilePath)
            && EncryptedTextStore.IsEncryptedFile(_textFilePath)
            && BiometricMasterPasswordStore.HasCachedPassword()
            && BiometricMasterPasswordStore.IsBiometricAvailable())
        {
            if (TryUnlockWithFingerprintPrompt(out var cachedPassword))
            {
                _masterPassword = cachedPassword;
                return true;
            }

            System.Windows.MessageBox.Show(
                "Fingerprint unlock did not complete. Please enter your master password.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        while (true)
        {
            var password = PromptForMasterPassword();
            if (password is null)
                return false;

            if (password.Length < 4)
            {
                System.Windows.MessageBox.Show("Master password must be at least 4 characters.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (!File.Exists(_textFilePath))
            {
                var defaultContent = SerializeSnippetsToJson(new[]
                {
                    new SnippetRow { Label = "Label1", ActualValue = "Your first snippet text here" },
                    new SnippetRow { Label = "Label2", ActualValue = "Your second snippet text here" }
                });
                EncryptedTextStore.EncryptAndSave(_textFilePath, password, defaultContent);
                _masterPassword = password;
                MaybeOfferBiometricUnlock(password);
                return true;
            }

            if (EncryptedTextStore.IsEncryptedFile(_textFilePath))
            {
                if (EncryptedTextStore.TryDecrypt(_textFilePath, password, out _))
                {
                    _masterPassword = password;
                    MaybeOfferBiometricUnlock(password);
                    return true;
                }

                System.Windows.MessageBox.Show("Incorrect master password. Try again.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
                continue;
            }

            try
            {
                var plaintext = File.ReadAllText(_textFilePath);
                EncryptedTextStore.EncryptAndSave(_textFilePath, password, plaintext);
                _masterPassword = password;
                MaybeOfferBiometricUnlock(password);
                return true;
            }
            catch
            {
                System.Windows.MessageBox.Show("Failed to migrate existing text file to encrypted format.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }

    private void MaybeOfferBiometricUnlock(string password)
    {
        if (!BiometricMasterPasswordStore.IsBiometricAvailable())
        {
            UpdateBiometricUi();
            return;
        }

        if (BiometricMasterPasswordStore.HasCachedPassword())
        {
            BiometricMasterPasswordStore.CachePassword(password);
            UpdateBiometricUi();
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Enable fingerprint authentication for future unlocks?",
            "PinBubble",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            UpdateBiometricUi();
            return;
        }

        if (!BiometricMasterPasswordStore.CachePassword(password))
        {
            System.Windows.MessageBox.Show(
                "Failed to enable fingerprint unlock.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        UpdateBiometricUi();
    }

    private bool TryUnlockWithFingerprintPrompt(out string password)
    {
        password = string.Empty;
        string unlockedPassword = string.Empty;
        bool unlockSucceeded = false;

        using var dialog = new WinForms.Form
        {
            Width = 420,
            Height = 180,
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 35),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true
        };

        dialog.Paint += (s, e) =>
        {
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 70, 75), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, dialog.Width - 1, dialog.Height - 1);
        };

        var titlePanel = new WinForms.Panel
        {
            Left = 0,
            Top = 0,
            Width = 420,
            Height = 45,
            BackColor = System.Drawing.Color.FromArgb(25, 25, 28)
        };

        var titleLabel = new WinForms.Label
        {
            Left = 20,
            Top = 12,
            Width = 260,
            Height = 22,
            Text = "FINGERPRINT UNLOCK",
            ForeColor = System.Drawing.Color.FromArgb(200, 200, 205),
            Font = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.Transparent
        };

        var instructionLabel = new WinForms.Label
        {
            Left = 20,
            Top = 62,
            Width = 380,
            Height = 40,
            Text = "Touch your fingerprint sensor to unlock PinBubble. If you want to stop, cancel it from the Windows Security prompt.",
            ForeColor = System.Drawing.Color.FromArgb(160, 160, 165),
            Font = new System.Drawing.Font("Segoe UI", 9f),
            BackColor = System.Drawing.Color.Transparent
        };

        var statusLabel = new WinForms.Label
        {
            Left = 20,
            Top = 108,
            Width = 380,
            Height = 20,
            Text = "Waiting for Windows Security...",
            ForeColor = System.Drawing.Color.FromArgb(190, 190, 195),
            Font = new System.Drawing.Font("Segoe UI", 8.5f),
            BackColor = System.Drawing.Color.Transparent
        };

        titlePanel.Controls.Add(titleLabel);
        dialog.Controls.Add(titlePanel);
        dialog.Controls.Add(instructionLabel);
        dialog.Controls.Add(statusLabel);

        dialog.Shown += async (_, _) =>
        {
            unlockSucceeded = await BiometricMasterPasswordStore.TryUnlockCachedPasswordAsync(_textFilePath);

            if (unlockSucceeded)
            {
                unlockSucceeded = BiometricMasterPasswordStore.TryGetCachedPassword(
                    _textFilePath,
                    out unlockedPassword);
            }

            if (dialog.IsDisposed)
                return;

            dialog.DialogResult = unlockSucceeded ? WinForms.DialogResult.OK : WinForms.DialogResult.Cancel;
            dialog.Close();
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK && unlockSucceeded)
        {
            password = unlockedPassword;
            return true;
        }

        return false;
    }

    private static string? PromptForMasterPassword()
    {
        using var dialog = new WinForms.Form
        {
            Width = 420,
            Height = 220,
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 35),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true
        };

        // Add a subtle border
        dialog.Paint += (s, e) =>
        {
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 70, 75), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, dialog.Width - 1, dialog.Height - 1);
        };

        // Title bar panel
        var titlePanel = new WinForms.Panel
        {
            Left = 0,
            Top = 0,
            Width = 420,
            Height = 45,
            BackColor = System.Drawing.Color.FromArgb(25, 25, 28)
        };

        // Lock icon using PictureBox with custom drawing
        var lockIcon = new WinForms.PictureBox
        {
            Left = 20,
            Top = 10,
            Width = 24,
            Height = 24,
            BackColor = System.Drawing.Color.Transparent
        };
        
        // Draw a lock icon
        var lockBitmap = new System.Drawing.Bitmap(24, 24);
        using (var g = System.Drawing.Graphics.FromImage(lockBitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            // Draw lock body (rectangle)
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 200, 205)))
            {
                g.FillRectangle(brush, 6, 12, 12, 10);
            }
            
            // Draw lock shackle (arc)
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 200, 205), 2.5f))
            {
                g.DrawArc(pen, 8, 4, 8, 10, 180, 180);
            }
            
            // Draw keyhole
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 30, 35)))
            {
                g.FillEllipse(brush, 10, 15, 4, 4);
                g.FillRectangle(brush, 11, 18, 2, 3);
            }
        }
        lockIcon.Image = lockBitmap;

        var titleLabel = new WinForms.Label
        {
            Left = 50,
            Top = 12,
            Width = 300,
            Height = 25,
            Text = "MASTER PASSWORD",
            ForeColor = System.Drawing.Color.FromArgb(200, 200, 205),
            Font = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.Transparent
        };

        var instructionLabel = new WinForms.Label
        {
            Left = 20,
            Top = 58,
            Width = 380,
            Height = 25,
            Text = "Enter your master password to unlock",
            ForeColor = System.Drawing.Color.FromArgb(160, 160, 165),
            Font = new System.Drawing.Font("Segoe UI", 9f),
            BackColor = System.Drawing.Color.Transparent,
            AutoSize = false
        };

        // Custom styled textbox with panel background
        var textBoxPanel = new WinForms.Panel
        {
            Left = 20,
            Top = 93,
            Width = 380,
            Height = 42,
            BackColor = System.Drawing.Color.FromArgb(45, 45, 50)
        };

        var textBox = new WinForms.TextBox
        {
            Left = 2,
            Top = 2,
            Width = 376,
            Height = 38,
            UseSystemPasswordChar = true,
            BorderStyle = WinForms.BorderStyle.None,
            BackColor = System.Drawing.Color.FromArgb(45, 45, 50),
            ForeColor = System.Drawing.Color.FromArgb(220, 220, 225),
            Font = new System.Drawing.Font("Segoe UI", 12f)
        };

        textBoxPanel.Controls.Add(textBox);

        // Paint border on textbox panel
        textBoxPanel.Paint += (s, e) =>
        {
            var borderColor = textBox.Focused ? 
                System.Drawing.Color.FromArgb(0, 120, 212) : 
                System.Drawing.Color.FromArgb(70, 70, 75);
            using var pen = new System.Drawing.Pen(borderColor, 2);
            e.Graphics.DrawRectangle(pen, 0, 0, textBoxPanel.Width - 1, textBoxPanel.Height - 1);
        };

        textBox.Enter += (s, e) => textBoxPanel.Invalidate();
        textBox.Leave += (s, e) => textBoxPanel.Invalidate();

        var okButton = new WinForms.Button
        {
            Text = "UNLOCK",
            Left = 205,
            Width = 95,
            Height = 38,
            Top = 155,
            DialogResult = WinForms.DialogResult.OK,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(0, 120, 212),
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        okButton.FlatAppearance.BorderSize = 0;
        okButton.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(0, 100, 192);
        okButton.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(0, 80, 172);

        var cancelButton = new WinForms.Button
        {
            Text = "CANCEL",
            Left = 305,
            Width = 95,
            Height = 38,
            Top = 155,
            DialogResult = WinForms.DialogResult.Cancel,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(55, 55, 60),
            ForeColor = System.Drawing.Color.FromArgb(200, 200, 205),
            Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        cancelButton.FlatAppearance.BorderSize = 0;
        cancelButton.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(70, 70, 75);
        cancelButton.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(85, 85, 90);

        // Make dialog draggable
        bool dragging = false;
        System.Drawing.Point dragCursor = System.Drawing.Point.Empty;
        System.Drawing.Point dragForm = System.Drawing.Point.Empty;

        titlePanel.MouseDown += (s, e) =>
        {
            dragging = true;
            dragCursor = System.Windows.Forms.Cursor.Position;
            dragForm = dialog.Location;
        };

        titlePanel.MouseMove += (s, e) =>
        {
            if (dragging)
            {
                var diff = System.Drawing.Point.Subtract(System.Windows.Forms.Cursor.Position, new System.Drawing.Size(dragCursor));
                dialog.Location = System.Drawing.Point.Add(dragForm, new System.Drawing.Size(diff));
            }
        };

        titlePanel.MouseUp += (s, e) => dragging = false;

        titlePanel.Controls.Add(lockIcon);
        titlePanel.Controls.Add(titleLabel);
        dialog.Controls.Add(titlePanel);
        dialog.Controls.Add(instructionLabel);
        dialog.Controls.Add(textBoxPanel);
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        textBox.Select();

        return dialog.ShowDialog() == WinForms.DialogResult.OK ? textBox.Text : null;
    }

    private static string? PromptForNewMasterPassword()
    {
        using var dialog = new WinForms.Form
        {
            Width = 420,
            Height = 300,
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            BackColor = Drawing.Color.FromArgb(30, 30, 35),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true
        };

        dialog.Paint += (_, paintArgs) =>
        {
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(70, 70, 75), 1);
            paintArgs.Graphics.DrawRectangle(pen, 0, 0, dialog.Width - 1, dialog.Height - 1);
        };

        var titleLabel = new WinForms.Label
        {
            Left = 20,
            Top = 15,
            Width = 380,
            Height = 25,
            Text = "CHANGE MASTER PASSWORD",
            ForeColor = Drawing.Color.FromArgb(220, 220, 225),
            Font = new Drawing.Font("Segoe UI", 10.5f, Drawing.FontStyle.Bold)
        };

        var instructionLabel = new WinForms.Label
        {
            Left = 20,
            Top = 48,
            Width = 380,
            Height = 22,
            Text = "Create a new password for this vault",
            ForeColor = Drawing.Color.FromArgb(160, 160, 165),
            Font = new Drawing.Font("Segoe UI", 9f)
        };

        var newPasswordLabel = new WinForms.Label
        {
            Left = 20,
            Top = 78,
            Width = 160,
            Height = 20,
            Text = "New password",
            ForeColor = Drawing.Color.FromArgb(200, 200, 205),
            Font = new Drawing.Font("Segoe UI", 9f)
        };

        var newPasswordBox = new WinForms.TextBox
        {
            Left = 20,
            Top = 98,
            Width = 380,
            Height = 34,
            UseSystemPasswordChar = true,
            BackColor = Drawing.Color.FromArgb(45, 45, 50),
            ForeColor = Drawing.Color.FromArgb(220, 220, 225),
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            Font = new Drawing.Font("Segoe UI", 11f)
        };

        var confirmPasswordLabel = new WinForms.Label
        {
            Left = 20,
            Top = 140,
            Width = 180,
            Height = 20,
            Text = "Confirm new password",
            ForeColor = Drawing.Color.FromArgb(200, 200, 205),
            Font = new Drawing.Font("Segoe UI", 9f)
        };

        var confirmPasswordBox = new WinForms.TextBox
        {
            Left = 20,
            Top = 160,
            Width = 380,
            Height = 34,
            UseSystemPasswordChar = true,
            BackColor = Drawing.Color.FromArgb(45, 45, 50),
            ForeColor = Drawing.Color.FromArgb(220, 220, 225),
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            Font = new Drawing.Font("Segoe UI", 11f)
        };

        var okButton = new WinForms.Button
        {
            Text = "CHANGE",
            Left = 205,
            Top = 220,
            Width = 95,
            Height = 38,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = Drawing.Color.FromArgb(0, 120, 212),
            ForeColor = Drawing.Color.White,
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        okButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new WinForms.Button
        {
            Text = "CANCEL",
            Left = 305,
            Top = 220,
            Width = 95,
            Height = 38,
            DialogResult = WinForms.DialogResult.Cancel,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = Drawing.Color.FromArgb(55, 55, 60),
            ForeColor = Drawing.Color.FromArgb(200, 200, 205),
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        cancelButton.FlatAppearance.BorderSize = 0;

        okButton.Click += (_, _) =>
        {
            if (newPasswordBox.Text.Length < 4)
            {
                WinForms.MessageBox.Show("Master password must be at least 4 characters.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return;
            }

            if (!string.Equals(newPasswordBox.Text, confirmPasswordBox.Text, StringComparison.Ordinal))
            {
                WinForms.MessageBox.Show("The passwords do not match.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return;
            }

            dialog.DialogResult = WinForms.DialogResult.OK;
        };

        dialog.Controls.Add(titleLabel);
        dialog.Controls.Add(instructionLabel);
        dialog.Controls.Add(newPasswordLabel);
        dialog.Controls.Add(newPasswordBox);
        dialog.Controls.Add(confirmPasswordLabel);
        dialog.Controls.Add(confirmPasswordBox);
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        newPasswordBox.Select();
        return dialog.ShowDialog() == WinForms.DialogResult.OK ? newPasswordBox.Text : null;
    }

    private void SetupTrayIcon()
    {
        try
        {
            _trayMenu = new WinForms.ContextMenuStrip();
            _trayMenu.Items.Add("Open", null, (_, _) =>
            {
                ShowInTaskbar = true;
                WindowState = WindowState.Normal;
                Show();
                Activate();
                UpdateTaskbarMenuText();
            });
            _trayMenu.Items.Add("Exit", null, (_, _) => Close());

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            var icon = !string.IsNullOrWhiteSpace(exePath)
                ? Drawing.Icon.ExtractAssociatedIcon(exePath)
                : null;

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = icon,
                Visible = true,
                Text = "PinBubble",
                ContextMenuStrip = _trayMenu
            };

            _trayIcon.DoubleClick += (_, _) =>
            {
                ShowInTaskbar = true;
                WindowState = WindowState.Normal;
                Show();
                Activate();
                UpdateTaskbarMenuText();
            };
        }
        catch
        {
            // Tray icon is optional; app can continue without it.
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_expanded) Collapse();
        if (_isPinned)
        {
            // Ensure MainWindow stays topmost when pinned
            Dispatcher.BeginInvoke(new Action(() => 
            {
                if (_isPinned)
                    Topmost = true;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void SetupWatcher()
    {
        _reloadDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            LoadSnippets();
            BuildBubbles(); // Rebuild bubbles when file changes
        };

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_textFilePath)!, Path.GetFileName(_textFilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite
        };
        _watcher.Changed += (_, _) => Dispatcher.Invoke(() => _reloadDebounce!.Start());
        _watcher.EnableRaisingEvents = true;
    }

    private void LoadSnippets()
    {
        try
        {
            if (string.IsNullOrEmpty(_masterPassword))
            {
                _labels = Array.Empty<string>();
                _fullLabels = Array.Empty<string>();
                _snippets = Array.Empty<string>();
                _snippetRows = Array.Empty<SnippetRow>();
                return;
            }

            if (!EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out var plaintext))
            {
                _labels = Array.Empty<string>();
                _fullLabels = Array.Empty<string>();
                _snippets = Array.Empty<string>();
                _snippetRows = Array.Empty<SnippetRow>();
                return;
            }

            var parsed = ParseSnippets(plaintext);

            _labels = new string[parsed.Count];
            _fullLabels = new string[parsed.Count];
            _snippets = new string[parsed.Count];
            _snippetRows = new SnippetRow[parsed.Count];

            for (int i = 0; i < parsed.Count; i++)
            {
                var displayLabel = BuildBubbleLabel(parsed[i].Label);
                _fullLabels[i] = parsed[i].Label;
                _labels[i] = displayLabel;
                _snippets[i] = parsed[i].ActualValue;
                _snippetRows[i] = parsed[i];
            }
        }
        catch { }
    }

    private static string BuildBubbleLabel(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                sb.Append(c);
        }

        if (sb.Length == 0)
        {
            // Fallback: keep first alphanumeric character and uppercase it.
            foreach (var c in input)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    return c.ToString().ToUpperInvariant();
                if (c >= '0' && c <= '9')
                    return c.ToString();
            }

            return "NA";
        }

        return sb.Length > 6 ? sb.ToString(0, 6) : sb.ToString();
    }

    private void BuildBubbles()
    {
        BubblesHost.Children.Clear();
        var bubbleDefault = new SolidColorBrush(_isDarkTheme ? BubbleDefaultColorDark : BubbleDefaultColorLight);
        var bubbleHover = new SolidColorBrush(_isDarkTheme ? BubbleHoverColorDark : BubbleHoverColorLight);
        var bubbleBorder = new SolidColorBrush(_isDarkTheme ? BubbleBorderColorDark : BubbleBorderColorLight);
        var bubbleForeground = _isDarkTheme ? WpfBrushes.White : WpfBrushes.Black;

        // Dynamic width based on count - 5 per row
        int count = _labels.Length;
        int cols = Math.Min(count, 5);
        int rows = (int)Math.Ceiling(count / 5.0);
        double wNeeded = 25 + cols * 56 + 20;
        double hNeeded = 25 + rows * 64 + 20;

        // Store for expand
        _expandWidth = Math.Max(wNeeded, 100);
        _expandHeight = Math.Max(hNeeded, 100);

        // Build shortcut lookup: label -> display string (e.g. "Ctrl+Alt+A")
        var shortcutLookup = _shortcuts
            .GroupBy(s => s.SnippetLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayShortcut, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            var fullLbl = i < _fullLabels.Length ? _fullLabels[i] : _labels[i];
            var tipText = shortcutLookup.TryGetValue(fullLbl, out var sc)
                ? $"{fullLbl}\n\nShortcut: {sc}"
                : fullLbl;
            
            // Add TOTP info if configured with improved formatting
            if (i < _snippetRows.Length && !string.IsNullOrWhiteSpace(_snippetRows[i].TotpSecret))
            {
                var totpInfo = _snippetRows[i].TotpWithExpiry;
                if (totpInfo.HasValue)
                {
                    var progress = (int)((30 - totpInfo.Value.RemainingSeconds) / 30.0 * 100);
                    tipText += $"\n━━━━━━━━━━━━━━━━━━\n🔐 TOTP: {totpInfo.Value.Code}\n⏱ Expires in {totpInfo.Value.RemainingSeconds}s [{'█'} {progress}%]";
                }
            }

            var btn = new WpfButton
            {
                Width = 44,
                Height = 44,
                Content = _labels[i],
                FontSize = _labels[i].Length <= 3 ? 12 : 9,
                FontWeight = FontWeights.Bold,
                Foreground = bubbleForeground,
                Background = bubbleDefault,
                BorderThickness = new Thickness(1),
                BorderBrush = bubbleBorder,
                Tag = i,
                ToolTip = tipText,
                Cursor = WpfCursors.Hand,
                Margin = new Thickness(4)
            };

            btn.Click += Bubble_Click;
            btn.MouseEnter += (s, _) => { if (s is WpfButton b) b.Background = bubbleHover; };
            btn.MouseLeave += (s, _) => { if (s is WpfButton b) b.Background = bubbleDefault; };
            btn.Template = CreateRoundButtonTemplate();
            BubblesHost.Children.Add(btn);
        }
    }

    private double _expandWidth = 340;
    private double _expandHeight = 160;

    private ControlTemplate CreateRoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(WpfButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfButton.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(WpfButton.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(WpfButton.BorderThicknessProperty));
        border.SetValue(Border.EffectProperty, new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private async void Bubble_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton b && b.Tag is int idx && idx < _snippets.Length)
        {
            try
            {
                var snippetRow = idx < _snippetRows.Length ? _snippetRows[idx] : null;
                CopySnippetToClipboard(_snippets[idx], snippetRow);
                b.Background = BubbleClicked;
                await Task.Delay(150);
                b.Background = new SolidColorBrush(_isDarkTheme ? BubbleDefaultColorDark : BubbleDefaultColorLight);
            }
            catch { }
            Collapse();
        }
    }

    private void ToggleExpand()
    {
        if (_expanded) Collapse();
        else Expand();
    }

    private void Expand()
    {
        LoadSnippets();
        BuildBubbles();
        _preExpandWindowLeft = Left;
        _preExpandWindowTop = Top;
        _expanded = true;
        Width = _expandWidth;
        Height = _expandHeight;

        // Keep expanded UI on the monitor where the user is currently interacting.
        var activeWorkArea = GetWorkAreaForActiveScreen();
        Left = Math.Max(activeWorkArea.Left, Math.Min(Left, activeWorkArea.Right - Width));
        Top = Math.Max(activeWorkArea.Top, Math.Min(Top, activeWorkArea.Bottom - Height));

        Pin.Opacity = 0.3;
        ExpandedBackdrop.Visibility = Visibility.Visible;
        BubblesHost.Visibility = Visibility.Visible;
        PositionBubbles();
    }

    private void Collapse()
    {
        _expanded = false;
        Width = 64;
        Height = 64;

        if (_preExpandWindowLeft is double originalLeft)
            Left = originalLeft;

        if (_preExpandWindowTop is double originalTop)
            Top = originalTop;

        if (_preExpandWindowLeft is not double || _preExpandWindowTop is not double)
            SnapToNearestEdge();

        _preExpandWindowLeft = null;
        _preExpandWindowTop = null;
        Pin.Opacity = 1.0;
        Pin.Background = new SolidColorBrush(WpfColor.FromRgb(102, 185, 51));
        ExpandedBackdrop.Visibility = Visibility.Collapsed;
        BubblesHost.Visibility = Visibility.Collapsed;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartCursor = GetCursorPositionInWpfUnits();
        _dragStartWindow = new System.Windows.Point(Left, Top);
        _isDrag = false;
        CaptureMouse();
    }

    private void Root_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var cur = GetCursorPositionInWpfUnits();
        double dx = cur.X - _dragStartCursor.X;
        double dy = cur.Y - _dragStartCursor.Y;
        if (!_isDrag && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _isDrag = true;
        Left = _dragStartWindow.X + dx;
        Top = _dragStartWindow.Y + dy;
    }

    private System.Windows.Point GetCursorPositionInWpfUnits()
    {
        if (!GetCursorPos(out POINT cursorPoint))
            return new System.Windows.Point(Left, Top);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return new System.Windows.Point(cursorPoint.X, cursorPoint.Y);

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        return fromDevice.Transform(new System.Windows.Point(cursorPoint.X, cursorPoint.Y));
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_isDrag)
        {
            SnapToNearestEdge();
            SaveUiSettings();
        }
        else ToggleExpand();
        _isDrag = false;
    }

    private Rect GetWorkAreaForActiveScreen()
    {
        if (!GetCursorPos(out POINT cursorPoint))
            return SystemParameters.WorkArea;

        var activeScreen = WinForms.Screen.FromPoint(new Drawing.Point(cursorPoint.X, cursorPoint.Y));
        return ConvertDeviceRectToWpfRect(activeScreen.WorkingArea);
    }

    private Rect GetWorkAreaForCurrentWindowScreen()
    {
        var windowRect = ConvertWpfRectToDeviceRect(new Rect(
            Left,
            Top,
            Math.Max(1, Width),
            Math.Max(1, Height)));

        var screen = WinForms.Screen.FromRectangle(windowRect);
        return ConvertDeviceRectToWpfRect(screen.WorkingArea);
    }

    private bool IsWindowOffAllScreens()
    {
        var windowRect = new Rect(Left, Top, Width, Height);
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var screenRect = ConvertDeviceRectToWpfRect(screen.WorkingArea);
            if (windowRect.IntersectsWith(screenRect))
                return false;
        }

        return true;
    }

    private Rect ConvertDeviceRectToWpfRect(Drawing.Rectangle deviceRect)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return new Rect(deviceRect.Left, deviceRect.Top, deviceRect.Width, deviceRect.Height);

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new System.Windows.Point(deviceRect.Left, deviceRect.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Drawing.Rectangle ConvertWpfRectToDeviceRect(Rect wpfRect)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Drawing.Rectangle(
                (int)Math.Round(wpfRect.Left),
                (int)Math.Round(wpfRect.Top),
                (int)Math.Round(wpfRect.Width),
                (int)Math.Round(wpfRect.Height));
        }

        var toDevice = source.CompositionTarget.TransformToDevice;
        var topLeft = toDevice.Transform(new System.Windows.Point(wpfRect.Left, wpfRect.Top));
        var bottomRight = toDevice.Transform(new System.Windows.Point(wpfRect.Right, wpfRect.Bottom));

        return Drawing.Rectangle.FromLTRB(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X),
            (int)Math.Round(bottomRight.Y));
    }

    private void MoveToActiveScreenIfOffscreen()
    {
        if (!IsWindowOffAllScreens())
            return;

        var activeWorkArea = GetWorkAreaForActiveScreen();
        Left = Math.Max(activeWorkArea.Left, Math.Min(Left, activeWorkArea.Right - Width));
        Top = Math.Max(activeWorkArea.Top, Math.Min(Top, activeWorkArea.Bottom - Height));
    }

    private void SnapToNearestEdge()
    {
        MoveToActiveScreenIfOffscreen();

        var wa = GetWorkAreaForCurrentWindowScreen();
        double leftDist = Math.Abs(Left - wa.Left);
        double rightDist = Math.Abs(Left + Width - wa.Right);
        double topDist = Math.Abs(Top - wa.Top);
        double bottomDist = Math.Abs(Top + Height - wa.Bottom);
        var minDist = Math.Min(Math.Min(leftDist, rightDist), Math.Min(topDist, bottomDist));

        if (minDist == leftDist) Left = wa.Left;
        else if (minDist == rightDist) Left = wa.Right - Width;
        else if (minDist == topDist) Top = wa.Top;
        else Top = wa.Bottom - Height;

        Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
        Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
    }

    private void RestoreWindowPlacement()
    {
        if (_savedWindowLeft is null || _savedWindowTop is null)
        {
            SnapToNearestEdge();
            return;
        }

        Left = _savedWindowLeft.Value;
        Top = _savedWindowTop.Value;

        if (!string.IsNullOrWhiteSpace(_savedMonitorDeviceName))
        {
            var savedScreen = WinForms.Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, _savedMonitorDeviceName, StringComparison.OrdinalIgnoreCase));

            if (savedScreen is not null)
            {
                var wa = ConvertDeviceRectToWpfRect(savedScreen.WorkingArea);
                Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            }
        }

        if (IsWindowOffAllScreens())
            MoveToActiveScreenIfOffscreen();

        SnapToNearestEdge();
    }

    private void PositionBubbles()
    {
        var wa = GetWorkAreaForCurrentWindowScreen();
        double startX = 25;
        double startY = 25;
        double spacingX = 56;
        double spacingY = 64;

        if (Math.Abs(Left + _expandWidth - wa.Right) < 50)
            startX = _expandWidth - 5 * spacingX - 20;

        for (int i = 0; i < BubblesHost.Children.Count; i++)
        {
            int row = i / 5;
            int col = i % 5;
            Canvas.SetLeft((UIElement)BubblesHost.Children[i], startX + col * spacingX);
            Canvas.SetTop((UIElement)BubblesHost.Children[i], startY + row * spacingY);
        }
    }

    // ── Global hotkey infrastructure ───────────────────────────────────────────

    private void SetupGlobalHotkeys()
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        // Ctrl+Alt+P → QWERTY quick-paste picker (VK 'P' = 0x50)
        if (RegisterHotKey(helper.Handle, HotkeyIdQwertyPicker, ModControl | ModAlt | ModNoRepeat, 0x50))
            _registeredHotkeyIds.Add(HotkeyIdQwertyPicker);

        RegisterShortcutHotkeys();
    }

    private void RegisterShortcutHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Unregister only user-defined hotkeys (keep QWERTY picker)
        foreach (var id in _registeredHotkeyIds.Where(id => id >= HotkeyIdUserBase).ToList())
        {
            UnregisterHotKey(hwnd, id);
            _registeredHotkeyIds.Remove(id);
        }

        for (int i = 0; i < _shortcuts.Count; i++)
        {
            var s = _shortcuts[i];
            int hotkeyId = HotkeyIdUserBase + i;
            if (s.VirtualKey > 0 && RegisterHotKey(hwnd, hotkeyId, s.Modifiers | ModNoRepeat, s.VirtualKey))
                _registeredHotkeyIds.Add(hotkeyId);
        }
    }

    private void UnregisterAllHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        foreach (var id in _registeredHotkeyIds)
            UnregisterHotKey(hwnd, id);
        _registeredHotkeyIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = (int)wParam;
            // Capture foreground window before any UI appears
            IntPtr prevHwnd = GetForegroundWindow();
            Dispatcher.BeginInvoke(new Action(() => HandleHotkey(id, prevHwnd)));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void HandleHotkey(int id, IntPtr prevHwnd)
    {
        if (id == HotkeyIdQwertyPicker)
        {
            await ShowQwertyPicker(prevHwnd);
            return;
        }

        int idx = id - HotkeyIdUserBase;
        if (idx < 0 || idx >= _shortcuts.Count) return;

        var shortcut = _shortcuts[idx];
        int snippetIdx = Array.FindIndex(_fullLabels, l =>
            string.Equals(l, shortcut.SnippetLabel, StringComparison.OrdinalIgnoreCase));

        if (snippetIdx < 0 || snippetIdx >= _snippets.Length) return;

        try
        {
            var snippetRow = snippetIdx < _snippetRows.Length ? _snippetRows[snippetIdx] : null;
            CopySnippetToClipboard(_snippets[snippetIdx], snippetRow);
        }
        catch { }

        if (prevHwnd != IntPtr.Zero)
            SetForegroundWindow(prevHwnd);
    }

    // ── Shortcut persistence ────────────────────────────────────────────────

    private void LoadShortcuts()
    {
        try
        {
            if (!File.Exists(_shortcutsFilePath)) return;
            var json = File.ReadAllText(_shortcutsFilePath);
            _shortcuts = JsonSerializer.Deserialize<List<ShortcutEntry>>(json, s_jsonOptions) ?? new();
        }
        catch { _shortcuts = new(); }
    }

    private void SaveShortcuts()
    {
        try
        {
            var json = JsonSerializer.Serialize(_shortcuts, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_shortcutsFilePath, json);
        }
        catch { }
    }

    private static string BuildShortcutDisplay(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        parts.Add(((WinForms.Keys)vk).ToString());
        return string.Join("+", parts);
    }

    private static string TruncateLabel(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    // ── Shortcut editor dialog ───────────────────────────────────────────────

    private void EditShortcuts_Click(object sender, RoutedEventArgs e)
    {
        // Work on a copy so Cancel is a true cancel
        var working = _shortcuts.Select(s => new ShortcutEntry
        {
            SnippetLabel = s.SnippetLabel,
            Modifiers = s.Modifiers,
            VirtualKey = s.VirtualKey,
            DisplayShortcut = s.DisplayShortcut
        }).ToList();

        using var dlg = new WinForms.Form
        {
            ClientSize = new Drawing.Size(680, 480),
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Text = "Manage Shortcuts",
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black
        };
        using (var dialogRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, dlg.Width - 1, dlg.Height - 1), 18))
            dlg.Region = new Drawing.Region(dialogRegionPath);
        dlg.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, dlg.Width - 1, dlg.Height - 1), 18);
            using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
            pe.Graphics.DrawPath(pen, path);
        };

        bool shortcutsDirty = false;
        bool allowShortcutClose = false;

        // ── Toolbar ──────────────────────────────────────────────────────────
        var toolbar = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 82,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(48, 48, 55) : Drawing.Color.White
        };
        toolbar.Padding = new WinForms.Padding(0);

        var shortcutIcon = new WinForms.Label
        {
            Left = 20, Top = 18, Width = 38, Height = 38,
            Text = "⌨",
            Font = new Drawing.Font("Segoe UI Emoji", 22f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(110, 195, 60) : Drawing.Color.FromArgb(55, 135, 45),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };
        var shortcutTitle = new WinForms.Label
        {
            Left = 66, Top = 12, Width = 190, Height = 28,
            Text = "Manage Shortcuts",
            Font = new Drawing.Font("Segoe UI", 15f, Drawing.FontStyle.Bold),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36),
            BackColor = Drawing.Color.Transparent
        };

        var infoLbl = new WinForms.Label
        {
            Left = 66, Top = 42, Width = 190, Height = 30,
            Text = "Copy snippets instantly.\nCtrl+Alt+P opens the picker.",
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(130, 130, 140) : Drawing.Color.Gray,
            Font = new Drawing.Font("Segoe UI", 8.5f),
            BackColor = Drawing.Color.Transparent
        };

        WinForms.Button MakeToolBtn(string text, Drawing.Color bg, int left) => new WinForms.Button
        {
            Text = text, Left = left, Top = 21, Width = 86, Height = 40,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Drawing.Color.White,
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = false
        };

        var btnAdd    = MakeToolBtn("＋ Add",    Drawing.Color.FromArgb(0, 110, 70),  280);
        var btnRemove = MakeToolBtn("✕ Remove",  Drawing.Color.FromArgb(130, 40, 40), 372);
        var btnSave   = MakeToolBtn("Save",      Drawing.Color.FromArgb(0, 100, 180), 464);
        var btnClose  = MakeToolBtn("Close",     Drawing.Color.FromArgb(60, 60, 68),  556);

        foreach (var b in new[] { btnAdd, btnRemove, btnSave, btnClose })
        {
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = b.BackColor;
            b.FlatAppearance.MouseDownBackColor = b.BackColor;
            toolbar.Controls.Add(b);
        }
        toolbar.Controls.Add(shortcutIcon);
        toolbar.Controls.Add(shortcutTitle);
        toolbar.Controls.Add(infoLbl);

        // ── ListView ─────────────────────────────────────────────────────────
        var list = new WinForms.ListView
        {
            Dock = WinForms.DockStyle.Fill,
            View = WinForms.View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            Font = new Drawing.Font("Segoe UI", 10f),
            BorderStyle = WinForms.BorderStyle.None,
            OwnerDraw = true,
            HeaderStyle = WinForms.ColumnHeaderStyle.Nonclickable
        };
        var listFrame = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Fill,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220),
            Padding = new WinForms.Padding(1)
        };
        listFrame.Controls.Add(list);
        list.Columns.Add("Shortcut", 220);
        list.Columns.Add("Linked Snippet", -2);
        list.DrawColumnHeader += (_, e) =>
        {
            using var brush = new Drawing.SolidBrush(_isDarkTheme ? Drawing.Color.FromArgb(48, 48, 55) : Drawing.Color.FromArgb(240, 241, 244));
            e.Graphics.FillRectangle(brush, e.Bounds);
            using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220));
            e.Graphics.DrawRectangle(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var headerTextColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.FromArgb(40, 40, 45);
            var textBounds = Drawing.Rectangle.Inflate(e.Bounds, -8, -2);
            TextRenderer.DrawText(
                e.Graphics,
                e.Header?.Text ?? string.Empty,
                list.Font,
                textBounds,
                headerTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;

        void RefreshList()
        {
            list.Items.Clear();
            foreach (var s in working)
            {
                var item = new WinForms.ListViewItem(s.DisplayShortcut);
                item.SubItems.Add(s.SnippetLabel);
                item.Tag = s;
                list.Items.Add(item);
            }
        }
        RefreshList();

        void MarkShortcutsDirty()
        {
            shortcutsDirty = true;
            btnSave.Visible = true;
        }

        // Tooltip label for shortcut column hover
        var tooltipLabel = new WinForms.Label
        {
            Visible = false,
            AutoSize = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(48, 48, 55) : Drawing.Color.FromArgb(255, 255, 255),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.FromArgb(30, 30, 35),
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            Font = new Drawing.Font("Segoe UI", 9f),
            Padding = new WinForms.Padding(6, 4, 6, 4),
            Text = "Right-click to edit shortcut key",
            Anchor = WinForms.AnchorStyles.None
        };
        dlg.Controls.Add(tooltipLabel);
        tooltipLabel.BringToFront();

        var tooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        var hideTooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        var currentHitItem = (WinForms.ListViewItem?)null;
        var lastMousePos = Drawing.Point.Empty;

        list.MouseMove += (_, mouseArgs) =>
        {
            var hit = list.HitTest(mouseArgs.Location);
            lastMousePos = mouseArgs.Location;
            
            if (hit.Item is null || hit.SubItem is null || hit.Item.SubItems.IndexOf(hit.SubItem) != 0)
            {
                tooltipTimer.Stop();
                hideTooltipTimer.Stop();
                tooltipLabel.Visible = false;
                currentHitItem = null;
                return;
            }

            // Only start timer if hovering over a different item
            if (!ReferenceEquals(currentHitItem, hit.Item))
            {
                tooltipTimer.Stop();
                hideTooltipTimer.Stop();
                tooltipLabel.Visible = false;
                currentHitItem = hit.Item;
                tooltipTimer.Start();
            }

            // Update tooltip position while visible
            if (tooltipLabel.Visible)
            {
                var tooltipX = Math.Max(0, lastMousePos.X + 10);
                var tooltipY = Math.Max(0, lastMousePos.Y + 15);
                tooltipLabel.Location = new Drawing.Point(tooltipX, tooltipY);
            }
        };
        tooltipTimer.Tick += (_, _) =>
        {
            tooltipTimer.Stop();
            tooltipLabel.Visible = true;
            // Position tooltip below cursor when it appears
            var tooltipX = Math.Max(0, lastMousePos.X + 10);
            var tooltipY = Math.Max(0, lastMousePos.Y + 15);
            tooltipLabel.Location = new Drawing.Point(tooltipX, tooltipY);
            // Start hide timer - tooltip will be visible for 2 seconds
            hideTooltipTimer.Start();
        };
        hideTooltipTimer.Tick += (_, _) =>
        {
            hideTooltipTimer.Stop();
            tooltipLabel.Visible = false;
        };
        list.MouseLeave += (_, _) =>
        {
            tooltipTimer.Stop();
            hideTooltipTimer.Stop();
            tooltipLabel.Visible = false;
            currentHitItem = null;
        };

        list.MouseUp += (_, mouseArgs) =>
        {
            if (mouseArgs.Button != WinForms.MouseButtons.Right)
                return;

            var hit = list.HitTest(mouseArgs.Location);
            if (hit.Item is null || hit.SubItem is null || hit.Item.Tag is not ShortcutEntry shortcut)
                return;
            if (hit.Item.SubItems.IndexOf(hit.SubItem) != 0)
                return;

            (int mods, int vk, string display)? recaptured = null;
            using var recorder = new WinForms.Form
            {
                ClientSize = new Drawing.Size(420, 220),
                FormBorderStyle = WinForms.FormBorderStyle.None,
                StartPosition = WinForms.FormStartPosition.CenterParent,
                Text = "Recapture Shortcut",
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true,
                KeyPreview = true,
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
            };
            using (var regionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, recorder.Width - 1, recorder.Height - 1), 18))
                recorder.Region = new Drawing.Region(regionPath);

            var prompt = new WinForms.Label
            {
                Left = 28, Top = 24, Width = 364, Height = 30,
                Text = "Recapture Shortcut",
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36),
                BackColor = Drawing.Color.Transparent
            };
            var hint = new WinForms.Label
            {
                Left = 28, Top = 64, Width = 364, Height = 22,
                Text = $"Press a new key for {shortcut.SnippetLabel}",
                Font = new Drawing.Font("Segoe UI", 9f),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                BackColor = Drawing.Color.Transparent
            };
            var display = new WinForms.Label
            {
                Left = 28, Top = 92, Width = 364, Height = 38,
                Text = shortcut.DisplayShortcut,
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = Drawing.Color.FromArgb(0, 180, 120),
                BackColor = Drawing.Color.Transparent,
                TextAlign = Drawing.ContentAlignment.MiddleCenter
            };
            var use = new WinForms.Button
            {
                Text = "Use This", Left = 180, Top = 156, Width = 104, Height = 40,
                DialogResult = WinForms.DialogResult.OK,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(0, 110, 70),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Enabled = false,
                Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            use.FlatAppearance.BorderSize = 0;
            use.FlatAppearance.MouseOverBackColor = use.BackColor;
            use.FlatAppearance.MouseDownBackColor = use.BackColor;
            var cancel = new WinForms.Button
            {
                Text = "Cancel", Left = 296, Top = 156, Width = 96, Height = 40,
                DialogResult = WinForms.DialogResult.Cancel,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(60, 60, 68),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            cancel.FlatAppearance.BorderSize = 0;
            cancel.FlatAppearance.MouseOverBackColor = cancel.BackColor;
            cancel.FlatAppearance.MouseDownBackColor = cancel.BackColor;
            recorder.Controls.AddRange(new WinForms.Control[] { prompt, hint, display, use, cancel });
            recorder.AcceptButton = use;
            recorder.CancelButton = cancel;
            recorder.KeyDown += (_, keyArgs) =>
            {
                keyArgs.SuppressKeyPress = true;
                var modifiers = 0;
                if (keyArgs.Control) modifiers |= ModControl;
                if (keyArgs.Alt) modifiers |= ModAlt;
                if (keyArgs.Shift) modifiers |= ModShift;
                if (keyArgs.KeyCode is WinForms.Keys.ControlKey or WinForms.Keys.Menu or WinForms.Keys.ShiftKey)
                    return;
                if (modifiers == 0) return;
                var virtualKey = (int)keyArgs.KeyCode;
                recaptured = (modifiers, virtualKey, BuildShortcutDisplay(modifiers, virtualKey));
                display.Text = recaptured.Value.display;
                use.Enabled = true;
            };

            if (recorder.ShowDialog(dlg) != WinForms.DialogResult.OK || recaptured is null)
                return;

            if (working.Any(s => !ReferenceEquals(s, shortcut) && s.VirtualKey == recaptured.Value.vk && s.Modifiers == recaptured.Value.mods))
            {
                WinForms.MessageBox.Show("That key combination is already assigned.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return;
            }

            shortcut.Modifiers = recaptured.Value.mods;
            shortcut.VirtualKey = recaptured.Value.vk;
            shortcut.DisplayShortcut = recaptured.Value.display;
            MarkShortcutsDirty();
            RefreshList();
        };

        // ── Add shortcut ─────────────────────────────────────────────────────
        btnAdd.Click += (_, _) =>
        {
            // Step 1: Record key combo
            (int mods, int vk, string display)? recorded = null;

            using var recorder = new WinForms.Form
            {
                ClientSize = new Drawing.Size(420, 220),
                FormBorderStyle = WinForms.FormBorderStyle.None,
                StartPosition = WinForms.FormStartPosition.CenterParent,
                Text = "Record Shortcut",
                MaximizeBox = false, MinimizeBox = false, TopMost = true,
                KeyPreview = true,
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
            };
            using (var recorderRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, recorder.Width - 1, recorder.Height - 1), 18))
                recorder.Region = new Drawing.Region(recorderRegionPath);
            recorder.Paint += (_, pe) =>
            {
                pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, recorder.Width - 1, recorder.Height - 1), 18);
                using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
                pe.Graphics.DrawPath(pen, path);
            };

            var recorderTitle = new WinForms.Label
            {
                Left = 28, Top = 22, Width = 350, Height = 30,
                Text = "Record Shortcut",
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36),
                BackColor = Drawing.Color.Transparent
            };

            var recLabel = new WinForms.Label
            {
                Left = 28, Top = 62, Width = 364, Height = 24,
                Text = "Press a key combination",
                Font = new Drawing.Font("Segoe UI", 9f),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                BackColor = Drawing.Color.Transparent
            };
            var recDisplay = new WinForms.Label
            {
                Left = 28, Top = 88, Width = 364, Height = 40,
                Text = "Waiting for input...",
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = Drawing.Color.FromArgb(0, 180, 120),
                BackColor = Drawing.Color.Transparent,
                TextAlign = Drawing.ContentAlignment.MiddleCenter
            };
            var recUse = new WinForms.Button
            {
                Text = "Use This", Left = 180, Top = 156, Width = 104, Height = 40,
                DialogResult = WinForms.DialogResult.OK,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(0, 110, 70),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Enabled = false, Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            recUse.FlatAppearance.BorderSize = 0;
            recUse.FlatAppearance.MouseOverBackColor = recUse.BackColor;
            recUse.FlatAppearance.MouseDownBackColor = recUse.BackColor;
            var recCancel = new WinForms.Button
            {
                Text = "Cancel", Left = 296, Top = 156, Width = 96, Height = 40,
                DialogResult = WinForms.DialogResult.Cancel,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(60, 60, 68),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            recCancel.FlatAppearance.BorderSize = 0;
            recCancel.FlatAppearance.MouseOverBackColor = recCancel.BackColor;
            recCancel.FlatAppearance.MouseDownBackColor = recCancel.BackColor;
            recorder.Controls.AddRange(new WinForms.Control[] { recorderTitle, recLabel, recDisplay, recUse, recCancel });
            recorder.AcceptButton = recUse;
            recorder.CancelButton = recCancel;

            recorder.KeyDown += (_, ke) =>
            {
                ke.SuppressKeyPress = true;
                var modifiers = 0;
                if (ke.Control) modifiers |= ModControl;
                if (ke.Alt)     modifiers |= ModAlt;
                if (ke.Shift)   modifiers |= ModShift;

                // Ignore standalone modifier keys
                var ignoreKeys = new[]
                {
                    WinForms.Keys.ControlKey, WinForms.Keys.Menu,
                    WinForms.Keys.ShiftKey, WinForms.Keys.LWin, WinForms.Keys.RWin
                };
                if (Array.IndexOf(ignoreKeys, ke.KeyCode) >= 0) return;
                if (modifiers == 0) return; // require at least one modifier

                int vkCode = (int)ke.KeyCode;
                string display = BuildShortcutDisplay(modifiers, vkCode);
                recDisplay.Text = display;
                recorded = (modifiers, vkCode, display);
                recUse.Enabled = true;
            };

            if (recorder.ShowDialog(dlg) != WinForms.DialogResult.OK || recorded is null)
                return;

            // Step 2: Pick a snippet
            if (_fullLabels.Length == 0)
            {
                WinForms.MessageBox.Show("No snippets found. Add snippets first via Edit Snippets.",
                    "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                return;
            }

            using var snippetPicker = new WinForms.Form
            {
                ClientSize = new Drawing.Size(440, 240),
                FormBorderStyle = WinForms.FormBorderStyle.None,
                StartPosition = WinForms.FormStartPosition.CenterParent,
                Text = "Link to Snippet",
                MaximizeBox = false, MinimizeBox = false, TopMost = true,
                KeyPreview = true,
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
            };
            using (var pickerRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, snippetPicker.Width - 1, snippetPicker.Height - 1), 18))
                snippetPicker.Region = new Drawing.Region(pickerRegionPath);
            snippetPicker.Paint += (_, pe) =>
            {
                pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, snippetPicker.Width - 1, snippetPicker.Height - 1), 18);
                using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
                pe.Graphics.DrawPath(pen, path);
            };

            var pickerTitle = new WinForms.Label
            {
                Left = 28, Top = 22, Width = 350, Height = 30,
                Text = "Link to Snippet",
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36),
                BackColor = Drawing.Color.Transparent
            };

            var pickLabel = new WinForms.Label
            {
                Left = 28, Top = 64, Width = 384, Height = 22,
                Text = $"Choose a snippet for {recorded.Value.display}",
                Font = new Drawing.Font("Segoe UI", 9f),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                BackColor = Drawing.Color.Transparent
            };
            var combo = new WinForms.ComboBox
            {
                Left = 28, Top = 94, Width = 384, Height = 30,
                DropDownStyle = WinForms.ComboBoxStyle.DropDownList,
                Font = new Drawing.Font("Segoe UI", 10f),
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                FlatStyle = WinForms.FlatStyle.Flat
            };
            combo.Items.AddRange(_fullLabels.Cast<object>().ToArray());
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;

            var pickOk = new WinForms.Button
            {
                Text = "Link", Left = 208, Top = 164, Width = 96, Height = 40,
                DialogResult = WinForms.DialogResult.OK,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(0, 110, 70),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            pickOk.FlatAppearance.BorderSize = 0;
            pickOk.FlatAppearance.MouseOverBackColor = pickOk.BackColor;
            pickOk.FlatAppearance.MouseDownBackColor = pickOk.BackColor;
            var pickCancel = new WinForms.Button
            {
                Text = "Cancel", Left = 316, Top = 164, Width = 96, Height = 40,
                DialogResult = WinForms.DialogResult.Cancel,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(60, 60, 68),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = false
            };
            pickCancel.FlatAppearance.BorderSize = 0;
            pickCancel.FlatAppearance.MouseOverBackColor = pickCancel.BackColor;
            pickCancel.FlatAppearance.MouseDownBackColor = pickCancel.BackColor;
            snippetPicker.Controls.AddRange(new WinForms.Control[] { pickerTitle, pickLabel, combo, pickOk, pickCancel });
            snippetPicker.AcceptButton = pickOk;
            snippetPicker.CancelButton = pickCancel;

            if (snippetPicker.ShowDialog(dlg) != WinForms.DialogResult.OK) return;

            var selectedSnippet = combo.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selectedSnippet)) return;

            // Check for duplicate hotkey
            if (working.Any(s => s.VirtualKey == recorded.Value.vk && s.Modifiers == recorded.Value.mods))
            {
                WinForms.MessageBox.Show("That key combination is already assigned.",
                    "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return;
            }

            working.Add(new ShortcutEntry
            {
                SnippetLabel = selectedSnippet,
                Modifiers = recorded.Value.mods,
                VirtualKey = recorded.Value.vk,
                DisplayShortcut = recorded.Value.display
            });
            RefreshList();
            MarkShortcutsDirty();
        };

        // ── Remove shortcut ───────────────────────────────────────────────────
        btnRemove.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            int idx = list.SelectedItems[0].Index;
            if (idx >= 0 && idx < working.Count)
            {
                working.RemoveAt(idx);
                RefreshList();
                MarkShortcutsDirty();
            }
        };

        // ── Save ──────────────────────────────────────────────────────────────
        btnSave.Click += (_, _) =>
        {
            _shortcuts = working.Select(s => new ShortcutEntry
            {
                SnippetLabel = s.SnippetLabel,
                Modifiers = s.Modifiers,
                VirtualKey = s.VirtualKey,
                DisplayShortcut = s.DisplayShortcut
            }).ToList();
            SaveShortcuts();
            RegisterShortcutHotkeys();
            shortcutsDirty = false;
            allowShortcutClose = true;
            dlg.DialogResult = WinForms.DialogResult.OK;
            dlg.Close();
        };

        // ── Close ─────────────────────────────────────────────────────────────
        void CloseShortcutDialog()
        {
            if (shortcutsDirty)
            {
                var result = WinForms.MessageBox.Show(
                    "You have unsaved shortcut changes. Save before closing?",
                    "PinBubble - Unsaved Changes",
                    WinForms.MessageBoxButtons.YesNoCancel,
                    WinForms.MessageBoxIcon.Question);
                if (result == WinForms.DialogResult.Cancel)
                    return;
                if (result == WinForms.DialogResult.Yes)
                {
                    btnSave.PerformClick();
                    return;
                }
            }

            allowShortcutClose = true;
            dlg.DialogResult = WinForms.DialogResult.Cancel;
            dlg.Close();
        }

        btnClose.Click += (_, _) => CloseShortcutDialog();
        dlg.KeyDown += (_, ke) =>
        {
            if (ke.Control && ke.KeyCode == WinForms.Keys.S)
            {
                ke.SuppressKeyPress = true;
                btnSave.PerformClick();
            }
            else if (ke.KeyCode == WinForms.Keys.Escape)
            {
                ke.SuppressKeyPress = true;
                CloseShortcutDialog();
            }
        };
        dlg.FormClosing += (_, closeArgs) =>
        {
            if (!allowShortcutClose && shortcutsDirty)
            {
                closeArgs.Cancel = true;
                CloseShortcutDialog();
            }
        };

        bool dragging = false;
        Drawing.Point dragCursor = Drawing.Point.Empty;
        Drawing.Point dragDialog = Drawing.Point.Empty;
        toolbar.MouseDown += (_, mouseArgs) =>
        {
            if (mouseArgs.Button != WinForms.MouseButtons.Left) return;
            dragging = true;
            dragCursor = WinForms.Cursor.Position;
            dragDialog = dlg.Location;
        };
        toolbar.MouseMove += (_, _) =>
        {
            if (!dragging) return;
            var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(dragCursor));
            dlg.Location = Drawing.Point.Add(dragDialog, new Drawing.Size(diff));
        };
        toolbar.MouseUp += (_, _) => dragging = false;
        foreach (var dragTarget in new WinForms.Control[] { shortcutIcon, shortcutTitle, infoLbl })
        {
            dragTarget.MouseDown += (_, mouseArgs) =>
            {
                if (mouseArgs.Button != WinForms.MouseButtons.Left) return;
                dragging = true;
                dragCursor = WinForms.Cursor.Position;
                dragDialog = dlg.Location;
            };
            dragTarget.MouseMove += (_, _) =>
            {
                if (!dragging) return;
                var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(dragCursor));
                dlg.Location = Drawing.Point.Add(dragDialog, new Drawing.Size(diff));
            };
            dragTarget.MouseUp += (_, _) => dragging = false;
        }

        dlg.Controls.Add(listFrame);
        dlg.Controls.Add(toolbar);
        dlg.ShowDialog();
    }

    // ── QWERTY quick-paste picker ────────────────────────────────────────────

    private async Task ShowQwertyPicker(IntPtr prevHwnd)
    {
        LoadSnippets();

        const int keySize = 65;
        const int keyGap = 5;
        const int stride = keySize + keyGap;

        // Row offsets (x pixel) to simulate QWERTY stagger
        int[] rowXOffsets = { 19, 52, 98 };
        int[][] rowKeyIndices =
        {
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },      // Q–P
            new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 }, // A–L
            new[] { 19, 20, 21, 22, 23, 24, 25 }           // Z–M
        };
        int[] rowYOffsets = { 55, 130, 204 };

        int maxRowWidth = rowXOffsets[0] + 10 * stride + 16;
        int totalHeight = rowYOffsets[2] + keySize + 20;

        using var picker = new WinForms.Form
        {
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Width = maxRowWidth,
            Height = totalHeight,
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = Drawing.Color.FromArgb(22, 22, 28),
            KeyPreview = true
        };

        // Rounded border via Paint
        picker.Paint += (s, pe) =>
        {
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(80, 80, 90), 1.5f);
            pe.Graphics.DrawRectangle(pen, 0, 0, picker.Width - 1, picker.Height - 1);
        };

        // Title / hint
        var hint = new WinForms.Label
        {
            Left = 0, Top = 10, Width = picker.Width, Height = 32,
            Text = "⌨  Ctrl+Alt+P  –  press a key to copy  |  Esc to close",
            ForeColor = Drawing.Color.FromArgb(120, 120, 135),
            Font = new Drawing.Font("Segoe UI", 10f),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };
        picker.Controls.Add(hint);

        string? copiedSnippet = null;

        // Tooltip component for key buttons
        var keyTooltip = new WinForms.ToolTip
        {
            InitialDelay = 600,
            ReshowDelay = 200,
            AutoPopDelay = 5000,
            ShowAlways = false,
            BackColor = Drawing.Color.FromArgb(22, 22, 28),
            ForeColor = Drawing.Color.FromArgb(210, 255, 210),
            IsBalloon = false
        };

        // Shared resources for custom key button painting (scoped to picker lifetime)
        using var activeLetterFont   = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold);
        using var inactiveLetterFont = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Regular);
        using var snippetLabelFont   = new Drawing.Font("Segoe UI", 9f);
        using var keyPaintSf = new Drawing.StringFormat
        {
            Alignment     = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
            Trimming      = Drawing.StringTrimming.Character
        };

        // Build keys
        for (int rowIdx = 0; rowIdx < 3; rowIdx++)
        {
            int xPos = rowXOffsets[rowIdx];
            int yPos = rowYOffsets[rowIdx];

            foreach (int charIdx in rowKeyIndices[rowIdx])
            {
                bool hasSnippet = charIdx < _snippets.Length && !string.IsNullOrEmpty(_snippets[charIdx]);
                string snippetLabel = hasSnippet && charIdx < _fullLabels.Length ? _fullLabels[charIdx] : "";
                string snippetValue = hasSnippet ? _snippets[charIdx] : "";
                char keyChar = QwertyOrder[charIdx];

                var keyBg   = hasSnippet ? Drawing.Color.FromArgb(42, 100, 48)  : Drawing.Color.FromArgb(32, 32, 38);
                var keyFg   = hasSnippet ? Drawing.Color.FromArgb(210, 255, 210) : Drawing.Color.FromArgb(60, 60, 72);
                var borderC = hasSnippet ? Drawing.Color.FromArgb(70, 145, 80)   : Drawing.Color.FromArgb(48, 48, 56);

                var capturedChar  = keyChar;
                var capturedLabel = BuildBubbleLabel(snippetLabel);
                var capturedHas   = hasSnippet;

                var btn = new WinForms.Button
                {
                    Left = xPos, Top = yPos,
                    Width = keySize, Height = keySize,
                    FlatStyle = WinForms.FlatStyle.Flat,
                    BackColor = keyBg,
                    ForeColor = keyFg,
                    Cursor = hasSnippet ? WinForms.Cursors.Hand : WinForms.Cursors.Default,
                    Enabled = hasSnippet,
                    Text = string.Empty,
                    Tag = snippetValue
                };
                btn.FlatAppearance.BorderColor = borderC;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.MouseOverBackColor = Drawing.Color.FromArgb(58, 140, 66);

                if (hasSnippet)
                    keyTooltip.SetToolTip(btn, $"\n\n{snippetLabel}");

                // Custom paint: bold key letter / thin divider / small snippet label
                btn.Paint += (_, pe) =>
                {
                    pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    pe.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    if (capturedHas)
                    {
                        // Key letter – upper portion, bold
                        using var lb = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 255, 230));
                        pe.Graphics.DrawString(capturedChar.ToString(), activeLetterFont, lb,
                            new Drawing.RectangleF(0, 1, keySize, keySize * 0.46f), keyPaintSf);
                        // Divider line
                        int ly = (int)(keySize * 0.51f);
                        using var lp = new Drawing.Pen(Drawing.Color.FromArgb(70, 145, 80), 1f);
                        pe.Graphics.DrawLine(lp, 6, ly, keySize - 6, ly);
                        // Snippet label – lower portion
                        using var slb = new Drawing.SolidBrush(Drawing.Color.FromArgb(150, 215, 155));
                        pe.Graphics.DrawString(capturedLabel, snippetLabelFont, slb,
                            new Drawing.RectangleF(2, ly + 2, keySize - 4, keySize - ly - 4), keyPaintSf);
                    }
                    else
                    {
                        // Inactive key: letter centered, dimmed
                        using var lb = new Drawing.SolidBrush(Drawing.Color.FromArgb(55, 55, 68));
                        pe.Graphics.DrawString(capturedChar.ToString(), inactiveLetterFont, lb,
                            new Drawing.RectangleF(0, 0, keySize, keySize), keyPaintSf);
                    }
                };

                if (hasSnippet)
                {
                    var capturedValue = snippetValue;
                    btn.Click += (_, _) =>
                    {
                        copiedSnippet = capturedValue;
                        picker.DialogResult = WinForms.DialogResult.OK;
                        picker.Close();
                    };
                }

                picker.Controls.Add(btn);
                xPos += stride;
            }
        }

        // Key press handler
        picker.KeyDown += (_, ke) =>
        {
            ke.SuppressKeyPress = true;
            if (ke.KeyCode == WinForms.Keys.Escape)
            {
                picker.DialogResult = WinForms.DialogResult.Cancel;
                picker.Close();
                return;
            }

            string keyName = ke.KeyCode.ToString();
            if (keyName.Length == 1)
            {
                char c = char.ToUpper(keyName[0]);
                int idx = Array.IndexOf(QwertyOrder, c);
                if (idx >= 0 && idx < _snippets.Length && !string.IsNullOrEmpty(_snippets[idx]))
                {
                    copiedSnippet = _snippets[idx];
                    picker.DialogResult = WinForms.DialogResult.OK;
                    picker.Close();
                }
            }
        };

        // Ensure picker grabs focus once fully shown, preventing spurious Deactivate
        bool pickerReady = false;
        picker.Shown += (_, _) =>
        {
            pickerReady = true;
            SetForegroundWindow(picker.Handle);
            picker.Activate();
        };

        // Auto-close if focus leaves (only after picker is fully shown)
        picker.Deactivate += (_, _) =>
        {
            if (!pickerReady) return;
            if (picker.DialogResult == WinForms.DialogResult.None)
            {
                picker.DialogResult = WinForms.DialogResult.Cancel;
                picker.Close();
            }
        };

        picker.ShowDialog();

        if (!string.IsNullOrEmpty(copiedSnippet))
        {
            try
            {
                // Find the index of the snippet to get its SnippetRow for TOTP
                int snippetIdx = Array.IndexOf(_snippets, copiedSnippet);
                var snippetRow = snippetIdx >= 0 && snippetIdx < _snippetRows.Length ? _snippetRows[snippetIdx] : null;
                CopySnippetToClipboard(copiedSnippet, snippetRow);
            }
            catch { }
        }

        if (prevHwnd != IntPtr.Zero)
            SetForegroundWindow(prevHwnd);

        // Quick Enter: auto-paste into the previously focused window.
        // AttachThreadInput (inside SendPasteToWindow) synchronises input queues so
        // credential dialogs and security windows accept the Ctrl+V reliably.
        if (_quickEnterEnabled && !string.IsNullOrEmpty(copiedSnippet) && prevHwnd != IntPtr.Zero)
        {
            await WaitForForegroundAsync(prevHwnd);
            await Task.Delay(60); // small settle margin after focus lands
            SendPasteToWindow(prevHwnd);
        }
    }

    // ── Edit Snippets (existing) ────────────────────────────────────────────

    private void EditSnippets_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_masterPassword))
        {
            System.Windows.MessageBox.Show("Master password is not available.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out var plaintext))
        {
            System.Windows.MessageBox.Show("Unable to decrypt snippets file.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        using var dialog = new WinForms.Form
        {
            ClientSize = new Drawing.Size(900, 600),
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Text = "PinBubble - Edit Snippets",
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black
        };

        // Apply rounded corners and border
        using (var dialogRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, dialog.Width - 1, dialog.Height - 1), 18))
            dialog.Region = new Drawing.Region(dialogRegionPath);
        dialog.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, dialog.Width - 1, dialog.Height - 1), 18);
            using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
            pe.Graphics.DrawPath(pen, path);
        };

        var isDecrypted = false;
        var hasChanges = false;
        var isUpdatingProgrammatically = false;
        
        // Toolbar panel
        var toolbar = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 82,
            BorderStyle = WinForms.BorderStyle.None,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
        };

        // Status indicator - LED Light
        var statusLED = new WinForms.PictureBox
        {
            Left = 20,
            Top = 18,
            Width = 34,
            Height = 34,
            BackColor = Drawing.Color.Transparent
        };
        
        // Function to draw LED with given color
        void DrawLED(Drawing.Color color)
        {
            var ledBitmap = new System.Drawing.Bitmap(34, 34);
            using (var g = System.Drawing.Graphics.FromImage(ledBitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                // Outer dark ring
                using (var brush = new System.Drawing.SolidBrush(Drawing.Color.FromArgb(40, 40, 45)))
                {
                    g.FillEllipse(brush, 0, 0, 34, 34);
                }
                
                // Main LED body
                using (var brush = new System.Drawing.SolidBrush(color))
                {
                    g.FillEllipse(brush, 3, 3, 28, 28);
                }
                
                // Inner glow effect
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddEllipse(6, 6, 22, 22);
                    using (var pgb = new System.Drawing.Drawing2D.PathGradientBrush(path))
                    {
                        pgb.CenterPoint = new System.Drawing.PointF(17, 17);
                        pgb.CenterColor = Drawing.Color.FromArgb(180, 255, 255, 255);
                        pgb.SurroundColors = new[] { Drawing.Color.FromArgb(0, 255, 255, 255) };
                        g.FillEllipse(pgb, 6, 6, 22, 22);
                    }
                }
                
                // Highlight (glossy effect)
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(10, 10, 12, 10),
                    Drawing.Color.FromArgb(200, 255, 255, 255),
                    Drawing.Color.FromArgb(0, 255, 255, 255),
                    45f))
                {
                    g.FillEllipse(brush, 10, 10, 12, 10);
                }
            }
            statusLED.Image = ledBitmap;
        }
        
        // Initialize with green LED
        DrawLED(Drawing.Color.FromArgb(0, 220, 0));
        toolbar.Controls.Add(statusLED);

        // Title label
        var titleLabel = new WinForms.Label
        {
            Left = 66,
            Top = 14,
            Width = 200,
            Height = 28,
            Text = "Manage Snippets",
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36),
            Font = new Drawing.Font("Segoe UI", 12f, Drawing.FontStyle.Bold),
            BackColor = Drawing.Color.Transparent
        };
        toolbar.Controls.Add(titleLabel);

        // Info label
        var infoLabel = new WinForms.Label
        {
            Left = 66,
            Top = 43,
            Width = 300,
            Height = 22,
            Text = "Click value or TOTP column to copy",
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
            Font = new Drawing.Font("Segoe UI", 9f),
            BackColor = Drawing.Color.Transparent
        };
        toolbar.Controls.Add(infoLabel);

        // Button: Decrypt All (toggle) - only visible when Control key is held
        var btnDecryptAll = new WinForms.Button
        {
            Text = "Show All",
            Left = 450,
            Top = 21,
            Width = 100,
            Height = 40,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(55, 55, 60) : Drawing.Color.FromArgb(240, 240, 240),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.FromArgb(40, 40, 40),
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            Visible = false,
            Enabled = false
        };
        btnDecryptAll.FlatAppearance.BorderSize = 0;
        btnDecryptAll.FlatAppearance.MouseOverBackColor = btnDecryptAll.BackColor;
        btnDecryptAll.FlatAppearance.MouseDownBackColor = btnDecryptAll.BackColor;

        // Button: Save - only visible when changes are made
        var btnSave = new WinForms.Button
        {
            Text = "Save",
            Left = 630,
            Top = 21,
            Width = 90,
            Height = 40,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.FromArgb(0, 145, 90),
            ForeColor = Drawing.Color.White,
            Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            DialogResult = WinForms.DialogResult.OK,
            Visible = false,
            TabStop = false
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.FlatAppearance.MouseOverBackColor = btnSave.BackColor;
        btnSave.FlatAppearance.MouseDownBackColor = btnSave.BackColor;

        // Button: Cancel
        var btnCancel = new WinForms.Button
        {
            Text = "Cancel",
            Left = 750,
            Top = 21,
            Width = 90,
            Height = 40,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
            ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            TabStop = false
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.FlatAppearance.MouseOverBackColor = btnCancel.BackColor;
        btnCancel.FlatAppearance.MouseDownBackColor = btnCancel.BackColor;

        toolbar.Controls.Add(btnDecryptAll);
        toolbar.Controls.Add(btnSave);
        toolbar.Controls.Add(btnCancel);

        // Parse snippets from JSON (or legacy comma-delimited format)
        var snippetRows = ParseSnippets(plaintext);

        // Create DataGridView with wrapper panel for border
        var gridFrame = new WinForms.Panel
        {
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            Location = new Drawing.Point(20, 82),
            Size = new Drawing.Size(900 - 40, 600 - 102),
            Anchor = WinForms.AnchorStyles.Top | WinForms.AnchorStyles.Bottom | WinForms.AnchorStyles.Left | WinForms.AnchorStyles.Right
        };
        gridFrame.Padding = new WinForms.Padding(1);

        // Create DataGridView
        var grid = new WinForms.DataGridView
        {
            Dock = WinForms.DockStyle.Fill,
            BackgroundColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            GridColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 65) : Drawing.Color.FromArgb(200, 200, 200),
            BorderStyle = WinForms.BorderStyle.None,
            AllowUserToResizeRows = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            ColumnHeadersHeightSizeMode = WinForms.DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = WinForms.DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            RowHeadersWidth = 25,
            Font = new Drawing.Font("Consolas", 10)
        };

        // Style the grid
        grid.ColumnHeadersDefaultCellStyle.BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 48) : Drawing.Color.FromArgb(240, 240, 240);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.FromArgb(40, 40, 40);
        grid.EnableHeadersVisualStyles = false;
        grid.DefaultCellStyle.BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White;
        grid.DefaultCellStyle.ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black;
        grid.DefaultCellStyle.SelectionBackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 100, 150) : Drawing.Color.LightBlue;
        grid.DefaultCellStyle.SelectionForeColor = Drawing.Color.White;
        grid.RowHeadersDefaultCellStyle.BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 48) : Drawing.Color.FromArgb(240, 240, 240);

        grid.UserDeletingRow += (_, ev) =>
        {
            if (ev.Row is not null && !ev.Row.IsNewRow && ev.Row.Tag is SnippetRow)
            {
                hasChanges = true;
                btnSave.Visible = true;
            }
        };

        // Add columns
        var colAge = new WinForms.DataGridViewTextBoxColumn
        {
            HeaderText = "",
            Name = "Age",
            FillWeight = 5,
            Width = 50,
            ReadOnly = true,
            SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
        };
        
        var colLabel = new WinForms.DataGridViewTextBoxColumn
        {
            HeaderText = "Label",
            Name = "Label",
            FillWeight = 30,
            SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
        };
        
        var colValue = new WinForms.DataGridViewTextBoxColumn
        {
            HeaderText = "Value",
            Name = "Value",
            FillWeight = 60,
            SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
        };
        
        var colToggle = new WinForms.DataGridViewTextBoxColumn
        {
            HeaderText = "",
            Name = "Toggle",
            FillWeight = 10,
            Width = 50,
            ReadOnly = true,
            SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
        };
        
        var colTotp = new WinForms.DataGridViewTextBoxColumn
        {
            HeaderText = "🔐",
            Name = "Totp",
            FillWeight = 15,
            Width = 80,
            ReadOnly = true,
            SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
        };

        grid.Columns.Add(colAge);
        grid.Columns.Add(colLabel);
        grid.Columns.Add(colValue);
        grid.Columns.Add(colToggle);
        grid.Columns.Add(colTotp);
        
        // Increase header height for cleaner look
        grid.ColumnHeadersHeight = 35;

        // Populate grid - Age column will be empty, clock icon shown via painting
        foreach (var row in snippetRows)
        {
            grid.Rows.Add("", row.Label, row.Value, "", "");
            grid.Rows[grid.Rows.Count - 2].Tag = row; // Store the SnippetRow in Tag
        }

        // Function to update status circle color
        void UpdateStatusColor()
        {
            bool hasDecrypted = false;
            int totalRows = 0;
            int encryptedRows = 0;
            
            foreach (WinForms.DataGridViewRow gridRow in grid.Rows)
            {
                if (gridRow.IsNewRow) continue;
                if (gridRow.Tag is SnippetRow snippetRow)
                {
                    totalRows++;
                    if (!snippetRow.IsEncrypted)
                    {
                        hasDecrypted = true;
                    }
                    else
                    {
                        encryptedRows++;
                    }
                }
            }
            
            // Update LED color
            DrawLED(hasDecrypted ? Drawing.Color.FromArgb(220, 0, 0) : Drawing.Color.FromArgb(0, 220, 0));
            
            // Auto-hide "Hide All" button if all entries are manually encrypted
            bool allEncrypted = (totalRows > 0 && encryptedRows == totalRows);
            if (allEncrypted && btnDecryptAll.Text == "Hide All")
            {
                isDecrypted = false;
                btnDecryptAll.Text = "Show All";
                btnDecryptAll.Visible = false;
                btnDecryptAll.Enabled = false;
            }
        }

        // Track hovered cell for eye icon display
        int hoveredRowIndex = -1;
        int hoveredColumnIndex = -1;
        
        // Enable tooltips on the grid
        grid.ShowCellToolTips = true;
        
        // Custom paint for age column (clock icon) and eye icon column
        grid.CellPainting += (s, ev) =>
        {
            // Age column (column 0) - show clock icon with color based on days until expiry
            if (ev.ColumnIndex == 0 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                ev.Paint(ev.CellBounds, WinForms.DataGridViewPaintParts.All);
                
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow && ev.Graphics != null)
                {
                    var daysUntilExpiry = (int)(snippetRow.ExpiryDate - DateTime.Now).TotalDays;
                    bool shouldShowClock = false;
                    Drawing.Color clockColor = Drawing.Color.FromArgb(100, 150, 200); // Default blue
                    
                    if (daysUntilExpiry < 7)
                    {
                        // Less than 7 days - Red and always visible
                        shouldShowClock = true;
                        clockColor = Drawing.Color.FromArgb(220, 0, 0);
                    }
                    else if (daysUntilExpiry <= 14)
                    {
                        // 7-14 days - Yellow and always visible
                        shouldShowClock = true;
                        clockColor = Drawing.Color.FromArgb(220, 180, 0);
                    }
                    else
                    {
                        // 15 days or more - Blue and only on hover
                        shouldShowClock = (hoveredRowIndex == ev.RowIndex && hoveredColumnIndex == ev.ColumnIndex);
                        clockColor = Drawing.Color.FromArgb(100, 150, 200);
                    }
                    
                    if (shouldShowClock)
                    {
                        var clockFont = new Drawing.Font("Segoe UI Emoji", 14f);
                        var clockBrush = new System.Drawing.SolidBrush(clockColor);
                        var sf = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center
                        };
                        ev.Graphics.DrawString("⏰", clockFont, clockBrush, ev.CellBounds, sf);
                        clockBrush.Dispose();
                        clockFont.Dispose();
                    }
                }
                
                ev.Handled = true;
            }
            // Toggle column (column 3) - eye icon
            else if (ev.ColumnIndex == 3 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                ev.Paint(ev.CellBounds, WinForms.DataGridViewPaintParts.Background | WinForms.DataGridViewPaintParts.Border);
                
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    bool shouldShowEye = false;
                    var eyeColor = Drawing.Color.Gray;
                    
                    if (!snippetRow.IsEncrypted)
                    {
                        // Decrypted: show persistent red eye
                        shouldShowEye = true;
                        eyeColor = Drawing.Color.FromArgb(220, 0, 0);
                    }
                    else if (hoveredRowIndex == ev.RowIndex && hoveredColumnIndex == ev.ColumnIndex)
                    {
                        // Encrypted but hovering: show gray eye
                        shouldShowEye = true;
                        eyeColor = Drawing.Color.FromArgb(120, 120, 125);
                    }
                    
                    if (shouldShowEye && ev.Graphics != null)
                    {
                        var eyeFont = new Drawing.Font("Segoe UI Emoji", 12f);
                        var eyeBrush = new System.Drawing.SolidBrush(eyeColor);
                        var sf = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center
                        };
                        ev.Graphics.DrawString("👁", eyeFont, eyeBrush, ev.CellBounds, sf);
                        eyeBrush.Dispose();
                        eyeFont.Dispose();
                    }
                }
                
                ev.Handled = true;
            }
            // TOTP column (column 4) - display TOTP code if secret is configured
            else if (ev.ColumnIndex == 4 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                ev.Paint(ev.CellBounds, WinForms.DataGridViewPaintParts.Background | WinForms.DataGridViewPaintParts.Border);
                
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow && ev.Graphics != null)
                {
                    // Show TOTP code if secret is configured
                    if (!string.IsNullOrWhiteSpace(snippetRow.TotpSecret))
                    {
                        var totpInfo = snippetRow.TotpWithExpiry;
                        if (totpInfo.HasValue)
                        {
                            var code = totpInfo.Value.Code;
                            var remaining = totpInfo.Value.RemainingSeconds;
                            
                            // Format: "123456 (15s)"
                            var displayText = $"{code}\n({remaining}s)";
                            var totpFont = new Drawing.Font("Courier New", 9f, Drawing.FontStyle.Bold);
                            var totpBrush = new System.Drawing.SolidBrush(Drawing.Color.FromArgb(0, 180, 0));
                            var sf = new System.Drawing.StringFormat
                            {
                                Alignment = System.Drawing.StringAlignment.Center,
                                LineAlignment = System.Drawing.StringAlignment.Center
                            };
                            ev.Graphics.DrawString(displayText, totpFont, totpBrush, ev.CellBounds, sf);
                            totpBrush.Dispose();
                            totpFont.Dispose();
                        }
                    }
                    else if (hoveredRowIndex == ev.RowIndex && hoveredColumnIndex == ev.ColumnIndex)
                    {
                        // Show "add" icon on hover if no TOTP secret
                        var addFont = new Drawing.Font("Segoe UI Emoji", 14f);
                        var addBrush = new System.Drawing.SolidBrush(Drawing.Color.FromArgb(100, 150, 200));
                        var sf = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center
                        };
                        ev.Graphics.DrawString("➕", addFont, addBrush, ev.CellBounds, sf);
                        addBrush.Dispose();
                        addFont.Dispose();
                    }
                }
                
                ev.Handled = true;
            }
        };
        
        // Track mouse movement for hover effect and tooltip
        grid.CellMouseEnter += (s, ev) =>
        {
            // Set tooltip directly on cells for age column (0) only
            if (ev.ColumnIndex == 0 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count)
            {
                var row = grid.Rows[ev.RowIndex];
                if (!row.IsNewRow && row.Tag is SnippetRow snippetRow)
                {
                    var daysUntilExpiry = (int)(snippetRow.ExpiryDate - DateTime.Now).TotalDays;
                    var expiryText = daysUntilExpiry >= 0 ? $"Expires in {daysUntilExpiry} days" : $"Expired {Math.Abs(daysUntilExpiry)} days ago";
                    var tooltipText = $"Entry: {snippetRow.Label}\n\nFirst Added: {snippetRow.Created:yyyy-MM-dd HH:mm}\nLast Modified: {snippetRow.Modified:yyyy-MM-dd HH:mm}\nExpiry: {snippetRow.ExpiryDate:yyyy-MM-dd}\n\n{expiryText}";
                    
                    // Set tooltip text directly on the cell
                    row.Cells[ev.ColumnIndex].ToolTipText = tooltipText;
                }
            }
            // Set tooltip on Value column (2) for password generator hint
            if (ev.ColumnIndex == 2 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count)
            {
                var row = grid.Rows[ev.RowIndex];
                if (!row.IsNewRow && row.Tag is SnippetRow)
                {
                    row.Cells[2].ToolTipText = "Right-click to generate a custom random string";
                }
            }

            // Set tooltip on TOTP column (4) for right-click hint
            else if (ev.ColumnIndex == 4 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count)
            {
                var row = grid.Rows[ev.RowIndex];
                if (!row.IsNewRow && row.Tag is SnippetRow snippetRow && !string.IsNullOrWhiteSpace(snippetRow.TotpSecret))
                {
                    row.Cells[4].ToolTipText = "Right-click to edit TOTP secret";
                }
            }
            
            if ((ev.ColumnIndex == 0 || ev.ColumnIndex == 3 || ev.ColumnIndex == 4) && ev.RowIndex >= 0)
            {
                hoveredRowIndex = ev.RowIndex;
                hoveredColumnIndex = ev.ColumnIndex;
                grid.InvalidateCell(ev.ColumnIndex, ev.RowIndex);
            }
        };
        
        grid.CellMouseLeave += (s, ev) =>
        {
            if ((ev.ColumnIndex == 0 || ev.ColumnIndex == 3 || ev.ColumnIndex == 4) && ev.RowIndex >= 0)
            {
                hoveredRowIndex = -1;
                hoveredColumnIndex = -1;
                grid.InvalidateCell(ev.ColumnIndex, ev.RowIndex);
            }
        };

        // Handle cell value changes
        grid.CellValueChanged += (s, ev) =>
        {
            if (isUpdatingProgrammatically || ev.RowIndex < 0 || ev.RowIndex >= grid.Rows.Count)
                return;

            var row = grid.Rows[ev.RowIndex];
            if (row.Tag is not SnippetRow snippetRow)
            {
                // New row - create SnippetRow with 30 days default expiry
                snippetRow = new SnippetRow { IsEncrypted = true, Created = DateTime.Now, Modified = DateTime.Now, ExpiryDate = DateTime.Now.AddDays(_defaultExpiryDays) };
                row.Tag = snippetRow;
            }

            hasChanges = true;
            btnSave.Visible = true;
            snippetRow.Modified = DateTime.Now; // Update modified timestamp

            // Update the SnippetRow based on which column changed
            if (ev.ColumnIndex == 1) // Label column (shifted from 0 to 1)
            {
                snippetRow.Label = row.Cells[1].Value?.ToString() ?? string.Empty;
            }
            else if (ev.ColumnIndex == 2) // Value column (shifted from 1 to 2)
            {
                var newValue = row.Cells[2].Value?.ToString() ?? string.Empty;
                
                // If encrypted and user types non-dots, store actual value and re-encrypt display
                if (snippetRow.IsEncrypted && newValue != "••••••••" && !newValue.All(c => c == '•'))
                {
                    snippetRow.ActualValue = newValue;
                    snippetRow.ExpiryDate = DateTime.Now.AddDays(_defaultExpiryDays);
                    
                    // Re-encrypt display after a brief delay
                    var timer = new System.Windows.Forms.Timer { Interval = 150 };
                    timer.Tick += (ts, te) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        if (!isUpdatingProgrammatically && row.Index < grid.Rows.Count)
                        {
                            isUpdatingProgrammatically = true;
                            row.Cells[2].Value = "••••••••";
                            isUpdatingProgrammatically = false;
                        }
                    };
                    timer.Start();
                }
                else if (!snippetRow.IsEncrypted)
                {
                    // Decrypted - just update actual value
                    snippetRow.ActualValue = newValue;
                    snippetRow.ExpiryDate = DateTime.Now.AddDays(_defaultExpiryDays);
                }
            }
        };

        // Handle end edit to commit changes
        grid.CellEndEdit += (s, ev) =>
        {
            if (ev.RowIndex < 0 || ev.RowIndex >= grid.Rows.Count)
                return;
                
            hasChanges = true;
            btnSave.Visible = true;
        };

        // Handle clicks on clock icon column and eye icon column
        grid.CellClick += (s, ev) =>
        {
            // Clock icon column - set expiry date
            if (ev.ColumnIndex == 0 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    var daysUntilExpiry = (int)(snippetRow.ExpiryDate - DateTime.Now).TotalDays;
                    var expiryText = daysUntilExpiry >= 0 ? $"Expires in {daysUntilExpiry} days" : $"Expired {Math.Abs(daysUntilExpiry)} days ago";
                    
                    const int expiryWidth = 430;
                    const int expiryHeight = 350;
                    const int expiryRadius = 18;
                    var expiryBack = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251);
                    var expiryFieldBack = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White;
                    var expiryTextColor = _isDarkTheme ? Drawing.Color.FromArgb(232, 232, 238) : Drawing.Color.FromArgb(30, 32, 36);
                    var expiryDimColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108);
                    var expiryAccent = _isDarkTheme ? Drawing.Color.FromArgb(110, 195, 60) : Drawing.Color.FromArgb(55, 135, 45);
                    var expiryStatusColor = daysUntilExpiry < 7 ? Drawing.Color.FromArgb(255, 100, 100) :
                                           (daysUntilExpiry <= 14 ? Drawing.Color.FromArgb(255, 200, 100) :
                                           Drawing.Color.FromArgb(100, 200, 100));

                    using var inputForm = new WinForms.Form
                    {
                        FormBorderStyle = WinForms.FormBorderStyle.None,
                        ClientSize = new Drawing.Size(expiryWidth, expiryHeight),
                        Text = "Expiry Date",
                        StartPosition = WinForms.FormStartPosition.CenterParent,
                        ShowInTaskbar = false,
                        TopMost = true,
                        KeyPreview = true,
                        BackColor = expiryBack
                    };
                    using (var expiryFormPath = RoundedRectPath(new Drawing.Rectangle(0, 0, expiryWidth - 1, expiryHeight - 1), expiryRadius))
                        inputForm.Region = new Drawing.Region(expiryFormPath);

                    var expiryCard = new WinForms.Panel { Dock = WinForms.DockStyle.Fill, BackColor = expiryBack };
                    expiryCard.Paint += (_, pe) =>
                    {
                        pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, expiryCard.Width - 1, expiryCard.Height - 1), expiryRadius);
                        using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
                        pe.Graphics.DrawPath(pen, path);
                    };

                    var expiryIcon = new WinForms.Label
                    {
                        Left = 28, Top = 22, Width = 34, Height = 34,
                        Text = "⏰",
                        Font = new Drawing.Font("Segoe UI Emoji", 19f),
                        ForeColor = expiryAccent,
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleCenter
                    };

                    var entryLabel = new WinForms.Label
                    {
                        Left = 68, Top = 24, Width = 150, Height = 28,
                        Text = "Expiry date",
                        ForeColor = expiryTextColor,
                        Font = new Drawing.Font("Segoe UI", 12f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var entryHeaderText = $"|  {snippetRow.Label}";
                    var entryHeaderFontSize = 12f;
                    while (entryHeaderFontSize > 8f)
                    {
                        using var measureFont = new Drawing.Font("Segoe UI", entryHeaderFontSize, Drawing.FontStyle.Bold);
                        if (WinForms.TextRenderer.MeasureText(entryHeaderText, measureFont).Width <= 205)
                            break;
                        entryHeaderFontSize -= 0.5f;
                    }
                    var entryNameLabel = new WinForms.Label
                    {
                        Left = 210, Top = 24, Width = 205, Height = 28,
                        Text = entryHeaderText,
                        ForeColor = expiryDimColor,
                        Font = new Drawing.Font("Segoe UI", entryHeaderFontSize, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleLeft
                    };
                    var label1 = new WinForms.Label
                    {
                        Left = 34, Top = 102, Width = 145, Height = 22,
                        Text = "First added",
                        ForeColor = expiryDimColor,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var label2 = new WinForms.Label
                    {
                        Left = 34, Top = 130, Width = 145, Height = 22,
                        Text = "Last modified",
                        ForeColor = expiryDimColor,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var label3 = new WinForms.Label
                    {
                        Left = 34, Top = 158, Width = 145, Height = 22,
                        Text = "Expiry date",
                        ForeColor = expiryDimColor,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var label1Value = new WinForms.Label
                    {
                        Left = 184, Top = 102, Width = 220, Height = 22,
                        Text = snippetRow.Created.ToString("yyyy-MM-dd HH:mm"),
                        ForeColor = expiryTextColor,
                        Font = new Drawing.Font("Consolas", 9f),
                        BackColor = Drawing.Color.Transparent
                    };
                    var label2Value = new WinForms.Label
                    {
                        Left = 184, Top = 130, Width = 220, Height = 22,
                        Text = snippetRow.Modified.ToString("yyyy-MM-dd HH:mm"),
                        ForeColor = expiryTextColor,
                        Font = new Drawing.Font("Consolas", 9f),
                        BackColor = Drawing.Color.Transparent
                    };
                    var label3Value = new WinForms.Label
                    {
                        Left = 184, Top = 158, Width = 220, Height = 22,
                        Text = snippetRow.ExpiryDate.ToString("yyyy-MM-dd"),
                        ForeColor = expiryStatusColor,
                        Font = new Drawing.Font("Consolas", 9f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var separatorLabel = new WinForms.Label
                    {
                        Left = 34, Top = 190, Width = 370, Height = 22,
                        Text = $"{expiryText}  |  Set new date",
                        ForeColor = expiryAccent,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        BackColor = Drawing.Color.Transparent
                    };
                    var datePicker = new WinForms.DateTimePicker
                    {
                        Left = 34, Top = 218, Width = 370, Height = 32,
                        Font = new Drawing.Font("Segoe UI", 10f),
                        Format = WinForms.DateTimePickerFormat.Short,
                        Value = snippetRow.ExpiryDate < DateTime.Now ? DateTime.Now.AddDays(_defaultExpiryDays) : snippetRow.ExpiryDate,
                        BackColor = expiryFieldBack,
                        ForeColor = expiryTextColor,
                        CalendarForeColor = expiryTextColor,
                        CalendarMonthBackground = expiryFieldBack
                    };
                    var btnOk = new WinForms.Button
                    {
                        Text = "Update", Left = 105, Top = 292, Width = 104, Height = 40,
                        DialogResult = WinForms.DialogResult.OK,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.FromArgb(0, 145, 90),
                        ForeColor = Drawing.Color.White,
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand,
                        UseVisualStyleBackColor = false,
                        TabStop = false
                    };
                    btnOk.FlatAppearance.BorderSize = 0;
                    btnOk.FlatAppearance.MouseOverBackColor = btnOk.BackColor;
                    btnOk.FlatAppearance.MouseDownBackColor = btnOk.BackColor;
                    var btnCancel = new WinForms.Button
                    {
                        Text = "Cancel", Left = 221, Top = 292, Width = 104, Height = 40,
                        DialogResult = WinForms.DialogResult.Cancel,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
                        ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand,
                        UseVisualStyleBackColor = false,
                        TabStop = false
                    };
                    btnCancel.FlatAppearance.BorderSize = 0;
                    btnCancel.FlatAppearance.MouseOverBackColor = btnCancel.BackColor;
                    btnCancel.FlatAppearance.MouseDownBackColor = btnCancel.BackColor;

                    expiryCard.Controls.AddRange(new WinForms.Control[]
                    {
                        expiryIcon, entryLabel, entryNameLabel, label1, label1Value, label2, label2Value,
                        label3, label3Value, separatorLabel, datePicker, btnOk, btnCancel
                    });
                    inputForm.Controls.Add(expiryCard);
                    inputForm.AcceptButton = btnOk;
                    inputForm.CancelButton = btnCancel;
                    
                    if (inputForm.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        snippetRow.ExpiryDate = datePicker.Value.Date.AddHours(23).AddMinutes(59).AddSeconds(59); // Set to end of day
                        hasChanges = true;
                        btnSave.Visible = true;
                        
                        // Refresh the clock icon to update color if needed
                        grid.InvalidateCell(0, ev.RowIndex);
                    }
                }
            }
            // Eye icon column - toggle encryption
            else if (ev.ColumnIndex == 3 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1) // Toggle column (now column 3), not new row
            {
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    isUpdatingProgrammatically = true;
                    snippetRow.IsEncrypted = !snippetRow.IsEncrypted;
                    
                    if (snippetRow.IsEncrypted)
                    {
                        // Switching to encrypted - show dots
                        row.Cells[2].Value = "••••••••";
                    }
                    else
                    {
                        // Switching to decrypted - show actual value
                        row.Cells[2].Value = snippetRow.ActualValue;
                    }
                    
                    isUpdatingProgrammatically = false;
                    UpdateStatusColor();
                    // Refresh the eye icon display
                    grid.InvalidateCell(3, ev.RowIndex);
                }
            }
            // Value column (column 2) - copy value with blink highlight
            else if (ev.ColumnIndex == 2 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    var value = snippetRow.ActualValue;
                    if (string.IsNullOrWhiteSpace(value)) return;

                    if (!TrySetClipboardWithoutHistory(value))
                        System.Windows.Clipboard.SetText(value);

                    // Clear any row selection to prevent whole row highlighting
                    grid.ClearSelection();

                    // Blink effect: briefly highlight the value cell with background color
                    var originalBackColor = row.Cells[2].Style.BackColor;
                    var highlightColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 120, 80) : Drawing.Color.FromArgb(180, 220, 180);
                    row.Cells[2].Style.BackColor = highlightColor;

                    if (btnDecryptAll.Text == "Show All")
                    {
                        btnDecryptAll.Visible = false;
                        btnDecryptAll.Enabled = false;
                    }

                    var blinkTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                    blinkTimer.Tick += (_, _) =>
                    {
                        blinkTimer.Stop();
                        blinkTimer.Dispose();
                        row.Cells[2].Style.BackColor = originalBackColor;
                    };
                    blinkTimer.Start();
                }
            }
            // TOTP column - copy TOTP or manage TOTP secret with blink highlight
            else if (ev.ColumnIndex == 4 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow && !string.IsNullOrWhiteSpace(snippetRow.TotpSecret))
                {
                    bool ctrlHeld = (WinForms.Control.ModifierKeys & WinForms.Keys.Control) != 0;
                    var totp = snippetRow.CurrentTotp;
                    if (string.IsNullOrWhiteSpace(totp)) return;

                    var highlightColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 120, 80) : Drawing.Color.FromArgb(180, 220, 180);
                    var blinkDuration = 1000;

                    // Clear any row selection to prevent whole row highlighting
                    grid.ClearSelection();

                    if (ctrlHeld)
                    {
                        var textToCopy = $"{snippetRow.ActualValue}{totp}";
                        if (!TrySetClipboardWithoutHistory(textToCopy))
                            System.Windows.Clipboard.SetText(textToCopy);

                        // Blink effect: highlight only value (2) and TOTP (4) cells, NOT label (1)
                        var originalBackColor2 = row.Cells[2].Style.BackColor;
                        var originalBackColor4 = row.Cells[4].Style.BackColor;
                        row.Cells[2].Style.BackColor = highlightColor;
                        row.Cells[4].Style.BackColor = highlightColor;

                        if (btnDecryptAll.Text == "Show All")
                        {
                            btnDecryptAll.Visible = false;
                            btnDecryptAll.Enabled = false;
                        }

                        var blinkTimer = new System.Windows.Forms.Timer { Interval = blinkDuration };
                        blinkTimer.Tick += (_, _) =>
                        {
                            blinkTimer.Stop();
                            blinkTimer.Dispose();
                            row.Cells[2].Style.BackColor = originalBackColor2;
                            row.Cells[4].Style.BackColor = originalBackColor4;
                        };
                        blinkTimer.Start();
                    }
                    else
                    {
                        if (!TrySetClipboardWithoutHistory(totp))
                            System.Windows.Clipboard.SetText(totp);

                        // Blink effect: briefly highlight only the TOTP cell
                        var originalBackColor = row.Cells[4].Style.BackColor;
                        row.Cells[4].Style.BackColor = highlightColor;

                        if (btnDecryptAll.Text == "Show All")
                        {
                            btnDecryptAll.Visible = false;
                            btnDecryptAll.Enabled = false;
                        }

                        var blinkTimer = new System.Windows.Forms.Timer { Interval = blinkDuration };
                        blinkTimer.Tick += (_, _) =>
                        {
                            blinkTimer.Stop();
                            blinkTimer.Dispose();
                            row.Cells[4].Style.BackColor = originalBackColor;
                        };
                        blinkTimer.Start();
                    }
                }
            }
        };

        // Helper function to generate a secure password
        string GenerateSecurePassword(int length = 16)
        {
            const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lowercase = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string specialChars = "!@#$%^&*-_+="; // RHEL-safe characters
            
            var rng = new System.Random();
            var password = new System.Text.StringBuilder();
            
            // Ensure at least one of each required character type
            password.Append(uppercase[rng.Next(uppercase.Length)]);
            password.Append(lowercase[rng.Next(lowercase.Length)]);
            password.Append(digits[rng.Next(digits.Length)]);
            password.Append(specialChars[rng.Next(specialChars.Length)]);
            
            // Fill the rest randomly
            var allChars = uppercase + lowercase + digits + specialChars;
            while (password.Length < length)
            {
                var nextChar = allChars[rng.Next(allChars.Length)];
                
                // Avoid repeating characters back-to-back
                if (password.Length > 0 && password[password.Length - 1] != nextChar)
                {
                    password.Append(nextChar);
                }
            }
            
            // Shuffle to mix required chars with random ones
            var shuffled = password.ToString().ToCharArray();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int randomIndex = rng.Next(i + 1);
                var temp = shuffled[i];
                shuffled[i] = shuffled[randomIndex];
                shuffled[randomIndex] = temp;
            }
            
            return new string(shuffled);
        }

        // Handle right-click on TOTP column to edit/manage TOTP secret
        grid.CellMouseDown += (s, ev) =>
        {
            if (ev.Button == WinForms.MouseButtons.Right && ev.ColumnIndex == 4 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    // Open TOTP management dialog — borderless glass card, styled like the About page.
                    var dimColor = _isDarkTheme ? Drawing.Color.FromArgb(150, 150, 156) : Drawing.Color.FromArgb(120, 120, 120);
                    var accentColor = _isDarkTheme ? Drawing.Color.FromArgb(110, 195, 60) : Drawing.Color.FromArgb(70, 140, 35);
                    var textColor = _isDarkTheme ? Drawing.Color.FromArgb(210, 210, 216) : Drawing.Color.FromArgb(40, 40, 40);
                    var separatorColor = _isDarkTheme ? Drawing.Color.FromArgb(70, 70, 78) : Drawing.Color.FromArgb(225, 225, 230);
                    var cardTopColor = _isDarkTheme ? Drawing.Color.FromArgb(48, 48, 55) : Drawing.Color.FromArgb(255, 255, 255);
                    var cardBottomColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(246, 247, 249);
                    var cardBorderColor = _isDarkTheme ? Drawing.Color.FromArgb(255, 255, 255) : Drawing.Color.FromArgb(0, 0, 0);
                    var fieldBg = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 52) : Drawing.Color.FromArgb(244, 245, 247);

                    const int cardWidth = 440;
                    const int cornerRadius = 18;
                    const int pad = 26;
                    const int collapsedHeight = 368;
                    const int expandedHeight = 624;
                    var contentWidth = cardWidth - pad * 2;

                    using var totpForm = new WinForms.Form
                    {
                        FormBorderStyle = WinForms.FormBorderStyle.None,
                        StartPosition = WinForms.FormStartPosition.CenterParent,
                        ClientSize = new Drawing.Size(cardWidth, collapsedHeight),
                        Text = "Manage TOTP",
                        ShowInTaskbar = false,
                        TopMost = true,
                        AutoScaleMode = WinForms.AutoScaleMode.None,
                        BackColor = cardBottomColor,
                        KeyPreview = true
                    };
                    using (var formRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, totpForm.Width - 1, totpForm.Height - 1), cornerRadius))
                    {
                        totpForm.Region = new Drawing.Region(formRegionPath);
                    }

                    var card = new WinForms.Panel
                    {
                        Left = 0,
                        Top = 0,
                        Width = cardWidth,
                        Height = collapsedHeight,
                        BackColor = cardBottomColor
                    };
                    card.Paint += (s3, e3) =>
                    {
                        var rect = new Drawing.Rectangle(0, 0, card.Width - 1, card.Height - 1);
                        e3.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var path = RoundedRectPath(rect, cornerRadius);
                        using var fill = new Drawing.Drawing2D.LinearGradientBrush(rect, cardTopColor, cardBottomColor, 90f);
                        e3.Graphics.FillPath(fill, path);
                        using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(_isDarkTheme ? 22 : 18, cardBorderColor), 1f);
                        e3.Graphics.DrawPath(borderPen, path);
                        using var highlightPen = new Drawing.Pen(Drawing.Color.FromArgb(_isDarkTheme ? 14 : 130, Drawing.Color.White), 1f);
                        e3.Graphics.DrawLine(highlightPen, 18, 1, card.Width - 18, 1);
                    };
                    using (var cardRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, card.Width - 1, card.Height - 1), cornerRadius))
                    {
                        card.Region = new Drawing.Region(cardRegionPath);
                    }

                    // Fancy circular close button, top-right corner (matches the About page).
                    const int closeSize = 26;
                    var closeButtonNormalBack = _isDarkTheme ? Drawing.Color.FromArgb(58, 58, 66) : Drawing.Color.FromArgb(228, 229, 233);
                    var closeButton = new WinForms.Button
                    {
                        Left = cardWidth - closeSize - 14,
                        Top = 14,
                        Width = closeSize,
                        Height = closeSize,
                        Text = "✕",
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        ForeColor = dimColor,
                        BackColor = closeButtonNormalBack,
                        Cursor = WinForms.Cursors.Hand,
                        TabStop = false,
                        UseVisualStyleBackColor = false
                    };
                    closeButton.FlatAppearance.BorderSize = 0;
                    using (var closeRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, closeSize - 1, closeSize - 1), closeSize / 2))
                    {
                        closeButton.Region = new Drawing.Region(closeRegionPath);
                    }
                    closeButton.MouseEnter += (_, _) =>
                    {
                        closeButton.BackColor = Drawing.Color.FromArgb(232, 17, 35);
                        closeButton.ForeColor = Drawing.Color.White;
                    };
                    closeButton.MouseLeave += (_, _) =>
                    {
                        closeButton.BackColor = closeButtonNormalBack;
                        closeButton.ForeColor = dimColor;
                    };

                    // Header: key icon + title, built as separate emoji/text labels so the glyph renders correctly.
                    var headerFont = new Drawing.Font("Segoe UI Emoji", 15f);
                    var titleFont = new Drawing.Font("Segoe UI", 15f, Drawing.FontStyle.Bold);
                    const string headerEmoji = "🔐";
                    const string headerText = "Manage TOTP";
                    var headerEmojiWidth = WinForms.TextRenderer.MeasureText(headerEmoji, headerFont).Width + 4;
                    var headerTextWidth = WinForms.TextRenderer.MeasureText(headerText, titleFont).Width + 4;
                    var headerRowWidth = headerEmojiWidth + headerTextWidth;

                    var headerRow = new WinForms.Panel
                    {
                        Left = pad + (contentWidth - headerRowWidth) / 2,
                        Top = 24,
                        Width = headerRowWidth,
                        Height = 30,
                        BackColor = Drawing.Color.Transparent
                    };
                    var headerEmojiLbl = new WinForms.Label
                    {
                        Left = 0,
                        Top = 0,
                        Width = headerEmojiWidth,
                        Height = 30,
                        Text = headerEmoji,
                        Font = headerFont,
                        ForeColor = accentColor,
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleCenter
                    };
                    var headerTextLbl = new WinForms.Label
                    {
                        Left = headerEmojiWidth,
                        Top = 0,
                        Width = headerTextWidth,
                        Height = 30,
                        Text = headerText,
                        Font = titleFont,
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(230, 230, 235) : Drawing.Color.FromArgb(25, 25, 25),
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleLeft
                    };
                    headerRow.Controls.AddRange(new WinForms.Control[] { headerEmojiLbl, headerTextLbl });

                    var labelEntry = new WinForms.Label
                    {
                        Left = pad,
                        Top = 64,
                        Width = contentWidth,
                        Height = 22,
                        Text = $"Entry: {snippetRow.Label}",
                        ForeColor = dimColor,
                        Font = new Drawing.Font("Segoe UI", 9.5f),
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleCenter
                    };

                    var separator1 = new WinForms.Panel { Left = pad, Top = 92, Width = contentWidth, Height = 1, BackColor = separatorColor };

                    var labelSecret = new WinForms.Label
                    {
                        Left = pad,
                        Top = 106,
                        Width = contentWidth,
                        Height = 18,
                        Text = "SECRET (BASE32)",
                        Font = new Drawing.Font("Segoe UI", 8.5f, Drawing.FontStyle.Bold),
                        ForeColor = accentColor,
                        BackColor = Drawing.Color.Transparent
                    };

                    var textSecret = new WinForms.TextBox
                    {
                        Left = pad,
                        Top = 126,
                        Width = contentWidth,
                        Height = 56,
                        Font = new Drawing.Font("Consolas", 10f),
                        BackColor = fieldBg,
                        ForeColor = textColor,
                        Multiline = true,
                        Text = snippetRow.TotpSecret ?? string.Empty,
                        BorderStyle = WinForms.BorderStyle.FixedSingle
                    };

                    var labelInfo = new WinForms.Label
                    {
                        Left = pad,
                        Top = 190,
                        Width = contentWidth,
                        Height = 42,
                        Text = "To get the secret: ipa otptoken-show <USERNAME> (look for 'Key')",
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(120, 190, 230) : Drawing.Color.FromArgb(0, 100, 150),
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Italic),
                        AutoSize = false
                    };

                    var separator2 = new WinForms.Panel { Left = pad, Top = 230, Width = contentWidth, Height = 1, BackColor = separatorColor };

                    var qrToggleButton = new WinForms.Button
                    {
                        Text = "▦  Show QR Code",
                        Left = pad + (contentWidth - 200) / 2,
                        Top = 246,
                        Width = 200,
                        Height = 34,
                        FlatStyle = WinForms.FlatStyle.Flat,
                        BackColor = fieldBg,
                        ForeColor = accentColor,
                        Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand
                    };
                    qrToggleButton.FlatAppearance.BorderColor = separatorColor;
                    qrToggleButton.FlatAppearance.BorderSize = 1;
                    using (var qrBtnRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, qrToggleButton.Width - 1, qrToggleButton.Height - 1), 10))
                    {
                        qrToggleButton.Region = new Drawing.Region(qrBtnRegionPath);
                    }

                    var qrPictureBox = new WinForms.PictureBox
                    {
                        Left = pad + (contentWidth - 200) / 2,
                        Top = 298,
                        Width = 200,
                        Height = 200,
                        BackColor = Drawing.Color.White,
                        SizeMode = WinForms.PictureBoxSizeMode.Zoom,
                        Visible = false
                    };

                    var qrCaptionLabel = new WinForms.Label
                    {
                        Left = pad,
                        Top = 508,
                        Width = contentWidth,
                        Height = 32,
                        Text = "Scan with Google Authenticator, Microsoft Authenticator, or a similar app.",
                        Font = new Drawing.Font("Segoe UI", 8f),
                        ForeColor = dimColor,
                        BackColor = Drawing.Color.Transparent,
                        TextAlign = Drawing.ContentAlignment.MiddleCenter,
                        Visible = false
                    };

                    var btnSaveTOTP = new WinForms.Button
                    {
                        Text = "Save",
                        Left = pad + (contentWidth - 264) / 2,
                        Top = collapsedHeight - 24 - 40,
                        Width = 128,
                        Height = 40,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.FromArgb(0, 150, 100),
                        ForeColor = Drawing.Color.White,
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand
                    };
                    btnSaveTOTP.FlatAppearance.BorderSize = 0;
                    using (var saveBtnRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, btnSaveTOTP.Width - 1, btnSaveTOTP.Height - 1), 10))
                    {
                        btnSaveTOTP.Region = new Drawing.Region(saveBtnRegionPath);
                    }

                    var btnCancelTOTP = new WinForms.Button
                    {
                        Text = "Cancel",
                        Left = btnSaveTOTP.Left + 128 + 8,
                        Top = collapsedHeight - 24 - 40,
                        Width = 128,
                        Height = 40,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
                        ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand
                    };
                    btnCancelTOTP.FlatAppearance.BorderSize = 0;
                    using (var cancelBtnRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, btnCancelTOTP.Width - 1, btnCancelTOTP.Height - 1), 10))
                    {
                        btnCancelTOTP.Region = new Drawing.Region(cancelBtnRegionPath);
                    }

                    // Track edits so closing (Escape/X/Cancel) with unsaved text always confirms first.
                    var totpHasChanges = false;
                    textSecret.TextChanged += (_, _) => totpHasChanges = true;

                    var qrExpanded = false;
                    void ApplyQrState(bool expanded)
                    {
                        var newHeight = expanded ? expandedHeight : collapsedHeight;
                        totpForm.ClientSize = new Drawing.Size(cardWidth, newHeight);
                        using (var formRegionPath2 = RoundedRectPath(new Drawing.Rectangle(0, 0, totpForm.Width - 1, totpForm.Height - 1), cornerRadius))
                        {
                            totpForm.Region = new Drawing.Region(formRegionPath2);
                        }
                        card.Height = newHeight;
                        using (var cardRegionPath2 = RoundedRectPath(new Drawing.Rectangle(0, 0, card.Width - 1, card.Height - 1), cornerRadius))
                        {
                            card.Region = new Drawing.Region(cardRegionPath2);
                        }
                        card.Invalidate();

                        qrPictureBox.Visible = expanded;
                        qrCaptionLabel.Visible = expanded;
                        qrToggleButton.Text = expanded ? "▲  Hide QR Code" : "▦  Show QR Code";

                        var buttonsTop = newHeight - 24 - 40;
                        btnSaveTOTP.Top = buttonsTop;
                        btnCancelTOTP.Top = buttonsTop;
                    }

                    qrToggleButton.Click += (_, _) =>
                    {
                        if (!qrExpanded)
                        {
                            var secretForQr = textSecret.Text.Trim();
                            if (string.IsNullOrWhiteSpace(secretForQr) || !TotpSetupHelper.IsValidBase32Secret(secretForQr))
                            {
                                WinForms.MessageBox.Show(
                                    "Enter a valid Base32 TOTP secret first.",
                                    "PinBubble",
                                    WinForms.MessageBoxButtons.OK,
                                    WinForms.MessageBoxIcon.Warning);
                                return;
                            }

                            try
                            {
                                var uri = TotpProvider.GetProvisioningUri(secretForQr, snippetRow.Label, "PinBubble");
                                using var qrData = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
                                using var qrRenderer = new QRCode(qrData);
                                var oldImage = qrPictureBox.Image;
                                qrPictureBox.Image = qrRenderer.GetGraphic(8);
                                oldImage?.Dispose();
                            }
                            catch
                            {
                                WinForms.MessageBox.Show(
                                    "Failed to generate the QR code.",
                                    "PinBubble",
                                    WinForms.MessageBoxButtons.OK,
                                    WinForms.MessageBoxIcon.Error);
                                return;
                            }
                        }

                        qrExpanded = !qrExpanded;
                        ApplyQrState(qrExpanded);
                    };

                    // Closing with unsaved text (Escape, the X button, or Cancel) always confirms first.
                    void CloseTotpDialog()
                    {
                        if (!totpHasChanges)
                        {
                            totpForm.DialogResult = WinForms.DialogResult.Cancel;
                            totpForm.Close();
                            return;
                        }

                        var result = WinForms.MessageBox.Show(
                            "You have an unsaved TOTP secret. Do you want to save before closing?",
                            "PinBubble - Unsaved Changes",
                            WinForms.MessageBoxButtons.YesNoCancel,
                            WinForms.MessageBoxIcon.Question);

                        if (result == WinForms.DialogResult.Cancel)
                            return;

                        totpHasChanges = false;
                        totpForm.DialogResult = result == WinForms.DialogResult.Yes
                            ? WinForms.DialogResult.OK
                            : WinForms.DialogResult.Cancel;
                        totpForm.Close();
                    }

                    closeButton.Click += (_, _) => CloseTotpDialog();
                    btnCancelTOTP.Click += (_, _) => CloseTotpDialog();
                    btnSaveTOTP.Click += (_, _) =>
                    {
                        totpHasChanges = false;
                        totpForm.DialogResult = WinForms.DialogResult.OK;
                        totpForm.Close();
                    };
                    totpForm.KeyDown += (_, ke) =>
                    {
                        if (ke.KeyCode == WinForms.Keys.Escape)
                            CloseTotpDialog();
                    };
                    totpForm.FormClosed += (_, _) => qrPictureBox.Image?.Dispose();

                    // Borderless window needs manual drag support via the card's empty background.
                    bool totpDragging = false;
                    Drawing.Point totpDragCursor = Drawing.Point.Empty;
                    Drawing.Point totpDragForm = Drawing.Point.Empty;
                    card.MouseDown += (_, _) =>
                    {
                        totpDragging = true;
                        totpDragCursor = WinForms.Cursor.Position;
                        totpDragForm = totpForm.Location;
                    };
                    card.MouseMove += (_, _) =>
                    {
                        if (!totpDragging) return;
                        var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(totpDragCursor));
                        totpForm.Location = Drawing.Point.Add(totpDragForm, new Drawing.Size(diff));
                    };
                    card.MouseUp += (_, _) => totpDragging = false;

                    card.Controls.AddRange(new WinForms.Control[]
                    {
                        headerRow,
                        labelEntry,
                        separator1,
                        labelSecret,
                        textSecret,
                        labelInfo,
                        separator2,
                        qrToggleButton,
                        qrPictureBox,
                        qrCaptionLabel,
                        btnSaveTOTP,
                        btnCancelTOTP,
                        closeButton
                    });
                    totpForm.Controls.Add(card);
                    totpForm.AcceptButton = btnSaveTOTP;

                    if (totpForm.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        snippetRow.TotpSecret = textSecret.Text.Trim();
                        if (string.IsNullOrWhiteSpace(snippetRow.TotpSecret))
                        {
                            snippetRow.TotpSecret = null;
                        }
                        hasChanges = true;
                        btnSave.Visible = true;
                        // Refresh TOTP display
                        grid.InvalidateCell(4, ev.RowIndex);
                    }
                }
            }
            else if (ev.Button == WinForms.MouseButtons.Right && ev.ColumnIndex == 2 && ev.RowIndex >= 0 && ev.RowIndex < grid.Rows.Count - 1)
            {
                // Handle right-click on Value column to open password generator
                var row = grid.Rows[ev.RowIndex];
                if (row.Tag is SnippetRow snippetRow)
                {
                    var currentValue = snippetRow.ActualValue;
                    var hasExistingValue = !string.IsNullOrWhiteSpace(currentValue);

                    // Open password generator dialog - larger to accommodate slider
                    using var genDialog = new WinForms.Form
                    {
                        ClientSize = new Drawing.Size(500, hasExistingValue ? 402 : 342),
                        FormBorderStyle = WinForms.FormBorderStyle.None,
                        StartPosition = WinForms.FormStartPosition.CenterParent,
                        Text = "Password Generator",
                        KeyPreview = true,
                        ShowInTaskbar = false,
                        TopMost = true,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
                    };

                    // Apply rounded corners
                    using (var genRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, genDialog.Width - 1, genDialog.Height - 1), 18))
                        genDialog.Region = new Drawing.Region(genRegionPath);
                    genDialog.Paint += (_, pe) =>
                    {
                        pe.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var path = RoundedRectPath(new Drawing.Rectangle(0, 0, genDialog.Width - 1, genDialog.Height - 1), 18);
                        using var pen = new Drawing.Pen(_isDarkTheme ? Drawing.Color.FromArgb(72, 72, 82) : Drawing.Color.FromArgb(210, 214, 220), 1f);
                        pe.Graphics.DrawPath(pen, path);
                    };

                    // Title label (for window dragging)
                    var titleLabel = new WinForms.Label
                    {
                        Text = "Password Generator",
                        Font = new Drawing.Font("Segoe UI", 12f, Drawing.FontStyle.Bold),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.FromArgb(30, 32, 36),
                        AutoSize = false,
                        Left = 20,
                        Top = 20,
                        Width = 460,
                        Height = 24,
                        Cursor = WinForms.Cursors.Hand
                    };

                    var entryLabel = new WinForms.Label
                    {
                        Text = snippetRow.Label,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(190, 190, 198) : Drawing.Color.FromArgb(65, 68, 76),
                        AutoSize = true,
                        Left = 20,
                        Top = 50,
                        Cursor = WinForms.Cursors.Hand
                    };

                    // Last modified label
                    var modifiedLabel = new WinForms.Label
                    {
                        Text = $"Last modified: {snippetRow.Modified:G}",
                        Font = new Drawing.Font("Segoe UI", 8f),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                        AutoSize = true,
                        Left = 20,
                        Top = 72
                    };

                    // Enable window dragging from title label
                    bool genDragging = false;
                    Drawing.Point genDragCursor = Drawing.Point.Empty;
                    Drawing.Point genDragDialog = Drawing.Point.Empty;
                    WinForms.MouseEventHandler dragMouseDown = (_, mouseArgs) =>
                    {
                        if (mouseArgs.Button != WinForms.MouseButtons.Left) return;
                        genDragging = true;
                        genDragCursor = WinForms.Cursor.Position;
                        genDragDialog = genDialog.Location;
                    };
                    WinForms.MouseEventHandler dragMouseMove = (_, _) =>
                    {
                        if (!genDragging) return;
                        var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(genDragCursor));
                        genDialog.Location = Drawing.Point.Add(genDragDialog, new Drawing.Size(diff));
                    };
                    WinForms.MouseEventHandler dragMouseUp = (_, _) => genDragging = false;
                    titleLabel.MouseDown += dragMouseDown;
                    titleLabel.MouseMove += dragMouseMove;
                    titleLabel.MouseUp += dragMouseUp;
                    modifiedLabel.MouseDown += dragMouseDown;
                    modifiedLabel.MouseMove += dragMouseMove;
                    modifiedLabel.MouseUp += dragMouseUp;
                    entryLabel.MouseDown += dragMouseDown;
                    entryLabel.MouseMove += dragMouseMove;
                    entryLabel.MouseUp += dragMouseUp;
                    genDialog.MouseDown += dragMouseDown;
                    genDialog.MouseMove += dragMouseMove;
                    genDialog.MouseUp += dragMouseUp;
                    genDialog.KeyDown += (_, keyArgs) =>
                    {
                        if (keyArgs.KeyCode != WinForms.Keys.Escape) return;
                        keyArgs.Handled = true;
                        genDialog.DialogResult = WinForms.DialogResult.Cancel;
                    };

                    // Password length control (default 16, range 16-32)
                    var selectedLength = 16;

                    // If there's an existing value, show it with eye toggle
                    WinForms.TextBox? existingValueBox = null;
                    WinForms.Button? toggleEyeBtn = null;
                    var showingExistingValue = false;

                    if (hasExistingValue)
                    {
                        var existingLabel = new WinForms.Label
                        {
                            Text = $"Current Value ({currentValue.Length} chars):",
                            Font = new Drawing.Font("Segoe UI", 10f),
                            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                            AutoSize = true,
                            Left = 20,
                            Top = 92
                        };
                        genDialog.Controls.Add(existingLabel);

                        existingValueBox = new WinForms.TextBox
                        {
                            Text = "••••••••",
                            ReadOnly = true,
                            Left = 20,
                            Top = 117,
                            Width = 412,
                            Height = 35,
                            Font = new Drawing.Font("Consolas", 10f),
                            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 52) : Drawing.Color.FromArgb(244, 245, 247),
                            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                            BorderStyle = WinForms.BorderStyle.FixedSingle
                        };
                        genDialog.Controls.Add(existingValueBox);

                        toggleEyeBtn = new WinForms.Button
                        {
                            Text = "👁",
                            Left = 440,
                            Top = 117,
                            Width = 40,
                            Height = 35,
                            Font = new Drawing.Font("Segoe UI Emoji", 12f),
                            FlatStyle = WinForms.FlatStyle.Flat,
                            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
                            ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
                            Cursor = WinForms.Cursors.Hand
                        };
                        toggleEyeBtn.FlatAppearance.BorderSize = 0;
                        toggleEyeBtn.Click += (_, _) =>
                        {
                            showingExistingValue = !showingExistingValue;
                            if (existingValueBox != null)
                                existingValueBox.Text = showingExistingValue ? currentValue : "••••••••";
                        };
                        genDialog.Controls.Add(toggleEyeBtn);
                    }

                    // Generated value label and textbox
                    var genLabel = new WinForms.Label
                    {
                        Text = hasExistingValue ? "Generated Value:" : "New Value:",
                        Font = new Drawing.Font("Segoe UI", 10f),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                        AutoSize = true,
                        Left = 20,
                        Top = hasExistingValue ? 167 : 92
                    };
                    genDialog.Controls.Add(genLabel);

                    var generatedValue = GenerateSecurePassword(selectedLength);
                    var genValueBox = new WinForms.TextBox
                    {
                        Text = generatedValue,
                        ReadOnly = true,
                        Left = 20,
                        Top = hasExistingValue ? 192 : 117,
                        Width = 460,
                        Height = 35,
                        Font = new Drawing.Font("Consolas", 10f),
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 52) : Drawing.Color.FromArgb(244, 245, 247),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 180, 0) : Drawing.Color.FromArgb(0, 120, 0),
                        BorderStyle = WinForms.BorderStyle.FixedSingle
                    };
                    genDialog.Controls.Add(genValueBox);

                    // Password length slider (positioned under generated value)
                    var lengthLabel = new WinForms.Label
                    {
                        Text = "Length:",
                        Font = new Drawing.Font("Segoe UI", 9f),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                        AutoSize = true,
                        Left = 20,
                        Top = hasExistingValue ? 237 : 162
                    };
                    genDialog.Controls.Add(lengthLabel);

                    var lengthSlider = new WinForms.TrackBar
                    {
                        Left = 80,
                        Top = hasExistingValue ? 232 : 157,
                        Width = 350,
                        Height = 30,
                        Minimum = 16,
                        Maximum = 32,
                        Value = 16,
                        TickFrequency = 1,
                        TickStyle = WinForms.TickStyle.BottomRight,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(248, 249, 251)
                    };
                    genDialog.Controls.Add(lengthSlider);

                    var lengthValueLabel = new WinForms.Label
                    {
                        Text = "16",
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 180, 0) : Drawing.Color.FromArgb(0, 120, 0),
                        AutoSize = true,
                        Left = 440,
                        Top = hasExistingValue ? 235 : 160
                    };
                    genDialog.Controls.Add(lengthValueLabel);

                    lengthSlider.ValueChanged += (_, _) =>
                    {
                        selectedLength = lengthSlider.Value;
                        lengthValueLabel.Text = selectedLength.ToString();
                        genValueBox.Text = GenerateSecurePassword(selectedLength);
                    };

                    // Regenerate button
                    var regenBtn = new WinForms.Button
                    {
                        Text = "↻ Regenerate",
                        Left = 20,
                        Top = hasExistingValue ? 277 : 202,
                        Width = 460,
                        Height = 35,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
                        ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
                        Cursor = WinForms.Cursors.Hand
                    };
                    regenBtn.FlatAppearance.BorderSize = 0;
                    regenBtn.Click += (_, _) =>
                    {
                        genValueBox.Text = GenerateSecurePassword(selectedLength);
                    };
                    genDialog.Controls.Add(regenBtn);

                    // Accept button
                    var acceptBtn = new WinForms.Button
                    {
                        Text = hasExistingValue ? "Use Generated" : "Use Value",
                        Left = 20,
                        Top = hasExistingValue ? 327 : 252,
                        Width = 225,
                        Height = 40,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.FromArgb(0, 145, 90),
                        ForeColor = Drawing.Color.White,
                        DialogResult = WinForms.DialogResult.OK,
                        Cursor = WinForms.Cursors.Hand
                    };
                    acceptBtn.FlatAppearance.BorderSize = 0;
                    
                    // Handle Ctrl+Click for save + copy to clipboard
                    acceptBtn.Click += (_, _) =>
                    {
                        // Check if Ctrl key is pressed during click
                        if ((WinForms.Control.ModifierKeys & WinForms.Keys.Control) == WinForms.Keys.Control)
                        {
                            // Copy the generated password to clipboard
                            try
                            {
                                WinForms.Clipboard.SetText(genValueBox.Text);
                                
                                // Show tooltip/hint about clipboard copy
                                var copiedHint = new WinForms.ToolTip();
                                copiedHint.Show("Password copied to clipboard! Will auto-clear if configured.", acceptBtn, 0, -40, 2000);
                            }
                            catch (Exception ex)
                            {
                                WinForms.MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Copy Error", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                            }
                        }
                    };
                    
                    genDialog.Controls.Add(acceptBtn);

                    // Cancel button
                    var cancelBtn = new WinForms.Button
                    {
                        Text = "Cancel",
                        Left = 255,
                        Top = hasExistingValue ? 327 : 252,
                        Width = 225,
                        Height = 40,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        FlatStyle = WinForms.FlatStyle.Flat,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 68) : Drawing.Color.FromArgb(225, 226, 230),
                        ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(40, 40, 40),
                        DialogResult = WinForms.DialogResult.Cancel,
                        Cursor = WinForms.Cursors.Hand
                    };
                    cancelBtn.FlatAppearance.BorderSize = 0;
                    genDialog.Controls.Add(cancelBtn);

                    // Hint label for Ctrl+Click feature
                    var hintLabel = new WinForms.Label
                    {
                        Text = "Ctrl+Click: Save + Copy",
                        Font = new Drawing.Font("Segoe UI", 8f),
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(155, 155, 165) : Drawing.Color.FromArgb(95, 100, 108),
                        AutoSize = true,
                        Left = 20,
                        Top = hasExistingValue ? 372 : 297
                    };
                    genDialog.Controls.Add(hintLabel);

                    genDialog.Controls.Add(titleLabel);
                    genDialog.Controls.Add(entryLabel);
                    genDialog.Controls.Add(modifiedLabel);

                    if (genDialog.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        // Update the snippet value with generated password
                        snippetRow.ActualValue = genValueBox.Text;
                        snippetRow.IsEncrypted = false;
                        grid.Rows[ev.RowIndex].Cells[2].Value = genValueBox.Text;
                        hasChanges = true;
                        btnSave.Visible = true;

                        // Auto-save the snippets
                        try
                        {
                            EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
                            hasChanges = false;
                            btnSave.Visible = false;
                        }
                        catch
                        {
                            WinForms.MessageBox.Show("Failed to save generated password.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                        }
                    }
                }
            }
        };

        // Decrypt All button click handler
        btnDecryptAll.Click += (s, ev) =>
        {
            // Check if there are unsaved changes
            if (hasChanges)
            {
                var result = WinForms.MessageBox.Show(
                    "You have unsaved changes. Save before showing all?",
                    "PinBubble - Unsaved Changes",
                    WinForms.MessageBoxButtons.YesNoCancel,
                    WinForms.MessageBoxIcon.Question);
                
                if (result == WinForms.DialogResult.Cancel)
                    return;
                
                if (result == WinForms.DialogResult.Yes)
                {
                    // Save the changes first
                    try
                    {
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
                        hasChanges = false;
                        btnSave.Visible = false;
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                        return;
                    }
                }
                else if (result == WinForms.DialogResult.No)
                {
                    // User chose not to save, reset the change flag
                    hasChanges = false;
                    btnSave.Visible = false;
                }
            }
            
            isDecrypted = !isDecrypted;
            isUpdatingProgrammatically = true;
            
            if (isDecrypted)
            {
                // Show all decrypted
                btnDecryptAll.Text = "Hide All";
                btnDecryptAll.Visible = true;
                btnDecryptAll.Enabled = true;
                
                foreach (WinForms.DataGridViewRow gridRow in grid.Rows)
                {
                    if (gridRow.IsNewRow) continue;
                    if (gridRow.Tag is SnippetRow snippetRow)
                    {
                        snippetRow.IsEncrypted = false;
                        gridRow.Cells[2].Value = snippetRow.ActualValue;
                        grid.InvalidateCell(3, gridRow.Index); // Refresh eye icon
                    }
                }
            }
            else
            {
                // Show all encrypted
                btnDecryptAll.Text = "Show All";
                btnDecryptAll.Visible = false;
                btnDecryptAll.Enabled = false;
                
                foreach (WinForms.DataGridViewRow gridRow in grid.Rows)
                {
                    if (gridRow.IsNewRow) continue;
                    if (gridRow.Tag is SnippetRow snippetRow)
                    {
                        snippetRow.IsEncrypted = true;
                        gridRow.Cells[2].Value = "••••••••";
                        grid.InvalidateCell(3, gridRow.Index); // Refresh eye icon
                    }
                }
            }
            
            isUpdatingProgrammatically = false;
            UpdateStatusColor();
        };

        // Cancel button click handler
        btnCancel.Click += (s, ev) =>
        {
            if (hasChanges)
            {
                var result = WinForms.MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "PinBubble - Unsaved Changes",
                    WinForms.MessageBoxButtons.YesNoCancel,
                    WinForms.MessageBoxIcon.Question);
                
                if (result == WinForms.DialogResult.Cancel)
                    return;
                
                if (result == WinForms.DialogResult.Yes)
                {
                    // Save before closing
                    try
                    {
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
                        hasChanges = false;
                        dialog.DialogResult = WinForms.DialogResult.Cancel;
                        dialog.Close();
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    // User chose No, close without saving
                    hasChanges = false;
                    dialog.DialogResult = WinForms.DialogResult.Cancel;
                    dialog.Close();
                }
            }
            else
            {
                // No changes, just close
                dialog.DialogResult = WinForms.DialogResult.Cancel;
                dialog.Close();
            }
        };

        // Control key handling for Show All button visibility and Ctrl+S save
        dialog.KeyDown += (s, ev) =>
        {
            // Handle Ctrl+S to save first (before showing the button)
            if (ev.Control && ev.KeyCode == WinForms.Keys.S)
            {
                ev.SuppressKeyPress = true; // Prevent beep sound
                
                // Hide Show All button to prevent glitch during save dialog
                btnDecryptAll.Visible = false;
                btnDecryptAll.Enabled = false;
                
                if (hasChanges)
                {
                    // Trigger save
                    try
                    {
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
                        hasChanges = false;
                        btnSave.Visible = false;
                        
                        WinForms.MessageBox.Show("Saved successfully.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                    }
                }
                return;
            }
            
            // Handle Escape key to cancel/close
            if (ev.KeyCode == WinForms.Keys.Escape)
            {
                ev.SuppressKeyPress = true;
                btnCancel.PerformClick();
                return;
            }
            
            // Only show the button when Control is pressed AND it's in "Show All" mode
            if (ev.Control && !ev.Alt && !ev.Shift && btnDecryptAll.Text == "Show All")
            {
                btnDecryptAll.Visible = true;
                btnDecryptAll.Enabled = true;
            }
        };

        dialog.KeyUp += (s, ev) =>
        {
            // Hide Show All button when Control is released (but keep Hide All visible)
            if (ev.KeyCode == WinForms.Keys.ControlKey && btnDecryptAll.Text == "Show All")
            {
                btnDecryptAll.Visible = false;
                btnDecryptAll.Enabled = false;
            }
        };

        // Handle form closing to prompt for unsaved changes
        dialog.FormClosing += (s, ev) =>
        {
            if (hasChanges && ((WinForms.FormClosingEventArgs)ev).CloseReason == WinForms.CloseReason.UserClosing)
            {
                var result = WinForms.MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "PinBubble - Unsaved Changes",
                    WinForms.MessageBoxButtons.YesNoCancel,
                    WinForms.MessageBoxIcon.Question);
                
                if (result == WinForms.DialogResult.Cancel)
                {
                    ((WinForms.FormClosingEventArgs)ev).Cancel = true;
                    return;
                }
                
                if (result == WinForms.DialogResult.Yes)
                {
                    // Save before closing
                    try
                    {
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                        ((WinForms.FormClosingEventArgs)ev).Cancel = true;
                    }
                }
            }
        };

        // Enable window dragging from toolbar
        bool dragging = false;
        Drawing.Point dragCursor = Drawing.Point.Empty;
        Drawing.Point dragDialog = Drawing.Point.Empty;
        toolbar.MouseDown += (_, mouseArgs) =>
        {
            if (mouseArgs.Button != WinForms.MouseButtons.Left) return;
            dragging = true;
            dragCursor = WinForms.Cursor.Position;
            dragDialog = dialog.Location;
        };
        toolbar.MouseMove += (_, _) =>
        {
            if (!dragging) return;
            var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(dragCursor));
            dialog.Location = Drawing.Point.Add(dragDialog, new Drawing.Size(diff));
        };
        toolbar.MouseUp += (_, _) => dragging = false;
        foreach (var dragTarget in new WinForms.Control[] { statusLED, titleLabel, infoLabel })
        {
            dragTarget.MouseDown += (_, mouseArgs) =>
            {
                if (mouseArgs.Button != WinForms.MouseButtons.Left) return;
                dragging = true;
                dragCursor = WinForms.Cursor.Position;
                dragDialog = dialog.Location;
            };
            dragTarget.MouseMove += (_, _) =>
            {
                if (!dragging) return;
                var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(dragCursor));
                dialog.Location = Drawing.Point.Add(dragDialog, new Drawing.Size(diff));
            };
            dragTarget.MouseUp += (_, _) => dragging = false;
        }

        gridFrame.Controls.Add(grid);
        dialog.Controls.Add(toolbar);
        dialog.Controls.Add(gridFrame);
        dialog.AcceptButton = btnSave;

        // Add a timer to refresh TOTP display every second
        var totpRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000 // Refresh every second
        };
        totpRefreshTimer.Tick += (s, e) =>
        {
            // Invalidate all TOTP cells (column 4) to trigger repaint
            for (int i = 0; i < grid.Rows.Count - 1; i++)
            {
                if (grid.Rows[i].Tag is SnippetRow row && !string.IsNullOrWhiteSpace(row.TotpSecret))
                {
                    grid.InvalidateCell(4, i);
                }
            }
        };
        totpRefreshTimer.Start();
        dialog.FormClosed += (s, e) => totpRefreshTimer.Dispose();

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword!, BuildGridSaveJson(grid));
            LoadSnippets();
            BuildBubbles();
        }
        catch
        {
            System.Windows.MessageBox.Show("Failed to save encrypted snippets.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleTaskbar_Click(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = !ShowInTaskbar;
        UpdateTaskbarMenuText();
        SaveUiSettings();
        
        if (!ShowInTaskbar && _trayIcon != null)
        {
            _trayIcon.BalloonTipTitle = "PinBubble";
            _trayIcon.BalloonTipText = "Taskbar icon hidden. App is still running and pinned on screen.";
            _trayIcon.ShowBalloonTip(2000);
        }
    }

    private void UpdateTaskbarMenuText()
    {
        TaskbarToggleMenuItem.Header = ShowInTaskbar ? "Hide from Taskbar" : "Show in Taskbar";
    }

    private void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        UpdatePinMenuText();
        SaveUiSettings();
    }

    private void UpdatePinMenuText()
    {
        PinToggleMenuItem.Header = _isPinned ? "Unpin the Bubble" : "Pin the Bubble";
    }

    private void ToggleBiometricUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (BiometricMasterPasswordStore.HasCachedPassword())
        {
            var disableResult = System.Windows.MessageBox.Show(
                "Disable fingerprint unlock and remove the cached credential?",
                "PinBubble",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (disableResult != MessageBoxResult.Yes)
                return;

            BiometricMasterPasswordStore.ClearCachedPassword();
            UpdateBiometricUi();
            return;
        }

        if (string.IsNullOrWhiteSpace(_masterPassword))
        {
            System.Windows.MessageBox.Show(
                "Fingerprint unlock can only be enabled after entering your master password.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdateBiometricUi();
            return;
        }

        if (!BiometricMasterPasswordStore.IsBiometricAvailable())
        {
            System.Windows.MessageBox.Show(
                "Fingerprint authentication is not available on this device.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdateBiometricUi();
            return;
        }

        if (!BiometricMasterPasswordStore.CachePassword(_masterPassword))
        {
            System.Windows.MessageBox.Show(
                "Failed to enable fingerprint unlock.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        UpdateBiometricUi();
    }

    private void UpdateBiometricUi()
    {
        UpdateBiometricMenuText();
    }

    private void ChangeMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_masterPassword))
        {
            System.Windows.MessageBox.Show(
                "Unlock the vault before changing its master password.",
                "PinBubble",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var newPassword = PromptForNewMasterPassword();
        if (newPassword is null || string.Equals(newPassword, _masterPassword, StringComparison.Ordinal))
            return;

        var oldPassword = _masterPassword;
        var biometricCacheEnabled = BiometricMasterPasswordStore.HasCachedPassword();

        if (!EncryptedTextStore.TryChangePassword(_textFilePath, oldPassword, newPassword, out var error))
        {
            System.Windows.MessageBox.Show(
                error,
                "Master Password Not Changed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (biometricCacheEnabled && !BiometricMasterPasswordStore.CachePassword(newPassword))
        {
            var rollbackSucceeded = EncryptedTextStore.TryChangePassword(_textFilePath, newPassword, oldPassword, out _);
            if (rollbackSucceeded)
            {
                System.Windows.MessageBox.Show(
                    "The biometric credential could not be updated, so the vault was safely left with its previous password.",
                    "Master Password Not Changed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "The vault password changed, but the biometric credential could not be updated. Use the new password to unlock.",
                    "PinBubble",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        _masterPassword = newPassword;
        UpdateBiometricUi();
        System.Windows.MessageBox.Show(
            "Master password changed. Your vault contents and app settings were preserved.",
            "PinBubble",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UpdateBiometricMenuText()
    {
        var enabled = BiometricMasterPasswordStore.HasCachedPassword();
        var available = BiometricMasterPasswordStore.IsBiometricAvailable();

        if (enabled)
        {
            BiometricToggleMenuItem.Header = "Disable Fingerprint Unlock";
            BiometricToggleMenuItem.IsEnabled = true;
            return;
        }

        BiometricToggleMenuItem.Header = available
            ? "Enable Fingerprint Unlock"
            : "Enable Fingerprint Unlock (Unavailable)";
        BiometricToggleMenuItem.IsEnabled = available;
    }

    private void ToggleDarkTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
        ApplyExpandedPanelTheme();
        BuildBubbles();
        SaveUiSettings();
    }

    private void QuickEnter_Click(object sender, RoutedEventArgs e)
    {
        _quickEnterEnabled = !_quickEnterEnabled;
        QuickEnterMenuItem.IsChecked = _quickEnterEnabled;
        SaveUiSettings();
    }

    private void ResetPreferences_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Reset UI preferences to defaults? This does not affect snippets.",
            "PinBubble",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        _backdropOpacity = DefaultBackdropOpacity;
        _isPinned = DefaultIsPinned;
        _isDarkTheme = DefaultIsDarkTheme;
        ShowInTaskbar = DefaultShowInTaskbar;
        _quickEnterEnabled = false;
        _clipboardClearSeconds = DefaultClipboardClearSeconds;

        Topmost = _isPinned;
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
        QuickEnterMenuItem.IsChecked = _quickEnterEnabled;
        ApplyExpandedPanelTheme();
        BuildBubbles();
        UpdateBackdropOpacityMenuChecks();
        UpdatePinMenuText();
        UpdateTaskbarMenuText();
        UpdateClearClipMenuChecks();
        SaveUiSettings();
    }

    private void BackdropOpacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string tagValue)
            return;

        if (!double.TryParse(tagValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            return;

        _backdropOpacity = Math.Clamp(opacity, 0.0, 1.0);
        ApplyBackdropOpacity();
        UpdateBackdropOpacityMenuChecks();
        SaveUiSettings();
    }

    private void ApplyBackdropOpacity()
    {
        var baseColor = _isDarkTheme ? BackdropBaseColorDark : BackdropBaseColorLight;
        var alpha = (byte)Math.Round(Math.Clamp(_backdropOpacity, 0.0, 1.0) * 255);
        ExpandedBackdrop.Background = new SolidColorBrush(
            WpfColor.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }

    private void ApplyExpandedPanelTheme()
    {
        ApplyBackdropOpacity();
        ExpandedBackdrop.BorderBrush = new SolidColorBrush(_isDarkTheme
            ? WpfColor.FromArgb(85, 255, 255, 255)
            : WpfColor.FromArgb(120, 45, 55, 70));
    }

    private void UpdateBackdropOpacityMenuChecks()
    {
        BackdropOpacity20MenuItem.IsChecked = Math.Abs(_backdropOpacity - 0.20) < 0.01;
        BackdropOpacity35MenuItem.IsChecked = Math.Abs(_backdropOpacity - 0.35) < 0.01;
        BackdropOpacity50MenuItem.IsChecked = Math.Abs(_backdropOpacity - 0.50) < 0.01;
        BackdropOpacity65MenuItem.IsChecked = Math.Abs(_backdropOpacity - 0.65) < 0.01;
        BackdropOpacity80MenuItem.IsChecked = Math.Abs(_backdropOpacity - 0.80) < 0.01;
    }

    private void LoadUiSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
                return;

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<UiSettings>(json);
            if (settings is not null)
            {
                _backdropOpacity = Math.Clamp(settings.BackdropOpacity, 0.0, 1.0);
                _isPinned = settings.IsPinned;
                _isDarkTheme = settings.IsDarkTheme;
                ShowInTaskbar = settings.ShowInTaskbar;
                _savedWindowLeft = settings.WindowLeft;
                _savedWindowTop = settings.WindowTop;
                _savedMonitorDeviceName = settings.MonitorDeviceName;
                _clipboardClearSeconds = settings.ClipboardClearSeconds;
                _quickEnterEnabled = settings.QuickEnterEnabled;
                _copyTotpTogether = settings.CopyTotpTogether;
                _defaultExpiryDays = Math.Max(1, settings.DefaultExpiryDays);
            }
        }
        catch
        {
            // Invalid or inaccessible settings should not block app startup.
        }
    }

    private void SaveUiSettings()
    {
        try
        {
            var settingsDir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(settingsDir))
                Directory.CreateDirectory(settingsDir);

            var windowRect = ConvertWpfRectToDeviceRect(new Rect(
                Left,
                Top,
                Math.Max(1, Width),
                Math.Max(1, Height)));
            var currentScreen = WinForms.Screen.FromRectangle(windowRect);

            var settings = new UiSettings
            {
                BackdropOpacity = _backdropOpacity,
                IsPinned = _isPinned,
                IsDarkTheme = _isDarkTheme,
                ShowInTaskbar = ShowInTaskbar,
                WindowLeft = Left,
                WindowTop = Top,
                MonitorDeviceName = currentScreen.DeviceName,
                ClipboardClearSeconds = _clipboardClearSeconds,
                QuickEnterEnabled = _quickEnterEnabled,
                CopyTotpTogether = _copyTotpTogether,
                DefaultExpiryDays = _defaultExpiryDays
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Failing to save UI preferences should be non-fatal.
        }
    }

    // ── Clipboard auto-clear ────────────────────────────────────────────────

    private void CopySnippetToClipboard(string text, SnippetRow? snippetRow = null)
    {
        // If snippet has TOTP and config says to copy together, append TOTP to text
        var textToCopy = text;
        if (_copyTotpTogether && snippetRow != null && !string.IsNullOrWhiteSpace(snippetRow.TotpSecret))
        {
            var totpCode = snippetRow.CurrentTotp;
            if (!string.IsNullOrWhiteSpace(totpCode))
            {
                textToCopy = $"{text}{totpCode}";
            }
        }

        if (!TrySetClipboardWithoutHistory(textToCopy))
            System.Windows.Clipboard.SetText(textToCopy);

        ScheduleClipboardClear(textToCopy);
    }

    private static bool TrySetClipboardWithoutHistory(string text)
    {
        try
        {
            var dataPackage = new WinDataPackage();
            dataPackage.SetText(text);

            var options = new WinClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };

            WinClipboard.SetContentWithOptions(dataPackage, options);
            return true;
        }
        catch
        {
            // Fallback for desktop clipboard pipelines: add Windows-recognized
            // metadata formats that request no history and no cloud sync.
            try
            {
                var data = new System.Windows.DataObject();
                data.SetText(text);
                data.SetData("CanIncludeInClipboardHistory", false);
                data.SetData("CanUploadToCloudClipboard", false);
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void ScheduleClipboardClear(string copiedText)
    {
        if (_clipboardClearSeconds <= 0) return;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_clipboardClearSeconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (System.Windows.Clipboard.ContainsText() &&
                    System.Windows.Clipboard.GetText() == copiedText)
                    System.Windows.Clipboard.Clear();
            }
            catch { }
        };
        timer.Start();
    }

    private void ClearClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked) return;
        _clipboardClearSeconds = int.TryParse(clicked.Tag?.ToString(), out var sec) ? sec : 0;
        UpdateClearClipMenuChecks();
        SaveUiSettings();
    }

    private void UpdateClearClipMenuChecks()
    {
        ClearClipDisabledMenuItem.IsChecked = _clipboardClearSeconds == 0;
        ClearClip30MenuItem.IsChecked       = _clipboardClearSeconds == 30;
        ClearClip60MenuItem.IsChecked       = _clipboardClearSeconds == 60;
        ClearClip120MenuItem.IsChecked      = _clipboardClearSeconds == 120;
    }

    private void CopyTotpTogether_Click(object sender, RoutedEventArgs e)
    {
        _copyTotpTogether = !_copyTotpTogether;
        CopyTotpTogetherMenuItem.IsChecked = _copyTotpTogether;
        SaveUiSettings();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Allow normal minimize/restore behavior when clicking taskbar icon
        // Window will minimize when clicked, and restore when clicked again
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterAllHotkeys();
        _hwndSource?.RemoveHook(WndProc);
        SaveUiSettings();
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _watcher?.Dispose();
        _reloadDebounce?.Stop();
        base.OnClosed(e);
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRectPath(Drawing.Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dimColor = _isDarkTheme ? Drawing.Color.FromArgb(150, 150, 156) : Drawing.Color.FromArgb(120, 120, 120);
        var accentColor = _isDarkTheme ? Drawing.Color.FromArgb(110, 195, 60) : Drawing.Color.FromArgb(70, 140, 35);
        var textColor = _isDarkTheme ? Drawing.Color.FromArgb(210, 210, 216) : Drawing.Color.FromArgb(40, 40, 40);
        var separatorColor = _isDarkTheme ? Drawing.Color.FromArgb(70, 70, 78) : Drawing.Color.FromArgb(225, 225, 230);
        var cardTopColor = _isDarkTheme ? Drawing.Color.FromArgb(48, 48, 55) : Drawing.Color.FromArgb(255, 255, 255);
        var cardBottomColor = _isDarkTheme ? Drawing.Color.FromArgb(38, 38, 44) : Drawing.Color.FromArgb(246, 247, 249);
        var cardBorderColor = _isDarkTheme ? Drawing.Color.FromArgb(255, 255, 255) : Drawing.Color.FromArgb(0, 0, 0);

        // The rounded card *is* the window: borderless, region-clipped to match the drawn shape.
        const int cardWidth = 492;
        const int cardHeight = 612;
        const int cornerRadius = 18;

        using var aboutDialog = new WinForms.Form
        {
            FormBorderStyle = WinForms.FormBorderStyle.None,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            ClientSize = new Drawing.Size(cardWidth, cardHeight),
            Text = "About PinBubble",
            ShowInTaskbar = false,
            TopMost = true,
            AutoScaleMode = WinForms.AutoScaleMode.None,
            BackColor = cardBottomColor,
            KeyPreview = true
        };

        using (var formRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, aboutDialog.Width - 1, aboutDialog.Height - 1), cornerRadius))
        {
            aboutDialog.Region = new Drawing.Region(formRegionPath);
        }

        aboutDialog.KeyDown += (s, e) =>
        {
            if (e.KeyCode == WinForms.Keys.Escape)
                aboutDialog.Close();
        };

        // ── Glass card: fills the whole window, rounded corners, subtle gradient + border ──
        var card = new WinForms.Panel
        {
            Left = 0,
            Top = 0,
            Width = cardWidth,
            Height = cardHeight,
            BackColor = cardBottomColor
        };
        card.Paint += (s, e) =>
        {
            var rect = new Drawing.Rectangle(0, 0, card.Width - 1, card.Height - 1);
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(rect, cornerRadius);
            using var fill = new Drawing.Drawing2D.LinearGradientBrush(rect, cardTopColor, cardBottomColor, 90f);
            e.Graphics.FillPath(fill, path);
            using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(_isDarkTheme ? 22 : 18, cardBorderColor), 1f);
            e.Graphics.DrawPath(borderPen, path);
            using var highlightPen = new Drawing.Pen(Drawing.Color.FromArgb(_isDarkTheme ? 14 : 130, Drawing.Color.White), 1f);
            e.Graphics.DrawLine(highlightPen, 18, 1, card.Width - 18, 1);
        };
        using (var cardRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, card.Width - 1, card.Height - 1), cornerRadius))
        {
            card.Region = new Drawing.Region(cardRegionPath);
        }

        // Fancy circular close button, top-right corner.
        const int closeSize = 28;
        var closeButtonNormalBack = _isDarkTheme ? Drawing.Color.FromArgb(58, 58, 66) : Drawing.Color.FromArgb(228, 229, 233);
        var closeButton = new WinForms.Button
        {
            Left = card.Width - closeSize - 16,
            Top = 16,
            Width = closeSize,
            Height = closeSize,
            Text = "✕",
            Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold),
            FlatStyle = WinForms.FlatStyle.Flat,
            ForeColor = dimColor,
            BackColor = closeButtonNormalBack,
            Cursor = WinForms.Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false
        };
        closeButton.FlatAppearance.BorderSize = 0;
        using (var closeRegionPath = RoundedRectPath(new Drawing.Rectangle(0, 0, closeSize - 1, closeSize - 1), closeSize / 2))
        {
            closeButton.Region = new Drawing.Region(closeRegionPath);
        }
        closeButton.MouseEnter += (_, _) =>
        {
            closeButton.BackColor = Drawing.Color.FromArgb(232, 17, 35);
            closeButton.ForeColor = Drawing.Color.White;
        };
        closeButton.MouseLeave += (_, _) =>
        {
            closeButton.BackColor = closeButtonNormalBack;
            closeButton.ForeColor = dimColor;
        };
        closeButton.Click += (_, _) => aboutDialog.Close();

        // Content is laid out relative to the card, with equal left/right padding.
        const int pad = 28;
        var sectionWidth = card.Width - pad * 2;
        const int sectionLeft = pad;

        var pinIcon = new WinForms.PictureBox
        {
            Left = sectionLeft + (sectionWidth - 60) / 2,
            Top = 24,
            Width = 60,
            Height = 60,
            BackColor = Drawing.Color.Transparent
        };
        var pinBitmap = new Drawing.Bitmap(60, 60);
        using (var g = Drawing.Graphics.FromImage(pinBitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new Drawing.SolidBrush(accentColor);
            g.FillEllipse(brush, 15, 5, 30, 30);
            g.FillPolygon(brush, new Drawing.Point[] { new(26, 35), new(34, 35), new(30, 52) });
        }
        pinIcon.Image = pinBitmap;

        // Idle bounce for the main pin icon.
        var pinBaseTop = pinIcon.Top;
        var pinBounceTimer = new WinForms.Timer { Interval = 18 };
        double pinBouncePhase = 0;
        pinBounceTimer.Tick += (_, _) =>
        {
            pinBouncePhase += 0.085;
            pinIcon.Top = pinBaseTop + (int)Math.Round(Math.Sin(pinBouncePhase) * 4.5);
        };
        pinBounceTimer.Start();

        var titleLabel = new WinForms.Label
        {
            Left = sectionLeft,
            Top = 98,
            Width = sectionWidth,
            Height = 40,
            Text = "PinBubble",
            Font = new Drawing.Font("Segoe UI", 24f, Drawing.FontStyle.Bold),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(230, 230, 235) : Drawing.Color.FromArgb(25, 25, 25),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        // Meta row (version | star | github link) — built as one unit and centered as a whole.
        var versionText = $"Version {s_appVersion}";
        var versionFont = new Drawing.Font("Segoe UI", 9f);
        var starFont = new Drawing.Font("Segoe UI Emoji", 13f);
        var githubFont = new Drawing.Font("Segoe UI", 9f);
        const string githubLinkText = "View on GitHub";
        var versionWidth = WinForms.TextRenderer.MeasureText(versionText, versionFont).Width + 2;
        var starWidth = WinForms.TextRenderer.MeasureText("⭐", starFont).Width + 2;
        var githubWidth = WinForms.TextRenderer.MeasureText(githubLinkText, githubFont).Width + 4;
        const int pipeWidth = 18;
        const int starGap = 2;
        var metaRowWidth = versionWidth + pipeWidth + starWidth + starGap + githubWidth;

        var metaRow = new WinForms.Panel
        {
            Left = sectionLeft + (sectionWidth - metaRowWidth) / 2,
            Top = 148,
            Width = metaRowWidth,
            Height = 22,
            BackColor = Drawing.Color.Transparent
        };

        var versionLabel = new WinForms.Label
        {
            Left = 0,
            Top = 0,
            Width = versionWidth,
            Height = 22,
            Text = versionText,
            Font = versionFont,
            ForeColor = dimColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleRight
        };

        var pipeLbl = new WinForms.Label
        {
            Left = versionWidth,
            Top = 0,
            Width = pipeWidth,
            Height = 22,
            Text = "│",
            Font = versionFont,
            ForeColor = separatorColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        var starLabel = new WinForms.Label
        {
            Left = versionWidth + pipeWidth,
            Top = 0,
            Width = starWidth,
            Height = 22,
            Text = "⭐",
            Font = starFont,
            ForeColor = accentColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter,
            Cursor = WinForms.Cursors.Hand
        };
        starLabel.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/niravp-0x/PinBubble") { UseShellExecute = true });
            }
            catch { }
        };

        var githubLink = new WinForms.LinkLabel
        {
            Left = versionWidth + pipeWidth + starWidth + starGap,
            Top = 0,
            Width = githubWidth,
            Height = 22,
            Text = githubLinkText,
            Font = githubFont,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleLeft,
            LinkColor = accentColor,
            ActiveLinkColor = _isDarkTheme ? Drawing.Color.FromArgb(150, 220, 80) : Drawing.Color.FromArgb(0, 80, 40),
            VisitedLinkColor = accentColor
        };
        githubLink.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/niravp-0x/PinBubble") { UseShellExecute = true });
            }
            catch { }
        };
        metaRow.Controls.AddRange(new WinForms.Control[] { versionLabel, pipeLbl, starLabel, githubLink });

        var separator1 = new WinForms.Panel { Left = sectionLeft, Top = 188, Width = sectionWidth, Height = 1, BackColor = separatorColor };

        var descriptionLabel = new WinForms.Label
        {
            Left = sectionLeft,
            Top = 204,
            Width = sectionWidth,
            Height = 64,
            Text = "A lightweight, always-on-screen snippet manager\nthat keeps your frequently used text snippets\nat your fingertips.",
            Font = new Drawing.Font("Segoe UI", 10f),
            ForeColor = textColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.TopCenter
        };

        var featuresLabel = new WinForms.Label
        {
            Left = sectionLeft + 12,
            Top = 282,
            Width = sectionWidth - 12,
            Height = 20,
            Text = "KEY FEATURES",
            Font = new Drawing.Font("Segoe UI", 8.5f, Drawing.FontStyle.Bold),
            ForeColor = accentColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleLeft
        };

        WinForms.Label MakeFeat(string text, int top) => new WinForms.Label
        {
            Left = sectionLeft + 18,
            Top = top,
            Width = sectionWidth - 18,
            Height = 22,
            Text = text,
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = textColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleLeft
        };

        var feature1 = MakeFeat("•  Encrypted snippet storage with master password", 310);
        var feature2 = MakeFeat("•  TOTP support with quick copy and Ctrl+click behavior", 334);
        var feature3 = MakeFeat("•  Global hotkeys, QWERTY picker, and instant paste", 358);
        var feature4 = MakeFeat("•  Pin/unpin and dark theme support for comfortable viewing", 382);
        var feature5 = MakeFeat("•  Configurable default expiry and clipboard auto-clear", 406);

        var separator2 = new WinForms.Panel { Left = sectionLeft, Top = 444, Width = sectionWidth, Height = 1, BackColor = separatorColor };

        var authorsHeaderLbl = new WinForms.Label
        {
            Left = sectionLeft,
            Top = 460,
            Width = sectionWidth,
            Height = 20,
            Text = "AUTHORS",
            Font = new Drawing.Font("Segoe UI", 8.5f, Drawing.FontStyle.Bold),
            ForeColor = accentColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        var authorNamesLbl = new WinForms.Label
        {
            Left = sectionLeft,
            Top = 486,
            Width = sectionWidth,
            Height = 24,
            Text = "niravp-0x  ·  biggrocer",
            Font = new Drawing.Font("Segoe UI", 9f),
            ForeColor = dimColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        // Hidden easter egg: hovering the main pin icon flashes the author names.
        var authorOriginalColor = authorNamesLbl.ForeColor;
        var authorOriginalFont = authorNamesLbl.Font;
        var authorFlashFont = new Drawing.Font(authorOriginalFont, Drawing.FontStyle.Bold);
        var authorFlashColors = new[]
        {
            Drawing.Color.FromArgb(255, 90, 90),
            Drawing.Color.FromArgb(255, 170, 60),
            Drawing.Color.FromArgb(255, 225, 60),
            Drawing.Color.FromArgb(120, 220, 120),
            Drawing.Color.FromArgb(90, 170, 255),
            Drawing.Color.FromArgb(190, 120, 255)
        };
        var authorFlashTimer = new WinForms.Timer { Interval = 90 };
        var authorFlashIndex = 0;
        authorFlashTimer.Tick += (_, _) =>
        {
            authorNamesLbl.ForeColor = authorFlashColors[authorFlashIndex % authorFlashColors.Length];
            authorFlashIndex++;
        };
        pinIcon.MouseEnter += (_, _) =>
        {
            authorNamesLbl.Font = authorFlashFont;
            authorFlashIndex = 0;
            authorFlashTimer.Start();
        };
        pinIcon.MouseLeave += (_, _) =>
        {
            authorFlashTimer.Stop();
            authorNamesLbl.ForeColor = authorOriginalColor;
            authorNamesLbl.Font = authorOriginalFont;
        };
        aboutDialog.FormClosed += (_, _) =>
        {
            pinBounceTimer.Stop();
            pinBounceTimer.Dispose();
            authorFlashTimer.Stop();
            authorFlashTimer.Dispose();
            authorFlashFont.Dispose();
        };

        var licenseLink = new WinForms.LinkLabel
        {
            Left = sectionLeft,
            Top = 516,
            Width = sectionWidth,
            Height = 20,
            Text = "Released under the MIT License",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter,
            LinkColor = dimColor,
            ActiveLinkColor = accentColor,
            VisitedLinkColor = dimColor
        };
        licenseLink.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/niravp-0x/PinBubble/blob/main/LICENSE") { UseShellExecute = true });
            }
            catch { }
        };

        var separator3 = new WinForms.Panel { Left = sectionLeft, Top = 552, Width = sectionWidth, Height = 1, BackColor = separatorColor };

        // Credit row (robot icon + text) — measured and built as one unit so the icon never overlaps the text.
        const string creditText = "Proudly vibecoded with GitHub Copilot";
        var creditFont = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Italic);
        var creditTextWidth = WinForms.TextRenderer.MeasureText(creditText, creditFont).Width + 4;
        const int robotSize = 32;
        const int robotTextGap = 6;
        var creditRowWidth = robotSize + robotTextGap + creditTextWidth;
        var creditRowHeight = robotSize + 4;

        var creditRow = new WinForms.Panel
        {
            Left = sectionLeft + (sectionWidth - creditRowWidth) / 2,
            Top = 562,
            Width = creditRowWidth,
            Height = creditRowHeight,
            BackColor = Drawing.Color.Transparent
        };

        var robotEyeColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White;

        Drawing.Bitmap DrawRobotBitmap(float angleDeg)
        {
            var bmp = new Drawing.Bitmap(robotSize, robotSize);
            using var g = Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TranslateTransform(robotSize / 2f, robotSize / 2f);
            g.RotateTransform(angleDeg);
            g.TranslateTransform(-robotSize / 2f, -robotSize / 2f);
            var scale = robotSize / 18f;
            using (var brush = new Drawing.SolidBrush(dimColor))
                g.FillRectangle(brush, 3 * scale, 5 * scale, 12 * scale, 10 * scale);
            using (var brush = new Drawing.SolidBrush(robotEyeColor))
            {
                g.FillEllipse(brush, 6 * scale, 8 * scale, 3 * scale, 3 * scale);
                g.FillEllipse(brush, 11 * scale, 8 * scale, 3 * scale, 3 * scale);
            }
            using (var pen = new Drawing.Pen(dimColor, 1.5f * scale))
                g.DrawLine(pen, 9 * scale, 2 * scale, 9 * scale, 5 * scale);
            using (var brush = new Drawing.SolidBrush(dimColor))
                g.FillEllipse(brush, 7 * scale, 0, 4 * scale, 4 * scale);
            return bmp;
        }

        var robotIcon = new WinForms.PictureBox
        {
            Left = 0,
            Top = 2,
            Width = robotSize,
            Height = robotSize,
            BackColor = Drawing.Color.Transparent,
            Cursor = WinForms.Cursors.Hand,
            Image = DrawRobotBitmap(0f)
        };

        // Hidden easter egg: click the robot for a spin + a random message; every 5th click throws confetti.
        var robotRng = new Random();
        var robotClickCount = 0;
        var robotMessages = new (string Emoji, string Text)[]
        {
            ("🤖", "Beep boop! You found me!"),
            ("☕", "Powered by coffee & Copilot"),
            ("🎉", "Shhh… it's a secret!"),
            ("🚀", "To the moon and back!"),
            ("🐛", "No bugs here (probably)"),
            ("✨", "You have curious hands!"),
            ("🎯", "Bullseye! Nice click!"),
            ("🧠", "Beep... calculating awesomeness"),
            ("🍪", "Here, have a virtual cookie!"),
            ("🌈", "You just found a bit of magic"),
            ("🔋", "Recharging... beep beep!"),
            ("🎈", "Pop! Another secret found")
        };
        WinForms.Panel? robotBubble = null;
        WinForms.Timer? robotBubbleTimer = null;

        // Emoji glyphs need the dedicated emoji font; mixing them into a plain "Segoe UI"
        // string leaves them as blank squares, so the icon and text are separate labels.
        void ShowRobotBubble(string emoji, string text)
        {
            robotBubbleTimer?.Stop();
            robotBubbleTimer?.Dispose();
            robotBubble?.Dispose();

            var emojiFont = new Drawing.Font("Segoe UI Emoji", 12f);
            var textFont = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Regular);
            var emojiWidth = WinForms.TextRenderer.MeasureText(emoji, emojiFont).Width + 4;
            var textWidth = WinForms.TextRenderer.MeasureText(text, textFont).Width + 4;
            var rowWidth = Math.Min(sectionWidth, emojiWidth + textWidth);

            robotBubble = new WinForms.Panel
            {
                Left = creditRow.Left + (creditRow.Width - rowWidth) / 2,
                Top = creditRow.Top - 24,
                Width = rowWidth,
                Height = 22,
                BackColor = Drawing.Color.Transparent
            };

            var emojiLbl = new WinForms.Label
            {
                Left = 0,
                Top = 0,
                Width = emojiWidth,
                Height = 22,
                Text = emoji,
                Font = emojiFont,
                ForeColor = accentColor,
                BackColor = Drawing.Color.Transparent,
                TextAlign = Drawing.ContentAlignment.MiddleCenter
            };
            var textLbl = new WinForms.Label
            {
                Left = emojiWidth,
                Top = 0,
                Width = textWidth,
                Height = 22,
                Text = text,
                Font = textFont,
                ForeColor = accentColor,
                BackColor = Drawing.Color.Transparent,
                TextAlign = Drawing.ContentAlignment.MiddleLeft
            };
            robotBubble.Controls.AddRange(new WinForms.Control[] { emojiLbl, textLbl });
            card.Controls.Add(robotBubble);
            robotBubble.BringToFront();

            robotBubbleTimer = new WinForms.Timer { Interval = 1700 };
            robotBubbleTimer.Tick += (_, _) =>
            {
                robotBubbleTimer?.Stop();
                robotBubbleTimer?.Dispose();
                robotBubbleTimer = null;
                robotBubble?.Dispose();
                robotBubble = null;
            };
            robotBubbleTimer.Start();
        }

        void SpinRobot()
        {
            var step = 0;
            const int totalSteps = 16;
            var spinTimer = new WinForms.Timer { Interval = 25 };
            spinTimer.Tick += (_, _) =>
            {
                step++;
                var oldImage = robotIcon.Image;
                robotIcon.Image = DrawRobotBitmap(step * (720f / totalSteps) % 360);
                oldImage?.Dispose();
                if (step >= totalSteps)
                {
                    spinTimer.Stop();
                    spinTimer.Dispose();
                }
            };
            spinTimer.Start();
        }

        var authorBigFont = new Drawing.Font(authorOriginalFont.FontFamily, authorOriginalFont.Size + 7, Drawing.FontStyle.Bold);

        Drawing.Bitmap DrawCoinBitmap(int size)
        {
            var bmp = new Drawing.Bitmap(size, size);
            using var g = Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var fill = new Drawing.Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(0, 0, size, size),
                Drawing.Color.FromArgb(255, 235, 180), Drawing.Color.FromArgb(230, 170, 40), 45f))
                g.FillEllipse(fill, 0, 0, size - 1, size - 1);
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(160, 110, 20), 1.2f))
                g.DrawEllipse(pen, 0, 0, size - 1, size - 1);
            using (var innerPen = new Drawing.Pen(Drawing.Color.FromArgb(120, 255, 255, 255), 1f))
                g.DrawEllipse(innerPen, size * 0.22f, size * 0.22f, size * 0.56f, size * 0.56f);
            return bmp;
        }

        // The robot throws a big burst of confetti + coins up toward the authors, who
        // briefly grow larger while it happens.
        void ConfettiBurst()
        {
            var confettiColors = new[]
            {
                Drawing.Color.FromArgb(255, 90, 90),
                Drawing.Color.FromArgb(255, 170, 60),
                Drawing.Color.FromArgb(255, 225, 60),
                Drawing.Color.FromArgb(120, 220, 120),
                Drawing.Color.FromArgb(90, 170, 255),
                Drawing.Color.FromArgb(190, 120, 255)
            };
            var originX = creditRow.Left + robotIcon.Width / 2;
            var originY = creditRow.Top + 4;

            authorNamesLbl.Font = authorBigFont;

            const int pieceCount = 28;
            var pieces = new List<WinForms.Control>();
            var velocities = new List<(int dx, int dy)>();
            for (var i = 0; i < pieceCount; i++)
            {
                WinForms.Control piece;
                var jitterX = robotRng.Next(-5, 6);
                var jitterY = robotRng.Next(-4, 5);

                if (i % 3 == 0)
                {
                    var coinSize = robotRng.Next(14, 19);
                    piece = new WinForms.PictureBox
                    {
                        Width = coinSize,
                        Height = coinSize,
                        BackColor = Drawing.Color.Transparent,
                        Left = originX - coinSize / 2 + jitterX,
                        Top = originY - coinSize / 2 + jitterY,
                        Image = DrawCoinBitmap(coinSize)
                    };
                }
                else
                {
                    var dotSize = robotRng.Next(12, 18);
                    piece = new WinForms.Label
                    {
                        AutoSize = false,
                        Text = "●",
                        Font = new Drawing.Font("Segoe UI", dotSize * 0.7f, Drawing.FontStyle.Bold),
                        ForeColor = confettiColors[robotRng.Next(confettiColors.Length)],
                        BackColor = Drawing.Color.Transparent,
                        Width = dotSize,
                        Height = dotSize,
                        Left = originX - dotSize / 2 + jitterX,
                        Top = originY - dotSize / 2 + jitterY
                    };
                }

                card.Controls.Add(piece);
                piece.BringToFront();
                pieces.Add(piece);
                velocities.Add((robotRng.Next(-8, 9), robotRng.Next(-13, -6)));
            }

            var frame = 0;
            const int totalFrames = 50;
            var confettiTimer = new WinForms.Timer { Interval = 28 };
            confettiTimer.Tick += (_, _) =>
            {
                frame++;
                for (var i = 0; i < pieces.Count; i++)
                {
                    pieces[i].Left += velocities[i].dx;
                    pieces[i].Top += velocities[i].dy + frame / 3;
                }
                if (frame > totalFrames)
                {
                    confettiTimer.Stop();
                    confettiTimer.Dispose();
                    foreach (var piece in pieces)
                    {
                        if (piece is WinForms.PictureBox pb) pb.Image?.Dispose();
                        piece.Dispose();
                    }
                    authorNamesLbl.Font = authorOriginalFont;
                }
            };
            confettiTimer.Start();
        }


        robotIcon.Click += (_, _) =>
        {
            robotClickCount++;
            SpinRobot();
            var msg = robotMessages[robotRng.Next(robotMessages.Length)];
            ShowRobotBubble(msg.Emoji, msg.Text);
            if (robotClickCount % 5 == 0)
                ConfettiBurst();
        };

        aboutDialog.FormClosed += (_, _) =>
        {
            robotIcon.Image?.Dispose();
            robotBubbleTimer?.Stop();
            robotBubbleTimer?.Dispose();
            authorBigFont.Dispose();
        };

        var copilotLabel = new WinForms.Label
        {
            Left = robotSize + robotTextGap,
            Top = (creditRowHeight - 22) / 2,
            Width = creditTextWidth,
            Height = 22,
            Text = creditText,
            Font = creditFont,
            ForeColor = dimColor,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleLeft
        };
        creditRow.Controls.AddRange(new WinForms.Control[] { robotIcon, copilotLabel });

        // Borderless window needs manual drag support via the card's empty background.
        bool dragging = false;
        Drawing.Point dragCursor = Drawing.Point.Empty;
        Drawing.Point dragForm = Drawing.Point.Empty;
        card.MouseDown += (s, e) =>
        {
            dragging = true;
            dragCursor = WinForms.Cursor.Position;
            dragForm = aboutDialog.Location;
        };
        card.MouseMove += (s, e) =>
        {
            if (!dragging) return;
            var diff = Drawing.Point.Subtract(WinForms.Cursor.Position, new Drawing.Size(dragCursor));
            aboutDialog.Location = Drawing.Point.Add(dragForm, new Drawing.Size(diff));
        };
        card.MouseUp += (s, e) => dragging = false;

        card.Controls.AddRange(new WinForms.Control[]
        {
            pinIcon,
            titleLabel,
            metaRow,
            separator1,
            descriptionLabel,
            featuresLabel,
            feature1, feature2, feature3, feature4, feature5,
            separator2,
            authorsHeaderLbl,
            authorNamesLbl,
            licenseLink,
            separator3,
            creditRow,
            closeButton
        });

        aboutDialog.Controls.Add(card);
        aboutDialog.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
