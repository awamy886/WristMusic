using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using WristMusic.Services;

namespace WristMusic;

internal static class UpdateDownloadTests
{
    private sealed class Endpoint(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }

    private sealed class ProgressLog : List<UpdateService.Transfer>, IProgress<UpdateService.Transfer>
    {
        public void Report(UpdateService.Transfer value) => Add(value);
    }

    internal static async Task Run(string folder, Action<bool, string> check)
    {
        var cache = Path.Combine(folder, "update-download-" + Guid.NewGuid().ToString("N"));
        const string tag = "v99.0.0";
        var payload = new byte[180000];
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var name = UpdateService.FileName(tag, File.Exists(Path.Combine(AppContext.BaseDirectory, "WristMusic.install.ini")));
        var online = true;
        int requests = 0;
        using var metadata = new HttpClient(new Endpoint(_ =>
        {
            if (!online) throw new HttpRequestException("offline");
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(UpdateService.Repository + "/releases/tag/" + tag);
            return redirect;
        }));
        using var assets = new HttpClient(new Endpoint(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS.txt"))
                return new(HttpStatusCode.OK) { Content = new StringContent(digest + "  " + name) };
            var data = ++requests == 1 ? payload[..1024] : payload;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        }));
        var service = new UpdateService(cache, metadata, assets);
        var progress = new ProgressLog();
        try { await service.Check(default, true, progress); }
        catch (EndOfStreamException) { }
        var path = Path.Combine(cache, name);
        var pending = Path.Combine(cache, "pending.json");
        check(requests == 1 && !File.Exists(path) && !File.Exists(path + ".part") && !File.Exists(pending),
            "Truncated downloads cannot become pending installations");
        online = false;
        await service.Check(default, true, progress);
        check(requests == 2 && File.Exists(pending) && File.ReadAllBytes(path).SequenceEqual(payload),
            "A retry recovers the known release after interruption even if latest lookup fails");
        check(progress.Any(p => p.Percent == 0) && progress.Any(p => p.Percent == 100 && p.Status.Contains("/s")),
            "Transfer reports include percentage and speed through completion");
        var status = await service.Check(default, true);
        check(requests == 2 && status.Contains("已下载并校验"), "Offline checks preserve a verified pending package");
        File.WriteAllText(path, "corrupted");
        await service.Check(default, true);
        check(requests == 3 && File.ReadAllBytes(path).SequenceEqual(payload), "A corrupt completed file is downloaded again");
        File.Move(pending, Path.Combine(cache, "candidate.json"));
        status = await new UpdateService(cache, metadata, assets).Check(default, true);
        check(requests == 3 && File.Exists(pending) && status.Contains("已下载并校验"),
            "Interrupted pending-record promotion recovers offline after restart without a duplicate download");
        File.Delete(pending);
        online = true;
        await service.Check(default, true);
        check(requests == 3 && File.Exists(pending), "A legacy orphaned package is revalidated and queued for installation");
    }
}
