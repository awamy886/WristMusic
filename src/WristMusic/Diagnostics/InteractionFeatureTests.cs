using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Valve.VR;
using WristMusic.Services;
using WristMusic.Views;

namespace WristMusic;

internal static class InteractionFeatureTests
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        var defaults = new UserSettings();
        check(!defaults.LaunchSteamVr && !defaults.ShowListeningTime &&
            defaults.ShowCpuUsage && defaults.ShowMemoryUsage && defaults.ShowGpuUsage,
            "Default settings enable the three utilization meters and leave listening time off");
        check(!SteamVrConnectionPolicy.ShouldInitialize(false, false) &&
            SteamVrConnectionPolicy.ApplicationType(true, false) == EVRApplicationType.VRApplication_Background &&
            SteamVrConnectionPolicy.ApplicationType(false, false) == EVRApplicationType.VRApplication_Background &&
            SteamVrConnectionPolicy.ApplicationType(false, true) == EVRApplicationType.VRApplication_Overlay,
            "Default startup and reconnect cannot start SteamVR, including a server exit race; explicit opt-in permits launch");
        var settings = new SettingsStore(Path.Combine(folder, "feature-settings.json"));
        File.WriteAllText(settings.FilePath, "{\"FollowSteamVr\":true,\"ExperimentalFeaturesEnabled\":false,\"ShowGpuUsage\":false}");
        check(settings.Load() is { FollowSteamVr: true, LaunchSteamVr: false, ShowCpuUsage: true, ShowMemoryUsage: true, ShowGpuUsage: false },
            "Legacy master setting is ignored and individual meter choices are preserved");
        settings.Save(new() { LaunchSteamVr = true, FollowSteamVr = false, ShowListeningTime = true, TotalListeningSeconds = 12345.5 });
        check(settings.Load() is { LaunchSteamVr: true, FollowSteamVr: false, ShowListeningTime: true, TotalListeningSeconds: 12345.5 },
            "The two startup directions, experimental flags and listening total persist independently");
        double now = 0;
        var tracker = new ListeningTimeTracker(10, () => now);
        tracker.Update(true); now = 2; tracker.Update(true);
        tracker.Update(false); now = 4; tracker.Update(false);
        check(tracker.TotalSeconds == 12, "Listening total accumulates actual playback time and freezes while paused");
        tracker.Update(true); now = 5; tracker.Update(true); tracker.Update(true);
        check(tracker.TotalSeconds == 13, "Repeated observations and seeking do not add track-position jumps to listening time");
        now = 100; tracker.Update(true); now = 101; tracker.Update(false);
        check(tracker.TotalSeconds == 14 && ListeningTimeTracker.Format(27 * 3600 + 69) == "27:01:09",
            "Suspension gaps are excluded and accumulated hours do not wrap at 24 hours");
        settings.Save(new());
        if (!SteamVrConnectionPolicy.IsRunning())
        {
            using var desktop = new MainViewModel(false, settings);
            using var runtime = new OverlayService(desktop); runtime.Start();
            check(desktop.VrStatus == "SteamVR 未运行" && !SteamVrConnectionPolicy.IsRunning(),
                "Native desktop startup waits for a stopped SteamVR runtime without starting it");
            var status = await new SteamVrAutoStartService().Execute(AutoStartOperation.Status);
            check(!status.Success && !SteamVrConnectionPolicy.IsRunning(),
                "Automatic startup-status detection also leaves a stopped SteamVR runtime stopped");
        }
        settings.Save(new());
        using (var desktop = new MainViewModel(false, settings))
        {
            desktop.OverlayEnabled = false;
            await desktop.Initialize(connectVr: false);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while ((desktop.UsageLabel.Contains("CPU —") || desktop.UsageLabel.Contains("内存 —")) && elapsed.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(100);
            check(desktop.UsageLabel.Contains('%') && !desktop.UsageLabel.Contains("CPU —") && !desktop.UsageLabel.Contains("内存 —"),
                "Desktop meters sample CPU and memory with SteamVR and the wrist panel disconnected");
        }
        settings.Save(new());
        using var vm = new MainViewModel(true, settings);
        check(!vm.ListeningTimeVisible, "The original music panel has no listening badge by default");
        vm.ShowListeningTime = true;
        vm.Save();
        check(vm.ListeningTimeVisible && settings.Load() is { ShowListeningTime: true },
            "The listening switch independently reveals and saves the badge");
        vm.ShowListeningTime = false;
        var slider = new Slider { Width = 400, Height = 28, Minimum = 0, Maximum = vm.Duration, DataContext = vm };
        slider.SetBinding(Slider.ValueProperty, new Binding(nameof(MainViewModel.SeekPosition)) { Mode = BindingMode.TwoWay });
        slider.Measure(new(400, 28)); slider.Arrange(new(0, 0, 400, 28)); slider.UpdateLayout();
        var input = new SeekSliderInput(slider);
        check(input.BeginAt(new(320, 14)), "A click on the desktop seek bar begins an absolute seek");
        await input.Commit();
        check(vm.Position > 170, "A single seek-bar click jumps to the clicked location without a drag");
        input.BeginAt(new(320, 14)); input.MoveAt(new(90, 14)); await input.Commit();
        check(vm.Position < 70, "The same seek bar retains drag-to-seek support");
        var overlay = new OverlayView { DataContext = vm };
        overlay.Measure(new(820, 500)); overlay.Arrange(new(0, 0, 820, 500)); overlay.UpdateLayout();
        async Task Click(string name)
        {
            overlay.UpdateLayout();
            var element = (FrameworkElement)overlay.FindName(name);
            var point = element.TranslatePoint(new(element.ActualWidth / 2, element.ActualHeight / 2), overlay);
            overlay.Interact(point, true, true); overlay.Interact(point, false, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); overlay.UpdateLayout();
        }
        await Click("SettingsButton");
        check(vm.VrSettingsOpen && ((Grid)overlay.FindName("PlayerContent")).Visibility == Visibility.Collapsed,
            "The VR controller ray opens settings inside the wrist panel");
        Render(overlay, Path.Combine(folder, "overlay-settings-wrist.png"));
        var scale = (Slider)overlay.FindName("VrScaleSlider");
        var start = scale.TranslatePoint(new(scale.ActualWidth * .8, 14), overlay);
        overlay.Interact(start, true, true);
        var end = scale.TranslatePoint(new(scale.ActualWidth * .2, 14), overlay);
        overlay.Interact(end, false, true); overlay.Interact(end, false, false);
        check(vm.Scale < 90, "VR settings sliders follow a held controller ray while dragging");
        await Click("GeneralSettingsTab"); await Click("VrDarkModeSwitch");
        check(vm.DarkMode, "VR settings switches update the shared desktop theme");
        await Click("VrDoubleBSwitch");
        check(!vm.DoubleBToOpen, "The VR settings double-B switch can disable the shortcut");
        await Click("VrDoubleBSwitch");
        check(vm.DoubleBToOpen, "The VR settings double-B switch retains its binding when re-enabled");
        Render(overlay, Path.Combine(folder, "overlay-settings-general-dark.png"));
        await Click("VrDarkModeSwitch");
        await Click("TestSettingsTab"); await Click("VrListeningSwitch");
        await Click("VrCpuSwitch");
        check(!vm.ShowCpuUsage && vm.UsageLabel.Contains("内存") && vm.UsageLabel.Contains("显卡") && !vm.UsageLabel.Contains("CPU"),
            "The VR CPU switch removes only CPU from the header");
        await Click("VrMemorySwitch"); await Click("VrGpuSwitch"); vm.Save();
        check(vm.UsageLabel == "" && settings.Load() is { ShowCpuUsage: false, ShowMemoryUsage: false, ShowGpuUsage: false },
            "All three switches work independently and persist");
        await Click("VrCpuSwitch"); await Click("VrMemorySwitch"); await Click("VrGpuSwitch");
        check(vm.ShowListeningTime && vm.ListeningTimeVisible,
            "VR meter switches leave listening time enabled");
        Render(overlay, Path.Combine(folder, "overlay-settings-tests.png"));
        await Click("BackToMusicButton");
        check(!vm.VrSettingsOpen && ((TextBlock)overlay.FindName("ListeningBadge")).Visibility == Visibility.Visible,
            "Returning from VR settings restores music controls with the optional listening badge");
        Render(overlay, Path.Combine(folder, "overlay-listening.png"));
        await vm.EndSeek(84);
        overlay.Interact(new(600, 190), true, true);
        check(vm.Position > 100, "VR seek press previews the absolute clicked position");
        overlay.CancelInteraction();
        check(vm.Position == 84, "Lost tracking cancels an unfinished seek instead of committing it");
        vm.ShowListeningTime = false; vm.DarkMode = false;
    }
    private static void Render(OverlayView view, string path)
    {
        view.UpdateLayout(); var bitmap = new RenderTargetBitmap(820, 500, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
