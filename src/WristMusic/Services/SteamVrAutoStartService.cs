using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Valve.VR;

namespace WristMusic.Services;

public enum AutoStartOperation { Status, Enable, Disable, RemoveOwned }
public sealed record AutoStartResult(bool Success, bool? Enabled, string Message, bool RegistrationCurrent = true);
public sealed record AppLaunch(string BinaryPath, string[] PrefixArguments, string WorkingDirectory)
{
    public string StartupArguments => string.Join(" ", PrefixArguments.Append("--steamvr-startup").Select(Quote));
    private static string Quote(string argument)
    {
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static AppLaunch Current => ForProcess(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"), Assembly.GetExecutingAssembly().Location);
    public static AppLaunch ForProcess(string processPath, string assemblyPath) => new(Path.GetFullPath(processPath),
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(assemblyPath)] : [], Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
}

public interface IAutoStartClient
{
    Task<AutoStartResult> Execute(AutoStartOperation operation);
}

public interface IAutoStartBackend : IDisposable
{
    bool Installed { get; }
    bool Enabled { get; }
    void Register(string manifestPath);
    void Unregister(string manifestPath);
    void SetEnabled(bool enabled);
}

public sealed class SteamVrAutoStartService : IAutoStartClient
{
    public const string ApplicationKey = "cn.xingmengyu.wristmusic";
    public const string HelperArgument = "--steamvr-autostart-helper";
    private const string ResultPrefix = "WRISTMUSIC_AUTOSTART_RESULT:";
    public static string ManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "SteamVR", "WristMusic.vrmanifest");

    // Isolate Utility initialization from the UI process's live Overlay connection.
    public async Task<AutoStartResult> Execute(AutoStartOperation operation)
    {
        if (operation != AutoStartOperation.RemoveOwned && !SteamVrConnectionPolicy.IsRunning()) return new(false, null, "SteamVR 未运行。请启动后重新检测；此操作不会主动启动 SteamVR。");
        try
        {
            var launch = AppLaunch.Current;
            var start = new ProcessStartInfo(launch.BinaryPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = launch.WorkingDirectory,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in launch.PrefixArguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(HelperArgument); start.ArgumentList.Add(operation.ToString());
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 SteamVR 设置进程。");
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                return new(false, null, "SteamVR 设置操作超时，当前状态未确认。请启动 SteamVR 后点击重新检测。");
            }
            var response = (await output).Split('\n').LastOrDefault(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal));
            await errors;
            if (response is null) return new(false, null, "无法读取 SteamVR 设置结果，请点击重新检测。");
            return JsonSerializer.Deserialize<AutoStartResult>(response[ResultPrefix.Length..]) ?? new(false, null, "SteamVR 返回了无效的设置结果。");
        }
        catch (Exception e) { return new(false, null, "SteamVR 自动启动设置失败：" + e.Message); }
    }

    public static int RunHelper(string operationText)
    {
        AutoStartResult result;
        if (!Enum.TryParse<AutoStartOperation>(operationText, true, out var operation) || !Enum.IsDefined(operation))
            result = new(false, null, "无效的 SteamVR 自动启动操作。");
        else
        {
            try
            {
                var launch = AppLaunch.Current;
                if (operation == AutoStartOperation.RemoveOwned && !RegistrationMatches(launch, ManifestPath))
                    result = new(true, null, "当前安装目录未持有自动启动注册，已保留其他目录的配置。");
                else
                {
                    using var backend = new OpenVrAutoStartBackend();
                    result = Configure(operation, backend, launch, ManifestPath);
                    if (operation == AutoStartOperation.RemoveOwned && result is { Success: true, Enabled: false })
                    {
                        var store = new SettingsStore();
                        if (File.Exists(store.FilePath))
                        {
                            var settings = store.Load();
                            if (store.LoadError is null) { settings.FollowSteamVr = false; store.Save(settings); }
                        }
                    }
                }
            }
            catch (DllNotFoundException) { result = new(false, null, "OpenVR 组件缺失，请保留程序目录中的 openvr_api.dll。"); }
            catch (Exception e) { result = new(false, null, e.Message); }
        }
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(result)); Console.Out.Flush();
        return result.Success ? 0 : 1;
    }

    public static AutoStartResult Configure(AutoStartOperation operation, IAutoStartBackend backend, AppLaunch launch, string manifestPath)
    {
        try
        {
            if (operation == AutoStartOperation.RemoveOwned)
            {
                if (!RegistrationMatches(launch, manifestPath)) return new(true, null, "已保留其他程序目录的自动启动配置。");
                if (backend.Installed)
                {
                    backend.SetEnabled(false);
                    if (backend.Enabled) return new(false, null, "SteamVR 未确认关闭自动启动。");
                    backend.Unregister(manifestPath);
                    if (backend.Installed) return new(false, null, "SteamVR 未确认移除应用注册。");
                }
                File.Delete(manifestPath);
                return new(true, false, "已清理当前安装目录的 SteamVR 自动启动注册。");
            }
            if (operation == AutoStartOperation.Enable)
            {
                if (!File.Exists(launch.BinaryPath)) throw new FileNotFoundException("程序文件已移动，请从新的程序目录重新开启此选项。", launch.BinaryPath);
                if (!backend.Installed || !RegistrationMatches(launch, manifestPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                    var temporary = manifestPath + ".tmp";
                    File.WriteAllText(temporary, CreateManifest(launch), new UTF8Encoding(false));
                    File.Move(temporary, manifestPath, true);
                    backend.Register(manifestPath);
                }
                if (!backend.Installed) return new(false, null, "SteamVR 未确认应用注册，请启动 SteamVR 后重试。");
                backend.SetEnabled(true);
            }
            else if (operation == AutoStartOperation.Disable && backend.Installed) backend.SetEnabled(false);

            var enabled = backend.Installed && backend.Enabled;
            if (operation != AutoStartOperation.Status && enabled != (operation == AutoStartOperation.Enable))
                return new(false, null, "SteamVR 未确认自动启动设置，当前状态未确认，请重新检测。");
            var current = !enabled || RegistrationMatches(launch, manifestPath);
            return new(true, enabled, enabled
                ? current ? "已启用：下次启动 SteamVR 时自动运行腕间音乐。" : "已启用，但程序目录已变化。请更新启动路径。"
                : "未启用：启动 SteamVR 时不会自动运行腕间音乐。", current);
        }
        catch (Exception e) { return new(false, null, "SteamVR 自动启动设置未完成：" + e.Message); }
    }

    public static string CreateManifest(AppLaunch launch) => JsonSerializer.Serialize(new
    {
        source = "builtin",
        applications = new[] { new {
            app_key = ApplicationKey, launch_type = "binary", binary_path_windows = launch.BinaryPath,
            arguments = launch.StartupArguments, working_directory = launch.WorkingDirectory,
            is_dashboard_overlay = true,
            strings = new {
                en_us = new { name = "Wrist Music", description = "Wrist Music - Xing Meng Yu Studio" },
                zh_cn = new { name = "腕间音乐", description = "星梦屿工作室 · SteamVR 音乐控制" }
            }
        } }
    }, new JsonSerializerOptions { WriteIndented = true });

    private static bool RegistrationMatches(AppLaunch launch, string manifestPath)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var app = json.RootElement.GetProperty("applications").EnumerateArray().First(a => a.GetProperty("app_key").GetString() == ApplicationKey);
            return string.Equals(app.GetProperty("binary_path_windows").GetString(), launch.BinaryPath, StringComparison.OrdinalIgnoreCase)
                && app.GetProperty("arguments").GetString() == launch.StartupArguments
                && string.Equals(app.GetProperty("working_directory").GetString(), launch.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

internal sealed class OpenVrAutoStartBackend : IAutoStartBackend
{
    private readonly CVRApplications _applications;
    private bool _initialized;
    public OpenVrAutoStartBackend()
    {
        if (!SteamVrConnectionPolicy.IsRunning()) throw new InvalidOperationException("SteamVR 未运行，请启动后重试。自动启动设置不会主动启动 SteamVR。");
        if (!OpenVR.IsRuntimeInstalled()) throw new InvalidOperationException("未检测到 SteamVR。请先安装 SteamVR，再开启此功能。");
        var error = EVRInitError.None;
        OpenVR.Init(ref error, EVRApplicationType.VRApplication_Utility);
        if (error != EVRInitError.None) throw new InvalidOperationException("无法访问 SteamVR 设置：" + error + "。请启动 SteamVR 后重试。");
        _initialized = true;
        try { _applications = OpenVR.Applications ?? throw new InvalidOperationException("SteamVR 应用管理接口不可用。"); }
        catch { Dispose(); throw; }
    }
    public bool Installed => _applications.IsApplicationInstalled(SteamVrAutoStartService.ApplicationKey);
    public bool Enabled => _applications.GetApplicationAutoLaunch(SteamVrAutoStartService.ApplicationKey);
    public void Register(string path)
    {
        var error = _applications.AddApplicationManifest(path, false);
        if (error == EVRApplicationError.AppKeyAlreadyExists)
        {
            // Only replace our own known manifest registration, never an unrelated application's entry.
            Check(_applications.RemoveApplicationManifest(path));
            error = _applications.AddApplicationManifest(path, false);
        }
        Check(error);
    }
    public void SetEnabled(bool enabled) => Check(_applications.SetApplicationAutoLaunch(SteamVrAutoStartService.ApplicationKey, enabled));
    public void Unregister(string path) => Check(_applications.RemoveApplicationManifest(path));
    private static void Check(EVRApplicationError error)
    {
        if (error != EVRApplicationError.None) throw new InvalidOperationException("SteamVR 返回错误：" + error);
    }
    public void Dispose() { if (_initialized) { OpenVR.Shutdown(); _initialized = false; } }
}
