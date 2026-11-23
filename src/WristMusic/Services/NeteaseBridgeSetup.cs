using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WristMusic.Services;

// Opt-in installation only. The normal application startup never invokes this service.
public static class NeteaseBridgeSetup
{
    public const string GuideUrl = "https://github.com/apoint123/inflink-rs#使用方法";
    internal static string ReportPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "BridgeDownloads", "setup-result.json");
    internal const string LoaderHash = "A7C77AF418D7940E63FAA58EA036FBA1F4BAAD497947109EA52ED78C8E86608F";
    internal const string PluginHash = "7D0B7B863B072A99B88EB614C788099315E690920889AACA22685D3663661468";
    private const string LoaderUrl = "https://github.com/std-microblock/chromatic/releases/download/1.3.4/BetterNCMII.dll";
    private const string PluginUrl = "https://github.com/apoint123/inflink-rs/releases/download/v3.4.0/InfLink-rs.plugin";

    public static async Task<string> Install(string executable)
    {
        var folder = ValidateExecutable(executable);
        EnsurePlayerExited();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BETTERNCM_PROFILE")))
            throw new InvalidOperationException("检测到自定义 BetterNCM 数据目录，请按插件说明手动安装，避免更改现有配置。");
        var profile = @"C:\betterncm";
        var loader = Path.Combine(folder, "msimg32.dll");
        var plugin = Path.Combine(profile, "plugins", "InfLink-rs.plugin");
        EnsureCompatibleFile(loader, LoaderHash);
        EnsureCompatibleFile(plugin, PluginHash);
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "BridgeDownloads");
        Directory.CreateDirectory(cache);
        var loaderCache = await Download(LoaderUrl, Path.Combine(cache, "BetterNCM-1.3.4-x64.dll"), LoaderHash);
        var pluginCache = await Download(PluginUrl, Path.Combine(cache, "InfLink-rs-3.4.0.plugin"), PluginHash);
        EnsurePlayerExited();
        var created = new List<string>();
        var newProfile = !Directory.Exists(profile) || !Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Any();
        try
        {
            // Stage the dependency before installing the loader; on failure remove only files created here.
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            CopyNew(pluginCache, plugin, created);
            if (newProfile)
            {
                WriteNew(Path.Combine(profile, "disable_list.txt"), "PluginMarket\n", created);
                WriteNew(Path.Combine(profile, "config.json"), "{\"cc.microblock.pluginmarket.source\":\"\"}", created);
            }
            CopyNew(loaderCache, loader, created);
            File.WriteAllText(Path.Combine(cache, "installation.json"), JsonSerializer.Serialize(new
            {
                installedAt = DateTimeOffset.Now, executable, loader, plugin, loaderVersion = "1.3.4", pluginVersion = "3.4.0",
                loaderSha256 = LoaderHash, pluginSha256 = PluginHash, createdFiles = created
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            foreach (var file in created.AsEnumerable().Reverse()) { try { File.Delete(file); } catch { } }
            throw;
        }
        return "网易云进度插件已安装。重新打开网易云并播放歌曲后，将自动读取真实进度并启用进度跳转。";
    }

    internal static string ValidateExecutable(string executable)
    {
        var full = Path.GetFullPath(executable);
        if (!File.Exists(full) || !Path.GetFileName(full).Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请先在应用管理中配置网易云 cloudmusic.exe 的路径。");
        var v = FileVersionInfo.GetVersionInfo(full);
        if (v.FileMajorPart != 3 || v.FileMinorPart != 1 || v.FileBuildPart < 20 || v.FileBuildPart > 41)
            throw new InvalidOperationException("此兼容方案已验证的网易云范围为 3.1.20～3.1.41。其他版本请使用插件作者的安装说明。");
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length < 64) throw new InvalidDataException("无效的网易云程序。");
        var pe = BitConverter.ToInt32(bytes, 0x3c);
        if (pe < 0 || pe + 6 > bytes.Length || BitConverter.ToUInt16(bytes, pe + 4) != 0x8664)
            throw new InvalidOperationException("此自动安装入口仅支持 64 位网易云，请按插件说明为其他架构手动安装。");
        return Path.GetDirectoryName(full)!;
    }
    internal static void EnsureCompatibleFile(string path, string hash)
    {
        if (File.Exists(path) && Hash(path) != hash)
            throw new InvalidOperationException("检测到已有其他版本组件，不会覆盖：" + path + "。请通过原有插件管理器安装或更新 InfLink-rs。");
    }
    private static async Task<string> Download(string url, string path, string hash)
    {
        if (File.Exists(path) && Hash(path) == hash) return path;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WristMusic/6.0.0");
        var data = await client.GetByteArrayAsync(url);
        if (Convert.ToHexString(SHA256.HashData(data)) != hash) throw new InvalidDataException("插件文件校验失败，安装已取消。");
        await File.WriteAllBytesAsync(path, data); return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void CopyNew(string source, string target, List<string> created)
    {
        EnsureCompatibleFile(target, Hash(source));
        if (File.Exists(target)) return;
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, staging, false); File.Move(staging, target, false); created.Add(target); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static void WriteNew(string path, string content, List<string> created)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); created.Add(path); using var writer = new StreamWriter(file); writer.Write(content); }
    private static void EnsurePlayerExited()
    {
        var processes = Process.GetProcessesByName("cloudmusic");
        try { if (processes.Length > 0) throw new InvalidOperationException("请先从网易云托盘菜单退出客户端，再安装进度插件。安装完成后重新打开网易云。"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
