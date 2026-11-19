using System.Text.Json;
using WristMusic.Services;

namespace WristMusic;

// Explicit diagnostic command only; never run during normal startup or self-tests.
internal static class BridgePlaybackTests
{
    internal static async Task<string> Restore(string title, double position)
    {
        using var service = new MediaService(); await service.Initialize();
        var player = new AppEntry("netease", "网易云音乐", "云", "#E45D71", ["cloudmusic", "inflink"]);
        try
        {
            var s = await service.Refresh(player) ?? throw new InvalidOperationException("网易云会话不可用。");
            var preceding = s.Title;
            await service.Control("pause");
            for (var attempt = 0; s.Title != title && attempt < 2; attempt++)
            {
                if (!await service.Control("previous")) throw new InvalidOperationException("恢复上一曲的请求被拒绝。");
                await Task.Delay(1500); s = await service.Refresh(player) ?? throw new InvalidOperationException("会话消失。");
                if (s.Title != preceding && s.Title != title) throw new InvalidOperationException("上一曲并非测试前歌曲，已停止恢复。");
            }
            if (s.Title != title) throw new InvalidOperationException("无法找到测试前歌曲。");
            await service.Control("pause"); await Task.Delay(500);
            if (!await service.Control("seek", position)) throw new InvalidOperationException("恢复位置的请求被拒绝。");
            await Task.Delay(2000); await service.Control("pause");
            s = await service.Refresh(player) ?? throw new InvalidOperationException("会话消失。");
            return JsonSerializer.Serialize(new { success = s.Title == title && !s.Playing && Math.Abs(s.Position - position) < 2, s.Title, s.Position, s.Playing });
        }
        catch (Exception e) { await service.Control("pause"); return JsonSerializer.Serialize(new { success = false, error = e.Message }); }
    }
    internal static async Task<string> Run()
    {
        using var service = new MediaService();
        var player = new AppEntry("netease", "网易云音乐", "云", "#E45D71", ["cloudmusic", "inflink"]);
        await service.Initialize();
        var samples = new List<object>();
        var checks = new Dictionary<string, bool>();
        var initial = await service.Refresh(player);
        if (initial is not { TimelineKnown: true, Seek: true, Max: > 15 })
            return JsonSerializer.Serialize(new { success = false, error = "网易云尚未提供有效且可跳转的真实时间轴，未执行播放测试。" });
        string? error = null;
        var changed = false;
        bool SameSong(MediaState s) => s.Title == initial.Title && s.Artist == initial.Artist;
        async Task<MediaState> Read()
        {
            var s = await service.Refresh(player) ?? throw new InvalidOperationException("媒体会话消失。");
            if (!SameSong(s)) throw new InvalidOperationException("测试期间歌曲已切换，停止操作。");
            samples.Add(new { at = DateTimeOffset.UtcNow, s.Source, s.Title, s.Playing, s.Position, duration = s.Max, s.Seek, s.Updated });
            return s;
        }
        async Task<MediaState> Wait(Func<MediaState, bool> predicate)
        {
            for (var i = 0; i < 80; i++) { var s = await Read(); if (predicate(s)) return s; await Task.Delay(250); }
            throw new TimeoutException("播放器未在 20 秒内同步请求的状态。");
        }
        try
        {
            // Pause first and move away from the end, avoiding accidental track changes.
            changed = true;
            if (!await service.Control("pause")) throw new InvalidOperationException("暂停请求被拒绝。");
            await Wait(s => !s.Playing);
            var anchor = Math.Min(initial.Position, initial.Max - 10);
            if (!await service.Control("seek", anchor)) throw new InvalidOperationException("定位请求被拒绝。");
            var paused = await Wait(s => !s.Playing && Math.Abs(s.Position - anchor) < 2);
            if (!await service.Control("play")) throw new InvalidOperationException("播放请求被拒绝。");
            var started = await Wait(s => s.Playing);
            await Task.Delay(2500);
            var advanced = await Read();
            checks["real_progress_advances"] = advanced.Position - started.Position >= 1;
            if (!await service.Control("pause")) throw new InvalidOperationException("暂停请求被拒绝。");
            var stopped = await Wait(s => !s.Playing);
            // The bridge publishes the pause event before the final in-flight progress sample.
            // Compare two settled readings, allowing the player's last real position to arrive.
            await Task.Delay(1200); stopped = await Read();
            await Task.Delay(1800);
            var frozen = await Read();
            checks["paused_position_freezes"] = !stopped.Playing && !frozen.Playing && Math.Abs(frozen.Position - stopped.Position) <= 0.5;
            var target = Math.Min(initial.Max - 5, Math.Max(5, frozen.Position + 5));
            if (!await service.Control("seek", target)) throw new InvalidOperationException("跳转请求被拒绝。");
            var sought = await Wait(s => !s.Playing && Math.Abs(s.Position - target) < 2);
            checks["seek_reaches_requested_position"] = Math.Abs(sought.Position - target) < 2;
        }
        catch (Exception e) { error = e.Message; }
        finally
        {
            if (changed)
            {
                try
                {
                    var current = await Read();
                    await service.Control("pause"); await Wait(s => !s.Playing);
                    var accepted = await service.Control("seek", initial.Position);
                    var restored = await Wait(s => !s.Playing && Math.Abs(s.Position - initial.Position) < 2);
                    if (initial.Playing) { await service.Control("play"); restored = await Wait(s => s.Playing); }
                    else { await Task.Delay(1500); restored = await Read(); }
                    checks["original_position_and_play_state_restored"] = accepted && restored.Playing == initial.Playing;
                }
                catch (Exception e) { checks["original_position_and_play_state_restored"] = false; error = (error is null ? "" : error + " ") + "恢复状态：" + e.Message; }
            }
        }
        return JsonSerializer.Serialize(new { success = error is null && checks.Count == 4 && checks.Values.All(v => v), error,
            initial = new { initial.Title, initial.Artist, initial.Position, initial.Playing, duration = initial.Max }, checks, samples }, new JsonSerializerOptions { WriteIndented = true });
    }
}
