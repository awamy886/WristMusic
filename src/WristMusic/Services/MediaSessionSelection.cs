namespace WristMusic.Services;

internal static class MediaSessionSelection
{
    private static readonly string[] BrowserMarkers = ["chrome", "chromium", "msedge", "microsoftedge", "microsoft.edge",
        "firefox", "mozilla", "brave", "opera", "vivaldi", "browser", "360se", "360chrome", "iexplore", "waterfox"];
    internal static bool IsBrowser(string source) =>
        BrowserMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // Choose a player first, then its best session (e.g. NetEase's timeline bridge).
    internal static T? Pick<T>(IEnumerable<T> sessions, IReadOnlyList<AppEntry> apps, AppEntry? selected,
        string? current, Func<T, string> sourceOf, Func<T, int> scoreOf) where T : class
    {
        var candidates = sessions.Select(session =>
        {
            var source = sourceOf(session);
            var player = string.IsNullOrEmpty(source) || IsBrowser(source) ? null :
                selected is null ? apps.FirstOrDefault(a => a.Matches(source)) :
                selected.Matches(source) ? selected : null;
            return (session, player, source);
        }).Where(c => c.player is not null)
          .Select(c => (c.session, c.player, c.source, score: scoreOf(c.session)))
          .Where(c => c.score >= 0);
        return candidates.GroupBy(c => c.player)
            .OrderByDescending(group => group.Any(c => (c.score & 1) != 0))
            .ThenByDescending(group => group.Any(c => string.Equals(c.source, current, StringComparison.OrdinalIgnoreCase)))
            .Select(group => group.OrderByDescending(c => c.score).First().session)
            .FirstOrDefault();
    }
    internal static bool IsNetease(string source) => source.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) || source.Contains("inflink", StringComparison.OrdinalIgnoreCase);
    internal static bool SamePlayer(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || IsNetease(a) && IsNetease(b);
    internal static int Score(bool validTimeline, bool canSeek, bool playing) => (validTimeline ? 100 : 0) + (canSeek ? 10 : 0) + (playing ? 1 : 0);
}
