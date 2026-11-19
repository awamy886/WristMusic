using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using Valve.VR;

namespace WristMusic.Services;

// Temporary input identity, independent of the user's persistent SteamVR autostart entry.
internal sealed class VrInputRegistration : IDisposable
{
    internal const string ApplicationKey = "cn.xingmengyu.wristmusic.input";
    internal string ActionManifest { get; }
    private readonly string _manifest;
    private bool _registered;
    internal VrInputRegistration(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "SteamVR", "Input");
        Directory.CreateDirectory(directory);
        foreach (var source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Input"), "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(source))!;
            if (json["bindings"] is not null) json["app_key"] = ApplicationKey;
            WriteIfChanged(Path.Combine(directory, Path.GetFileName(source)), json.ToJsonString());
        }
        ActionManifest = Path.Combine(directory, "actions.json");
        _manifest = Path.Combine(directory, "WristMusic.input.vrmanifest");
        WriteIfChanged(_manifest, CreateManifest(AppLaunch.Current, ActionManifest));
    }
    internal void Register()
    {
        var apps = OpenVR.Applications ?? throw new InvalidOperationException("SteamVR 应用管理不可用。");
        Check(apps.AddApplicationManifest(_manifest, true)); _registered = true;
        Check(apps.IdentifyApplication(0, ApplicationKey));
    }
    internal void DiscoverControllerBindings(CVRSystem? system)
    {
        if (system is null) return;
        for (uint device = 0; device < OpenVR.k_unMaxTrackedDeviceCount; device++)
        {
            if (!system.IsTrackedDeviceConnected(device) || system.GetTrackedDeviceClass(device) != ETrackedDeviceClass.Controller) continue;
            string Read(ETrackedDeviceProperty property)
            {
                var error = ETrackedPropertyError.TrackedProp_Success; var text = new StringBuilder(4096);
                system.GetStringTrackedDeviceProperty(device, property, text, (uint)text.Capacity, ref error);
                return error == ETrackedPropertyError.TrackedProp_Success ? text.ToString() : "";
            }
            var controller = Read(ETrackedDeviceProperty.Prop_ControllerType_String);
            var profile = Read(ETrackedDeviceProperty.Prop_InputProfilePath_String);
            if (string.IsNullOrEmpty(controller) || string.IsNullOrEmpty(profile)) continue;
            try
            {
                var path = profile;
                if (!File.Exists(path))
                {
                    var buffer = new StringBuilder(8192);
                    OpenVR.Resources?.GetResourceFullPath(profile, "", buffer, (uint)buffer.Capacity); path = buffer.ToString();
                }
                if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                if (AddControllerProfile(controller, File.ReadAllText(path))) VrDiagnostics.Write($"Created B binding from live driver: controller={controller}, profile={path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { VrDiagnostics.Write($"Driver profile {controller}: {e.Message}"); }
        }
    }
    internal bool AddControllerProfile(string controllerType, string profileJson)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ActionManifest))!;
        var defaults = manifest["default_bindings"]!.AsArray();
        if (defaults.Any(binding => binding?["controller_type"]?.GetValue<string>() == controllerType)) return false;
        var profile = JsonNode.Parse(profileJson);
        if (profile?["input_source"] is not JsonObject sources || sources["/input/b"] is not JsonObject b ||
            b["type"]?.GetValue<string>() != "button" || b["click"]?.GetValue<bool>() != true) return false;
        var entries = new JsonArray();
        void Add(string path, string mode, string action) => entries.Add(new JsonObject {
            ["path"] = path, ["mode"] = mode, ["inputs"] = new JsonObject { ["click"] = new JsonObject { ["output"] = "/actions/wrist/in/" + action } } });
        Add("/user/hand/right/input/b", "button", "open_right");
        if (sources["/input/trigger"] is JsonObject trigger && trigger["type"]?.GetValue<string>() == "trigger")
        { Add("/user/hand/right/input/trigger", "trigger", "select_right"); Add("/user/hand/left/input/trigger", "trigger", "select_left"); }
        var binding = new JsonObject { ["app_key"] = ApplicationKey, ["controller_type"] = controllerType,
            ["name"] = "Wrist Music", ["description"] = "B and trigger bindings from the connected driver profile",
            ["bindings"] = new JsonObject { ["/actions/wrist"] = new JsonObject { ["sources"] = entries } } };
        var name = "bindings_driver_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(controllerType)))[..16] + ".json";
        WriteIfChanged(Path.Combine(Path.GetDirectoryName(ActionManifest)!, name), binding.ToJsonString());
        defaults.Add(new JsonObject { ["controller_type"] = controllerType, ["binding_url"] = name });
        WriteIfChanged(ActionManifest, manifest.ToJsonString()); return true;
    }
    internal static string CreateManifest(AppLaunch launch, string actions) => JsonSerializer.Serialize(new
    {
        applications = new[] { new { app_key = ApplicationKey, launch_type = "binary", binary_path_windows = launch.BinaryPath,
            arguments = launch.StartupArguments, working_directory = launch.WorkingDirectory, is_dashboard_overlay = true, action_manifest_path = actions,
            strings = new { en_us = new { name = "Wrist Music", description = "Wrist Music controller input" },
                zh_cn = new { name = "腕间音乐", description = "腕间音乐 · 手柄按键" } } } }
    });
    private static void WriteIfChanged(string path, string content)
    { if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content); }
    private static void Check(EVRApplicationError error)
    { if (error != EVRApplicationError.None) throw new InvalidOperationException("SteamVR 按键身份注册失败：" + error); }
    public void Dispose()
    {
        if (!_registered) return;
        OpenVR.Applications?.RemoveApplicationManifest(_manifest); _registered = false;
    }
}
