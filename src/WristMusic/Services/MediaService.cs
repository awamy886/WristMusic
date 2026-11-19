using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WristMusic.Services;

public sealed record MediaState(string Source, string Title, string Artist, bool Playing, double Position, double Min, double Max,
    bool Seek, bool Previous, bool Next, bool Toggle, BitmapImage? Artwork, DateTimeOffset Updated,
    bool TimelineKnown = true, double Rate = 1, string TimelineSource = "Windows")
{
    internal MediaTimeline Timeline => new(Position, Min, Max, TimelineKnown, Updated, Rate);
}

public sealed class MediaService(IReadOnlyList<AppEntry>? players = null) : IDisposable
{
    private readonly IReadOnlyList<AppEntry> _players = players ?? new PlayerService().Apps;
    internal bool CanSendGlobalKey(AppEntry? target)
    {
        try
        {
            var source = _manager?.GetCurrentSession()?.SourceAppUserModelId;
            return _manager is not null && (source is null ||
                !MediaSessionSelection.IsBrowser(source) && target?.Matches(source) == true);
        }
        catch { return false; }
    }

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _metadataKey = "";
    private string _title = "", _artist = "";
    private BitmapImage? _artwork;
    private volatile bool _metadataDirty = true;
    private DateTime _nextRetry;
    private bool _initializationRequested;
    private readonly NeteaseTimelineReader _netease = new();
    public string? Error { get; private set; }
    public string? Source => _session?.SourceAppUserModelId;
    public async Task Initialize()
    {
        _initializationRequested = true;
        _nextRetry = DateTime.UtcNow.AddSeconds(8);
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); Error = null; }
        catch (Exception e) { Error = "Windows 媒体会话不可用：" + e.Message; }
    }

    public async Task<MediaState?> Refresh(AppEntry? selected)
    {
        if (_manager is null && _initializationRequested && DateTime.UtcNow >= _nextRetry) await Initialize();
        if (_manager is null) return null;
        var sessions = _manager.GetSessions();
        string? current;
        try { current = _manager.GetCurrentSession()?.SourceAppUserModelId; }
        catch (System.Runtime.InteropServices.COMException) { current = null; }
        var next = MediaSessionSelection.Pick(sessions, _players, selected, current, session =>
        {
            try { return session.SourceAppUserModelId; }
            catch (System.Runtime.InteropServices.COMException) { return ""; }
        }, SessionScore);
        if (next is null) { SetSession(null); _metadataKey = ""; _netease.Read(""); return null; }
        SetSession(next);
        // Metadata is queried, rather than assumed, so same-title track changes and cover updates are observed.
        var properties = await next.TryGetMediaPropertiesAsync();
        var key = next.SourceAppUserModelId + "|" + properties.Title + "|" + properties.Artist + "|" + properties.AlbumTitle;
        _title = properties.Title; _artist = properties.Artist;
        if (_metadataDirty || key != _metadataKey)
        {
            _metadataDirty = false;
            _metadataKey = key; _artwork = null;
            if (properties.Thumbnail is not null)
            {
                try
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync();
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    if (stream.Size <= 16 * 1024 * 1024)
                    {
                        await reader.LoadAsync((uint)stream.Size);
                        var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                        using var memory = new System.IO.MemoryStream(bytes);
                        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 512; image.StreamSource = memory; image.EndInit(); image.Freeze(); _artwork = image;
                    }
                }
                catch { /* Broken artwork should not disable music controls. */ }
            }
        }
        var playback = next.GetPlaybackInfo(); var timeline = next.GetTimelineProperties(); var c = playback.Controls;
        var normalized = MediaTimeline.Normalize(timeline.Position.TotalSeconds, timeline.StartTime.TotalSeconds,
            timeline.EndTime.TotalSeconds, timeline.MinSeekTime.TotalSeconds, timeline.MaxSeekTime.TotalSeconds,
            timeline.LastUpdatedTime, playback.PlaybackRate, DateTimeOffset.UtcNow);
        var origin = "Windows";
        var needsFallback = !normalized.Known && next.SourceAppUserModelId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase);
        var fallback = _netease.Read(needsFallback ? _title : "", needsFallback ? _metadataKey : "",
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (!normalized.Known && fallback is not null)
        {
            normalized = new(fallback.Position, 0, fallback.Duration, true, fallback.ReadAt, 0);
            origin = "网易云客户端";
        }
        return new(next.SourceAppUserModelId, _title, _artist, playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            normalized.Position, normalized.Min, normalized.Max, origin == "Windows" && c.IsPlaybackPositionEnabled && normalized.Max > normalized.Min, c.IsPreviousEnabled, c.IsNextEnabled,
            c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled, _artwork, normalized.Updated, normalized.Known, normalized.Rate, origin);
    }

    private static int SessionScore(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties(); var p = session.GetPlaybackInfo();
            var timeline = MediaTimeline.Normalize(t.Position.TotalSeconds, t.StartTime.TotalSeconds, t.EndTime.TotalSeconds,
                t.MinSeekTime.TotalSeconds, t.MaxSeekTime.TotalSeconds, t.LastUpdatedTime, p.PlaybackRate, DateTimeOffset.UtcNow);
            return MediaSessionSelection.Score(timeline.Known && timeline.Max > timeline.Min, p.Controls.IsPlaybackPositionEnabled,
                p.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }
        catch { return -1; }
    }

    private void SetSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (Equals(_session, session)) return;
        if (_session is not null) _session.MediaPropertiesChanged -= OnPropertiesChanged;
        _session = session; _metadataDirty = true;
        if (_session is not null) _session.MediaPropertiesChanged += OnPropertiesChanged;
    }
    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _metadataDirty = true;
    public void Dispose() { SetSession(null); _manager = null; _netease.Dispose(); }

    public async Task<bool> Control(string action, double value = 0)
    {
        var s = _session; if (s is null) return false;
        var info = s.GetPlaybackInfo();
        return action switch
        {
            "toggle" => info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? await s.TryPauseAsync() : await s.TryPlayAsync(),
            "pause" => await s.TryPauseAsync(),
            "play" => await s.TryPlayAsync(),
            "previous" => await s.TrySkipPreviousAsync(),
            "next" => await s.TrySkipNextAsync(),
            "seek" => await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(value).Ticks),
            _ => false
        };
    }
}
