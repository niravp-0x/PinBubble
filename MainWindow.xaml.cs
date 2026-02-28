using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PinBubble;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly string _textFilePath;
    private string[] _labels = Array.Empty<string>();
    private string[] _snippets = Array.Empty<string>();
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadDebounce;
    private bool _expanded;
    private bool _isDrag;
    private POINT _dragStartCursor;
    private System.Windows.Point _dragStartWindow;

    private static readonly SolidColorBrush BubbleDefault = new SolidColorBrush(Color.FromRgb(45, 45, 48));
    private static readonly SolidColorBrush BubbleHover = new SolidColorBrush(Color.FromRgb(102, 185, 51));
    private static readonly SolidColorBrush BubbleClicked = new SolidColorBrush(Color.FromRgb(0, 255, 0));

    public MainWindow()
    {
        InitializeComponent();
        _textFilePath = Path.Combine(AppContext.BaseDirectory, "text.text");
        EnsureFileExists();
        LoadSnippets();
        BuildBubbles();
        SetupWatcher();
        Loaded += (_, _) => SnapToNearestEdge();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _expanded) Collapse(); };
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_expanded) Collapse();
        Topmost = true;
    }

    private void EnsureFileExists()
    {
        if (!File.Exists(_textFilePath))
            File.WriteAllText(_textFilePath,
                "Label1, Your first snippet text here\n" +
                "Label2, Your second snippet text here\n");
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
            var lines = File.ReadAllLines(_textFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains(','))
                .ToArray();

            _labels = new string[lines.Length];
            _snippets = new string[lines.Length];

            for (int i = 0; i < lines.Length; i++)
            {
                var commaIndex = lines[i].IndexOf(',');
                var labelFull = lines[i][..commaIndex].Trim();
                var value = lines[i][(commaIndex + 1)..].Trim();

                // Truncate label to 3 chars
                _labels[i] = labelFull.Length > 3 ? labelFull[..3].ToUpper() : labelFull.ToUpper();
                _snippets[i] = value;
            }
        }
        catch { }
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
            var btn = new Button
            {
                Width = 44,
                Height = 44,
                Content = _labels[i],
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = BubbleDefault,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Tag = i,
                Cursor = Cursors.Hand,
                Margin = new Thickness(4)
            };

            btn.Click += Bubble_Click;
            btn.MouseEnter += (s, _) => { if (s is Button b) b.Background = BubbleHover; };
            btn.MouseLeave += (s, _) => { if (s is Button b) b.Background = BubbleDefault; };
            btn.Template = CreateRoundButtonTemplate();
            BubblesHost.Children.Add(btn);
        }
    }

    private double _expandWidth = 340;
    private double _expandHeight = 160;

    private ControlTemplate CreateRoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.EffectProperty, new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private async void Bubble_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int idx && idx < _snippets.Length)
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
        Pin.Background = new SolidColorBrush(Color.FromRgb(102, 185, 51));
        BubblesHost.Visibility = Visibility.Collapsed;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _dragStartCursor);
        _dragStartWindow = new System.Windows.Point(Left, Top);
        _isDrag = false;
        CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
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

    private void OpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        EnsureFileExists();
        try { Process.Start(new ProcessStartInfo("code", $"\"{_textFilePath}\"") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_textFilePath}\"") { UseShellExecute = true }); }
    }

    private void OpenInNotepad_Click(object sender, RoutedEventArgs e)
    {
        EnsureFileExists();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_textFilePath}\"") { UseShellExecute = true });
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        EnsureFileExists();
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_textFilePath}\"") { UseShellExecute = true });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
