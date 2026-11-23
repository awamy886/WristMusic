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
    private readonly HttpClient _metadata;
    private readonly HttpClient _downloads;
    private bool Installed => File.Exists(Path.Combine(_root, "WristMusic.install.ini"));
    private string Pending => Path.Combine(_cache, "pending.json");
    private sealed record Package(string Tag, string Digest, bool Setup);

    internal UpdateService(string? cache = null, HttpClient? metadata = null, HttpClient? downloads = null)
    {
        var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_root.ToUpperInvariant()));
        _cache = cache ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WristMusic", "Updates", Convert.ToHexString(key.AsSpan(0, 12)));
        _metadata = metadata ?? Redirect;
        _downloads = downloads ?? Http;
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

    private Package? ReadEntry(string path)
    {
        if (!File.Exists(path)) return null;
        Package? p;
        try { p = JsonSerializer.Deserialize<Package>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
        return p is { Tag: not null, Digest: not null } && Newer(p.Tag, _current) && p.Setup == Installed &&
            Regex.IsMatch(p.Digest, @"\A[0-9A-F]{64}\z") ? p : null;
    }

    private string Candidate => Path.Combine(_cache, "candidate.json");
    private string PackagePath(Package p) => Path.Combine(_cache, FileName(p.Tag, p.Setup));

    private static void SaveEntry(string path, Package entry)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entry));
        File.Move(path + ".tmp", path, true);
    }

    private static async Task<bool> Matches(string path, string digest, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == digest;
    }

    private async Task<Package?> FindReady(CancellationToken token)
    {
        // A crash can occur after the file is renamed and before pending.json is committed.
        var candidate = ReadEntry(Candidate);
        if (candidate != null && await Matches(PackagePath(candidate), candidate.Digest, token).ConfigureAwait(false))
        {
            SaveEntry(Pending, candidate);
            File.Delete(Candidate);
            return candidate;
        }
        var pending = ReadEntry(Pending);
        return pending != null && await Matches(PackagePath(pending), pending.Digest, token).ConfigureAwait(false) ? pending : null;
    }

    internal readonly record struct Transfer(string Status, double? Percent = null);

    private string ReadyStatus(string tag, bool automatic)
    {
        var next = !automatic ? "开启自动更新后可安装" :
            InstallError is { Length: > 0 } error ? $"上次安装未完成：{error}" :
            "可立即重启安装，也可下次启动时更新";
        return $"{tag} 已下载并校验，{next}";
    }

    internal async Task<string> Check(CancellationToken token, bool downloadUpdate, IProgress<Transfer>? progress = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var ready = await FindReady(token).ConfigureAwait(false);
            HasReadyPackage = ready != null;
            string tag;
            try { tag = await GetLatestTag(token).ConfigureAwait(false); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
            {
                if (ready != null) return ReadyStatus(ready.Tag, downloadUpdate);
                // The release lookup may be down even when its assets are reachable.
                var known = ReadEntry(Candidate) ?? ReadEntry(Pending);
                if (known == null || !downloadUpdate) throw;
                tag = known.Tag;
            }
            if (ready != null && !Newer(tag, Version.Parse(ready.Tag[1..]))) return ReadyStatus(ready.Tag, downloadUpdate);
            if (!Newer(tag, _current)) return "已是最新版本";
            if (!downloadUpdate) return $"发现新版本 {tag}，开启自动更新即可下载";
            progress?.Report(new($"发现 {tag}，正在准备下载…"));
            var setup = Installed;
            var name = FileName(tag, setup);
            var download = Repository + "/releases/download/" + tag + "/";
            var entry = ReadEntry(Candidate);
            if (entry?.Tag != tag) entry = ReadEntry(Pending);
            if (entry?.Tag != tag)
            {
                var digest = ReadDigest(await _downloads.GetStringAsync(download + "SHA256SUMS.txt", token).ConfigureAwait(false), name);
                entry = new(tag, digest, setup);
            }
            Directory.CreateDirectory(_cache);
            SaveEntry(Candidate, entry);
            var path = PackagePath(entry);
            if (!await Matches(path, entry.Digest, token).ConfigureAwait(false))
            {
                var tmp = path + ".part";
                try
                {
                    await Download(download + name, tmp, tag, progress, token).ConfigureAwait(false);
                    progress?.Report(new($"{tag} 下载完成，正在校验…"));
                    if (!await Matches(tmp, entry.Digest, token).ConfigureAwait(false))
                    {
                        File.Delete(Candidate);
                        if (ReadEntry(Pending) == entry) File.Delete(Pending);
                        throw new InvalidDataException("下载校验失败");
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(tmp, path, true);
                }
                finally { File.Delete(tmp); }
            }
            SaveEntry(Pending, entry);
            File.Delete(Candidate);
            File.Delete(ErrorFile);
            HasReadyPackage = true;
            return ReadyStatus(tag, downloadUpdate);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> GetLatestTag(CancellationToken token)
    {
        // GitHub redirects /latest to the stable release, independently of the REST API quota.
        using var request = new HttpRequestMessage(HttpMethod.Head, Repository + "/releases/latest");
        using var latest = await _metadata.SendAsync(request, token).ConfigureAwait(false);
        var location = latest.Headers.Location;
        const string prefix = Repository + "/releases/tag/";
        var url = location is null ? "" : new Uri(request.RequestUri!, location).AbsoluteUri;
        if ((int)latest.StatusCode is < 300 or > 399 || !url.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("无法识别 GitHub 发行页。");
        return url[prefix.Length..];
    }

    private async Task Download(string url, string tmp, string tag, IProgress<Transfer>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var idle = TimeSpan.FromSeconds(30);
        timeout.CancelAfter(idle);
        using var response = await _downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = File.Create(tmp);
        var clock = Stopwatch.StartNew();
        long received = 0, previous = 0;
        void Report()
        {
            var rate = (received - previous) / Math.Max(clock.Elapsed.TotalSeconds, .001);
            double? percent = total > 0 ? Math.Min(100, received * 100d / total.Value) : null;
            var amount = percent.HasValue ? $"{percent:0.#}% · {received / 1048576d:0.0}/{total / 1048576d:0.0} MB" : $"{received / 1048576d:0.0} MB";
            var speed = rate >= 1048576 ? $"{rate / 1048576:0.0} MB/s" : $"{rate / 1024:0} KB/s";
            progress?.Report(new($"正在下载 {tag} · {amount} · {speed}", percent));
            previous = received;
            clock.Restart();
        }
        Report();
        var buffer = new byte[81920];
        while (true)
        {
            timeout.CancelAfter(idle);
            var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            received += count;
            if (clock.ElapsedMilliseconds >= 300) Report();
        }
        if (total.HasValue && total != received) throw new EndOfStreamException("下载中断");
        if (received != previous) Report();
    }

    internal static bool Applying()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose(); return true;
    }

    internal bool HasReadyPackage { get; private set; }
    private string ErrorFile => Path.Combine(_cache, "error.txt");
    internal string? InstallError
    {
        get
        {
            try { return File.Exists(ErrorFile) ? File.ReadAllText(ErrorFile).Trim() : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    internal async Task<bool> StartPending(bool steamVr)
    {
        await _gate.WaitAsync();
        try
        {
            var pending = await FindReady(CancellationToken.None);
            HasReadyPackage = pending != null;
            if (pending is null) return false;
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("请退出腕间音乐，使用普通用户权限重新打开后安装更新");
            if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "WristMusic.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用 WristMusic.exe 启动更新");

            // A separate copy uses the Windows inbox CLR. The old runtime and the
            // camera worker can exit before any files in the installation are replaced.
            var helper = Path.Combine(_cache, "WristUpdater.exe");
            using (var resource = typeof(UpdateService).Assembly.GetManifestResourceStream("WristMusic.UpdaterExe")!)
            using (var file = File.Create(helper)) resource.CopyTo(file);
            var signal = @"Local\WristMusic.Update." + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
            using var proceed = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".proceed");
            var start = new ProcessStartInfo(helper)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _cache };
            foreach (var arg in new[] { _root, PackagePath(pending), pending.Digest, Environment.ProcessId.ToString(),
                signal, pending.Tag[1..], pending.Setup ? "setup" : "portable", steamVr ? "steamvr" : "desktop" })
                start.ArgumentList.Add(arg);
            File.Delete(ErrorFile);
            using var worker = Process.Start(start) ?? throw new IOException("无法启动更新程序");
            await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                while (!ready.WaitOne(100))
                {
                    if (worker.HasExited) throw new IOException(InstallError ?? $"更新程序已退出（{worker.ExitCode}）");
                    if (clock.Elapsed >= TimeSpan.FromSeconds(45)) throw new TimeoutException("更新程序启动超时，请重试");
                }
            });
            proceed.Set();
            return true;
        }
        catch (Exception ex)
        {
            VrDiagnostics.Write("Update startup: " + ex.Message);
            try { Directory.CreateDirectory(_cache); File.WriteAllText(ErrorFile, ex.Message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        finally { _gate.Release(); }
    }
}
