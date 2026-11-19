using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WristMusic.Services;

public sealed class PlayerService
{
    public List<AppEntry> Apps { get; } = [
        new("netease", "网易云音乐", "云", "#E45D71", ["cloudmusic", "netease", "inflink"]),
        new("qq", "QQ音乐", "Q", "#46CCA1", ["qqmusic"]),
        new("kugou", "酷狗音乐", "K", "#5EA8F2", ["kugou", "kgmusic"]),
        new("soda", "汽水音乐", "汽", "#D0DB70", ["soda", "sodamusic", "qishui"]),
        new("spotify", "Spotify", "S", "#1DB954", ["spotify"]),
        new("custom", "其他播放器", "♫", "#B79AFF", [])
    ];

    public void Discover(UserSettings settings)
    {
        foreach (var app in Apps)
        {
            if (app.Id == "spotify")
            {
                try
                {
                    var manager = new Windows.Management.Deployment.PackageManager();
                    app.StorePackage = manager.FindPackagesForUser("", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0").FirstOrDefault();
                }
                catch (Exception e) when (e is UnauthorizedAccessException or COMException or InvalidOperationException) { }
            }
            if (settings.AppPaths.TryGetValue(app.Id, out var configured) && File.Exists(configured)) { app.ExePath = configured; continue; }
            var candidates = new List<string>();
            if (app.Id == "spotify")
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Spotify\Spotify.exe"));
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var alias in app.Aliases)
            {
                try
                {
                    using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + alias + ".exe");
                    if (key?.GetValue(null) is string path) candidates.Add(path.Trim('"'));
                }
                catch (System.Security.SecurityException) { }
            }
            // Non-default drives are common. Use installer metadata instead of scanning disks.
            if (app.Id != "custom")
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var branch in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var uninstall = root.OpenSubKey(branch);
                    if (uninstall is null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var key = uninstall.OpenSubKey(name);
                        var displayName = key?.GetValue("DisplayName") as string ?? "";
                        if (!displayName.Contains(app.Name, StringComparison.OrdinalIgnoreCase) && !app.Aliases.Any(a => displayName.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;
                        if (key?.GetValue("DisplayIcon") is string icon)
                        {
                            var iconPath = System.Text.RegularExpressions.Regex.Replace(icon, @",\s*-?\d+$", "").Trim('"');
                            if (string.Equals(Path.GetExtension(iconPath), ".exe", StringComparison.OrdinalIgnoreCase)) candidates.Add(iconPath);
                        }
                        if (key?.GetValue("InstallLocation") is string location && Directory.Exists(location))
                            foreach (var alias in app.Aliases) candidates.Add(Path.Combine(location, alias + ".exe"));
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            var relative = app.Id switch
            {
                "netease" => new[] { @"NetEase\CloudMusic\cloudmusic.exe", @"CloudMusic\cloudmusic.exe" },
                "qq" => new[] { @"Tencent\QQMusic\QQMusic.exe", @"QQMusic\QQMusic.exe" },
                "kugou" => new[] { @"KuGou\KGMusic\KuGou.exe", @"KuGou\KuGou.exe" },
                "soda" => new[] { @"Soda Music\SodaMusic.exe", @"SodaMusic\SodaMusic.exe", @"SodaMusic\soda.exe" },
                "spotify" => new[] { @"Spotify\Spotify.exe" },
                _ => Array.Empty<string>()
            };
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                candidates.AddRange(relative.Select(r => Path.Combine(folder, r)));
            app.ExePath = candidates.FirstOrDefault(path => File.Exists(path) && (app.Id != "spotify" || Path.GetFileName(path).Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase))) ?? "";
        }
        Refresh();
    }

    public void Refresh()
    {
        using var snapshot = new ProcessSnapshot();
        foreach (var app in Apps) app.Running = snapshot.Items.Any(p => MatchesProcess(app, p.ProcessName));
    }
    private static bool MatchesProcess(AppEntry app, string name) => app.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(app.ExePath) && string.Equals(Path.GetFileNameWithoutExtension(app.ExePath), name, StringComparison.OrdinalIgnoreCase));

    public async Task<(bool Success, string Message)> Launch(AppEntry? app)
    {
        if (app is null) return (false, "请先在音乐来源中选择播放器，或前往应用管理。");
        if (app.Running) return (true, app.Name + " 已在运行。");
        if (!File.Exists(app.ExePath) && app.StorePackage is not null)
        {
            var entries = await app.StorePackage.GetAppListEntriesAsync();
            if (entries.FirstOrDefault() is { } entry && await entry.LaunchAsync())
                return (true, "已启动 " + app.Name);
        }
        if (!File.Exists(app.ExePath) || !string.Equals(Path.GetExtension(app.ExePath), ".exe", StringComparison.OrdinalIgnoreCase)) return (false, "未找到 " + app.Name + "，请在应用管理中选择它的 exe 文件。");
        using var process = Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.ExePath) });
        return (true, "已启动 " + app.Name);
    }
    public string Close(AppEntry? app, bool force = false)
    {
        if (app is null) return "请先选择需要关闭的播放器。";
        using var snapshot = new ProcessSnapshot();
        var matches = snapshot.Items.Where(p => MatchesProcess(app, p.ProcessName)).ToArray();
        if (matches.Length == 0) return app.Name + " 没有运行。";
        using var current = Process.GetCurrentProcess();
        var sent = false;
        var denied = false;
        foreach (var p in matches)
        {
            try
            {
                if (p.Id == current.Id || p.SessionId != current.SessionId) continue;
                if (force)
                {
                    var expected = app.ExePath;
                    if (string.IsNullOrEmpty(expected) && app.StorePackage is not null)
                        expected = Path.Combine(app.StorePackage.InstalledLocation.Path, "Spotify.exe");
                    if (string.IsNullOrEmpty(expected) || !expected.Equals(p.MainModule?.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    sent = true;
                }
                else if (p.MainWindowHandle != IntPtr.Zero) sent |= p.CloseMainWindow();
            }
            catch (System.ComponentModel.Win32Exception) { denied = true; }
            catch (InvalidOperationException) { }
        }
        if (denied) return "部分播放器进程无法关闭，请检查权限。";
        if (force) return sent ? "已向 " + app.Name + " 发送终止指令。" : "未找到路径匹配的进程，请在应用管理中确认播放器路径。";
        return sent ? "已请求关闭 " + app.Name + "；若客户端设置为关闭到托盘，请在客户端开启关闭时退出。" : "播放器位于托盘或没有主窗口，无法正常关闭；请在客户端退出。";
    }
    public bool SendMediaCommand(AppEntry? app, int command)
    {
        if (app is null) return false;
        using var snapshot = new ProcessSnapshot();
        foreach (var p in snapshot.Items.Where(p => MatchesProcess(app, p.ProcessName)))
        {
            try
            {
                var window = p.MainWindowHandle;
                if (window != IntPtr.Zero && PostMessage(window, 0x0319, window, (IntPtr)(command << 16))) return true;
            }
            catch (InvalidOperationException) { }
        }
        return false;
    }

    // Optional global compatibility path; Windows dispatches these to its current media target.
    public static void SendGlobalMediaKey(byte key)
    {
        INPUT[] input = [new() { type = 1, data = new() { keyboard = new() { wVk = key } } }, new() { type = 1, data = new() { keyboard = new() { wVk = key, dwFlags = 2 } } }];
        if (SendInput(2, input, Marshal.SizeOf<INPUT>()) != 2) throw new InvalidOperationException("Windows 未接受媒体按键。请检查客户端权限级别。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private sealed class ProcessSnapshot : IDisposable
    {
        public Process[] Items { get; } = Process.GetProcesses();
        public void Dispose() { foreach (var p in Items) p.Dispose(); }
    }
}
