using System.Diagnostics;
using System.Text.Json;
using Windows.Media.Control;
using WristMusic.Services;

namespace WristMusic;

internal static class MediaDiagnostics
{
    public static async Task<string> Read()
    {
        var samples = new List<object>();
        try
        {
            using var reader = new NeteaseTimelineReader();
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            for (var sample = 0; sample < 8; sample++)
            {
                foreach (var session in manager.GetSessions())
                {
                    var media = await session.TryGetMediaPropertiesAsync();
                    var playback = session.GetPlaybackInfo(); var time = session.GetTimelineProperties();
                    samples.Add(new {
                        at = DateTimeOffset.UtcNow, source = session.SourceAppUserModelId,
                        title = media.Title, artist = media.Artist, status = playback.PlaybackStatus.ToString(),
                        position = time.Position.TotalSeconds, start = time.StartTime.TotalSeconds, end = time.EndTime.TotalSeconds,
                        seekMin = time.MinSeekTime.TotalSeconds, seekMax = time.MaxSeekTime.TotalSeconds,
                        updated = time.LastUpdatedTime, rate = playback.PlaybackRate,
                        seekEnabled = playback.Controls.IsPlaybackPositionEnabled,
                        neteaseTimeline = reader.Read(media.Title), readerDetail = reader.Detail
                    });
                }
                if (sample < 7) await Task.Delay(1000);
            }
            return JsonSerializer.Serialize(new { samples }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { error = e.Message }); }
    }
}
