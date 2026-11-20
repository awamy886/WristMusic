using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Valve.VR;
using WristMusic.Services;

namespace WristMusic;

internal static class SmokeTests
{
    public static async Task<int> Run(bool liveCheck, bool backgroundSoak = false)
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "validation"); Directory.CreateDirectory(folder);
        var results = new List<object>();
        var liveChecksPassed = true;
        void Check(bool ok, string name) { results.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        try
        {
            await CheckReleaseNotes(folder, Check);
            CheckBrowserExclusion(folder, Check);
            var updateSettings = JsonSerializer.Deserialize<UserSettings>("{}")!;
            Check(updateSettings.AutoUpdate && !JsonSerializer.Deserialize<UserSettings>("{\"AutoUpdate\":false}")!.AutoUpdate,
                "Automatic updates default on for existing settings and preserve explicit opt-out");
            var current = new Version(3, 0, 0);
            Check(UpdateService.Newer("v3.0.1", current) && !UpdateService.Newer("v3.0.0", current) &&
                !UpdateService.Newer("v2.9.9", current) && !UpdateService.Newer("v4.0.0-beta", current),
                "Only strictly newer stable versions can be applied");
            var portable = UpdateService.FileName("v3.1.0", false);
            var setup = UpdateService.FileName("v3.1.0", true);
            var manifest = new string('a', 64) + "  " + portable + "\n" + new string('b', 64) + "  " + setup;
            Check(UpdateService.ReadDigest(manifest, setup) == new string('B', 64) &&
                UpdateService.ReadDigest(manifest, portable) == new string('A', 64), "Select the exact package digest for each distribution");
            CheckPlayerOptions(folder, Check);
            await BackgroundRefreshTests.Run(folder, Check, backgroundSoak);
            await AutoStartTests.Run(folder, Check);
            TimelineTests.Run(Check);
            BridgeTests.Run(folder, Check);
            VrStabilityTests.Run(folder, Check);
            GpuOverlayTests.Run(Check);
            CursorOverlayTests.Run(folder, Check);
            await InteractionFeatureTests.Run(folder, Check);
            await CompactOverlayTests.Run(folder, Check);
            results.Add(new { name = "SteamVR autostart verification scope", detail = "Registration and flag modification tests use a simulated backend and do not change the actual SteamVR configuration. A real SteamVR runtime is required to validate startup end to end." });
            var store = new SettingsStore(Path.Combine(folder, "test-settings.json"));
            store.Save(new() { Scale = 900, OffsetX = -500, Opacity = 0, SelectedApp = "qq", RightWrist = true, DarkMode = true });
            var restored = store.Load();
            Check(restored is { Scale: 160, OffsetX: -30, Opacity: 30, SelectedApp: "qq", RightWrist: true, DarkMode: true }, "Settings clamp invalid values and retain player, wrist and dark mode");
            File.WriteAllText(store.FilePath, "{\"SelectedApp\":\"netease\",\"Scale\":110}");
            Check(store.Load() is { DarkMode: false, Scale: 110, SelectedApp: "netease" }, "Existing settings without a theme default to light and retain player preferences");
            File.WriteAllText(store.FilePath, "{invalid");
            Check(store.Load().Scale == 100 && store.LoadError is not null, "Corrupt settings recover with a visible diagnostic");
            Check(MediaMath.Position(224, 30, 0, 228, true) == 228 && MediaMath.Position(84, 30, 0, 228, false) == 84, "Timeline does not overshoot or advance while paused");
            var settings = new UserSettings();
            var identity = new HmdMatrix34_t { m0 = 1, m5 = 1, m10 = 1 };
            var raisedWrist = identity; raisedWrist.m7 = 1.3f; raisedWrist.m11 = -0.4f;
            var head = identity; head.m7 = 1.65f;
            var lowWrist = raisedWrist; lowWrist.m7 = 0.7f;
            Check(OverlayService.Raised(raisedWrist, head, OverlayService.MakeTransform(settings), false) && !OverlayService.Raised(lowWrist, head, OverlayService.MakeTransform(settings), false), "Raise-to-show accepts a facing raised wrist and rejects a lowered wrist");
            var pointerPose = identity; pointerPose.m11 = 1;
            Check(OverlayService.TryRayHit(identity, identity, pointerPose, 0.36, out var hit) && Math.Abs(hit.X - 410) < 0.01 && Math.Abs(hit.Y - 250) < 0.01, "Controller forward ray maps to panel center in WPF coordinates");
            pointerPose.m3 = 1;
            Check(!OverlayService.TryRayHit(identity, identity, pointerPose, 0.36, out _), "Controller rays outside the panel do not click");

            using var vm = new MainViewModel(true, new SettingsStore(Path.Combine(folder, "demo-settings.json")));
            vm.DarkMode = false;
            Check(vm.Playing && vm.Title == "夜航" && vm.CanSeek, "Demo starts with explicit simulated music");
            await vm.RunMedia("toggle"); Check(!vm.Playing, "Pause changes demo playback state");
            await vm.RunMedia("next"); Check(vm.Title == "月光漫游" && vm.Position == 0, "Next track updates title and resets timeline");
            await vm.RunMedia("previous"); Check(vm.Title == "夜航", "Previous returns to the preceding track");
            await vm.EndSeek(9999); Check(vm.Position == vm.Duration, "Seeking past the end clamps to the track duration");
            await vm.EndSeek(84);
            vm.SelectedSource = vm.Sources.First(s => s.Id == "qq");
            Check(vm.UseDemo && vm.Title == "夜航" && vm.CanToggle, "Switching source in demo retains simulated playback");
            vm.Volume = 27; Check(vm.Volume == 27 && vm.UseDemo, "Demo volume uses simulated state"); vm.Volume = 65;
            var window = new MainWindow(vm);
            var overlay = new Views.OverlayView { DataContext = vm };
            overlay.Measure(new(820, 500)); overlay.Arrange(new(0, 0, 820, 500)); overlay.UpdateLayout();
            // Invoke the same hit-testing route used by a controller ray; no operating system input is injected.
            await vm.RunMedia("toggle"); var wasPlaying = vm.Playing;
            overlay.SetPointer(new(410, 250));
            overlay.UpdateLayout();
            overlay.Interact(new(410, 250), true, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Playing != wasPlaying, "Overlay ray hit-test activates the play / pause button");
            overlay.Interact(new(700, 358), true, true);
            Check(vm.Volume > 80 && vm.UseDemo, "Overlay volume ray updates only the simulated volume");
            overlay.Interact(new(700, 358), false, false);
            overlay.Interact(new(600, 190), true, true);
            overlay.Interact(new(600, 190), false, false);
            Check(vm.Position > 100, "Overlay timeline ray seeks within the simulated track");
            vm.UseDemo = false;
            Check(!vm.CanSeek && !vm.HasSession && vm.Title == "等待音乐响起", "Leaving demo clears all simulated media state");
            var unavailable = new MediaState("cloudmusic.exe", "答案", "杨坤", false, 0, 0, 0, false, true, true, true, null, DateTimeOffset.UtcNow, false, 0);
            vm.ApplyMediaState(unavailable);
            Check(vm.PositionLabel == "--:--" && vm.DurationLabel == "--:--" && !vm.CanSeek && vm.CanToggle,
                "Missing NetEase time displays unavailable while retaining basic playback controls");
            vm.ApplyMediaState(unavailable with { Position = 84, TimelineKnown = true });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.Position == 84 && vm.PositionLabel == "01:24" && vm.DurationLabel == "--:--",
                "Desktop slider coercion never writes a missing duration back over the real position");
            vm.ApplyMediaState(unavailable with { Position = 84, Max = 228, TimelineKnown = true, TimelineSource = "网易云客户端" });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(vm.PositionLabel == "01:24" && vm.DurationLabel == "03:48" && vm.SeekPosition == 84 && !vm.CanSeek,
                "Accessibility time is displayed without claiming unsupported native seeking");
            vm.ApplyMediaState(null);
            Check(vm.PositionLabel == "--:--" && vm.DurationLabel == "--:--", "Disconnect clears the old track timeline and labels");
            foreach (var page in new[] { "player", "apps", "wrist", "hotkeys", "settings" })
            {
                vm.Page = page; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.DesktopUsageHeader.Visibility == (page == "player" ? Visibility.Visible : Visibility.Collapsed) && window.DesktopUsageHeader.Text == vm.UsageLabel,
                    $"Light desktop meter visibility and content on {page}");
                Render(window, Path.Combine(folder, page + ".png"));
            }
            Check(File.Exists(Path.Combine(folder, "wrist.png")), "All five desktop pages render to PNG");
            overlay.UpdateLayout();
            overlay.Measure(new(820, vm.VrPanelHeight)); overlay.Arrange(new(0, 0, 820, vm.VrPanelHeight)); overlay.UpdateLayout();
            RenderVisual(overlay, 820, (int)vm.VrPanelHeight, Path.Combine(folder, "overlay.png"));
            var lightCanvas = ((SolidColorBrush)window.Background).Color;
            var lightArtwork = vm.Artwork;
            var lightOverlay = ((SolidColorBrush)((System.Windows.Controls.Border)((System.Windows.Controls.Grid)overlay.Content).Children[0]).Background).Color;
            vm.DarkMode = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((SolidColorBrush)window.Background).Color != lightCanvas && !ReferenceEquals(vm.Artwork, lightArtwork), "Theme toggle updates the existing window palette and placeholder artwork without a restart");
            Check(((SolidColorBrush)((System.Windows.Controls.Border)((System.Windows.Controls.Grid)overlay.Content).Children[0]).Background).Color != lightOverlay,
                "Offscreen VR panel follows a theme change immediately without a Window ancestor");
            vm.Save();
            Check(new SettingsStore(Path.Combine(folder, "demo-settings.json")).Load().DarkMode, "Dark mode selection persists to the settings file");
            foreach (var page in new[] { "player", "apps", "wrist", "hotkeys", "settings" })
            {
                vm.Page = page; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.DesktopUsageHeader.Visibility == (page == "player" ? Visibility.Visible : Visibility.Collapsed) && window.DesktopUsageHeader.Text == vm.UsageLabel,
                    $"Dark desktop meter visibility and content on {page}");
                Render(window, Path.Combine(folder, page + "-dark.png"));
            }
            overlay.UpdateLayout();
            RenderVisual(overlay, 820, (int)vm.VrPanelHeight, Path.Combine(folder, "overlay-dark.png"));
            vm.DarkMode = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            vm.Save();
            Check(((SolidColorBrush)window.Background).Color == lightCanvas && !new SettingsStore(Path.Combine(folder, "demo-settings.json")).Load().DarkMode, "Disabling dark mode restores the light palette and saved preference");
            vm.Page = "player"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render(window, Path.Combine(folder, "waiting.png"));
            window.Close();

            if (liveCheck)
            {
                using (var gpu = new OverlayGpuTexture())
                {
                    var pixels = new byte[64 * 64 * 4]; Array.Fill(pixels, (byte)127);
                    gpu.Upload(pixels, 64, 64);
                    Check(gpu.ReadBack().AsSpan().SequenceEqual(pixels), "Hardware D3D11 shared texture upload, copy and pixel readback");
                    for (var y = 9; y < 20; y++) for (var x = 5; x < 12; x++) pixels[(y * 64 + x) * 4] = 231;
                    gpu.UploadRegion(pixels, 64, 64, new(5, 9, 7, 11));
                    Check(gpu.ReadBack().AsSpan().SequenceEqual(pixels), "Hardware D3D11 partial texture updates preserve the cursor region and all surrounding pixels");
                }
                var media = new MediaService(); await media.Initialize(); var state = await media.Refresh(null);
                liveChecksPassed &= media.Error is null;
                results.Add(new { name = "Windows SMTC read-only probe", passed = media.Error is null, detail = media.Error ?? (state is null ? "No active media session" : "Active session discovered") });
                try { var volume = new VolumeService().Get(); results.Add(new { name = "Windows Core Audio read-only probe", passed = true, detail = $"Default endpoint volume: {volume:0}%" }); }
                catch (Exception e) { liveChecksPassed = false; results.Add(new { name = "Windows Core Audio read-only probe", passed = false, detail = e.Message }); }
                var runtime = OpenVR.IsRuntimeInstalled(); var hmd = OpenVR.IsHmdPresent();
                results.Add(new { name = "OpenVR native DLL loads", passed = true, runtimeInstalled = runtime, hmdPresent = hmd });
                results.Add(new { name = "Hardware verification", detail = "No playback, volume, application-close, or headset-input mutations performed. VR hardware interaction and each music client require manual acceptance." });
            }
            File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new { success = liveChecksPassed, timestamp = DateTimeOffset.Now, results }, new JsonSerializerOptions { WriteIndented = true }));
            return liveChecksPassed ? 0 : 1;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new { success = false, error = e.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    private static async Task CheckReleaseNotes(string folder, Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(folder, "release-notes-settings.json"));
        store.Save(new() { DarkMode = true, LastSeenReleaseNotes = "3.5.5" });
        using var vm = new MainViewModel(true, store);
        var owner = new Window { Width = 460, Height = 380, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Minimized };
        try
        {
            var settingsPage = new Views.SettingsPage { DataContext = vm };
            owner.Content = settingsPage;
            Views.ReleaseNotesWindow.ShowOnFirstVisit(owner, vm);
            owner.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(owner.OwnedWindows.Count == 0 && store.Load().LastSeenReleaseNotes == "3.5.5",
                "Minimized startup preserves the unread release announcement");
            owner.WindowState = WindowState.Normal;
            var timeout = Stopwatch.StartNew();
            while (ReleaseNotes.IsUnread(vm.Settings.LastSeenReleaseNotes) && timeout.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(25);
            var notice = owner.OwnedWindows.OfType<Views.ReleaseNotesWindow>().Single();
            check(notice.IsVisible && notice.Heading.Text == "v4.0.0更新内容" && store.Load().LastSeenReleaseNotes == "4.0.0",
                "Opening the desktop renders the 4.0.0 announcement and persists its version for older settings");
            notice.Close();
            var click = System.Windows.Controls.Primitives.ButtonBase.ClickEvent;
            settingsPage.OpenNotesButton.RaiseEvent(new RoutedEventArgs(click));
            settingsPage.OpenNotesButton.RaiseEvent(new RoutedEventArgs(click));
            var reopened = owner.OwnedWindows.OfType<Views.ReleaseNotesWindow>().Single();
            check(reopened.IsVisible && reopened.Heading.Text == "v4.0.0更新内容",
                "The desktop About button reopens the announcement without duplicating the window");
            reopened.Close();
            using var restarted = new MainViewModel(true, store);
            Views.ReleaseNotesWindow.ShowOnFirstVisit(owner, restarted);
            owner.WindowState = WindowState.Minimized;
            owner.WindowState = WindowState.Normal;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(owner.OwnedWindows.Count == 0, "Restart with the saved settings does not repeat the announcement");
        }
        finally { owner.Close(); }
    }

    private static void CheckBrowserExclusion(string folder, Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(folder, "browser-selection.json"));
        store.Save(new());
        using var vm = new MainViewModel(false, store);
        var kugou = vm.Players.Apps.Single(a => a.Id == "kugou");
        const string web = "Microsoft.AutoGenerated.A1B2C3"; // Unknown browser identity
        const string kg = "KuGou.exe";
        string? Pick(string? current, params string[] sources) =>
            MediaSessionSelection.Pick(sources, vm.Players.Apps, null, current, s => s,
                s => MediaSessionSelection.Score(s != kg, true, s != "Spotify.exe"));
        check(Pick(web, web, "Chrome.Spotify", "Microsoft.MicrosoftEdge!KuGou", "Brave") is null,
            "Browser and unknown sessions never become automatic media sources");
        check(Pick(web, web, kg) == kg && Pick(null, kg) == kg &&
            Pick(web, kg, "Spotify.exe", web) == kg, "Playing KuGou wins despite browser focus or a paused player with a timeline");
        kugou.Running = true;
        vm.ApplyMediaState(null);
        check(vm.TargetPlayer == kugou && vm.CanToggle && vm.CanNext && !vm.HasTimeline &&
            vm.Title.Contains("酷狗"), "A running KuGou without SMTC becomes the directed fallback without fabricated media data");
        kugou.Running = false;
        vm.ApplyMediaState(null);
        check(vm.TargetPlayer is null && !vm.CanNext, "Exiting KuGou clears the fallback target");
    }

    private static void CheckPlayerOptions(string folder, Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(folder, "player-options.json"));
        File.WriteAllText(store.FilePath, "{}");
        using var vm = new MainViewModel(false, store);
        var spotify = vm.Players.Apps.Single(a => a.Id == "spotify");
        var qq = vm.Players.Apps.Single(a => a.Id == "qq");
        check(vm.OpenLastPlayer && !vm.ForceClosePlayer && vm.ResolveOpenTarget(null) is null,
            "Settings without a preference enable open-last while keeping force-close off");
        check(!JsonSerializer.Deserialize<UserSettings>("{\"OpenLastPlayer\":false}")!.OpenLastPlayer,
            "Existing explicit open-last opt-out survives the default change");
        foreach (var source in new[] { "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" })
        {
            vm.ApplyMediaState(new(source, "Track", "Artist", true, 5, 0, 100, true, true, true, true, null, DateTimeOffset.UtcNow));
            check(vm.TargetPlayer == spotify && vm.Settings.LastPlayerId == "spotify" && vm.CanSeek,
                "Spotify media session recognized: " + source);
        }
        vm.ApplyMediaState(null);
        vm.OpenLastPlayer = true; vm.ForceClosePlayer = true; vm.Save();
        check(vm.ResolveOpenTarget(null) == spotify && vm.ResolveOpenTarget(qq) == qq,
            "Generic open remembers disconnected Spotify while explicit app selection wins");
        check(store.Load() is { LastPlayerId: "spotify", ForceClosePlayer: true, OpenLastPlayer: true },
            "Player options and last player survive restart");

        // Two private helpers with the same process name test exact-path targeting.
        var name = "WristTest" + Guid.NewGuid().ToString("N") + ".exe";
        using var target = StartHelper("target");
        using var other = StartHelper("other");
        try
        {
            var app = new AppEntry("custom", "Test", "", "", []) { ExePath = target.StartInfo.FileName };
            vm.Players.Close(app);
            check(!target.HasExited, "Normal close leaves a windowless player running");
            vm.Players.Close(app, true);
            check(target.WaitForExit(3000) && !other.HasExited,
                "Force close terminates only the configured player executable");
        }
        finally
        {
            foreach (var process in new[] { target, other })
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit();
                File.Delete(process.StartInfo.FileName);
            }
        }
        Process StartHelper(string subfolder)
        {
            var dir = Directory.CreateDirectory(Path.Combine(folder, subfolder));
            var path = Path.Combine(dir.FullName, name);
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), path);
            return Process.Start(new ProcessStartInfo(path, "-n 20 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        }
    }
    private static void Render(MainWindow window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new(1360, 870)); root.Arrange(new(0, 0, 1360, 870)); root.UpdateLayout();
        RenderVisual(root, 1360, 870, path);
    }
    private static void RenderVisual(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
