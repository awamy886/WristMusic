using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Valve.VR;
using WristMusic.Services;
using WristMusic.Views;

namespace WristMusic;

internal static class CompactOverlayTests
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(folder, "compact-settings.json")); store.Save(new());
        using var vm = new MainViewModel(false, store);
        var view = new OverlayView { DataContext = vm };
        using var renderer = new OverlayService(vm); // No Start(): allocation/layout only, never initializes OpenVR.
        void Layout()
        {
            view.Measure(new(820, vm.VrPanelHeight)); view.Arrange(new(0, 0, 820, vm.VrPanelHeight)); view.UpdateLayout();
        }
        FrameworkElement Element(string name) => (FrameworkElement)view.FindName(name);
        double Top(string name) => Element(name).TranslatePoint(new(0, 0), view).Y;
        async Task Click(string name)
        {
            Layout(); var target = Element(name);
            var point = target.TranslatePoint(new(target.ActualWidth / 2, target.ActualHeight / 2), view);
            view.Interact(point, true, true); view.Interact(point, false, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout(); renderer.EnsureSurface();
        }
        Layout();
        check(vm.VrPanelHeight == 405 && renderer.TextureSize.Height == 405 && Element("TimelineRegion").Visibility == Visibility.Collapsed,
            "No media timeline starts with a compact panel and an actual 405-pixel texture");
        var noTime = new Services.MediaState("cloudmusic.exe", "时光漫游", "腕间音乐", false, 0, 0, 0, false, true, true, true, null, DateTimeOffset.UtcNow, false, 0);
        vm.ApplyMediaState(noTime with { TimelineKnown = true, Position = 84, Max = 228, Seek = true });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout(); renderer.EnsureSurface();
        var fullPlayTop = Top("PlayPauseButton"); var fullVolumeTop = Top("VolumeSlider");
        check(vm.VrHasProgress && vm.VrPanelHeight == 500 && renderer.TextureSize.Height == 500 && Element("TimelineRegion").Visibility == Visibility.Visible,
            "A real timeline restores the full panel, seek region and 500-pixel texture");
        vm.ApplyMediaState(noTime);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout(); renderer.EnsureSurface();
        check(view.ActualHeight == 405 && renderer.TextureSize.Height == 405 && Element("TimelineRegion").Visibility == Visibility.Collapsed &&
            Top("PlayPauseButton") < fullPlayTop && Math.Abs(fullVolumeTop - Top("VolumeSlider") - 95) < .1,
            "Losing the timeline collapses the empty transport row and moves controls upward by reducing the panel height");
        check(Top("PlayPauseButton") >= Top("MusicSourceText") + Element("MusicSourceText").ActualHeight + 2,
            "Compact transport buttons remain below the metadata without overlapping it");
        Render(view, Path.Combine(folder, "overlay-compact.png"));
        vm.Settings.LastSeenReleaseNotes = ReleaseNotes.BuildVersion;
        vm.Settings.LastSeenVrReleaseNotes = "3.5.5";
        vm.ShowUnreadVrNotes();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        check(Element("VrNotesDialog").Visibility == Visibility.Visible && Top("VrDismissNotesButton") + Element("VrDismissNotesButton").ActualHeight <= view.ActualHeight,
            "First VR visit shows the announcement even after desktop viewing and fits the compact texture");
        await Click("SettingsButton");
        check(vm.VrNotesOpen && !vm.VrSettingsOpen, "The VR announcement blocks ray clicks on covered controls");
        Render(view, Path.Combine(folder, "overlay-release-notes.png"));
        await Click("VrDismissNotesButton");
        vm.ShowUnreadVrNotes();
        check(!vm.VrNotesOpen && store.Load().LastSeenVrReleaseNotes == ReleaseNotes.BuildVersion,
            "Confirming the VR notice persists its read receipt and prevents repeat prompts");
        vm.DarkMode = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        Render(view, Path.Combine(folder, "overlay-compact-dark.png"));
        vm.ShowListeningTime = true; Layout();
        check(Element("ListeningBadge").Visibility == Visibility.Visible && Top("ListeningBadge") + Element("ListeningBadge").ActualHeight < view.ActualHeight,
            "The optional listening badge still fits in the compact panel");
        await Click("SettingsButton");
        check(vm.VrSettingsOpen && view.ActualHeight == 500 && renderer.TextureSize.Height == 500,
            "Opening settings from compact mode expands the actual surface to full height");
        var scale = Element("VrScaleSlider");
        var start = scale.TranslatePoint(new(scale.ActualWidth * .8, 14), view);
        view.Interact(start, true, true);
        vm.ApplyMediaState(noTime); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        var end = scale.TranslatePoint(new(scale.ActualWidth * .2, 14), view);
        view.Interact(end, false, true); view.Interact(end, false, false);
        check(vm.Scale < 90, "A media-state refresh does not interrupt dragging a visible VR settings slider");
        await Click("AboutSettingsTab");
        check(vm.VrSettingsTab == "about" && Element("VrAboutPanel").Visibility == Visibility.Visible &&
            ((TextBlock)Element("VrAboutVersion")).Text == "v4.2.0·星梦屿工作室" &&
            ((TextBlock)Element("VrAboutAuthor")).Text == "作者:awa星野厨のMY" &&
            ((TextBlock)Element("VrAboutGroup")).Text == "交流群:1049685082" &&
            ((TextBlock)Element("VrAboutFree")).Text == "本程序完全免费·o(*≧▽≦)ツ┏━┓",
            "The VR About tab opens by controller ray and preserves the exact desktop About text");
        Render(view, Path.Combine(folder, "overlay-about-dark.png"));
        await Click("VrOpenNotesButton");
        check(vm.VrNotesOpen && Element("VrNotesDialog").Visibility == Visibility.Visible,
            "The VR settings button next to About reopens the same announcement by ray");
        await Click("VrDismissNotesButton");
        check(vm.VrSettingsOpen && vm.VrSettingsTab == "about" && !vm.VrNotesOpen,
            "Dismissing a reopened notice returns to the previous VR settings tab");
        vm.DarkMode = false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        Render(view, Path.Combine(folder, "overlay-about.png"));
        await Click("BackToMusicButton");
        check(!vm.VrSettingsOpen && view.ActualHeight == 405 && renderer.TextureSize.Height == 405,
            "Returning from About restores compact music controls and their smaller texture");
        vm.ApplyMediaState(noTime with { TimelineKnown = true, Position = 84 });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        check(!vm.VrHasProgress && view.ActualHeight == 405,
            "Position without a usable duration does not expose an invalid progress bar");
        vm.ApplyMediaState(noTime with { TimelineKnown = true, Position = 84, Max = 228 });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
        check(vm.VrHasProgress && Element("TimelineRegion").Visibility == Visibility.Visible && !vm.CanSeek,
            "A valid read-only timeline remains visible even when native seeking is unsupported");
        var identity = new HmdMatrix34_t { m0 = 1, m5 = 1, m10 = 1 };
        var pointer = identity; pointer.m11 = 1;
        check(OverlayService.TryRayHit(identity, identity, pointer, .36, out var center, 405) &&
            Math.Abs(center.X - 410) < .001 && Math.Abs(center.Y - 202.5) < .001,
            "Compact controller rays use the shortened texture's real center");
        pointer.m7 = .1f;
        check(OverlayService.TryRayHit(identity, identity, pointer, .36, out _) &&
            !OverlayService.TryRayHit(identity, identity, pointer, .36, out _, 405),
            "Space removed by compact mode is excluded from the VR controller hit area");
        vm.DarkMode = false;
    }
    private static void Render(OverlayView view, string path)
    {
        var bitmap = new RenderTargetBitmap(820, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
