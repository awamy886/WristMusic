using System.Windows;

namespace WristMusic.Views;

public partial class ReleaseNotesWindow : Window
{
    // This identifies the notes themselves, so a later build without new notes
    // does not present this announcement under a different version number.
    internal const string NotesVersion = "3.5.0";

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        Heading.Text = $"v{NotesVersion} 更新内容";
    }

    internal static void ShowOnFirstVisit(Window owner, MainViewModel vm)
    {
        if (Version.TryParse(vm.Settings.LastSeenReleaseNotes, out var seen) &&
            seen >= Version.Parse(NotesVersion)) return;

        owner.ContentRendered += ShowWhenVisible;
        owner.StateChanged += ShowWhenVisible;
        void ShowWhenVisible(object? sender, EventArgs e)
        {
            if (!owner.IsLoaded || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
            owner.ContentRendered -= ShowWhenVisible;
            owner.StateChanged -= ShowWhenVisible;
            var notice = new ReleaseNotesWindow { Owner = owner };
            notice.ContentRendered += (_, _) =>
            {
                if (vm.Settings.LastSeenReleaseNotes == NotesVersion) return;
                vm.Settings.LastSeenReleaseNotes = NotesVersion;
                vm.Save();
            };
            notice.Show();
        }
    }

    private void Dismiss(object sender, RoutedEventArgs e) => Close();
}
