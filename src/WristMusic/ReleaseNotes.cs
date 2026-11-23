namespace WristMusic;

public static class ReleaseNotes
{
    // Use the distributed build version for read receipts, including preview builds.
    internal const string BuildVersion = "6.0.0";
    public const string Heading = "v6.0.0更新内容";
    public static string Body { get; } = ReadBody();

    private static string ReadBody()
    {
        const string notes = "1.优化更新问题\n2.在百宝箱里添加了内存优化工具\n3.加入了首次使用打开歌曲信息教程";
        using var extra = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("WristMusic.LocalReleaseNotes");
        if (extra == null) return notes;
        using var reader = new System.IO.StreamReader(extra);
        return notes + "\n" + reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    internal static bool IsUnread(string? lastSeen) =>
        !Version.TryParse(lastSeen, out var seen) || seen < Version.Parse(BuildVersion);
}
