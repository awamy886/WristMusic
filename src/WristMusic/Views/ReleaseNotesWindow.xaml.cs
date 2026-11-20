using System.Windows;

namespace WristMusic.Views;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow()
    {
        InitializeComponent();
    }

    internal static void ShowOnFirstVisit(Window owner, MainViewModel vm)
    {
        if (!ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) return;

        owner.ContentRendered += ShowWhenVisible;
        owner.StateChanged += ShowWhenVisible;
        void ShowWhenVisible(object? sender, EventArgs e)
        {
            if (!owner.IsLoaded || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
            owner.ContentRendered -= ShowWhenVisible;
            owner.StateChanged -= ShowWhenVisible;
            if (ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) Open(owner, vm);
        }
    }

    internal static void Open(Window owner, MainViewModel vm)
    {
        if (owner.OwnedWindows.OfType<ReleaseNotesWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }
        var notice = new ReleaseNotesWindow { Owner = owner };
        notice.ContentRendered += (_, _) =>
        {
            if (!ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) return;
            vm.Settings.LastSeenReleaseNotes = ReleaseNotes.BuildVersion;
            vm.Save();
        };
        notice.Show();
    }

    private void Dismiss(object sender, RoutedEventArgs e) => Close();
}
