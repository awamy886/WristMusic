using System.Windows.Controls;
using System.Windows;
namespace WristMusic.Views;
public partial class AppsPage : UserControl { public AppsPage() { InitializeComponent(); } }
public partial class WristPage : UserControl { public WristPage() { InitializeComponent(); } }
public partial class HotkeysPage : UserControl { public HotkeysPage() { InitializeComponent(); } }
public partial class SettingsPage : UserControl
{
    public SettingsPage() { InitializeComponent(); }
    private void ShowReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } owner && DataContext is MainViewModel vm)
            ReleaseNotesWindow.Open(owner, vm);
    }
}
