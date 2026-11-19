using System.IO;
using System.Text.Json;
using WristMusic.Services;

namespace WristMusic;

internal static class AutoStartTests
{
    public static async Task Run(string folder, Action<bool, string> check)
    {
        var directory = Path.Combine(folder, "autostart-tests"); Directory.CreateDirectory(directory);
        // File paths contain spaces and Chinese characters. These fixtures are never executed.
        var binary = Path.Combine(directory, "腕间 音乐.exe"); File.WriteAllBytes(binary, [0]);
        var launch = AppLaunch.ForProcess(binary, Path.ChangeExtension(binary, ".dll"));
        var manifest = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".vrmanifest");
        using var backend = new FakeBackend();
        var status = SteamVrAutoStartService.Configure(AutoStartOperation.Status, backend, launch, manifest);
        check(status is { Success: true, Enabled: false } && !File.Exists(manifest) && backend.Changes == 0,
            "Autostart status is read-only and does not register an app by default");
        var enabled = SteamVrAutoStartService.Configure(AutoStartOperation.Enable, backend, launch, manifest);
        using (var json = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            var app = json.RootElement.GetProperty("applications")[0];
            check(enabled is { Success: true, Enabled: true } && backend.Registrations == 1 &&
                app.GetProperty("app_key").GetString() == SteamVrAutoStartService.ApplicationKey &&
                app.GetProperty("is_dashboard_overlay").GetBoolean() && app.GetProperty("binary_path_windows").GetString() == binary &&
                app.GetProperty("arguments").GetString() == "\"--steamvr-startup\"",
                "Enabling autostart registers a dashboard overlay with the exact executable and startup argument");
        }
        var disabled = SteamVrAutoStartService.Configure(AutoStartOperation.Disable, backend, launch, manifest);
        check(disabled is { Success: true, Enabled: false } && backend.Installed && backend.Registrations == 1,
            "Disabling clears the SteamVR flag without changing registration or closing the app");
        backend.Enabled = true; var changes = backend.Changes;
        status = SteamVrAutoStartService.Configure(AutoStartOperation.Status, backend, launch, manifest);
        check(status is { Success: true, Enabled: true, RegistrationCurrent: true } && backend.Changes == changes,
            "Reading autostart respects a flag changed outside Wrist Music");
        enabled = SteamVrAutoStartService.Configure(AutoStartOperation.Enable, backend, launch, manifest);
        check(enabled.Success && backend.Registrations == 1, "Re-enabling a current registration does not duplicate the manifest entry");

        var newBinary = Path.Combine(directory, "新版 腕间音乐.exe"); File.WriteAllBytes(newBinary, [0]);
        var moved = AppLaunch.ForProcess(newBinary, Path.ChangeExtension(newBinary, ".dll"));
        status = SteamVrAutoStartService.Configure(AutoStartOperation.Status, backend, moved, manifest);
        enabled = SteamVrAutoStartService.Configure(AutoStartOperation.Enable, backend, moved, manifest);
        check(status is { Enabled: true, RegistrationCurrent: false } && enabled is { Success: true, RegistrationCurrent: true } && backend.Registrations == 2,
            "Moving the executable reports a stale path and re-registration updates it");

        var hosted = AppLaunch.ForProcess(Path.Combine(directory, "dotnet.exe"), Path.Combine(directory, "中文 空格", "WristMusic.dll"));
        check(hosted.PrefixArguments.Length == 1 && hosted.StartupArguments.StartsWith("\"" + hosted.PrefixArguments[0] + "\" ", StringComparison.Ordinal),
            "Development launches quote the assembly path and retain the dotnet host");

        using var rejected = new FakeBackend { IgnoreFlagChanges = true };
        var unconfirmed = SteamVrAutoStartService.Configure(AutoStartOperation.Enable, rejected, launch, Path.Combine(directory, "rejected.vrmanifest"));
        check(unconfirmed is { Success: false, Enabled: null }, "A rejected SteamVR flag is never reported as enabled");
        using var broken = new FakeBackend { FailRegistration = true };
        var failed = SteamVrAutoStartService.Configure(AutoStartOperation.Enable, broken, launch, Path.Combine(directory, "broken.vrmanifest"));
        check(!failed.Success && broken.FlagChanges == 0, "A registration failure does not attempt to enable autostart");
        using var unknown = new FakeBackend();
        disabled = SteamVrAutoStartService.Configure(AutoStartOperation.Disable, unknown, launch, Path.Combine(directory, "absent.vrmanifest"));
        check(disabled is { Success: true, Enabled: false } && unknown.Changes == 0, "Disabling an unregistered application makes no SteamVR changes");

        var ownedManifest = Path.Combine(directory, "owned-uninstall.vrmanifest");
        File.WriteAllText(ownedManifest, SteamVrAutoStartService.CreateManifest(launch));
        using var ownedBackend = new FakeBackend { Installed = true, Enabled = true };
        var foreign = SteamVrAutoStartService.Configure(AutoStartOperation.RemoveOwned, ownedBackend, moved, ownedManifest);
        check(foreign.Success && File.Exists(ownedManifest) && ownedBackend.Changes == 0 && ownedBackend.Enabled,
            "Uninstall preserves an autostart registration owned by another executable path");
        var removed = SteamVrAutoStartService.Configure(AutoStartOperation.RemoveOwned, ownedBackend, launch, ownedManifest);
        check(removed is { Success: true, Enabled: false } && !File.Exists(ownedManifest) && !ownedBackend.Installed,
            "Uninstall disables and unregisters only its own executable before deleting its manifest");

        var client = new FakeClient();
        var store = new SettingsStore(Path.Combine(directory, "ui-settings.json")); store.Save(new UserSettings());
        using var vm = new MainViewModel(true, store, client);
        client.Next = new(false, null, "注册被拒绝"); await vm.SetFollowSteamVr(true);
        check(!vm.FollowSteamVr && !store.Load().FollowSteamVr && vm.CanChangeSteamVrStartup,
            "The settings switch rolls back after failure and does not save an enabled preference");
        client.Next = new(true, true, "已启用"); await vm.SetFollowSteamVr(true);
        check(vm.FollowSteamVr && store.Load().FollowSteamVr, "A confirmed SteamVR registration is saved");
        client.Next = new(false, null, "无法确认关闭"); await vm.SetFollowSteamVr(false);
        check(vm.FollowSteamVr && store.Load().FollowSteamVr, "A failed disable preserves the last confirmed preference");
        client.Next = new(true, false, "已关闭"); await vm.RefreshSteamVrStartup();
        check(!vm.FollowSteamVr && !store.Load().FollowSteamVr, "Reading SteamVR synchronizes an external disable into the settings switch");
        client.Next = new(true, true, "路径已变化", false); await vm.RefreshSteamVrStartup();
        check(vm.NeedsSteamVrStartupRepair, "An outdated autostart path exposes the update action");
        client.Next = new(true, true, "路径已更新"); await vm.SetFollowSteamVr(true);
        check(!vm.NeedsSteamVrStartupRepair && vm.FollowSteamVr, "Updating the launch path clears the repair state");

        // A second thread is necessary because Windows mutex ownership is reentrant on one thread.
        var name = @"Local\WristMusic.Test." + Guid.NewGuid().ToString("N");
        using var owner = new SingleInstanceGate(name);
        var duplicateBlocked = await Task.Run(() => { using var other = new SingleInstanceGate(name); return !other.Entered; });
        check(owner.Entered && duplicateBlocked, "A running desktop instance prevents a duplicate SteamVR startup instance");
    }

    private sealed class FakeBackend : IAutoStartBackend
    {
        public bool Installed { get; set; }
        public bool Enabled { get; set; }
        public bool IgnoreFlagChanges { get; init; }
        public bool FailRegistration { get; init; }
        public int Registrations { get; private set; }
        public int FlagChanges { get; private set; }
        public int Changes => Registrations + FlagChanges;
        public void Register(string path) { if (FailRegistration) throw new InvalidOperationException("Registration rejected"); Registrations++; Installed = true; }
        public void Unregister(string path) { Installed = false; Enabled = false; }
        public void SetEnabled(bool enabled) { FlagChanges++; if (!IgnoreFlagChanges) Enabled = enabled; }
        public void Dispose() { }
    }
    private sealed class FakeClient : IAutoStartClient
    {
        public AutoStartResult Next { get; set; } = new(true, false, "未启用");
        public Task<AutoStartResult> Execute(AutoStartOperation operation) => Task.FromResult(Next);
    }
}
