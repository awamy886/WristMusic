using WristMusic.Services;

namespace WristMusic;

internal static class TimelineTests
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var empty = MediaTimeline.Normalize(0, 0, 0, 0, 0, DateTimeOffset.FromFileTime(0), null, now);
        check(!empty.Known && empty.At(now.AddSeconds(5), true) == 0, "NetEase zero/default timestamp means unavailable, never an invented elapsed timer");
        var seekRange = MediaTimeline.Normalize(84, 0, 0, 0, 228, now, 1, now);
        check(seekRange.Known && seekRange.Max == 228 && seekRange.At(now.AddSeconds(2), true) == 86,
            "MaxSeekTime supplies a missing EndTime without clamping real position to zero");
        check(seekRange.At(now.AddSeconds(30), false) == 84 && seekRange.At(now.AddHours(1), true) == 228,
            "Paused time freezes and playing time stops at the actual duration");
        var noDuration = MediaTimeline.Normalize(84, 0, 0, 0, 0, now, 1, now);
        check(noDuration.Known && noDuration.Max == 0 && noDuration.At(now.AddSeconds(2), true) == 86,
            "A missing duration does not erase a valid current position");
        var missingStamp = MediaTimeline.Normalize(84, 0, 228, 0, 228, DateTimeOffset.FromFileTime(0), null, now);
        var futureStamp = MediaTimeline.Normalize(84, 0, 228, 0, 228, now.AddMinutes(1), 1, now);
        check(missingStamp.At(now.AddSeconds(10), true) == 84 && futureStamp.At(now.AddSeconds(10), true) == 84,
            "Missing and future update timestamps do not produce runaway progress");
        var speed = MediaTimeline.Normalize(84, 0, 228, 0, 228, now, 2, now);
        var seek = MediaTimeline.Normalize(10, 0, 228, 0, 228, now.AddSeconds(3), 1, now.AddSeconds(3));
        check(speed.At(now.AddSeconds(3), true) == 90 && seek.At(now.AddSeconds(4), true) == 11,
            "Playback rate is respected and a new seek snapshot replaces the preceding anchor");
        check(NeteaseTimelineReader.TryParse(" 01:24 / 03:48 ", out var pos, out var duration) && pos == 84 && duration == 228 &&
            NeteaseTimelineReader.TryParse("01:24 | 03:48", out _, out _) &&
            !NeteaseTimelineReader.TryParse("00:00 / 00:00", out _, out _) &&
            !NeteaseTimelineReader.TryParse("05:00 / 03:48", out _, out _) &&
            !NeteaseTimelineReader.TryParse("歌词 01:24 / 03:48", out _, out _),
            "NetEase accessibility parser accepts actual progress and rejects missing/invalid/unrelated text");
    }
}
