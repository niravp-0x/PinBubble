using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PinBubble;

public partial class MainWindow : Window
{
    private readonly string _textFilePath;
    private readonly string[] _labels = { "ONE", "TWO", "THR", "FOR", "FIV", "SIX", "SEV", "EIG", "NIN", "TEN" };
    private string[] _snippets = new string[10];
    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadDebounce;
    private bool _expanded;
    private bool _dragging;
    private System.Windows.Point _dragStartMouse;
    private System.Windows.Point _dragStartWindow;

    public MainWindow()
    {
        InitializeComponent();
        _textFilePath = Path.Combine(AppContext.BaseDirectory, "text.text");
        EnsureFileExists();
        LoadSnippets();
        BuildBubbles();
        SetupWatcher();
        Loaded += (_, _) => SnapToNearestEdge();
    }

    private void Window_Deactivated(object sender, EventArgs e) => Topmost = true;

    private void EnsureFileExists()
    {
        if (!File.Exists(_textFilePath))
            File.WriteAllLines(_textFilePath, new string[10]);
    }

    private void SetupWatcher()
    {
        _reloadDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); LoadSnippets(); };

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_textFilePath)!, Path.GetFileName(_textFilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += (_, _) => Dispatcher.Invoke(() => _reloadDebounce!.Start());
        _watcher.EnableRaisingEvents = true;
    }

    private void LoadSnippets()
    {
        try
        {
            var lines = File.ReadAllLines(_textFilePath);
            _snippets = Enumerable.Range(0, 10).Select(i => i < lines.Length ? lines[i] : "").ToArray();
        }
        catch { }
    }

    private void BuildBubbles()
    {
        BubblesHost.Children.Clear();
        for (int i = 0; i < 10; i++)
        {
            var btn = new Button
            {
                Width = 40,
                Height = 40,
                Content = _labels[i],
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(30, 144, 255)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = i,
                Cursor = Cursors.Hand
            };
            btn.Click += Bubble_Click;
            btn.Template = CreateRoundButtonTemplate();
            BubblesHost.Children.Add(btn);
        }
    }

    private ControlTemplate CreateRoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private void Bubble_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int idx && idx < _snippets.Length)
        {
            try { System.Windows.Clipboard.SetText(_snippets[idx]); }
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
        _expanded = true;
        Width = 320;
        Height = 140;
        Canvas.SetLeft(BubblesHost, 0);
        Canvas.SetTop(BubblesHost, 0);
        BubblesHost.Visibility = Visibility.Visible;
        PositionBubbles();
    }

    private void Collapse()
    {
        _expanded = false;
        Width = 64;
        Height = 64;
        BubblesHost.Visibility = Visibility.Collapsed;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartMouse = e.GetPosition(null);
        _dragStartWindow = new System.Windows.Point(Left, Top);
        CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(null);
        Left = _dragStartWindow.X + (cur.X - _dragStartMouse.X);
        Top = _dragStartWindow.Y + (cur.Y - _dragStartMouse.Y);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SnapToNearestEdge();

        double movedDistance = Math.Abs(Left - _dragStartWindow.X) + Math.Abs(Top - _dragStartWindow.Y);
        if (movedDistance <= 4) ToggleExpand();
    }

    private void SnapToNearestEdge()
    {
        var wa = SystemParameters.WorkArea;
        double leftDist = Math.Abs(Left - wa.Left);
        double rightDist = Math.Abs((Left + Width) - wa.Right);
        double topDist = Math.Abs(Top - wa.Top);
        double bottomDist = Math.Abs((Top + Height) - wa.Bottom);
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
        // 5x2 grid layout - perfectly visible
        double startX = 20;
        double startY = 20;
        double spacingX = 52;
        double spacingY = 52;

        for (int i = 0; i < BubblesHost.Children.Count; i++)
        {
            int row = i / 5;
            int col = i % 5;
            double x = startX + col * spacingX;
            double y = startY + row * spacingY;
            Canvas.SetLeft((UIElement)BubblesHost.Children[i], x);
            Canvas.SetTop((UIElement)BubblesHost.Children[i], y);
        }
    }

    private void OpenInNotepad_Click(object sender, RoutedEventArgs e)
    {
        EnsureFileExists();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_textFilePath}\"") { UseShellExecute = true });
    }

    private void OpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        EnsureFileExists();
        try
        {
            Process.Start(new ProcessStartInfo("code", $"\"{_textFilePath}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_textFilePath}\"") { UseShellExecute = true });
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => LoadSnippets();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
