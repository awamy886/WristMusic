using System.IO;
using System.Text.Json;
using WristMusic.Services;

namespace WristMusic;

internal static class VrStabilityTests
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        var defaults = new UserSettings();
        check(VrControllerInput.CombinePhysicalB(new(true, false), new(true, true)) is { Valid: true, Down: true } &&
            VrControllerInput.CombinePhysicalB(new(true, true), new(true, false)) is { Valid: true, Down: true },
            "A valid model transform with an inactive press path cannot hide a real press on that B component's legacy mask");
        check(VrControllerInput.CombinePhysicalB(new(false, true), new(false, true)) is { Valid: false, Down: false },
            "Unavailable physical B channels cannot contribute stale pressed bits");
        var shortcut = new VrDoubleBShortcut();
        var released = new VrButtonState(true, false); var pressed = new VrButtonState(true, true);
        shortcut.Update(true, 0, default, default, released, default);
        shortcut.Update(true, .1, default, default, pressed, default);
        shortcut.Update(true, .2, default, default, released, default);
        check(shortcut.Update(true, .35, default, default, pressed, default),
            "An inactive or failed SteamVR action still allows double B through the physical component path");
        shortcut.Reset();
        shortcut.Update(true, 1, released, default, released, default);
        shortcut.Update(true, 1.1, released, default, pressed, default);
        shortcut.Update(true, 1.13, pressed, default, pressed, default);
        shortcut.Update(true, 1.2, released, default, released, default);
        var physicalOpened = shortcut.Update(true, 1.35, released, default, pressed, default);
        check(physicalOpened && !shortcut.Update(true, 1.38, pressed, default, pressed, default),
            "Action and physical channels reporting the same double press one poll apart open the panel once");
        shortcut.Reset();
        shortcut.Update(true, 2, default, default, released, released);
        shortcut.Update(true, 2.1, default, default, pressed, released);
        shortcut.Update(true, 2.2, default, default, released, released);
        check(!shortcut.Update(true, 2.35, default, default, released, pressed),
            "One physical B press on each hand cannot combine into a double press");
        shortcut.Reset(); shortcut.Update(false, 3, default, default, released, default);
        shortcut.Update(false, 3.1, default, default, pressed, default); shortcut.Update(false, 3.2, default, default, released, default);
        check(!shortcut.Update(false, 3.35, default, default, pressed, default),
            "The disabled shortcut also suppresses physical-component fallback input");
        shortcut.Reset();
        shortcut.Update(true, 4, default, default, released, default);
        shortcut.Update(true, 4.1, default, default, pressed, default);
        shortcut.Update(true, 4.15, default, default, released, default);
        var firstToggle = shortcut.Update(true, 4.25, default, default, pressed, default);
        shortcut.Update(true, 4.3, default, default, released, default);
        shortcut.Update(true, 4.4, default, default, pressed, default);
        shortcut.Update(true, 4.45, default, default, released, default);
        check(firstToggle && shortcut.Update(true, 4.55, default, default, pressed, default),
            "Two deliberate double presses in quick succession both activate, so opening can be followed immediately by hiding");
        check(VrControllerInput.IsBComponent("button_b") && VrControllerInput.IsBComponent("b_button") && VrControllerInput.IsBComponent("B") &&
            !VrControllerInput.IsBComponent("button_enter") && !VrControllerInput.IsBComponent("button_system") && !VrControllerInput.IsBComponent("button_a"),
            "Physical fallback identifies a B model component without treating menu, system or A as B");
        using (var registration = new VrInputRegistration(Path.Combine(folder, "input-registration")))
        {
            using var manifestJson = JsonDocument.Parse(VrInputRegistration.CreateManifest(AppLaunch.Current, registration.ActionManifest));
            var application = manifestJson.RootElement.GetProperty("applications")[0];
            check(application.GetProperty("app_key").GetString() != SteamVrAutoStartService.ApplicationKey &&
                application.GetProperty("action_manifest_path").GetString() == registration.ActionManifest && application.GetProperty("is_dashboard_overlay").GetBoolean(),
                "Input registration has an explicit action manifest and an identity independent of the autostart entry");
            using var bindingJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(registration.ActionManifest)!, "bindings_oculus_touch.json")));
            check(bindingJson.RootElement.GetProperty("app_key").GetString() == VrInputRegistration.ApplicationKey,
                "The Quest binding uses the same explicit application identity as the runtime input manifest");
            using var picoJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(registration.ActionManifest)!, "bindings_pico_controller.json")));
            using var beforeDiscovery = JsonDocument.Parse(File.ReadAllText(registration.ActionManifest));
            check(picoJson.RootElement.GetProperty("controller_type").GetString() == "pico_controller" &&
                picoJson.RootElement.GetProperty("app_key").GetString() == VrInputRegistration.ApplicationKey &&
                beforeDiscovery.RootElement.GetProperty("default_bindings").EnumerateArray().Any(b => b.GetProperty("controller_type").GetString() == "pico_controller"),
                "PICO Connect has its native controller binding before a headset or controller is discovered");
            check(registration.AddControllerProfile("pico_driver_fixture", "{\"input_source\":{\"/input/b\":{\"type\":\"button\",\"click\":true},\"/input/trigger\":{\"type\":\"trigger\",\"value\":true}}}"),
                "A new controller type exposing physical B gets a default binding directly from its real driver profile");
            using var adapted = JsonDocument.Parse(File.ReadAllText(registration.ActionManifest));
            var driverBinding = adapted.RootElement.GetProperty("default_bindings").EnumerateArray().Single(b => b.GetProperty("controller_type").GetString() == "pico_driver_fixture");
            using var driverJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(registration.ActionManifest)!, driverBinding.GetProperty("binding_url").GetString()!)));
            var driverSources = driverJson.RootElement.GetProperty("bindings").GetProperty("/actions/wrist").GetProperty("sources");
            check(driverSources.GetArrayLength() == 3 && driverSources[0].GetProperty("path").GetString() == "/user/hand/right/input/b" &&
                !registration.AddControllerProfile("pico_driver_fixture", "{}"),
                "Driver-generated bindings contain physical right B and both triggers without duplicating a controller entry");
            check(!registration.AddControllerProfile("vive_fixture", "{\"input_source\":{\"/input/system\":{\"type\":\"button\",\"click\":true}}}"),
                "A driver with only menu or system buttons does not receive an invented B binding");
        }
        check(!defaults.RaiseToShow && defaults.DoubleBToOpen, "Raise-to-show defaults off while double-B opening defaults on");
        var store = new SettingsStore(Path.Combine(folder, "visibility-settings.json"));
        File.WriteAllText(store.FilePath, "{\"RaiseToShow\":true,\"RightWrist\":true,\"Scale\":112}");
        var upgraded = store.Load();
        check(!upgraded.RaiseToShow && upgraded.DoubleBToOpen && upgraded.RightWrist && upgraded.Scale == 112,
            "Upgrading the old automatic raise default disables it without resetting wrist placement");
        upgraded.RaiseToShow = true; upgraded.DoubleBToOpen = false; store.Save(upgraded);
        check(store.Load() is { RaiseToShow: true, DoubleBToOpen: false, VisibilitySettingsVersion: 1 },
            "Explicit visibility preferences survive after the one-time migration");
        var visibility = new OverlayVisibilityState();
        check(!visibility.Update(true, true, true, 0) && visibility.Update(true, true, true, .13),
            "Automatic showing requires a short stable interval");
        var transitions = 0; var last = visibility.Visible;
        for (var i = 0; i < 180; i++)
        {
            var shown = visibility.Update(true, true, i % 2 == 0, .2 + i / 30d);
            if (shown != last) transitions++; last = shown;
        }
        check(transitions == 0 && visibility.Visible, "Rapid raised-pose boundary jitter does not repeatedly show and hide the panel");
        visibility.Update(true, true, false, 7);
        check(!visibility.Update(true, true, false, 7.46), "A sustained lowered wrist still hides the panel");
        visibility.Update(true, true, true, 8, true);
        check(visibility.Update(true, false, false, 8.01) && visibility.Update(true, true, true, 8.1),
            "A brief tracking dropout preserves a visible panel instead of flashing it");
        visibility.Update(true, false, false, 9);
        check(!visibility.Update(true, false, false, 9.26), "A sustained tracking loss hides the panel safely");
        check(visibility.Update(true, true, true, 10, true) && !visibility.Update(false, true, true, 10.01),
            "Manual opening is immediate and explicit hiding is immediate");
        var button = new DoublePressDetector();
        check(!button.Update(true, true, true, 0) && !button.Update(true, true, true, .3),
            "A held B button on connection cannot open the panel");
        button.Update(true, true, false, .4); button.Update(true, true, true, .5);
        button.Update(true, true, false, .6);
        check(button.Update(true, true, true, .75) && !button.Update(true, true, true, .8),
            "Two separate B presses open once, with no repeats while held");
        button.Reset(); button.Update(true, true, false, 1); button.Update(true, true, true, 1.1); button.Update(true, true, false, 1.2);
        check(!button.Update(true, true, true, 1.8), "Slow separate B presses are not treated as a double press");
        button.Update(false, true, false, 2); button.Update(false, true, true, 2.1);
        check(!button.Update(false, true, false, 2.2) && !button.Update(false, true, true, 2.3),
            "Turning off double-B suppresses shortcut requests");
        button.Update(true, true, false, 3); button.Update(true, true, true, 3.1); button.Update(true, false, false, 3.2);
        check(!button.Update(true, true, true, 3.3), "Input loss cancels a pending double press rather than completing it on reconnection");
        using (var vm = new MainViewModel(true, store))
        {
            vm.OverlayEnabled = false; vm.VrSettingsOpen = true;
            vm.ToggleVrPanelFromController(false);
            check(vm.OverlayEnabled && !vm.VrSettingsOpen && vm.VrOpenRequest == 1,
                "A controller shortcut restores a hidden panel directly to the music UI");
            vm.VrSettingsOpen = true; vm.ToggleVrPanelFromController(true);
            check(!vm.OverlayEnabled && !vm.VrSettingsOpen && vm.VrOpenRequest == 1,
                "The next controller double press hides a visible panel, including from its settings view");
            vm.ToggleVrPanelFromController(false);
            check(vm.OverlayEnabled && vm.VrOpenRequest == 2, "A third controller double press reopens the panel after hiding it");
            vm.RaiseToShow = true; vm.ToggleVrPanelFromController(false);
            check(vm.OverlayEnabled && vm.VrOpenRequest == 3,
                "A raise-hidden enabled panel opens on double B instead of being mistaken for a visible panel");
            vm.HideVrPanelCommand.Execute(null);
            check(!vm.OverlayEnabled, "The VR hide control can close a manually opened panel");
        }
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Input");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(inputPath, "actions.json")));
        var names = manifest.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();
        foreach (var binding in manifest.RootElement.GetProperty("default_bindings").EnumerateArray())
        {
            var path = Path.Combine(inputPath, binding.GetProperty("binding_url").GetString()!);
            using var profile = JsonDocument.Parse(File.ReadAllText(path));
            var sources = profile.RootElement.GetProperty("bindings").GetProperty("/actions/wrist").GetProperty("sources").EnumerateArray().ToArray();
            var outputs = sources.Select(s => s.GetProperty("inputs").GetProperty("click").GetProperty("output").GetString()).ToArray();
            check(profile.RootElement.GetProperty("controller_type").GetString() == binding.GetProperty("controller_type").GetString() &&
                outputs.All(n => names.Contains(n)) && outputs.Contains("/actions/wrist/in/select_right") && outputs.Contains("/actions/wrist/in/select_left"),
                "Packaged controller binding actions and both pointer triggers match the action manifest: " + binding.GetProperty("controller_type").GetString());
            check(sources.Where(s => s.GetProperty("inputs").GetProperty("click").GetProperty("output").GetString()!.Contains("open_")).All(s => s.GetProperty("path").GetString()!.EndsWith("/input/b")),
                "Shortcut bindings use a physical B source rather than a system/menu button: " + binding.GetProperty("controller_type").GetString());
        }
    }
}
