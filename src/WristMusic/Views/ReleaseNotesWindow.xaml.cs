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
        if (vm.Settings.SeenDesktopSmtcGuide && !ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) return;

        owner.ContentRendered += ShowWhenVisible;
        owner.StateChanged += ShowWhenVisible;
        void ShowWhenVisible(object? sender, EventArgs e)
        {
            if (!owner.IsLoaded || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
            owner.ContentRendered -= ShowWhenVisible;
            owner.StateChanged -= ShowWhenVisible;
            if (!vm.Settings.SeenDesktopSmtcGuide)
            {
                var guide = Open(owner, vm, guide: true);
                guide.Closed += (_, _) => owner.Dispatcher.InvokeAsync(() =>
                {
                    if (owner.IsLoaded && ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes))
                    {
                        if (owner.IsVisible && owner.WindowState != WindowState.Minimized) Open(owner, vm);
                        else ShowOnFirstVisit(owner, vm);
                    }
                });
            }
            else if (ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) Open(owner, vm);
        }
    }

    private bool _guide;
    internal static ReleaseNotesWindow Open(Window owner, MainViewModel vm, bool guide = false)
    {
        if (owner.OwnedWindows.OfType<ReleaseNotesWindow>().FirstOrDefault(w => w._guide == guide) is { } existing)
        {
            existing.Activate();
            return existing;
        }
        var notice = new ReleaseNotesWindow { Owner = owner, _guide = guide };
        if (guide)
        {
            notice.Title = "腕间音乐 · 首次使用须知";
            notice.Heading.Text = SmtcGuide.Heading;
            notice.NoticeBody.Text = SmtcGuide.Body;
        }
        notice.ContentRendered += (_, _) =>
        {
            if (guide)
            {
                if (vm.Settings.SeenDesktopSmtcGuide) return;
                vm.Settings.SeenDesktopSmtcGuide = true;
            }
            else
            {
                if (!ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes)) return;
                vm.Settings.LastSeenReleaseNotes = ReleaseNotes.BuildVersion;
            }
            vm.Save();
        };
        notice.Show();
        return notice;
    }

    private void Dismiss(object sender, RoutedEventArgs e) => Close();
}
