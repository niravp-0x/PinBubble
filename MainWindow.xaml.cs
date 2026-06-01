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
}

// Lightweight DTO for JSON persistence (no UI-only fields)
class SnippetEntry
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime ExpiryDate { get; set; } = DateTime.Now.AddDays(30);
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
                ExpiryDate = r.ExpiryDate
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
                ExpiryDate = e.ExpiryDate
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

    private const double DefaultBackdropOpacity = 0.50;
    private const bool DefaultIsPinned = true;
    private const bool DefaultIsDarkTheme = true;
    private const bool DefaultShowInTaskbar = true;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly string _textFilePath;
    private readonly string _settingsFilePath;
    private string? _masterPassword;
    private string[] _labels = Array.Empty<string>();
    private string[] _fullLabels = Array.Empty<string>();
    private string[] _snippets = Array.Empty<string>();
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
    private int _clipboardClearSeconds = 0;

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
        public int ClipboardClearSeconds { get; set; } = 0;
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
                return;
            }

            if (!EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out var plaintext))
            {
                _labels = Array.Empty<string>();
                _fullLabels = Array.Empty<string>();
                _snippets = Array.Empty<string>();
                return;
            }

            var parsed = ParseSnippets(plaintext);

            _labels = new string[parsed.Count];
            _fullLabels = new string[parsed.Count];
            _snippets = new string[parsed.Count];

            for (int i = 0; i < parsed.Count; i++)
            {
                var displayLabel = BuildBubbleLabel(parsed[i].Label);
                _fullLabels[i] = parsed[i].Label;
                _labels[i] = displayLabel;
                _snippets[i] = parsed[i].ActualValue;
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
                System.Windows.Clipboard.SetText(_snippets[idx]);
                ScheduleClipboardClear(_snippets[idx]);
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

    private void HandleHotkey(int id, IntPtr prevHwnd)
    {
        if (id == HotkeyIdQwertyPicker)
        {
            ShowQwertyPicker(prevHwnd);
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
            System.Windows.Clipboard.SetText(_snippets[snippetIdx]);
            ScheduleClipboardClear(_snippets[snippetIdx]);
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
            Width = 680,
            Height = 480,
            MinimumSize = new Drawing.Size(580, 380),
            FormBorderStyle = WinForms.FormBorderStyle.Sizable,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Text = "PinBubble – Manage Shortcuts",
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black
        };

        // ── Toolbar ──────────────────────────────────────────────────────────
        var toolbar = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 64,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(40, 40, 44) : Drawing.Color.FromArgb(240, 240, 240)
        };

        var infoLbl = new WinForms.Label
        {
            Left = 14, Top = 10, Width = 254, Height = 44,
            Text = "Shortcuts copy a snippet instantly.\nCtrl+Alt+P always opens the QWERTY picker.",
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(130, 130, 140) : Drawing.Color.Gray,
            Font = new Drawing.Font("Segoe UI", 8.5f),
            BackColor = Drawing.Color.Transparent
        };

        WinForms.Button MakeToolBtn(string text, Drawing.Color bg, int left) => new WinForms.Button
        {
            Text = text, Left = left, Top = 12, Width = 85, Height = 40,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Drawing.Color.White,
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };

        var btnAdd    = MakeToolBtn("＋ Add",    Drawing.Color.FromArgb(0, 110, 70),  272);
        var btnRemove = MakeToolBtn("✕ Remove",  Drawing.Color.FromArgb(130, 40, 40), 364);
        var btnSave   = MakeToolBtn("Save",      Drawing.Color.FromArgb(0, 100, 180), 456);
        var btnClose  = MakeToolBtn("Close",     Drawing.Color.FromArgb(60, 60, 68),  548);

        foreach (var b in new[] { btnAdd, btnRemove, btnSave, btnClose })
        {
            b.FlatAppearance.BorderSize = 0;
            toolbar.Controls.Add(b);
        }
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
            BorderStyle = WinForms.BorderStyle.None
        };
        list.Columns.Add("Shortcut", 200);
        list.Columns.Add("Linked Snippet", -2);

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

        // ── Add shortcut ─────────────────────────────────────────────────────
        btnAdd.Click += (_, _) =>
        {
            // Step 1: Record key combo
            (int mods, int vk, string display)? recorded = null;

            using var recorder = new WinForms.Form
            {
                Width = 420, Height = 180,
                FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
                StartPosition = WinForms.FormStartPosition.CenterParent,
                Text = "Record Shortcut",
                MaximizeBox = false, MinimizeBox = false, TopMost = true,
                KeyPreview = true,
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White
            };

            var recLabel = new WinForms.Label
            {
                Left = 20, Top = 20, Width = 380, Height = 30,
                Text = "Press your desired key combination…",
                Font = new Drawing.Font("Segoe UI", 10f),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
                BackColor = Drawing.Color.Transparent
            };
            var recDisplay = new WinForms.Label
            {
                Left = 20, Top = 56, Width = 380, Height = 36,
                Text = "",
                Font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold),
                ForeColor = Drawing.Color.FromArgb(0, 180, 120),
                BackColor = Drawing.Color.Transparent,
                TextAlign = Drawing.ContentAlignment.MiddleCenter
            };
            var recUse = new WinForms.Button
            {
                Text = "Use This", Left = 220, Top = 100, Width = 85, Height = 38,
                DialogResult = WinForms.DialogResult.OK,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(0, 110, 70),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Enabled = false, Cursor = WinForms.Cursors.Hand
            };
            recUse.FlatAppearance.BorderSize = 0;
            var recCancel = new WinForms.Button
            {
                Text = "Cancel", Left = 314, Top = 100, Width = 85, Height = 38,
                DialogResult = WinForms.DialogResult.Cancel,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(60, 60, 68),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand
            };
            recCancel.FlatAppearance.BorderSize = 0;
            recorder.Controls.AddRange(new WinForms.Control[] { recLabel, recDisplay, recUse, recCancel });
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
                Width = 400, Height = 170,
                FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
                StartPosition = WinForms.FormStartPosition.CenterParent,
                Text = "Link to Snippet",
                MaximizeBox = false, MinimizeBox = false, TopMost = true,
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White
            };

            var pickLabel = new WinForms.Label
            {
                Left = 20, Top = 20, Width = 360, Height = 22,
                Text = $"Link  {recorded.Value.display}  to:",
                Font = new Drawing.Font("Segoe UI", 10f),
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
                BackColor = Drawing.Color.Transparent
            };
            var combo = new WinForms.ComboBox
            {
                Left = 20, Top = 50, Width = 360, Height = 30,
                DropDownStyle = WinForms.ComboBoxStyle.DropDownList,
                Font = new Drawing.Font("Segoe UI", 10f),
                BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 50) : Drawing.Color.White,
                ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black
            };
            combo.Items.AddRange(_fullLabels.Cast<object>().ToArray());
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;

            var pickOk = new WinForms.Button
            {
                Text = "Link", Left = 200, Top = 95, Width = 85, Height = 38,
                DialogResult = WinForms.DialogResult.OK,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(0, 110, 70),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand
            };
            pickOk.FlatAppearance.BorderSize = 0;
            var pickCancel = new WinForms.Button
            {
                Text = "Cancel", Left = 295, Top = 95, Width = 85, Height = 38,
                DialogResult = WinForms.DialogResult.Cancel,
                FlatStyle = WinForms.FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(60, 60, 68),
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
                Cursor = WinForms.Cursors.Hand
            };
            pickCancel.FlatAppearance.BorderSize = 0;
            snippetPicker.Controls.AddRange(new WinForms.Control[] { pickLabel, combo, pickOk, pickCancel });
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
            dlg.DialogResult = WinForms.DialogResult.OK;
            dlg.Close();
        };

        // ── Close ─────────────────────────────────────────────────────────────
        btnClose.Click += (_, _) => { dlg.DialogResult = WinForms.DialogResult.Cancel; dlg.Close(); };
        dlg.KeyDown += (_, ke) => { if (ke.KeyCode == WinForms.Keys.Escape) btnClose.PerformClick(); };

        dlg.Controls.Add(list);
        dlg.Controls.Add(toolbar);
        dlg.ShowDialog();
    }

    // ── QWERTY quick-paste picker ────────────────────────────────────────────

    private void ShowQwertyPicker(IntPtr prevHwnd)
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
                System.Windows.Clipboard.SetText(copiedSnippet);
                ScheduleClipboardClear(copiedSnippet);
            }
            catch { }
        }

        if (prevHwnd != IntPtr.Zero)
            SetForegroundWindow(prevHwnd);
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
            Width = 800,
            Height = 600,
            FormBorderStyle = WinForms.FormBorderStyle.Sizable,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Text = "PinBubble - Edit Snippets",
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black
        };

        var isDecrypted = false;
        var hasChanges = false;
        var isUpdatingProgrammatically = false;
        
        // Toolbar panel
        var toolbar = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 70,
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 48) : Drawing.Color.FromArgb(240, 240, 240)
        };

        // Status indicator - LED Light
        var statusLED = new WinForms.PictureBox
        {
            Left = 30,
            Top = 20,
            Width = 30,
            Height = 30,
            BackColor = Drawing.Color.Transparent
        };
        
        // Function to draw LED with given color
        void DrawLED(Drawing.Color color)
        {
            var ledBitmap = new System.Drawing.Bitmap(30, 30);
            using (var g = System.Drawing.Graphics.FromImage(ledBitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                // Outer dark ring
                using (var brush = new System.Drawing.SolidBrush(Drawing.Color.FromArgb(40, 40, 45)))
                {
                    g.FillEllipse(brush, 0, 0, 30, 30);
                }
                
                // Main LED body
                using (var brush = new System.Drawing.SolidBrush(color))
                {
                    g.FillEllipse(brush, 3, 3, 24, 24);
                }
                
                // Inner glow effect
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddEllipse(6, 6, 18, 18);
                    using (var pgb = new System.Drawing.Drawing2D.PathGradientBrush(path))
                    {
                        pgb.CenterPoint = new System.Drawing.PointF(15, 15);
                        pgb.CenterColor = Drawing.Color.FromArgb(180, 255, 255, 255);
                        pgb.SurroundColors = new[] { Drawing.Color.FromArgb(0, 255, 255, 255) };
                        g.FillEllipse(pgb, 6, 6, 18, 18);
                    }
                }
                
                // Highlight (glossy effect)
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(8, 8, 10, 8),
                    Drawing.Color.FromArgb(200, 255, 255, 255),
                    Drawing.Color.FromArgb(0, 255, 255, 255),
                    45f))
                {
                    g.FillEllipse(brush, 8, 8, 10, 8);
                }
            }
            statusLED.Image = ledBitmap;
        }
        
        // Initialize with green LED
        DrawLED(Drawing.Color.FromArgb(0, 220, 0));
        toolbar.Controls.Add(statusLED);

        // Button: Decrypt All (toggle) - only visible when Control key is held
        var btnDecryptAll = new WinForms.Button
        {
            Text = "Show All",
            Left = 80,
            Top = 12,
            Width = 110,
            Height = 46,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(55, 55, 60) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            Font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            Visible = false,
            Enabled = false
        };
        btnDecryptAll.FlatAppearance.BorderColor = _isDarkTheme ? Drawing.Color.FromArgb(80, 80, 85) : Drawing.Color.Gray;

        // Button: Save - only visible when changes are made
        var btnSave = new WinForms.Button
        {
            Text = "Save",
            Left = 580,
            Top = 12,
            Width = 90,
            Height = 46,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 100, 70) : Drawing.Color.LightGreen,
            ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.Black,
            Font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand,
            DialogResult = WinForms.DialogResult.OK,
            Visible = false
        };
        btnSave.FlatAppearance.BorderColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.DarkGreen;

        // Button: Cancel
        var btnCancel = new WinForms.Button
        {
            Text = "Cancel",
            Left = 680,
            Top = 12,
            Width = 90,
            Height = 46,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(80, 40, 40) : Drawing.Color.LightCoral,
            ForeColor = _isDarkTheme ? Drawing.Color.White : Drawing.Color.Black,
            Font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderColor = _isDarkTheme ? Drawing.Color.FromArgb(100, 50, 50) : Drawing.Color.DarkRed;

        toolbar.Controls.Add(btnDecryptAll);
        toolbar.Controls.Add(btnSave);
        toolbar.Controls.Add(btnCancel);

        // Parse snippets from JSON (or legacy comma-delimited format)
        var snippetRows = ParseSnippets(plaintext);

        // Create DataGridView
        var grid = new WinForms.DataGridView
        {
            Dock = WinForms.DockStyle.Fill,
            BackgroundColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            GridColor = _isDarkTheme ? Drawing.Color.FromArgb(60, 60, 65) : Drawing.Color.Gray,
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
        grid.ColumnHeadersDefaultCellStyle.ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black;
        grid.EnableHeadersVisualStyles = false;
        grid.DefaultCellStyle.BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White;
        grid.DefaultCellStyle.ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black;
        grid.DefaultCellStyle.SelectionBackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 100, 150) : Drawing.Color.LightBlue;
        grid.DefaultCellStyle.SelectionForeColor = Drawing.Color.White;
        grid.RowHeadersDefaultCellStyle.BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 48) : Drawing.Color.FromArgb(240, 240, 240);

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

        grid.Columns.Add(colAge);
        grid.Columns.Add(colLabel);
        grid.Columns.Add(colValue);
        grid.Columns.Add(colToggle);
        
        // Increase header height for cleaner look
        grid.ColumnHeadersHeight = 35;

        // Populate grid - Age column will be empty, clock icon shown via painting
        foreach (var row in snippetRows)
        {
            grid.Rows.Add("", row.Label, row.Value);
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
            
            if ((ev.ColumnIndex == 0 || ev.ColumnIndex == 3) && ev.RowIndex >= 0)
            {
                hoveredRowIndex = ev.RowIndex;
                hoveredColumnIndex = ev.ColumnIndex;
                grid.InvalidateCell(ev.ColumnIndex, ev.RowIndex);
            }
        };
        
        grid.CellMouseLeave += (s, ev) =>
        {
            if ((ev.ColumnIndex == 0 || ev.ColumnIndex == 3) && ev.RowIndex >= 0)
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
                snippetRow = new SnippetRow { IsEncrypted = true, Created = DateTime.Now, Modified = DateTime.Now, ExpiryDate = DateTime.Now.AddDays(30) };
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
                    
                    // Show a dialog with summary and date picker
                    using var inputForm = new WinForms.Form
                    {
                        Width = 450,
                        Height = 420,
                        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
                        Text = "Expiry Date",
                        StartPosition = WinForms.FormStartPosition.CenterParent,
                        MaximizeBox = false,
                        MinimizeBox = false,
                        TopMost = true,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
                        Padding = new WinForms.Padding(25)
                    };
                    
                    // Entry label
                    var entryLabel = new WinForms.Label
                    {
                        Left = 30,
                        Top = 30,
                        Width = 380,
                        Height = 35,
                        Text = $"Entry: {snippetRow.Label}",
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
                        Font = new Drawing.Font("Segoe UI", 11f, Drawing.FontStyle.Bold),
                        AutoSize = false
                    };
                    
                    var label1 = new WinForms.Label
                    {
                        Left = 30,
                        Top = 75,
                        Width = 380,
                        Height = 30,
                        Text = $"First Added: {snippetRow.Created:yyyy-MM-dd HH:mm}",
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                        Font = new Drawing.Font("Segoe UI", 10f),
                        AutoSize = false
                    };
                    
                    var label2 = new WinForms.Label
                    {
                        Left = 30,
                        Top = 110,
                        Width = 380,
                        Height = 30,
                        Text = $"Last Modified: {snippetRow.Modified:yyyy-MM-dd HH:mm}",
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                        Font = new Drawing.Font("Segoe UI", 10f),
                        AutoSize = false
                    };
                    
                    var label3 = new WinForms.Label
                    {
                        Left = 30,
                        Top = 145,
                        Width = 380,
                        Height = 30,
                        Text = expiryText,
                        ForeColor = daysUntilExpiry < 7 ? Drawing.Color.FromArgb(255, 100, 100) : 
                                   (daysUntilExpiry <= 14 ? Drawing.Color.FromArgb(255, 200, 100) : 
                                   Drawing.Color.FromArgb(100, 200, 100)),
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        AutoSize = false
                    };
                    
                    var separatorLabel = new WinForms.Label
                    {
                        Left = 30,
                        Top = 195,
                        Width = 380,
                        Height = 30,
                        Text = "Set new expiry date:",
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(160, 160, 165) : Drawing.Color.DarkGray,
                        Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold)
                    };
                    
                    var datePicker = new WinForms.DateTimePicker
                    {
                        Left = 30,
                        Top = 235,
                        Width = 380,
                        Height = 35,
                        Font = new Drawing.Font("Segoe UI", 11f),
                        Format = WinForms.DateTimePickerFormat.Short,
                        Value = snippetRow.ExpiryDate < DateTime.Now ? DateTime.Now.AddDays(30) : snippetRow.ExpiryDate,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 50) : Drawing.Color.White,
                        ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                        CalendarForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
                        CalendarMonthBackground = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 50) : Drawing.Color.White
                    };
                    
                    var btnOk = new WinForms.Button
                    {
                        Text = "Update",
                        Left = 220,
                        Top = 300,
                        Width = 90,
                        Height = 45,
                        DialogResult = WinForms.DialogResult.OK,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(0, 120, 80) : Drawing.Color.LightGreen,
                        ForeColor = Drawing.Color.White,
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand
                    };
                    btnOk.FlatAppearance.BorderSize = 0;
                    
                    var btnCancel = new WinForms.Button
                    {
                        Text = "Cancel",
                        Left = 320,
                        Top = 300,
                        Width = 90,
                        Height = 45,
                        DialogResult = WinForms.DialogResult.Cancel,
                        BackColor = _isDarkTheme ? Drawing.Color.FromArgb(80, 40, 40) : Drawing.Color.LightCoral,
                        ForeColor = Drawing.Color.White,
                        FlatStyle = WinForms.FlatStyle.Flat,
                        Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold),
                        Cursor = WinForms.Cursors.Hand
                    };
                    btnCancel.FlatAppearance.BorderSize = 0;
                    
                    inputForm.Controls.Add(entryLabel);
                    inputForm.Controls.Add(label1);
                    inputForm.Controls.Add(label2);
                    inputForm.Controls.Add(label3);
                    inputForm.Controls.Add(separatorLabel);
                    inputForm.Controls.Add(datePicker);
                    inputForm.Controls.Add(btnOk);
                    inputForm.Controls.Add(btnCancel);
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

        dialog.Controls.Add(grid);
        dialog.Controls.Add(toolbar);
        dialog.AcceptButton = btnSave;

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

        Topmost = _isPinned;
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
        ApplyExpandedPanelTheme();
        BuildBubbles();
        UpdateBackdropOpacityMenuChecks();
        UpdatePinMenuText();
        UpdateTaskbarMenuText();
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
                ClipboardClearSeconds = _clipboardClearSeconds
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

    private void About_Click(object sender, RoutedEventArgs e)
    {
        using var aboutDialog = new WinForms.Form
        {
            Width = 500,
            Height = 520,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            Text = "About PinBubble",
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            KeyPreview = true
        };

        aboutDialog.KeyDown += (s, e) =>
        {
            if (e.KeyCode == WinForms.Keys.Escape)
                aboutDialog.Close();
        };

        // Pin icon using custom drawing
        var pinIcon = new WinForms.PictureBox
        {
            Left = 210,
            Top = 15,
            Width = 60,
            Height = 60,
            BackColor = Drawing.Color.Transparent
        };
        
        var pinBitmap = new System.Drawing.Bitmap(60, 60);
        using (var g = System.Drawing.Graphics.FromImage(pinBitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var pinColor = _isDarkTheme ? Drawing.Color.FromArgb(102, 185, 51) : Drawing.Color.FromArgb(80, 150, 40);
            
            // Draw pin head (circle)
            using (var brush = new System.Drawing.SolidBrush(pinColor))
            {
                g.FillEllipse(brush, 15, 5, 30, 30);
            }
            
            // Draw pin point (triangle)
            using (var brush = new System.Drawing.SolidBrush(pinColor))
            {
                var points = new System.Drawing.Point[] 
                {
                    new System.Drawing.Point(26, 35),
                    new System.Drawing.Point(34, 35),
                    new System.Drawing.Point(30, 52)
                };
                g.FillPolygon(brush, points);
            }
        }
        pinIcon.Image = pinBitmap;

        // Title
        var titleLabel = new WinForms.Label
        {
            Left = 20,
            Top = 80,
            Width = 460,
            Height = 35,
            Text = "PinBubble",
            Font = new Drawing.Font("Segoe UI", 24f, Drawing.FontStyle.Bold),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        // Version
        var versionLabel = new WinForms.Label
        {
            Left = 20,
            Top = 118,
            Width = 460,
            Height = 20,
            Text = "Version 1.0.0",
            Font = new Drawing.Font("Segoe UI", 9f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(160, 160, 165) : Drawing.Color.FromArgb(100, 100, 100),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleCenter
        };

        // Separator line
        var separator1 = new WinForms.Panel
        {
            Left = 20,
            Top = 148,
            Width = 440,
            Height = 1,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(70, 70, 75) : Drawing.Color.FromArgb(200, 200, 200)
        };

        // Description
        var descriptionLabel = new WinForms.Label
        {
            Left = 30,
            Top = 158,
            Width = 440,
            Height = 45,
            Text = "A lightweight, always-on-screen snippet manager\nthat keeps your frequently used text snippets\nat your fingertips.",
            Font = new Drawing.Font("Segoe UI", 9f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.TopCenter
        };

        // Features header - more spacing above
        var featuresLabel = new WinForms.Label
        {
            Left = 40,
            Top = 225,
            Width = 420,
            Height = 20,
            Text = "KEY FEATURES",
            Font = new Drawing.Font("Segoe UI", 8.5f, Drawing.FontStyle.Bold),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(102, 185, 51) : Drawing.Color.FromArgb(80, 150, 40),
            BackColor = Drawing.Color.Transparent
        };

        // Features list with better spacing - increased height to 22px each
        var feature1 = new WinForms.Label
        {
            Left = 60,
            Top = 253,
            Width = 420,
            Height = 22,
            Text = "> Encrypted snippet storage with master password",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent
        };

        var feature2 = new WinForms.Label
        {
            Left = 60,
            Top = 280,
            Width = 420,
            Height = 22,
            Text = "> Pin/unpin to stay on top of other windows",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent
        };

        var feature3 = new WinForms.Label
        {
            Left = 60,
            Top = 307,
            Width = 420,
            Height = 22,
            Text = "> Dark theme support for comfortable viewing",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent
        };

        var feature4 = new WinForms.Label
        {
            Left = 60,
            Top = 334,
            Width = 420,
            Height = 22,
            Text = "> Quick copy snippets with a single click",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent
        };

        var feature5 = new WinForms.Label
        {
            Left = 60,
            Top = 361,
            Width = 420,
            Height = 22,
            Text = "> Line-by-line encryption controls",
            Font = new Drawing.Font("Segoe UI", 8.5f),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(200, 200, 205) : Drawing.Color.Black,
            BackColor = Drawing.Color.Transparent
        };

        // Separator line 2
        var separator2 = new WinForms.Panel
        {
            Left = 20,
            Top = 400,
            Width = 440,
            Height = 1,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(70, 70, 75) : Drawing.Color.FromArgb(200, 200, 200)
        };

        // Robot icon for copilot
        var robotIcon = new WinForms.PictureBox
        {
            Left = 75,
            Top = 425,
            Width = 18,
            Height = 18,
            BackColor = Drawing.Color.Transparent
        };
        
        var robotBitmap = new System.Drawing.Bitmap(18, 18);
        using (var g = System.Drawing.Graphics.FromImage(robotBitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var robotColor = _isDarkTheme ? Drawing.Color.FromArgb(140, 140, 145) : Drawing.Color.FromArgb(120, 120, 120);
            
            // Draw robot head (rectangle)
            using (var brush = new System.Drawing.SolidBrush(robotColor))
            {
                g.FillRectangle(brush, 3, 5, 12, 10);
            }
            
            // Draw robot eyes (two small circles)
            using (var brush = new System.Drawing.SolidBrush(_isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White))
            {
                g.FillEllipse(brush, 6, 8, 3, 3);
                g.FillEllipse(brush, 11, 8, 3, 3);
            }
            
            // Draw antenna
            using (var pen = new System.Drawing.Pen(robotColor, 1.5f))
            {
                g.DrawLine(pen, 9, 2, 9, 5);
            }
            using (var brush = new System.Drawing.SolidBrush(robotColor))
            {
                g.FillEllipse(brush, 7, 0, 4, 4);
            }
        }
        robotIcon.Image = robotBitmap;

        // Copilot credit
        var copilotLabel = new WinForms.Label
        {
            Left = 98,
            Top = 425,
            Width = 330,
            Height = 22,
            Text = "Proudly vibecoded with GitHub Copilot",
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Italic),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(140, 140, 145) : Drawing.Color.FromArgb(120, 120, 120),
            BackColor = Drawing.Color.Transparent,
            TextAlign = Drawing.ContentAlignment.MiddleLeft
        };

        aboutDialog.Controls.Add(pinIcon);
        aboutDialog.Controls.Add(titleLabel);
        aboutDialog.Controls.Add(versionLabel);
        aboutDialog.Controls.Add(separator1);
        aboutDialog.Controls.Add(descriptionLabel);
        aboutDialog.Controls.Add(featuresLabel);
        aboutDialog.Controls.Add(feature1);
        aboutDialog.Controls.Add(feature2);
        aboutDialog.Controls.Add(feature3);
        aboutDialog.Controls.Add(feature4);
        aboutDialog.Controls.Add(feature5);
        aboutDialog.Controls.Add(separator2);
        aboutDialog.Controls.Add(robotIcon);
        aboutDialog.Controls.Add(copilotLabel);

        aboutDialog.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
