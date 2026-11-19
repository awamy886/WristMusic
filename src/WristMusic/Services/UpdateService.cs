using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WristMusic.Services;

internal sealed class UpdateService
{
    internal const string Repository = "https://github.com/awamy886/WristMusic";
    internal const string MutexName = @"Local\XingMengYu.WristMusic.Updating";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly HttpClient Redirect = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private readonly Version _current = new(typeof(UpdateService).Assembly.GetName().Version!.ToString(3));
    private readonly string _cache;
    private bool Installed => File.Exists(Path.Combine(_root, "WristMusic.install.ini"));
    private string Pending => Path.Combine(_cache, "pending.json");
    private sealed record Package(string Tag, string Digest, bool Setup);

    internal UpdateService()
    {
        var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_root.ToUpperInvariant()));
        _cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WristMusic", "Updates", Convert.ToHexString(key.AsSpan(0, 12)));
    }

    internal static bool Newer(string tag, Version current) =>
        Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") &&
        Version.TryParse(tag[1..], out var version) && version > current;

    internal static string FileName(string tag, bool setup) =>
        $"WristMusic-{tag[1..]}-win-x64-" + (setup ? "setup.exe" : "portable.zip");

    internal static string ReadDigest(string manifest, string name)
    {
        var matches = Regex.Matches(manifest, @"(?im)^([a-f0-9]{64})[ \t]+\*?" + Regex.Escape(name) + @"\r?$");
        if (matches.Count != 1) throw new InvalidDataException("发行包缺少唯一有效的 SHA256。");
        return matches[0].Groups[1].Value.ToUpperInvariant();
    }

    private Package? ReadPending()
    {
        if (!File.Exists(Pending)) return null;
        Package? p;
        try { p = JsonSerializer.Deserialize<Package>(File.ReadAllText(Pending)); }
        catch (JsonException) { File.Delete(Pending); return null; }
        return p is { Tag: not null, Digest: not null } && Newer(p.Tag, _current) && p.Setup == Installed &&
            Regex.IsMatch(p.Digest, @"\A[0-9A-F]{64}\z") ? p : null;
    }

    internal async Task<string> Check(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // GitHub redirects /latest to the stable release. This endpoint also
            // works when the shared IP's anonymous REST API quota is exhausted.
            using var request = new HttpRequestMessage(HttpMethod.Head, Repository + "/releases/latest");
            using var latest = await Redirect.SendAsync(request, token).ConfigureAwait(false);
            var location = latest.Headers.Location;
            const string prefix = Repository + "/releases/tag/";
            var url = location is null ? "" : new Uri(request.RequestUri!, location).AbsoluteUri;
            if ((int)latest.StatusCode is < 300 or > 399 || !url.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("无法识别 GitHub 发行页。");
            var tag = url[prefix.Length..];
            if (!Newer(tag, _current)) return "已是最新版本";
            var setup = Installed;
            var name = FileName(tag, setup);
            var download = Repository + "/releases/download/" + tag + "/";
            var digest = ReadDigest(await Http.GetStringAsync(download + "SHA256SUMS.txt", token).ConfigureAwait(false), name);
            var entry = new Package(tag, digest, setup);
            Directory.CreateDirectory(_cache);
            var path = Path.Combine(_cache, name);
            if (ReadPending() != entry || !File.Exists(path))
            {
                var tmp = path + ".part";
                try
                {
                    using var response = await Http.GetAsync(download + name, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using (var output = File.Create(tmp))
                        await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
                    using (var input = File.OpenRead(tmp))
                        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != digest)
                            throw new InvalidDataException("下载校验失败。");
                    token.ThrowIfCancellationRequested();
                    File.Move(tmp, path, true);
                    File.WriteAllText(Pending + ".tmp", JsonSerializer.Serialize(entry));
                    File.Move(Pending + ".tmp", Pending, true);
                }
                finally { File.Delete(tmp); }
            }
            return tag + " 已下载，下次启动时更新";
        }
        finally { _gate.Release(); }
    }

    internal static bool Applying()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose(); return true;
    }

    internal bool StartPending(bool steamVr)
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "WristMusic.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var pending = ReadPending();
            if (pending is null) return false;
            // The cache is per-user. Never execute a cached helper at elevated integrity.
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
            var package = Path.Combine(_cache, FileName(pending.Tag, pending.Setup));
            if (!File.Exists(package)) { File.Delete(Pending); return false; }
            var script = Path.Combine(_cache, "apply.ps1");
            using (var resource = typeof(UpdateService).Assembly.GetManifestResourceStream("WristMusic.ApplyUpdate")!)
            using (var file = File.Create(script)) resource.CopyTo(file);
            var signal = @"Local\WristMusic.Update." + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
            var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", script,
                "-Root", _root, "-Package", package, "-Hash", pending.Digest, "-ParentId", Environment.ProcessId.ToString(),
                "-Ready", signal }) start.ArgumentList.Add(arg);
            if (pending.Setup) start.ArgumentList.Add("-Setup");
            if (steamVr) start.ArgumentList.Add("-SteamVr");
            using var worker = Process.Start(start);
            if (worker is not null && ready.WaitOne(TimeSpan.FromSeconds(10))) return true;
            VrDiagnostics.Write("Update helper did not start.");
        }
        catch (Exception ex) { VrDiagnostics.Write("Update startup: " + ex.Message); }
        return false;
    }
}
