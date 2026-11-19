using System.IO;
using System.Windows;
using WristMusic.Services;

namespace WristMusic;
public partial class App : Application
{
    private SingleInstanceGate? _instance;
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0] == "--restore-netease-test-track" && double.TryParse(e.Args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var restorePosition))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(await BridgePlaybackTests.Restore(e.Args[1], restorePosition)); Console.Out.Flush(); Shutdown(); return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--netease-pause")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var service = new MediaService(); await service.Initialize();
            var state = await service.Refresh(new AppEntry("netease", "网易云音乐", "云", "#E45D71", ["cloudmusic", "inflink"]));
            var accepted = state is not null && await service.Control("pause");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { accepted, state?.Title, state?.Position })); Console.Out.Flush(); Shutdown(accepted ? 0 : 1); return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--bridge-playback-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var result = await BridgePlaybackTests.Run(); Console.WriteLine(result); Console.Out.Flush();
            using var report = System.Text.Json.JsonDocument.Parse(result);
            Shutdown(report.RootElement.GetProperty("success").GetBoolean() ? 0 : 1); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--netease-bridge-setup")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string report; int code;
            try { report = System.Text.Json.JsonSerializer.Serialize(new { success = true, message = await NeteaseBridgeSetup.Install(e.Args[1]) }); code = 0; }
            catch (Exception error) { report = System.Text.Json.JsonSerializer.Serialize(new { success = false, error = error.Message }); code = 1; }
            Console.WriteLine(report); Console.Out.Flush();
            try { Directory.CreateDirectory(Path.GetDirectoryName(NeteaseBridgeSetup.ReportPath)!); File.WriteAllText(NeteaseBridgeSetup.ReportPath, report); } catch { }
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--media-diagnostics")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(await MediaDiagnostics.Read()); Console.Out.Flush(); Shutdown(); return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--uninstall-cleanup")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var cleanup = await new SteamVrAutoStartService().Execute(AutoStartOperation.RemoveOwned);
            Shutdown(cleanup.Success ? 0 : 1); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == SteamVrAutoStartService.HelperArgument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(SteamVrAutoStartService.RunHelper(e.Args[1])); return;
        }
        if (e.Args.Contains("--self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await SmokeTests.Run(e.Args.Contains("--live-check"), e.Args.Contains("--background-soak")); Shutdown(code); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "WristMusic-error.log"), DateTimeOffset.Now + " " + args.Exception + Environment.NewLine); } catch { }
            MessageBox.Show("程序遇到错误，详情已写入临时目录 WristMusic-error.log。\n" + args.Exception.Message, "腕间音乐", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        if (UpdateService.Applying()) { Shutdown(); return; }
        _instance = new SingleInstanceGate();
        if (!_instance.Entered)
        {
            if (!e.Args.Contains("--steamvr-startup")) MessageBox.Show("腕间音乐已在运行，请查看任务栏中的窗口。", "腕间音乐", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        var vm = new MainViewModel();
        if (vm.AutoUpdate && vm.Updates.StartPending(e.Args.Contains("--steamvr-startup"))) { Shutdown(); return; }
        vm.StartUpdates();
        var window = new MainWindow(vm); MainWindow = window;
        if (e.Args.Contains("--steamvr-startup")) window.WindowState = WindowState.Minimized;
        window.Show();
        await vm.Initialize();
    }
    private void OnExit(object sender, ExitEventArgs e) { _instance?.Dispose(); }
}
