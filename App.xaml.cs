using System.Windows;

namespace PinBubble;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Single-instance guard is handled in MainWindow for simplicity
    }
}
