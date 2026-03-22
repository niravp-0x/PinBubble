using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;
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

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly string _textFilePath;
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
    private POINT _dragStartCursor;
    private System.Windows.Point _dragStartWindow;
    private bool _isPinned = true; // Default to pinned
    private bool _isDarkTheme = true; // Default to dark theme

    private static readonly SolidColorBrush BubbleDefault = new SolidColorBrush(WpfColor.FromRgb(45, 45, 48));
    private static readonly SolidColorBrush BubbleHover = new SolidColorBrush(WpfColor.FromRgb(102, 185, 51));
    private static readonly SolidColorBrush BubbleClicked = new SolidColorBrush(WpfColor.FromRgb(0, 255, 0));

    public MainWindow()
    {
        InitializeComponent();
        _textFilePath = Path.Combine(AppContext.BaseDirectory, "text.text");
        if (!InitializeSecureStore())
        {
            Close();
            return;
        }

        LoadSnippets();
        BuildBubbles();
        SetupWatcher();
        SetupTrayIcon();
        Loaded += (_, _) => SnapToNearestEdge();
        Loaded += (_, _) => UpdateTaskbarMenuText();
        Loaded += (_, _) => UpdatePinMenuText();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _expanded) Collapse(); };
    }

    private bool InitializeSecureStore()
    {
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
                var defaultContent =
                    "Label1, Your first snippet text here\n" +
                    "Label2, Your second snippet text here\n";
                EncryptedTextStore.EncryptAndSave(_textFilePath, password, defaultContent);
                _masterPassword = password;
                return true;
            }

            if (EncryptedTextStore.IsEncryptedFile(_textFilePath))
            {
                if (EncryptedTextStore.TryDecrypt(_textFilePath, password, out _))
                {
                    _masterPassword = password;
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
                return true;
            }
            catch
            {
                System.Windows.MessageBox.Show("Failed to migrate existing text file to encrypted format.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
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
            Topmost = true;
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

            var lines = plaintext
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains(','))
                .ToArray();

            _labels = new string[lines.Length];
            _fullLabels = new string[lines.Length];
            _snippets = new string[lines.Length];

            for (int i = 0; i < lines.Length; i++)
            {
                var commaIndex = lines[i].IndexOf(',');
                var labelFull = lines[i][..commaIndex].Trim();
                var value = lines[i][(commaIndex + 1)..].Trim();

                // Bubble text: uppercase A-Z and 0-9 only, max 6 chars.
                var displayLabel = BuildBubbleLabel(labelFull);
                _fullLabels[i] = labelFull;
                _labels[i] = displayLabel;
                _snippets[i] = value;
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

        // Dynamic width based on count - 5 per row
        int count = _labels.Length;
        int cols = Math.Min(count, 5);
        int rows = (int)Math.Ceiling(count / 5.0);
        double wNeeded = 25 + cols * 56 + 20;
        double hNeeded = 25 + rows * 64 + 20;

        // Store for expand
        _expandWidth = Math.Max(wNeeded, 100);
        _expandHeight = Math.Max(hNeeded, 100);

        for (int i = 0; i < count; i++)
        {
            var btn = new WpfButton
            {
                Width = 44,
                Height = 44,
                Content = _labels[i],
                FontSize = _labels[i].Length <= 3 ? 12 : 9,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
                Background = BubbleDefault,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(80, 80, 80)),
                Tag = i,
                ToolTip = i < _fullLabels.Length ? _fullLabels[i] : _labels[i],
                Cursor = WpfCursors.Hand,
                Margin = new Thickness(4)
            };

            btn.Click += Bubble_Click;
            btn.MouseEnter += (s, _) => { if (s is WpfButton b) b.Background = BubbleHover; };
            btn.MouseLeave += (s, _) => { if (s is WpfButton b) b.Background = BubbleDefault; };
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
                b.Background = BubbleClicked;
                await Task.Delay(150);
                b.Background = BubbleDefault;
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
        _expanded = true;
        Width = _expandWidth;
        Height = _expandHeight;
        Pin.Opacity = 0.3;
        BubblesHost.Visibility = Visibility.Visible;
        PositionBubbles();
    }

    private void Collapse()
    {
        _expanded = false;
        Width = 64;
        Height = 64;
        Pin.Opacity = 1.0;
        Pin.Background = new SolidColorBrush(WpfColor.FromRgb(102, 185, 51));
        BubblesHost.Visibility = Visibility.Collapsed;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _dragStartCursor);
        _dragStartWindow = new System.Windows.Point(Left, Top);
        _isDrag = false;
        CaptureMouse();
    }

    private void Root_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out POINT cur);
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;
        if (!_isDrag && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _isDrag = true;
        Left = _dragStartWindow.X + dx;
        Top = _dragStartWindow.Y + dy;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_isDrag) SnapToNearestEdge();
        else ToggleExpand();
        _isDrag = false;
    }

    private void SnapToNearestEdge()
    {
        var wa = SystemParameters.WorkArea;
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

    private void PositionBubbles()
    {
        var wa = SystemParameters.WorkArea;
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
        var originalLines = plaintext.Split('\n');
        var hasChanges = false;
        var isUpdatingProgrammatically = false;
        
        // Session-level storage for decrypted line values (maps label to full line content)
        var lineContentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Toolbar panel
        var toolbar = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 70,
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(45, 45, 48) : Drawing.Color.FromArgb(240, 240, 240)
        };

        // Status indicator - Circle
        var statusCircle = new WinForms.Panel
        {
            Left = 30,
            Top = 20,
            Width = 30,
            Height = 30,
            BackColor = Drawing.Color.Green
        };
        // Create circular appearance
        var circlePath = new System.Drawing.Drawing2D.GraphicsPath();
        circlePath.AddEllipse(0, 0, statusCircle.Width, statusCircle.Height);
        statusCircle.Region = new System.Drawing.Region(circlePath);
        toolbar.Controls.Add(statusCircle);

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

        // Button: Toggle Current Line
        var btnToggleCurrent = new WinForms.Button
        {
            Text = "Toggle Line",
            Left = 205,
            Top = 12,
            Width = 120,
            Height = 46,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(55, 55, 60) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            Font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold),
            Cursor = WinForms.Cursors.Hand
        };
        btnToggleCurrent.FlatAppearance.BorderColor = _isDarkTheme ? Drawing.Color.FromArgb(80, 80, 85) : Drawing.Color.Gray;

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
        toolbar.Controls.Add(btnToggleCurrent);
        toolbar.Controls.Add(btnSave);
        toolbar.Controls.Add(btnCancel);

        // Format instruction label (below toolbar)
        var lblFormat = new WinForms.Label
        {
            Text = "#Comment: Format > Label, Snippet text",
            Dock = WinForms.DockStyle.Top,
            Height = 25,
            TextAlign = Drawing.ContentAlignment.MiddleLeft,
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(35, 35, 40) : Drawing.Color.FromArgb(250, 250, 250),
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(140, 140, 145) : Drawing.Color.FromArgb(100, 100, 100),
            Font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Italic),
            Padding = new WinForms.Padding(10, 0, 0, 0),
            BorderStyle = WinForms.BorderStyle.FixedSingle
        };

        // Prepare editor text (no format example in the text itself)
        var editorInitialText = EncryptLines(plaintext, lineContentMap);

        // Editor
        var editor = new WinForms.RichTextBox
        {
            Dock = WinForms.DockStyle.Fill,
            ScrollBars = WinForms.RichTextBoxScrollBars.Vertical,
            AcceptsTab = true,
            WordWrap = false,
            Font = new Drawing.Font("Consolas", 10),
            BackColor = _isDarkTheme ? Drawing.Color.FromArgb(30, 30, 35) : Drawing.Color.White,
            ForeColor = _isDarkTheme ? Drawing.Color.FromArgb(220, 220, 225) : Drawing.Color.Black,
            Text = NormalizeForEditor(editorInitialText)
        };

        // Function to update status circle color
        void UpdateStatusColor()
        {
            var lines = editor.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool hasDecrypted = false;
            foreach (var line in lines)
            {
                if (!line.StartsWith("#") && !line.StartsWith("🔒") && !string.IsNullOrWhiteSpace(line) && line.Contains(","))
                {
                    hasDecrypted = true;
                    break;
                }
            }
            
            statusCircle.BackColor = hasDecrypted ? Drawing.Color.Red : Drawing.Color.Green;
        }

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
                        var editorText = NormalizeForStorage(editor.Text);
                        var lines = editorText.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();
                        editorText = string.Join("\n", lines).Trim();
                        var finalText = DecryptAllLines(editorText, lineContentMap);
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword, finalText);
                        
                        // Reload plaintext with saved changes
                        if (EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out plaintext))
                        {
                            hasChanges = false;
                            btnSave.Visible = false;
                            lineContentMap.Clear(); // Clear old mappings
                        }
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
            if (isDecrypted)
            {
                // Show decrypted
                isUpdatingProgrammatically = true;
                btnDecryptAll.Text = "Hide All";
                btnDecryptAll.Visible = true; // Hide All should always be visible
                btnDecryptAll.Enabled = true;
                editor.Text = NormalizeForEditor(plaintext);
                isUpdatingProgrammatically = false;
            }
            else
            {
                // Show encrypted
                isUpdatingProgrammatically = true;
                btnDecryptAll.Text = "Show All";
                btnDecryptAll.Visible = false; // Show All requires Control key
                btnDecryptAll.Enabled = false;
                var currentText = NormalizeForStorage(editor.Text);
                if (!string.IsNullOrEmpty(currentText.Trim()))
                    editor.Text = NormalizeForEditor(EncryptLines(currentText, lineContentMap));
                isUpdatingProgrammatically = false;
            }
            UpdateStatusColor();
        };

        // Toggle Current Line button click handler
        btnToggleCurrent.Click += (s, ev) =>
        {
            var selectionStart = editor.SelectionStart;
            var lines = editor.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            
            // Find current line
            int currentPos = 0;
            int lineIndex = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                int lineLength = lines[i].Length + Environment.NewLine.Length;
                if (currentPos + lines[i].Length >= selectionStart)
                {
                    lineIndex = i;
                    break;
                }
                currentPos += lineLength;
            }

            if (lineIndex < lines.Length && !lines[lineIndex].StartsWith("#"))
            {
                var line = lines[lineIndex];
                if (line.StartsWith("🔒 "))
                {
                    // Decrypt this line
                    lines[lineIndex] = DecryptLine(line, lineContentMap);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Encrypt this line
                    lines[lineIndex] = EncryptLine(line, lineContentMap);
                }

                isUpdatingProgrammatically = true;
                editor.Text = string.Join(Environment.NewLine, lines);
                editor.SelectionStart = selectionStart;
                editor.ScrollToCaret();
                isUpdatingProgrammatically = false;
                UpdateStatusColor();
            }
        };

        // Track text changes
        editor.TextChanged += (s, ev) =>
        {
            if (!isUpdatingProgrammatically)
            {
                hasChanges = true;
                btnSave.Visible = true;
            }
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
                        var editorText = NormalizeForStorage(editor.Text);
                        var lines = editorText.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();
                        editorText = string.Join("\n", lines).Trim();
                        var finalText = DecryptAllLines(editorText, lineContentMap);
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword, finalText);
                        hasChanges = false; // Prevent FormClosing from prompting again
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
                    hasChanges = false; // Prevent FormClosing from prompting again
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

        // Control key handling for Show All button visibility
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
                        var editorText = NormalizeForStorage(editor.Text);
                        var lines = editorText.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();
                        editorText = string.Join("\n", lines).Trim();
                        var finalText = DecryptAllLines(editorText, lineContentMap);
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword, finalText);
                        
                        // Reload plaintext with saved changes
                        if (EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out plaintext))
                        {
                            hasChanges = false;
                            btnSave.Visible = false;
                            lineContentMap.Clear();
                            
                            // Reload the editor content with updated data
                            isUpdatingProgrammatically = true;
                            if (isDecrypted)
                            {
                                editor.Text = NormalizeForEditor(plaintext);
                            }
                            else
                            {
                                editor.Text = NormalizeForEditor(EncryptLines(plaintext, lineContentMap));
                            }
                            isUpdatingProgrammatically = false;
                            
                            WinForms.MessageBox.Show("Saved successfully.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                        }
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                    }
                }
                return; // Don't process other Control key logic
            }
            
            // Handle Escape key to cancel/close
            if (ev.KeyCode == WinForms.Keys.Escape)
            {
                ev.SuppressKeyPress = true;
                btnCancel.PerformClick(); // Trigger the Cancel button click which handles unsaved changes
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
                        var editorText = NormalizeForStorage(editor.Text);
                        var lines = editorText.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();
                        editorText = string.Join("\n", lines).Trim();
                        var finalText = DecryptAllLines(editorText, lineContentMap);
                        EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword, finalText);
                    }
                    catch
                    {
                        WinForms.MessageBox.Show("Failed to save changes.", "PinBubble", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                        ((WinForms.FormClosingEventArgs)ev).Cancel = true;
                    }
                }
            }
        };

        dialog.Controls.Add(editor);
        dialog.Controls.Add(lblFormat);
        dialog.Controls.Add(toolbar);
        dialog.AcceptButton = btnSave;

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var editorText = NormalizeForStorage(editor.Text);
            // Remove all comment lines (lines starting with #)
            var lines = editorText.Split('\n').Where(l => !l.TrimStart().StartsWith("#")).ToArray();
            editorText = string.Join("\n", lines).Trim();
            // Decrypt any encrypted lines before saving
            var finalText = DecryptAllLines(editorText, lineContentMap);
            EncryptedTextStore.EncryptAndSave(_textFilePath, _masterPassword, finalText);
            LoadSnippets();
            BuildBubbles();
        }
        catch
        {
            System.Windows.MessageBox.Show("Failed to save encrypted snippets.", "PinBubble", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string EncryptLines(string text, Dictionary<string, string> lineContentMap)
    {
        var lines = text.Split('\n');
        var encrypted = new StringBuilder();
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                encrypted.AppendLine(EncryptLine(line.Trim(), lineContentMap));
            else
                encrypted.AppendLine();
        }
        return encrypted.ToString().TrimEnd('\r', '\n');
    }

    private string EncryptLine(string line, Dictionary<string, string> lineContentMap)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        if (line.StartsWith("🔒 ")) return line; // Already encrypted
        
        // Store the original line in the map before encrypting
        var commaIndex = line.IndexOf(',');
        if (commaIndex > 0)
        {
            var label = line[..commaIndex].Trim();
            lineContentMap[label] = line; // Store the full line
            return $"🔒 {label}, ••••••••";
        }
        return $"🔒 {line}";
    }

    private string DecryptLine(string line, Dictionary<string, string> lineContentMap)
    {
        if (!line.StartsWith("🔒 ")) return line;
        
        var withoutLock = line.Substring(2).Trim();
        var commaIndex = withoutLock.IndexOf(',');
        if (commaIndex > 0)
        {
            var label = withoutLock[..commaIndex].Trim();
            
            // First check the session dictionary
            if (lineContentMap.TryGetValue(label, out var storedLine))
                return storedLine;
            
            // Fall back to reading from file (for lines that existed before this session)
            if (!string.IsNullOrEmpty(_masterPassword))
            {
                if (EncryptedTextStore.TryDecrypt(_textFilePath, _masterPassword, out var plaintext))
                {
                    var originalLines = plaintext.Split('\n');
                    foreach (var origLine in originalLines)
                    {
                        if (origLine.Trim().StartsWith(label + ","))
                        {
                            var decryptedLine = origLine.Trim();
                            lineContentMap[label] = decryptedLine; // Cache it
                            return decryptedLine;
                        }
                    }
                }
            }
        }
        return withoutLock;
    }

    private string DecryptAllLines(string text, Dictionary<string, string> lineContentMap)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var decrypted = new StringBuilder();
        
        foreach (var line in lines)
        {
            if (line.StartsWith("🔒 "))
            {
                decrypted.AppendLine(DecryptLine(line, lineContentMap));
            }
            else
            {
                decrypted.AppendLine(line);
            }
        }
        return decrypted.ToString().TrimEnd('\r', '\n');
    }

    private static string NormalizeForEditor(string value)
    {
        // WinForms multiline text boxes reliably display CRLF line endings.
        return value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private static string NormalizeForStorage(string value)
    {
        return value.Replace("\r\n", "\n");
    }

    private void ToggleTaskbar_Click(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = !ShowInTaskbar;
        UpdateTaskbarMenuText();
        
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
    }

    private void UpdatePinMenuText()
    {
        PinToggleMenuItem.Header = _isPinned ? "Unpin the Bubble" : "Pin the Bubble";
    }

    private void ToggleDarkTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Allow normal minimize/restore behavior when clicking taskbar icon
        // Window will minimize when clicked, and restore when clicked again
    }

    protected override void OnClosed(EventArgs e)
    {
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
